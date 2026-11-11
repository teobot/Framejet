using System;
using System.Linq;
using Framejet.Core.Audio;
using Framejet.Core.Devices;
using Framejet.Core.Settings;
using Framejet.Core.Tests.Fakes;
using Xunit;
using static Framejet.Core.Tests.Devices.TestDevices;

namespace Framejet.Core.Tests.Audio
{
    public class AudioSessionTests
    {
        readonly FakeAudioBackend backend = new FakeAudioBackend();
        readonly AppSettings settings = new AppSettings();
        readonly FakeScheduler clock = new FakeScheduler();
        readonly RecordingLog log = new RecordingLog();
        int saves;
        AudioSession New() => new AudioSession(backend, settings, new ImmediateDispatcher(), clock, () => saves++, log);
        static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

        [Fact] public void Plays_the_cards_own_input_to_the_default_output()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer);
          Assert.Equal((RipsawHdmi, Speakers, 1f), backend.Starts.Single()); Assert.Equal(AudioInputSource.SameDevice, a.InputSource); Assert.True(a.OutputIsDefault); Assert.Null(a.Warning); }
        static bool Playing(string line) => line.StartsWith("Audio playing:");
        [Fact] public void Logs_the_pair_when_audio_starts()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer);
          Assert.Equal("Audio playing: " + RipsawHdmi.Name + " → " + Speakers.Name, log.Infos.Single(Playing)); }
        [Fact] public void Logs_again_when_the_output_follows_a_windows_default_change()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.Default = Headphones; a.DefaultOutputChanged();
          Assert.Equal(new[] { "Audio playing: " + RipsawHdmi.Name + " → " + Speakers.Name, "Audio playing: " + RipsawHdmi.Name + " → " + Headphones.Name },
              log.Infos.Where(Playing).ToArray()); }
        [Fact] public void Logs_nothing_when_a_reconcile_changes_nothing()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); a.DevicesChanged(); a.Follow(Ripsaw, RipsawContainer); a.DefaultOutputChanged();
          Assert.Single(log.Infos, Playing); }
        [Fact] public void Logs_no_playing_line_for_each_three_second_retry()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
          for (int i = 0; i < 3; i++) { backend.StartError = new InvalidOperationException("busy"); clock.Advance(S(3)); }
          backend.StartError = null; clock.Advance(S(3));
          Assert.Single(log.Infos, Playing); }

        [Fact] public void Webcam_without_audio_gets_no_audio_and_never_a_random_mic()
        { var a = New(); a.Follow(Webcam, WebcamContainer); Assert.Empty(backend.Starts); Assert.Equal("No audio from the card", a.Warning); }
        [Fact] public void Unrelated_audio_device_does_not_restart_playback()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.Inputs.Add(new AudioInputInfo("usb", "Microphone (USB Headset)", Guid.NewGuid())); a.DevicesChanged();
          Assert.Single(backend.Starts); Assert.Equal(0, backend.Stops); }
        [Fact] public void Default_output_switch_moves_the_sound()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.Default = Headphones; a.DefaultOutputChanged();
          Assert.Equal((RipsawHdmi, Headphones, 1f), backend.Starts.Last()); Assert.Equal(1, backend.Stops); }
        [Fact] public void Chosen_output_ignores_default_switches()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); a.SelectOutput(UsbSpeakers); int n = backend.Starts.Count;
          backend.Default = Headphones; a.DefaultOutputChanged(); Assert.Equal(n, backend.Starts.Count); Assert.Same(UsbSpeakers, backend.Starts.Last().output); }
        [Fact] public void Missing_chosen_output_falls_back_to_default_and_says_so()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); a.SelectOutput(UsbSpeakers); backend.Outputs.Remove(UsbSpeakers); a.DevicesChanged();
          Assert.Same(Speakers, backend.Starts.Last().output); Assert.Equal("Speakers (USB Audio Device) is gone — using Default output", a.Warning); }
        [Fact] public void Volume_uses_the_squared_curve_and_mute_silences()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer);
          a.SetVolume(0.5f); Assert.Equal(0.25, (double)backend.Gains.Last(), 4); a.SetMuted(true); Assert.Equal(0f, backend.Gains.Last());
          a.SetMuted(false); Assert.Equal(0.25, (double)backend.Gains.Last(), 4); Assert.Equal(0.5f, settings.Volume); Assert.False(settings.Muted); Assert.True(saves >= 3); }
        [Fact] public void Start_uses_the_saved_volume_and_mute()
        { settings.Volume = 0.5f; settings.Muted = true; New().Follow(Ripsaw, RipsawContainer); Assert.Equal(0f, backend.Starts.Single().gain); }
        [Fact] public void Backend_failure_shows_an_audio_error()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
          Assert.Equal("Audio error: Device invalidated — retrying", a.Warning); Assert.True(backend.Stops >= 1); }
        [Fact] public void Override_input_is_used_until_it_disappears()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); a.SelectInputOverride(HeadsetMic);
          Assert.Same(HeadsetMic, backend.Starts.Last().input); Assert.Equal(AudioInputSource.Override, a.InputSource);
          backend.Inputs.Remove(HeadsetMic); a.DevicesChanged(); Assert.Same(RipsawHdmi, backend.Starts.Last().input); Assert.Equal(AudioInputSource.SameDevice, a.InputSource); }
        [Fact] public void No_video_stops_audio()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); a.Follow(null, Guid.Empty); Assert.True(backend.Stops >= 1); Assert.Null(a.Warning); }
        [Fact] public void Fallback_follows_default_output_changes_while_chosen_output_is_missing()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); a.SelectOutput(UsbSpeakers); backend.Outputs.Remove(UsbSpeakers); a.DevicesChanged();
          Assert.Same(Speakers, backend.Starts.Last().output);
          backend.Default = Headphones; a.DefaultOutputChanged();
          Assert.Same(Headphones, backend.Starts.Last().output); }
        [Fact] public void Late_failure_from_an_old_playback_does_not_kill_the_new_one()
        { var q = new QueuedDispatcher(); var a = new AudioSession(backend, settings, q, clock, () => saves++, log);
          a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
          backend.Default = Headphones; a.DefaultOutputChanged(); int stops = backend.Stops;
          q.RunAll();
          Assert.Equal(stops, backend.Stops); Assert.True(backend.IsRunning); Assert.Null(a.Warning); }
        [Fact] public void Current_failure_is_still_reported_through_a_queued_dispatcher()
        { var q = new QueuedDispatcher(); var a = new AudioSession(backend, settings, q, clock, () => saves++, log);
          a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated"); q.RunAll();
          Assert.Equal("Audio error: Device invalidated — retrying", a.Warning); Assert.False(backend.IsRunning); }
        [Fact] public void Throwing_enumeration_becomes_an_audio_error()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.ListInputsThrows = new InvalidOperationException("enumeration broke"); a.DevicesChanged();
          Assert.Equal("Audio error: enumeration broke — retrying", a.Warning); Assert.False(backend.IsRunning); }

        // Retrying: real failures (another app taking the device exclusively, a format change, an audio-service
        // restart) come with no device event, so the session tries again every 3 s by itself.
        [Fact] public void A_failure_is_followed_by_a_start_3_seconds_later()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
            Assert.False(backend.IsRunning);
            clock.Advance(S(2.9)); Assert.Single(backend.Starts);
            clock.Advance(S(0.1)); Assert.Equal(2, backend.Starts.Count); Assert.True(backend.IsRunning); Assert.Null(a.Warning);
            Assert.Equal((RipsawHdmi, Speakers, 1f), backend.Starts.Last());
        }
        [Fact] public void Repeated_failures_keep_retrying_every_3_seconds()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer);
            backend.StartError = new InvalidOperationException("Device in use"); backend.RaiseFailed("Device invalidated");
            clock.Advance(S(3)); Assert.Equal(2, backend.Starts.Count); Assert.Equal("Audio error: Device in use — retrying", a.Warning);
            clock.Advance(S(3)); Assert.Equal(3, backend.Starts.Count);
            backend.StartError = null; clock.Advance(S(3));
            Assert.Equal(4, backend.Starts.Count); Assert.True(backend.IsRunning); Assert.Null(a.Warning);
            backend.RaiseFailed("Device invalidated"); clock.Advance(S(3));     // a later failure is retried too
            Assert.Equal(5, backend.Starts.Count); Assert.True(backend.IsRunning);
        }
        [Fact] public void A_success_stops_the_retries()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
            clock.Advance(S(3)); Assert.Equal(2, backend.Starts.Count);
            clock.Advance(S(30)); Assert.Equal(2, backend.Starts.Count); Assert.True(backend.IsRunning);
        }
        [Fact] public void A_stale_failure_schedules_nothing()
        {
            var q = new QueuedDispatcher(); var a = new AudioSession(backend, settings, q, clock, () => saves++, log);
            a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");    // from the first playback, still queued
            backend.Default = Headphones; a.DefaultOutputChanged();                       // a new playback starts
            q.RunAll(); int starts = backend.Starts.Count, stops = backend.Stops;
            clock.Advance(S(30));
            Assert.Equal(starts, backend.Starts.Count); Assert.Equal(stops, backend.Stops); Assert.True(backend.IsRunning);
        }
        [Fact] public void Stopping_cancels_a_pending_retry()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
            a.Follow(null, Guid.Empty);                                                   // the video stopped
            clock.Advance(S(30)); Assert.Single(backend.Starts); Assert.Null(a.Warning);
        }
        [Fact] public void Dispose_cancels_a_pending_retry_and_ignores_later_failures()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated");
            a.Dispose(); clock.Advance(S(30)); Assert.Single(backend.Starts);
            backend.RaiseFailed("late"); clock.Advance(S(30)); Assert.Single(backend.Starts);
        }
        [Fact] public void A_throwing_start_is_retried()
        {
            backend.StartError = new InvalidOperationException("Device in use");
            var a = New(); a.Follow(Ripsaw, RipsawContainer);
            Assert.False(backend.IsRunning); Assert.Equal("Audio error: Device in use — retrying", a.Warning);
            backend.StartError = null; clock.Advance(S(3));
            Assert.Equal(2, backend.Starts.Count); Assert.True(backend.IsRunning); Assert.Null(a.Warning);
        }
        [Fact] public void A_failing_device_listing_is_retried()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer);
            backend.ListInputsThrows = new InvalidOperationException("enumeration broke"); a.DevicesChanged();
            backend.ListInputsThrows = null; clock.Advance(S(3));
            Assert.Equal(2, backend.Starts.Count); Assert.True(backend.IsRunning); Assert.Null(a.Warning);
        }
        [Fact] public void An_explicit_change_replaces_the_pending_retry()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.RaiseFailed("Device invalidated"); // a retry is due at 3 s
            backend.StartError = new InvalidOperationException("Device in use");
            clock.Advance(S(1)); a.SelectOutput(Headphones);                              // fails at 1 s, so the next try is at 4 s
            clock.Advance(S(2.5)); Assert.Equal(2, backend.Starts.Count);
            clock.Advance(S(0.5)); Assert.Equal(3, backend.Starts.Count);
        }
        [Fact] public void Logs_the_first_failure_and_the_recovery_but_not_each_retry()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer);
            backend.StartError = new InvalidOperationException("Device in use"); backend.RaiseFailed("Device invalidated");
            clock.Advance(S(9));                                                          // three more failures
            backend.StartError = null; clock.Advance(S(3));                               // it plays again
            Assert.Single(log.Errors); Assert.Empty(log.Infos.Where(i => !Playing(i)));                           // recovered once it keeps playing
            clock.Advance(S(3));
            Assert.Single(log.Errors); Assert.Single(log.Infos, i => i.StartsWith("Audio recovered"));
        }
        [Fact] public void Playback_that_fails_again_at_once_is_not_logged_as_recovered()
        {
            var a = New(); a.Follow(Ripsaw, RipsawContainer);
            for (int i = 0; i < 5; i++) { backend.RaiseFailed("Device invalidated"); clock.Advance(S(3)); } // starts, then fails again
            Assert.Single(log.Errors); Assert.Empty(log.Infos.Where(i => !Playing(i)));
        }
        [Fact] public void Null_default_output_means_no_output_and_stops()
        { var a = New(); a.Follow(Ripsaw, RipsawContainer); backend.Default = null; a.DefaultOutputChanged();
          Assert.Equal("Audio error: no audio output device", a.Warning); Assert.False(backend.IsRunning); Assert.Single(backend.Starts); }
    }
}
