using System;
using System.Drawing;
using System.Linq;
using Framejet.Core.Tests.Fakes;
using Framejet.Core.Timing;
using Framejet.Core.Ui;
using Framejet.Core.Video;
using Xunit;

namespace Framejet.Core.Tests.Ui
{
    public class UiLogicTests
    {
        static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

        [Theory] // fw, fh, area w, area h, mode, expected
        [InlineData(2560, 1440, 2560, 1360, ScalingMode.OneToOne,        "1:1")]   // cropped is still 1:1
        [InlineData(2560, 1440, 2560, 1360, ScalingMode.WholeStepsCrisp, "94%")]   // fell back to Fill (2418 px wide)
        [InlineData(1920, 1080, 2560, 1440, ScalingMode.WholeStepsCrisp, "1:1")]
        [InlineData(1920, 1080, 2560, 1440, ScalingMode.Fill,            "133%")]
        [InlineData(1280,  720, 2560, 1440, ScalingMode.WholeStepsSmooth,"200%")]
        public void Scale_follows_the_scaling_mode(int fw, int fh, int aw, int ah, ScalingMode mode, string expected)
            => Assert.Equal(expected, StatusText.Scale(fw, fh, new Size(aw, ah), mode));

        public static System.Collections.Generic.IEnumerable<object[]> AllModes => Scaling.All.Select(m => new object[] { m });

        [Theory, MemberData(nameof(AllModes))]
        public void Scale_is_null_with_no_area(ScalingMode mode)
            => Assert.Null(StatusText.Scale(1920, 1080, Size.Empty, mode));

        [Fact]
        public void Scaling_labels_are_the_spec_copy()
            => Assert.Equal(new[] { "Fill", "Whole steps (crisp)", "Whole steps (smooth)", "1:1" }, Scaling.All.Select(UiText.ScalingLabel).ToArray());

        [Theory]
        [InlineData("1.0.0", "Framejet 1.0.0")]
        [InlineData("1.1.0-beta.1", "Framejet 1.1.0-beta.1")]
        [InlineData("1.0.0+8c63b00", "Framejet 1.0.0")]
        [InlineData("0.0.0-dev", "Framejet (dev build)")]          // built from no release tag: no real version to show
        [InlineData("0.0.0-dev+8c63b00", "Framejet (dev build)")]
        [InlineData("", "Framejet")]
        [InlineData(null, "Framejet")]
        public void Name_and_version_reads_like_the_menu(string version, string expected)
            => Assert.Equal(expected, UiText.NameAndVersion(version));

        [Fact] public void Debouncer_runs_once_800ms_after_the_last_trigger()
        {
            var clock = new FakeScheduler(); int runs = 0; var d = new Debouncer(clock, TimeSpan.FromMilliseconds(800), () => runs++);
            d.Trigger(); clock.Advance(S(0.3)); d.Trigger(); clock.Advance(S(0.3)); d.Trigger();
            clock.Advance(S(0.79)); Assert.Equal(0, runs); clock.Advance(S(0.01)); Assert.Equal(1, runs);
        }
        [Fact] public void Toolbar_shows_for_2s_after_start_and_after_each_mouse_move()
        {
            var t = new ToolbarAutoHide(S(0));
            t.Tick(S(1.9)); Assert.True(t.Visible); t.Tick(S(2.0)); Assert.False(t.Visible);
            t.MouseMoved(S(3)); Assert.True(t.Visible); t.Tick(S(4.99)); Assert.True(t.Visible); t.Tick(S(5)); Assert.False(t.Visible);
        }
        [Fact] public void Toolbar_stays_while_held_then_hides_2s_after_release()
        { var t = new ToolbarAutoHide(S(0)); t.SetHeld(true, S(1)); t.Tick(S(10)); Assert.True(t.Visible);
          t.SetHeld(false, S(10)); t.Tick(S(11.9)); Assert.True(t.Visible); t.Tick(S(12)); Assert.False(t.Visible); }
        [Fact] public void A_warning_shows_the_toolbar_for_5s()
        { var t = new ToolbarAutoHide(S(0)); t.Tick(S(3)); t.WarningRaised(S(3)); t.Tick(S(7.9)); Assert.True(t.Visible); t.Tick(S(8)); Assert.False(t.Visible); }
        [Fact] public void Toolbar_never_shows_in_fullscreen()
        { var t = new ToolbarAutoHide(S(0)); t.SetFullscreen(true); t.MouseMoved(S(1)); t.WarningRaised(S(1)); t.Tick(S(1)); Assert.False(t.Visible);
          t.SetFullscreen(false); t.MouseMoved(S(2)); Assert.True(t.Visible); }

        static readonly VideoMode Mode1440 = new VideoMode(2560, 1440, 166667, PixelFormat.NV12);
        static readonly Size Screen1440 = new Size(2560, 1440);
        [Fact] public void Status_shows_lines_and_measured_fps() => Assert.Equal("1440p · 59.9 fps · 1:1", StatusText.Format(Mode1440, 59.94, Screen1440, new string[0]));
        [Fact] public void Status_passes_the_scaling_through() => Assert.Equal("1440p · 59.9 fps · 1:1", StatusText.Format(Mode1440, 59.94, new Size(2560, 1360), new string[0], ScalingMode.OneToOne));
        [Fact] public void Status_appends_warnings() => Assert.Equal("1440p · 59.9 fps · 1:1 · No audio from the card", StatusText.Format(Mode1440, 59.94, Screen1440, new[] { UiText.NoAudio }));
        [Fact] public void Status_without_video_is_just_warnings() => Assert.Equal("No picture — is the PS5 on?", StatusText.Format(null, 0, Screen1440, new[] { UiText.NoPicture }));

        // The last part is how the picture is drawn in the video area: 1:1, or its size against the mode's.
        [Theory]
        [InlineData(2560, 1440, 2560, 1440, "1440p · 59.9 fps · 1:1")]  // fullscreen on a screen of the mode's size
        [InlineData(2560, 1440, 2560, 1600, "1440p · 59.9 fps · 1:1")]  // placed with bars, but not scaled
        [InlineData(2560, 1440, 2560, 1369, "1440p · 59.9 fps · 95%")]  // maximized on that screen: drawn at 2434x1369
        [InlineData(1920, 1080, 2560, 1440, "1080p · 59.9 fps · 133%")] // 1080p fullscreen on a 1440p screen
        [InlineData(1920, 1080, 2560, 1369, "1080p · 59.9 fps · 127%")] // drawn at 2434x1369: 126.8% rounds up
        [InlineData(2560, 1440, 2559, 1440, "1440p · 59.9 fps · 99%")]  // 99.96% is scaled, so never "100%"
        [InlineData(1920, 1080, 1921, 1081, "1080p · 59.9 fps · 101%")] // 100.05% is scaled, so never "100%"
        public void Status_ends_with_the_picture_scale(int modeWidth, int modeHeight, int areaWidth, int areaHeight, string expected)
            => Assert.Equal(expected, StatusText.Format(new VideoMode(modeWidth, modeHeight, 166667, PixelFormat.NV12), 59.94, new Size(areaWidth, areaHeight), new string[0]));
        [Fact] public void Status_has_no_scale_without_an_area_to_draw_on()
            => Assert.Equal("1440p · 59.9 fps", StatusText.Format(Mode1440, 59.94, Size.Empty, new string[0]));

        [Fact] public void Taken_copy_is_exact()
            => Assert.Equal("Razer Ripsaw X is in use by another app — press Rescan to take it back", UiText.Taken("Razer Ripsaw X"));
        [Fact] public void Copy_is_exact()
        {
            Assert.Equal("No capture card found", UiText.NoCard);
            Assert.Equal("Waiting for Razer Ripsaw X… plug it in and it starts by itself", UiText.Waiting("Razer Ripsaw X"));
            Assert.Equal("Couldn't start Razer Ripsaw X — retrying", UiText.Busy("Razer Ripsaw X"));
            Assert.Equal("No picture — is the PS5 on?", UiText.NoPicture);
            Assert.Equal("No audio from the card", UiText.NoAudio);
            Assert.Equal("Couldn't start 3840×2160 · 30 fps — back to 2560×1440 · 60 fps", UiText.ModeReverted(new VideoMode(3840, 2160, 333333, PixelFormat.NV12), Mode1440));
            Assert.Equal("Speakers (USB Audio Device) is gone — using Default output", UiText.OutputGone("Speakers (USB Audio Device)"));
            Assert.Equal("Audio error: Device invalidated", UiText.AudioError("Device invalidated"));
            Assert.Equal("Audio error: Device invalidated — retrying", UiText.AudioRetrying("Device invalidated"));
            Assert.Equal("Default output", UiText.DefaultOutput); Assert.Equal("Automatic", UiText.Automatic);
        }
    }
}
