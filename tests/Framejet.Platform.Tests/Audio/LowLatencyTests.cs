using System;
using System.Linq;
using System.Threading;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Platform.Audio;
using Framejet.Platform.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Framejet.Platform.Tests.Audio
{
    public class LowLatencyTests
    {
        readonly ITestOutputHelper _out;
        public LowLatencyTests(ITestOutputHelper output) { _out = output; }

        /// <summary>Writes the backend's log lines to the test output.</summary>
        sealed class OutputLog : ILog
        {
            readonly ITestOutputHelper _out;
            public OutputLog(ITestOutputHelper output) { _out = output; }
            public void Info(string message) => _out.WriteLine(message);
            public void Error(string message, Exception error = null) => _out.WriteLine("ERROR " + message + (error == null ? "" : ": " + error.Message));
        }

        static Framejet.Core.Devices.AudioInputInfo Input(WasapiAudioBackend b)
        {
            var input = b.ListInputs().FirstOrDefault(i => i.Name == "HDMI (Razer Ripsaw X)") ?? b.ListInputs().FirstOrDefault(i => i.Name == Hardware.Microphone());
            Skip.If(input == null, "no Ripsaw input, and no microphone set in hardware.local.ini");
            return input;
        }

        static (bool Capture, string WhyIn, bool Render, string WhyOut) Support(Framejet.Core.Devices.AudioInputInfo input, Framejet.Core.Devices.AudioOutputInfo output)
        {
            string whyIn, whyOut; bool capture, render;
            using (var c = LowLatencyCapture.TryOpen(input.Id, out whyIn)) capture = c != null;
            using (var r = LowLatencyRender.TryOpen(output.Id, out whyOut)) render = r != null;
            return (capture, whyIn, render, whyOut);
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Render_opens_low_latency_on_default_output_or_says_why()
        {
            using var b = new WasapiAudioBackend(NullLog.Instance);
            var output = b.DefaultOutput(); Assert.NotNull(output);
            using (var render = LowLatencyRender.TryOpen(output.Id, out var whyNot))
            {
                if (render != null)
                {
                    _out.WriteLine(FormattableString.Invariant($"default output: period {render.PeriodMs:0.00} ms, buffer {render.BufferFrames} frames, {render.MixFormat}"));
                    Assert.True(render.PeriodMs < 10, $"period {render.PeriodMs} ms");
                    Assert.True(render.PeriodMs > 0);
                    Assert.NotNull(render.MixFormat);
                    return;
                }
                _out.WriteLine("default output: " + whyNot);
                Assert.False(string.IsNullOrEmpty(whyNot));
            }
            var input = Input(b); string failed = null; b.Failed += m => failed = m;
            b.Start(input, output, 0f); Thread.Sleep(1000); var s = b.GetStats(reset: false); b.Stop();
            Assert.Null(failed);
            Assert.Equal("normal", s.Delay.Mode);
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Render_stream_pulls_at_the_device_rate()
        {
            using var b = new WasapiAudioBackend(NullLog.Instance);
            var output = b.DefaultOutput(); Assert.NotNull(output);
            using var render = LowLatencyRender.TryOpen(output.Id, out var whyNot);
            Skip.If(render == null, "default output has no low-latency mode: " + whyNot);
            var silence = new Silence(render.MixFormat); Exception stopped = null; int maxPadding = 0;
            render.Stopped += e => stopped = e;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            render.Init(silence); render.Play();
            while (clock.ElapsedMilliseconds < 1000) { Thread.Sleep(5); maxPadding = Math.Max(maxPadding, render.CurrentPaddingFrames); }
            render.Dispose();
            double elapsed = clock.Elapsed.TotalSeconds, pulled = silence.Bytes / (double)render.MixFormat.AverageBytesPerSecond;
            _out.WriteLine(FormattableString.Invariant($"pulled {pulled:0.000} s of audio in {elapsed:0.000} s; padding up to {maxPadding} frames"));
            Assert.Null(stopped);
            Assert.InRange(pulled, elapsed - 0.05, elapsed + 0.05);
            Assert.InRange(maxPadding, 1, render.BufferFrames);
            Assert.True(maxPadding * 1000.0 / render.MixFormat.SampleRate <= 3 * render.PeriodMs, $"padding up to {maxPadding} frames");
        }

        sealed class Silence : NAudio.Wave.IWaveProvider
        {
            long _bytes;
            public Silence(NAudio.Wave.WaveFormat format) { WaveFormat = format; }
            public NAudio.Wave.WaveFormat WaveFormat { get; }
            public long Bytes => Interlocked.Read(ref _bytes);
            public int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); Interlocked.Add(ref _bytes, count); return count; }
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Plays_through_low_latency_at_gain_zero()
        {
            using var b = new WasapiAudioBackend(new OutputLog(_out)); string failed = null; b.Failed += m => failed = m;
            var input = Input(b); var output = b.DefaultOutput(); Assert.NotNull(output);
            var support = Support(input, output);
            Skip.If(!support.Capture || !support.Render, "low-latency mode needs both ends; input: " + (support.WhyIn ?? "yes") + ", default output: " + (support.WhyOut ?? "yes"));

            b.Start(input, output, 0f); Thread.Sleep(5000); var s = b.GetStats(reset: false); b.Stop(); b.Stop();
            _out.WriteLine(s.Delay.ReportLine());
            Assert.Null(failed);
            Assert.Equal("low-latency", s.Delay.Mode);
            Assert.True(s.Delay.PeriodMs < 10, $"period {s.Delay.PeriodMs} ms");
            Assert.True(s.BytesCaptured > 0);
            Assert.True(s.Delay.CaptureMs > 0); Assert.True(s.Delay.OutputMs > 0);
            Assert.Equal(0, s.Trims);
            Assert.True(s.Delay.TotalMeanMs < 100, $"total {s.Delay.TotalMeanMs} ms");
        }

        // A 10 ms end can't share the 10 ms buffer target with a low-latency one, so it's both ends or neither.
        [SkippableFact, Trait("Category", "Hardware")] public void Runs_normal_mode_unless_both_ends_have_low_latency()
        {
            var registry = new LowLatencyRegistry();
            using var b = new WasapiAudioBackend(new OutputLog(_out), registry); string failed = null; b.Failed += m => failed = m;
            var input = Input(b); var output = b.DefaultOutput(); Assert.NotNull(output);
            var support = Support(input, output);
            Skip.If(support.Capture == support.Render, "needs exactly one end with a low-latency mode");

            b.Start(input, output, 0f); Thread.Sleep(1500); var s = b.GetStats(reset: false); b.Stop();
            Assert.Null(failed);
            Assert.Equal("normal", s.Delay.Mode);
            Assert.True(s.BytesCaptured > 0);
            Assert.Equal(support.Capture, registry.ShouldTry(input.Id)); // only the end that can't is remembered
            Assert.Equal(support.Render, registry.ShouldTry(output.Id));
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Unsupported_endpoint_runs_normal_mode()
        {
            var registry = new LowLatencyRegistry();
            using var b = new WasapiAudioBackend(NullLog.Instance, registry); string failed = null; b.Failed += m => failed = m;
            var input = Input(b); var output = b.DefaultOutput(); Assert.NotNull(output);
            registry.Unsupported(output.Id);

            b.Start(input, output, 0f); Thread.Sleep(1500); var s = b.GetStats(reset: false); b.Stop();
            Assert.Null(failed);
            Assert.Equal("normal", s.Delay.Mode);
            Assert.True(s.BytesCaptured > 0);
            Assert.True(registry.ShouldTry(input.Id) || !Support(input, output).Capture); // the input wasn't blamed for the output
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Capture_stream_delivers_short_packets()
        {
            using var b = new WasapiAudioBackend(NullLog.Instance);
            var input = Input(b);
            using var c = LowLatencyCapture.TryOpen(input.Id, out var whyNot);
            Skip.If(c == null, "input has no low-latency mode: " + whyNot);
            int packets = 0; long bytes = 0; Exception stopped = null;
            c.DataAvailable += (buf, n) => { Interlocked.Increment(ref packets); Interlocked.Add(ref bytes, n); };
            c.Stopped += e => stopped = e;
            c.Start(); Thread.Sleep(1000); c.Dispose();
            Assert.Null(stopped);
            double expected = 1000 / c.PeriodMs;
            Assert.InRange(packets, expected * 0.8, expected * 1.2);
            Assert.InRange(bytes, c.WaveFormat.AverageBytesPerSecond * 0.8, c.WaveFormat.AverageBytesPerSecond * 1.2);
        }
    }
}
