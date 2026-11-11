using System;
using System.Drawing;
using System.Linq;
using Framejet.Core.Devices;
using Framejet.Core.Settings;
using Framejet.Core.Tests.Fakes;
using Framejet.Core.Video;
using Xunit;
using static Framejet.Core.Tests.Devices.TestDevices;
using static Framejet.Core.Tests.Video.TestModes;

namespace Framejet.Core.Tests.Video
{
    public class VideoSessionTests
    {
        readonly FakeVideoBackend backend = new FakeVideoBackend();
        readonly FakeScheduler clock = new FakeScheduler();
        readonly AppSettings settings = new AppSettings();
        readonly RecordingLog log = new RecordingLog();
        int saves;
        VideoSession New()
        {
            backend.ModesByPath[Ripsaw.Path] = RipsawRaw; backend.ModesByPath[RipsawOtherPort.Path] = RipsawRaw; backend.ModesByPath[Webcam.Path] = WebcamRaw;
            return new VideoSession(backend, settings, clock, () => new Size(2560, 1440), () => saves++, log);
        }
        static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

        [Fact] public void Opens_the_card_at_the_screen_default_and_remembers_the_card()
        {
            backend.Devices.AddRange(new[] { Webcam, Ripsaw }); var s = New(); s.Refresh();
            Assert.Equal(VideoState.Running, s.State); Assert.Null(s.Message);
            Assert.Same(Ripsaw, backend.Starts.Single().device); Assert.Equal("2560x1440@60/NV12", backend.Starts.Single().mode.Key);
            Assert.Equal(VideoPickReason.LooksLikeCard, s.PickReason);
            Assert.Equal(Ripsaw.Path, settings.VideoDevicePath); Assert.Equal("Razer Ripsaw X", settings.VideoDeviceName);
            Assert.True(saves > 0);
        }
        // Spec 4.3: the automatic choice follows the screen, so only a mode the user picks is remembered.
        [Fact] public void An_automatic_start_saves_no_mode()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            Assert.Equal("2560x1440@60/NV12", s.Mode.Key); Assert.Equal("", settings.ModeKey);
            clock.Advance(S(6)); backend.RaiseDeviceLost();                      // a reopen is automatic too
            Assert.Equal(2, backend.Starts.Count); Assert.Equal("", settings.ModeKey);
        }
        [Fact] public void An_explicit_pick_saves_the_mode()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); int before = saves;
            s.SelectMode(s.Modes.Single(m => m.Key == "1920x1080@60/NV12"));
            Assert.Equal("1920x1080@60/NV12", settings.ModeKey); Assert.True(saves > before);
        }
        [Fact] public void A_fallback_keeps_the_saved_pick()
        {
            backend.Devices.Add(Ripsaw); settings.ModeKey = "1920x1080@120/NV12"; var s = New();
            backend.ModesByPath[Ripsaw.Path] = RipsawRaw.Where(m => m.Fps < 100).ToArray(); // 120 fps is missing for now
            s.Refresh();
            Assert.Equal("2560x1440@60/NV12", s.Mode.Key); Assert.Equal("1920x1080@120/NV12", settings.ModeKey);
            backend.ModesByPath[Ripsaw.Path] = RipsawRaw; backend.Devices.Remove(Ripsaw); s.Refresh(); backend.Devices.Add(Ripsaw); s.Refresh();
            Assert.Equal("1920x1080@120/NV12", s.Mode.Key); Assert.Equal("1920x1080@120/NV12", settings.ModeKey);
        }
        [Fact] public void Unrelated_device_change_does_not_restart_the_video()
        { backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); backend.Devices.Add(PhoneCamera); s.Refresh(); s.Refresh();
          Assert.Single(backend.Starts); Assert.Equal(0, backend.Stops); }
        [Fact] public void Busy_card_retries_every_3_seconds_then_starts()
        {
            backend.Devices.Add(Ripsaw); backend.StartResults.Enqueue(false); backend.StartResults.Enqueue(false);
            var s = New(); s.Refresh();
            Assert.Equal(VideoState.Busy, s.State); Assert.Equal("Couldn't start Razer Ripsaw X — retrying", s.Message);
            clock.Advance(S(2.9)); Assert.Single(backend.Starts);
            clock.Advance(S(0.1)); Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Busy, s.State);
            clock.Advance(S(3)); Assert.Equal(3, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State); Assert.Null(s.Message);
        }
        [Fact] public void Unplugged_card_waits_then_resumes_on_another_port()
        {
            backend.Devices.AddRange(new[] { Webcam, Ripsaw }); var s = New(); s.Refresh();
            backend.Devices.Remove(Ripsaw); s.Refresh();
            Assert.Equal(1, backend.Stops); Assert.Equal(VideoState.Waiting, s.State);
            Assert.Equal("Waiting for Razer Ripsaw X… plug it in and it starts by itself", s.Message);
            backend.Devices.Add(RipsawOtherPort); s.Refresh();
            Assert.Same(RipsawOtherPort, backend.Starts.Last().device); Assert.Equal("2560x1440@60/NV12", backend.Starts.Last().mode.Key);
            Assert.Equal(RipsawOtherPort.Path, settings.VideoDevicePath); Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void Device_lost_reopens_the_card_when_it_is_still_there()
        { backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); clock.Advance(S(6)); backend.RaiseDeviceLost();
          Assert.Equal(1, backend.Stops); Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State); }
        [Fact] public void Card_taken_by_another_app_is_left_alone_until_rescan()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            backend.RaiseDeviceTaken();
            Assert.Equal(1, backend.Stops); Assert.Equal(VideoState.Busy, s.State);
            Assert.Equal("Razer Ripsaw X is in use by another app — press Rescan to take it back", s.Message);
            clock.Advance(S(10)); s.Refresh();
            Assert.Single(backend.Starts);                       // never retakes by itself
            s.Rescan();
            Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State); Assert.Null(s.Message);
        }
        [Fact] public void Picking_a_mode_takes_the_card_back()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); backend.RaiseDeviceTaken();
            s.SelectMode(s.Modes.Single(m => m.Key == "1920x1080@60/NV12"));
            Assert.Equal(VideoState.Running, s.State); Assert.Equal("1920x1080@60/NV12", backend.Starts.Last().mode.Key);
        }
        [Fact] public void A_taken_card_that_is_unplugged_waits_and_reopens_normally()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); backend.RaiseDeviceTaken();
            backend.Devices.Remove(Ripsaw); s.Refresh();
            Assert.Equal(VideoState.Waiting, s.State); Assert.Equal("Waiting for Razer Ripsaw X… plug it in and it starts by itself", s.Message);
            backend.Devices.Add(Ripsaw); s.Refresh();
            Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void Device_lost_right_after_start_backs_off_for_3_seconds()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            clock.Advance(S(1)); backend.RaiseDeviceLost();
            Assert.Equal(1, backend.Stops); Assert.Single(backend.Starts);
            Assert.Equal(VideoState.Busy, s.State); Assert.Equal("Couldn't start Razer Ripsaw X — retrying", s.Message);
            clock.Advance(S(3));
            Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void Never_auto_opens_a_webcam()
        { backend.Devices.AddRange(new[] { Webcam, PhoneCamera, EffectsCamera }); var s = New(); s.Refresh();
          Assert.Empty(backend.Starts); Assert.Equal(VideoState.NoCard, s.State); Assert.Equal("No capture card found", s.Message); }
        [Fact] public void Saved_webcam_pick_wins()
        { backend.Devices.AddRange(new[] { Webcam, Ripsaw }); settings.VideoDevicePath = Webcam.Path; var s = New(); s.Refresh();
          Assert.Same(Webcam, backend.Starts.Single().device); Assert.Equal("1920x1080@30/MJPG", backend.Starts.Single().mode.Key); }
        [Fact] public void Saved_mode_is_used_when_offered()
        { backend.Devices.Add(Ripsaw); settings.ModeKey = "1920x1080@120/NV12"; var s = New(); s.Refresh(); Assert.Equal("1920x1080@120/NV12", s.Mode.Key); }
        [Fact] public void Failed_mode_switch_goes_back_to_the_last_working_mode()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            backend.StartResults.Enqueue(false);
            s.SelectMode(s.Modes.Single(m => m.Key == "3840x2160@30/NV12"));
            Assert.Equal("2560x1440@60/NV12", backend.Starts.Last().mode.Key); Assert.Equal("2560x1440@60/NV12", s.Mode.Key);
            Assert.Equal("Couldn't start 3840×2160 · 30 fps — back to 2560×1440 · 60 fps", s.Warning);
            Assert.Equal("", settings.ModeKey); Assert.Equal(VideoState.Running, s.State); // the failed pick is not saved
        }
        VideoSession RevertedFrom4k()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            backend.StartResults.Enqueue(false); s.SelectMode(s.Modes.Single(m => m.Key == "3840x2160@30/NV12"));
            Assert.Equal(VideoState.Running, s.State); Assert.NotNull(s.Warning);
            return s;
        }
        [Fact] public void Mode_revert_warning_stays_after_the_revert_succeeds()
        {
            var s = RevertedFrom4k();
            backend.Devices.Add(PhoneCamera); s.Refresh(); clock.Advance(S(10));       // an unrelated plug and time passing
            Assert.Equal("Couldn't start 3840×2160 · 30 fps — back to 2560×1440 · 60 fps", s.Warning);
        }
        [Fact] public void Mode_revert_warning_clears_when_the_card_is_unplugged()
        {
            var s = RevertedFrom4k();
            backend.Devices.Remove(Ripsaw); s.Refresh();
            Assert.Equal(VideoState.Waiting, s.State); Assert.Null(s.Warning);
        }
        [Fact] public void Mode_revert_warning_clears_when_the_device_is_lost()
        {
            var s = RevertedFrom4k();
            clock.Advance(S(6)); backend.StartResults.Enqueue(false); backend.RaiseDeviceLost(); // the reopen fails too
            Assert.Equal(VideoState.Busy, s.State); Assert.Null(s.Warning);
        }
        [Fact] public void Mode_revert_warning_clears_on_the_next_successful_start()
        {
            var s = RevertedFrom4k();
            backend.RaiseDeviceTaken(); Assert.NotNull(s.Warning);              // yielding loses nothing: the card is still there
            s.Rescan();
            Assert.Equal(VideoState.Running, s.State); Assert.Null(s.Warning);
        }
        [Fact] public void Selecting_a_device_switches_and_remembers_it()
        { backend.Devices.AddRange(new[] { Webcam, Ripsaw }); var s = New(); s.Refresh(); s.SelectDevice(Webcam);
          Assert.Same(Webcam, backend.Starts.Last().device); Assert.Equal(Webcam.Path, settings.VideoDevicePath); Assert.Equal(1, backend.Stops); }
        [Fact] public void Mode_listing_failure_counts_as_busy()
        { backend.Devices.Add(Ripsaw); backend.ListModesThrows = true; var s = New(); s.Refresh(); Assert.Equal(VideoState.Busy, s.State);
          backend.ListModesThrows = false; clock.Advance(S(3)); Assert.Equal(VideoState.Running, s.State); }
        [Fact] public void Unexpected_start_exception_is_busy_and_retries()
        {
            backend.Devices.Add(Ripsaw); backend.StartError = new InvalidOperationException("boom");
            var s = New(); s.Refresh();
            Assert.Equal(VideoState.Busy, s.State); Assert.Equal("Couldn't start Razer Ripsaw X — retrying", s.Message);
            backend.StartError = null; clock.Advance(S(3));
            Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void Unexpected_mode_listing_exception_is_busy_and_retries()
        {
            backend.Devices.Add(Ripsaw); backend.ListModesError = new InvalidOperationException("boom");
            var s = New(); s.Refresh();
            Assert.Equal(VideoState.Busy, s.State);
            backend.ListModesError = null; clock.Advance(S(3));
            Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void Screen_size_exception_is_busy_and_retries()
        {
            backend.Devices.Add(Ripsaw); backend.ModesByPath[Ripsaw.Path] = RipsawRaw; bool fail = true;
            var s = new VideoSession(backend, settings, clock, () => { if (fail) throw new InvalidOperationException("boom"); return new Size(2560, 1440); }, () => saves++, NullLog.Instance);
            s.Refresh(); Assert.Equal(VideoState.Busy, s.State); Assert.Empty(backend.Starts);
            fail = false; clock.Advance(S(3)); Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void Empty_mode_list_is_busy_and_never_starts_with_a_null_mode()
        {
            backend.Devices.Add(Ripsaw); var s = New(); backend.ModesByPath[Ripsaw.Path] = new VideoMode[0]; s.Refresh();
            Assert.Equal(VideoState.Busy, s.State); Assert.Empty(backend.Starts);
            backend.ModesByPath[Ripsaw.Path] = RipsawRaw; clock.Advance(S(3));
            Assert.Equal(VideoState.Running, s.State);
        }
        // The stall watchdog: while Running it checks FramesArrived every second.
        void DeliverFor(int seconds) { for (int i = 0; i < seconds; i++) { backend.FramesArrived += 60; clock.Advance(S(1)); } }
        VideoSession StalledOnce()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            DeliverFor(6);                                                       // frames up to the check at 6 s, past the start-up grace
            clock.Advance(S(2.9)); Assert.Single(backend.Starts); Assert.Equal(0, backend.Stops);
            clock.Advance(S(0.1));                                               // the check at 9 s: nothing new for 3 s
            Assert.Equal(1, backend.Stops); Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State);
            return s;
        }
        [Fact] public void A_start_that_never_delivers_a_frame_is_left_alone()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); clock.Advance(S(30));
            Assert.Single(backend.Starts); Assert.Equal(0, backend.Stops); Assert.Equal(VideoState.Running, s.State);
        }
        [Fact] public void A_stream_that_stops_delivering_for_3_seconds_restarts_once()
        {
            StalledOnce();
            Assert.Single(log.Infos, "Video stalled: no frames for 3 s — restarting");
            clock.Advance(S(30));                                                // the new start has delivered nothing: no second trigger
            Assert.Equal(2, backend.Starts.Count); Assert.Equal(1, backend.Stops);
        }
        [Fact] public void The_watchdog_starts_over_after_a_restart()
        {
            StalledOnce(); clock.Advance(S(30));
            DeliverFor(1); clock.Advance(S(2.9)); Assert.Equal(2, backend.Starts.Count);
            clock.Advance(S(0.1)); Assert.Equal(3, backend.Starts.Count);
        }
        [Fact] public void A_stall_inside_the_start_up_grace_counts_as_a_failed_start()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            DeliverFor(1); clock.Advance(S(3));                                  // frames at the 1 s check, none by 4 s: inside the 5 s grace
            Assert.Equal(VideoState.Busy, s.State); Assert.Equal("Couldn't start Razer Ripsaw X — retrying", s.Message);
            Assert.Equal(1, backend.Stops); Assert.Single(backend.Starts);
            clock.Advance(S(3));
            Assert.Equal(2, backend.Starts.Count); Assert.Equal(VideoState.Running, s.State);
        }
        [Theory] [InlineData("yielded")] [InlineData("waiting")] [InlineData("stopped")]
        public void No_stall_trigger_while_not_running(string state)
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh(); DeliverFor(6);
            if (state == "yielded") backend.RaiseDeviceTaken();
            else if (state == "waiting") { backend.Devices.Remove(Ripsaw); s.Refresh(); }
            else s.Stop();
            int starts = backend.Starts.Count, stops = backend.Stops;
            clock.Advance(S(30));                                                // no frames from here on
            Assert.Equal(starts, backend.Starts.Count); Assert.Equal(stops, backend.Stops); Assert.NotEqual(VideoState.Running, s.State);
        }
        [Fact] public void Unexpected_exception_during_mode_switch_and_revert_is_busy_and_retries()
        {
            backend.Devices.Add(Ripsaw); var s = New(); s.Refresh();
            backend.StartError = new InvalidOperationException("boom");
            s.SelectMode(s.Modes.Single(m => m.Key == "3840x2160@30/NV12"));
            Assert.Equal(VideoState.Busy, s.State);
            backend.StartError = null; clock.Advance(S(3));
            Assert.Equal(VideoState.Running, s.State);
        }
    }
}
