using System;
using System.Linq;
using System.Threading;
using Framejet.Core;
using Framejet.Platform.Audio;
using Framejet.Platform.Devices;
using Framejet.Platform.Tests.TestSupport;
using Xunit;

namespace Framejet.Platform.Tests.Audio
{
    public class WasapiAudioBackendTests
    {
        [SkippableFact, Trait("Category", "Hardware")] public void Ripsaw_hdmi_input_shares_the_cameras_container()
        { var ripsaw = Hardware.Ripsaw(); using var b = new WasapiAudioBackend(NullLog.Instance);
          var hdmi = b.ListInputs().SingleOrDefault(i => i.Name == "HDMI (Razer Ripsaw X)"); Assert.NotNull(hdmi);
          Assert.Equal(Hardware.RipsawContainer(), hdmi.ContainerId); Assert.Equal(hdmi.ContainerId, DeviceContainers.ForDirectShowDevicePath(ripsaw.Path)); }

        [SkippableFact, Trait("Category", "Hardware")] public void Plays_through_at_gain_zero()
        {
            var registry = new Framejet.Core.Audio.LowLatencyRegistry(); // normal mode: LowLatencyTests cover the other
            using var b = new WasapiAudioBackend(NullLog.Instance, registry); string failed = null; b.Failed += m => failed = m;
            var input = b.ListInputs().FirstOrDefault(i => i.Name == "HDMI (Razer Ripsaw X)") ?? b.ListInputs().FirstOrDefault(i => i.Name == Hardware.Microphone());
            Skip.If(input == null, "no Ripsaw input, and no microphone set in hardware.local.ini"); var output = b.DefaultOutput(); Assert.NotNull(output); Assert.Contains(b.ListOutputs(), o => o.Id == output.Id);
            registry.Unsupported(input.Id); registry.Unsupported(output.Id);
            b.Start(input, output, 0f); Thread.Sleep(5000); var s = b.GetStats(reset: false); b.Stop(); b.Stop();
            Assert.Null(failed); Assert.True(s.BytesCaptured > 0); Assert.True(s.MaxBufferedMs <= 70, $"max {s.MaxBufferedMs} ms");
            Assert.NotNull(s.Delay); Assert.True(s.Delay.CaptureMs > 0); Assert.True(s.Delay.OutputMs > 0); Assert.Equal("normal", s.Delay.Mode); Assert.True(s.Delay.PeriodMs > 0, "live capture client not read");
            Assert.True(s.Delay.BufferP95Ms <= 35, $"buffer p95 {s.Delay.BufferP95Ms} ms"); // 13 ms headroom + a 10 ms packet + capture jitter Assert.Equal(0, s.Trims); Assert.True(s.Delay.TotalMeanMs < 200, $"total {s.Delay.TotalMeanMs} ms");
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Block_tap_reports_1ms_blocks()
        {
            using var b = new WasapiAudioBackend(NullLog.Instance); string failed = null; b.Failed += m => failed = m;
            var mic = Hardware.Microphone(); Skip.If(mic == null, "microphone isn't set in hardware.local.ini");
            var input = b.ListInputs().FirstOrDefault(i => i.Name == mic); Skip.If(input == null, mic + " not connected");
            var output = b.DefaultOutput(); Assert.NotNull(output);
            var gate = new object(); int calls = 0; long last = long.MinValue, maxAgeTicks = 0, maxBackTicks = 0;
            b.Start(input, output, 0f);
            b.BlockPeakTap = (ticks, peak) =>
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (gate) { calls++; if (last != long.MinValue) maxBackTicks = Math.Max(maxBackTicks, last - ticks); last = ticks; maxAgeTicks = Math.Max(maxAgeTicks, Math.Abs(now - ticks)); }
            };
            Thread.Sleep(2000);
            b.BlockPeakTap = null; b.Stop();
            Assert.Null(failed);
            lock (gate)
            {
                Assert.InRange(calls, 1800, 2200);
                double backMs = maxBackTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Assert.True(maxBackTicks <= 0, $"block stamps went back by up to {backMs:0.00} ms");
                double maxAgeMs = maxAgeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Assert.True(maxAgeMs <= 50, $"a block stamp was {maxAgeMs:0.0} ms from now");
            }
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Watcher_registers_and_unregisters()
        { var error = Record.Exception(() => { using var w = new AudioDeviceWatcher(new ImmediateTestDispatcher()); }); Assert.Null(error); }
    }
}
