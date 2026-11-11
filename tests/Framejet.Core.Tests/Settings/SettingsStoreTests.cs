using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using Framejet.Core.Settings;
using Xunit;

namespace Framejet.Core.Tests.Settings
{
    public class SettingsStoreTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "fj-tests-" + Guid.NewGuid().ToString("N"));
        string File1 => Path.Combine(dir, "sub", "settings.ini");
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        [Fact] public void Missing_file_gives_defaults()
        { var s = new SettingsStore(File1).Load(); Assert.True(s.VSync); Assert.Equal(1.0, (double)s.Volume); Assert.False(s.Muted); Assert.Equal("", s.ModeKey); Assert.False(s.Window.HasBounds); }
        [Fact] public void Round_trips_every_field()
        {
            var s = new AppSettings { VideoDevicePath = @"@device:pnp:\\?\usb#vid_1532&pid_0e6f#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global", VideoDeviceName = "Café Cam",
                ModeKey = "2560x1440@60/NV12", AudioOutputId = "{0.0.0.00000000}.{a}", AudioOutputName = "Speakers (USB Audio Device)",
                AudioInputId = "{0.0.1.00000000}.{b}", AudioInputName = "HDMI (Razer Ripsaw X)", Volume = 0.35f, Muted = true, VSync = false,
                Window = new WindowPlacement { X = -1700, Y = 400, Width = 1280, Height = 720, HasBounds = true, Maximized = true, Fullscreen = true, ScreenName = @"\\.\DISPLAY3" } };
            var store = new SettingsStore(File1); store.Save(s); var r = store.Load();
            Assert.Equal(s.VideoDevicePath, r.VideoDevicePath); Assert.Equal("Café Cam", r.VideoDeviceName); Assert.Equal(s.ModeKey, r.ModeKey);
            Assert.Equal(s.AudioOutputId, r.AudioOutputId); Assert.Equal(s.AudioOutputName, r.AudioOutputName);
            Assert.Equal(s.AudioInputId, r.AudioInputId); Assert.Equal(s.AudioInputName, r.AudioInputName);
            Assert.Equal(0.35, (double)r.Volume, 3); Assert.True(r.Muted); Assert.False(r.VSync);
            Assert.Equal(new Rectangle(-1700, 400, 1280, 720), r.Window.Bounds); Assert.True(r.Window.HasBounds && r.Window.Maximized && r.Window.Fullscreen);
            Assert.Equal(@"\\.\DISPLAY3", r.Window.ScreenName);
        }
        [Fact] public void Tolerates_junk()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File1));
            File.WriteAllText(File1, "garbage\nvolume=abc\nvsync=maybe\n=novalue\nmode=1920x1080@60/NV12\nunknown.key=1\n# comment\nvideo.name=A=B\n");
            var s = new SettingsStore(File1).Load();
            Assert.Equal("1920x1080@60/NV12", s.ModeKey); Assert.Equal(1.0, (double)s.Volume); Assert.True(s.VSync); Assert.Equal("A=B", s.VideoDeviceName);
        }
        [Fact] public void Uses_invariant_numbers()
        {
            var old = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); var st = new SettingsStore(File1); st.Save(new AppSettings { Volume = 0.5f });
                  Assert.Contains("volume=0.5", File.ReadAllText(File1)); Assert.Equal(0.5, (double)st.Load().Volume, 3); }
            finally { CultureInfo.CurrentCulture = old; }
        }
        [Fact] public void Save_replaces_atomically()
        { var st = new SettingsStore(File1); st.Save(new AppSettings()); st.Save(new AppSettings { ModeKey = "x" });
          Assert.Equal("x", st.Load().ModeKey); Assert.False(File.Exists(File1 + ".tmp")); }
    }
}
