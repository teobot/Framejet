using Framejet.Core.Diagnostics;
using Xunit;

namespace Framejet.Core.Tests.Diagnostics
{
    public class HealthMonitorTests
    {
        static HealthSample Steady(double t, long arrived, long drawn, long dropped, double fps = 60)
            => new HealthSample { Seconds = t, VideoRunning = true, ModeFps = 60, Arrived = arrived, Drawn = drawn, Dropped = dropped, Fps = fps, AudioRunning = true };

        static HealthMonitor Started()
        {
            var m = new HealthMonitor();
            Assert.Null(m.Sample(Steady(0, 0, 0, 0)));
            return m;
        }

        [Fact] public void First_sample_is_not_judged()
        {
            var m = new HealthMonitor();
            Assert.Null(m.Sample(Steady(0, 1000, 0, 1000))); // big totals, nothing to compare them with
        }

        [Fact] public void A_normal_second_is_quiet() => Assert.Null(Started().Sample(Steady(1.0, 60, 59, 1)));

        [Fact] public void Too_few_frames_from_the_card_is_a_hiccup()
        {
            var line = Started().Sample(Steady(1.0, 50, 50, 0));
            Assert.Contains("card sent too few frames", line);
            Assert.Contains("arrived 50 (expected 60)", line);
        }

        [Fact] public void Dropping_over_a_tenth_is_a_hiccup()
        {
            Assert.Contains("dropped frames", Started().Sample(Steady(1.0, 60, 52, 8)));
            Assert.Null(Started().Sample(Steady(1.0, 60, 54, 6)));
        }

        [Fact] public void A_device_reset_is_a_hiccup()
        {
            var s = Steady(1.0, 60, 60, 0); s.DeviceResets = 1;
            Assert.Contains("device reset", Started().Sample(s));
        }

        [Fact] public void An_audio_trim_is_a_hiccup()
        {
            var s = Steady(1.0, 60, 60, 0); s.AudioTrims = 1;
            Assert.Contains("audio trimmed", Started().Sample(s));
        }

        [Fact] public void An_audio_underrun_is_a_hiccup()
        {
            var s = Steady(1.0, 60, 60, 0); s.AudioUnderruns = 2;
            var line = Started().Sample(s);
            Assert.Contains("audio underrun", line);
            Assert.Contains("underruns 2", line);
        }

        [Fact] public void A_slow_tick_is_a_hiccup_scaled_by_elapsed()
        {
            var line = Started().Sample(Steady(2.5, 150, 150, 0));
            Assert.Contains("slow tick 2.5 s", line);
            Assert.DoesNotContain("too few", line);
        }

        [Fact] public void Paused_seconds_are_not_judged_for_frames()
        {
            var m = new HealthMonitor();
            var a = Steady(0, 0, 0, 0); a.Paused = true;
            var b = Steady(1, 60, 0, 60, fps: 0); b.Paused = true;
            m.Sample(a);
            Assert.Null(m.Sample(b));
        }

        [Fact] public void A_mode_change_is_not_judged()
        {
            var m = Started();
            var s = Steady(1, 30, 30, 0); s.ModeFps = 30;
            Assert.Null(m.Sample(s));
        }

        [Fact] public void A_counter_that_falls_counts_from_zero()
        {
            var m = Started();
            Assert.Null(m.Sample(Steady(1, 60, 60, 0)));
            var line = m.Sample(Steady(2, 30, 30, 0)); // the backend restarted
            Assert.Contains("arrived 30 (expected 60)", line);
            Assert.Equal(90, m.TakeMinute().Arrived);
        }

        [Fact] public void Audio_not_running_is_not_judged()
        {
            var m = new HealthMonitor();
            var a = Steady(0, 0, 0, 0); a.AudioRunning = false;
            var b = Steady(1, 60, 60, 0); b.AudioRunning = false; b.AudioTrims = 5; b.AudioUnderruns = 5;
            m.Sample(a);
            Assert.Null(m.Sample(b));
        }

        [Fact] public void Video_not_running_is_not_judged()
        {
            var m = new HealthMonitor();
            var a = Steady(0, 0, 0, 0); a.VideoRunning = false;
            var b = Steady(1, 0, 0, 0); b.VideoRunning = false;
            m.Sample(a);
            Assert.Null(m.Sample(b));
        }

        [Fact] public void TakeMinute_sums_and_resets()
        {
            var m = Started();
            m.Sample(Steady(1, 60, 60, 0, fps: 60));
            m.Sample(Steady(2, 120, 119, 1, fps: 59.5));
            m.Sample(Steady(3, 180, 179, 1, fps: 60));
            Assert.NotNull(m.Sample(Steady(4, 220, 219, 1, fps: 40)));
            var minute = m.TakeMinute();
            Assert.Equal(220, minute.Arrived);
            Assert.Equal(219, minute.Drawn);
            Assert.Equal(1, minute.Dropped);
            Assert.Equal(1, minute.Hiccups);
            Assert.Equal(40, minute.LowestFps);
            Assert.Equal(4, minute.Seconds, 3);
            var empty = m.TakeMinute();
            Assert.Equal(0, empty.Arrived);
            Assert.Equal(0, empty.Hiccups);
            Assert.Equal(0, empty.LowestFps);
        }
    }
}
