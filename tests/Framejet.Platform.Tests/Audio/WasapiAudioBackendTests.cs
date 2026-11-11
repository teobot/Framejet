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
            using var b = new WasapiAudioBackend(NullLog.Instance); string failed = null; b.Failed += m => failed = m;
            var input = b.ListInputs().FirstOrDefault(i => i.Name == "HDMI (Razer Ripsaw X)") ?? b.ListInputs().FirstOrDefault(i => i.Name == Hardware.Microphone());
            Skip.If(input == null, "no Ripsaw input, and no microphone set in hardware.local.ini"); var output = b.DefaultOutput(); Assert.NotNull(output); Assert.Contains(b.ListOutputs(), o => o.Id == output.Id);
            b.Start(input, output, 0f); Thread.Sleep(2000); var s = b.GetStats(reset: false); b.Stop(); b.Stop();
            Assert.Null(failed); Assert.True(s.BytesCaptured > 0); Assert.True(s.MaxBufferedMs <= 70, $"max {s.MaxBufferedMs} ms");
        }

        [SkippableFact, Trait("Category", "Hardware")] public void Watcher_registers_and_unregisters()
        { var error = Record.Exception(() => { using var w = new AudioDeviceWatcher(new ImmediateTestDispatcher()); }); Assert.Null(error); }
    }
}
