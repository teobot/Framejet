using System;
using System.Linq;
using System.Windows.Forms;
using Framejet.Core;
using Framejet.Core.Devices;
using Framejet.Core.Video;
using Framejet.Platform.Tests.TestSupport;
using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.Video
{
    public class DirectShowVideoBackendTests
    {
        static VideoMode Mode(VideoDeviceInfo d, string key) => ModeCatalog.Build(DirectShowDevices.ListRawModes(d)).Single(m => m.Key == key);

        [SkippableFact, Trait("Category", "Hardware")] public void Ripsaw_default_mode_draws_with_no_added_delay()
        {
            var ripsaw = Hardware.Ripsaw();
            Sta.Run(() => { using var host = new TestVideoHost(0, 0, fullscreenOnPrimary: true); using var b = new DirectShowVideoBackend(host, NullLog.Instance);
                var size = Screen.PrimaryScreen.Bounds.Size; var mode = ModeCatalog.PickDefault(ModeCatalog.Build(b.ListModes(ripsaw)), size.Width, size.Height);
                b.MeasureLatency = true; b.Start(ripsaw, mode); Sta.Pump(TimeSpan.FromSeconds(1)); b.ResetStats(); Sta.Pump(TimeSpan.FromSeconds(4));
                var fmt = b.Format; var s = b.GetStats(); b.Stop();
                Assert.Equal(mode.Width, fmt.Width); Assert.Equal(mode.Height, fmt.Height); Assert.True(s.Drawn >= 200, $"drawn {s.Drawn}");
                var v = DelayCheck.Evaluate(s.ArrivalToPresent, s.PresentToScreen, s.RefreshHz); Assert.True(v.Pass, v.Reason); });
        }
        [SkippableFact, Trait("Category", "Hardware")] public void Webcam_mjpg_arrives_as_bottom_up_rgb32()
        {
            var webcam = Hardware.Webcam();
            Sta.Run(() => { using var host = new TestVideoHost(1280, 720, false); using var b = new DirectShowVideoBackend(host, NullLog.Instance);
                b.Start(webcam, Mode(webcam, "1920x1080@30/MJPG")); Sta.Pump(TimeSpan.FromSeconds(4));
                Assert.Equal(GrabberFormat.Rgb32, b.Format.Format); Assert.True(b.Format.BottomUp); Assert.True(b.GetStats().Drawn >= 50); b.Stop(); });
        }
        [SkippableFact, Trait("Category", "Hardware")] public void Switching_modes_back_to_back_keeps_drawing()
        {
            var webcam = Hardware.Webcam();
            Sta.Run(() => { using var host = new TestVideoHost(1280, 720, false); using var b = new DirectShowVideoBackend(host, NullLog.Instance);
                foreach (var key in new[] { "1920x1080@30/MJPG", "640x480@30/YUY2", "1280x720@30/MJPG" })
                { b.Stop(); b.ResetStats(); var m = Mode(webcam, key); b.Start(webcam, m); Sta.Pump(TimeSpan.FromSeconds(1.5));
                  Assert.Equal(m.Width, b.Format.Width); Assert.True(b.GetStats().Drawn >= 20, key); }
                b.Stop(); });
        }
        // A second open doesn't fail as busy: Windows' camera frame server gives the device to the newest opener, so the first
        // graph gets EC_ERRORABORT with MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED (0xC00D3EA3) and the first backend reports DeviceTaken.
        [SkippableFact, Trait("Category", "Hardware")] public void Second_open_of_the_same_device_preempts_the_first()
        {
            var webcam = Hardware.Webcam();
            Sta.Run(() => { using var h1 = new TestVideoHost(640, 360, false); using var h2 = new TestVideoHost(640, 360, false);
                using var a = new DirectShowVideoBackend(h1, NullLog.Instance); using var b = new DirectShowVideoBackend(h2, NullLog.Instance);
                int aLost = 0, aTaken = 0; a.DeviceLost += () => aLost++; a.DeviceTaken += () => aTaken++;
                var m = Mode(webcam, "1280x720@30/MJPG"); a.Start(webcam, m); Sta.Pump(TimeSpan.FromSeconds(1));
                b.Start(webcam, m); Sta.Pump(TimeSpan.FromSeconds(2));
                Assert.Equal(1, aTaken); Assert.Equal(0, aLost); Assert.True(b.GetStats().Drawn > 0, $"drawn {b.GetStats().Drawn}"); a.Stop(); b.Stop(); b.Stop(); });
        }

        // A device can't be unplugged on demand, so these queue the graph events a capture filter raises.
        [SkippableFact, Trait("Category", "Hardware")] public void Graph_errors_raise_device_lost_once()
        {
            var webcam = Hardware.Webcam();
            Sta.Run(() => { using var host = new TestVideoHost(640, 360, false); using var b = new DirectShowVideoBackend(host, NullLog.Instance);
                int lost = 0; b.DeviceLost += () => lost++;
                b.Start(webcam, Mode(webcam, "640x480@30/YUY2")); Sta.Pump(TimeSpan.FromSeconds(0.5));
                b.SimulateGraphEvent(GraphEventPolicy.EC_ERRORABORT); Sta.Pump(TimeSpan.FromSeconds(0.3));
                b.SimulateGraphEvent(GraphEventPolicy.EC_STREAM_ERROR_STOPPED); Sta.Pump(TimeSpan.FromSeconds(0.3));
                Assert.Equal(1, lost); b.Stop(); });
        }
        [SkippableFact, Trait("Category", "Hardware")] public void A_device_lost_handler_can_restart_the_video()
        {
            var webcam = Hardware.Webcam();
            Sta.Run(() => { using var host = new TestVideoHost(640, 360, false); using var b = new DirectShowVideoBackend(host, NullLog.Instance);
                var m = Mode(webcam, "640x480@30/YUY2"); int lost = 0;
                b.DeviceLost += () => { lost++; b.Stop(); b.Start(webcam, m); }; // as VideoSession does, from inside the event window's message
                b.Start(webcam, m); Sta.Pump(TimeSpan.FromSeconds(0.5));
                b.SimulateGraphEvent(GraphEventPolicy.EC_ERRORABORT); b.SimulateGraphEvent(GraphEventPolicy.EC_STREAM_ERROR_STOPPED); Sta.Pump(TimeSpan.FromSeconds(0.5));
                b.ResetStats(); Sta.Pump(TimeSpan.FromSeconds(1.5)); // webcams often halve their rate to 15 fps in a dim room
                Assert.Equal(1, lost); Assert.NotNull(b.Format); Assert.True(b.GetStats().Drawn >= 10, $"drawn {b.GetStats().Drawn}"); b.Stop(); });
        }
    }
}
