using System;
using System.Collections.Generic;
using Framejet.Core.Audio;
using Framejet.Core.Tests.Fakes;
using Framejet.Core.Ui;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class SyncCheckTests
    {
        const long Tps = 10_000_000;
        const long TicksPerMs = Tps / 1000;
        const int Fps = 60;

        sealed class FakeTaps : ISyncTaps
        {
            public int Begins, Ends;
            public IReadOnlyList<(long Ticks, double Luma)> Frames = new List<(long, double)>();
            public IReadOnlyList<(long Ticks, float Peak)> Audio = new List<(long, float)>();
            public double AudioDelayMs = 20, PictureDelayMs = 6;

            public void Begin() => Begins++;

            public (IReadOnlyList<(long Ticks, double Luma)> Frames, IReadOnlyList<(long Ticks, float Peak)> Audio, double AudioDelayMs, double PictureDelayMs) End()
            {
                Ends++;
                return (Frames, Audio, AudioDelayMs, PictureDelayMs);
            }
        }

        // Flash every second from frame 30, a beep offsetMs after each.
        static FakeTaps SeriesTaps(double offsetMs)
        {
            var frames = new List<(long, double)>();
            for (int i = 0; i < 20 * Fps; i++)
            {
                bool lit = i >= 30 && (i - 30) % Fps < 3;
                frames.Add((i * Tps / Fps, lit ? 235.0 : 16.0));
            }
            var audio = new List<(long, float)>();
            for (int ms = 0; ms < 20_000; ms++)
            {
                bool beep = false;
                for (int s = 0; s < 20; s++)
                {
                    double start = (30 + s * Fps) * 1000.0 / Fps + offsetMs;
                    if (ms >= start && ms < start + 10) beep = true;
                }
                audio.Add((ms * TicksPerMs, beep ? 0.5f : 0.002f));
            }
            return new FakeTaps { Frames = frames, Audio = audio };
        }

        static (SyncCheck Check, FakeScheduler Sched, List<(string, string)> Done, List<int> Seconds) Make(FakeTaps taps)
        {
            var sched = new FakeScheduler();
            var check = new SyncCheck(taps, sched, Tps);
            var done = new List<(string, string)>();
            var seconds = new List<int>();
            check.Finished += (h, s) => done.Add((h, s));
            check.SecondsLeft += seconds.Add;
            return (check, sched, done, seconds);
        }

        [Fact]
        public void Counts_down_each_second_then_reports()
        {
            var taps = SeriesTaps(40);
            var (check, sched, done, seconds) = Make(taps);

            check.Start();
            sched.Advance(SyncCheck.Duration);

            var expected = new List<int>();
            for (int n = 20; n >= 1; n--) expected.Add(n);
            Assert.Equal(expected, seconds);
            Assert.Equal(1, taps.Begins);
            Assert.Equal(1, taps.Ends);
            var f = Assert.Single(done);
            Assert.Equal("Sound leaves the PC 54 ms after the picture (from 20 flash-beep pairs, spread ±0 ms).", f.Item1);
            Assert.Equal("card: 40 ms · Framejet audio: 20 ms · Framejet picture: 6 ms", f.Item2);
        }

        [Fact]
        public void Cancel_turns_taps_off_and_reports_nothing()
        {
            var taps = SeriesTaps(40);
            var (check, sched, done, seconds) = Make(taps);

            check.Start();
            sched.Advance(TimeSpan.FromSeconds(5));
            check.Cancel();
            int shown = seconds.Count;
            sched.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(1, taps.Ends);
            Assert.Empty(done);
            Assert.Equal(shown, seconds.Count);
            check.Cancel();
            Assert.Equal(1, taps.Ends);
        }

        [Fact]
        public void Source_stopped_mid_run_ends_with_message()
        {
            var taps = SeriesTaps(40);
            var (check, sched, done, _) = Make(taps);

            check.Start();
            sched.Advance(TimeSpan.FromSeconds(7));
            check.SourceStopped();
            sched.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(1, taps.Ends);
            var f = Assert.Single(done);
            Assert.Equal(UiText.SyncSourceStopped, f.Item1);
            Assert.Null(f.Item2);
        }

        [Fact]
        public void Stop_and_cancel_when_idle_do_nothing_and_start_while_running_is_ignored()
        {
            var taps = SeriesTaps(40);
            var (check, sched, done, _) = Make(taps);

            check.SourceStopped();
            check.Cancel();
            Assert.Equal(0, taps.Ends);

            check.Start();
            check.Start();
            Assert.Equal(1, taps.Begins);
            sched.Advance(SyncCheck.Duration);
            Assert.Single(done);
        }

        [Fact]
        public void Failure_outcomes_map_to_copy()
        {
            // No flashes: flat video.
            var flat = SeriesTaps(40);
            var flatFrames = new List<(long, double)>();
            for (int i = 0; i < 20 * Fps; i++) flatFrames.Add((i * Tps / Fps, 16.0));
            flat.Frames = flatFrames;
            Assert.Equal(UiText.SyncNoFlashes, RunToEnd(flat));

            // No beeps: silence.
            var silent = SeriesTaps(40);
            var quiet = new List<(long, float)>();
            for (int ms = 0; ms < 20_000; ms++) quiet.Add((ms * TicksPerMs, 0.002f));
            silent.Audio = quiet;
            Assert.Equal(UiText.SyncNoBeeps, RunToEnd(silent));

            // Too few pairs: two flashes, two beeps.
            var few = SeriesTaps(40);
            var fewFrames = new List<(long, double)>();
            for (int i = 0; i < 5 * Fps; i++)
                fewFrames.Add((i * Tps / Fps, (i >= 30 && i < 33) || (i >= 90 && i < 93) ? 235.0 : 16.0));
            var fewAudio = new List<(long, float)>();
            for (int ms = 0; ms < 5000; ms++)
                fewAudio.Add((ms * TicksPerMs, (ms >= 540 && ms < 550) || (ms >= 1540 && ms < 1550) ? 0.5f : 0.002f));
            few.Frames = fewFrames;
            few.Audio = fewAudio;
            Assert.Equal(UiText.SyncTooFewPairs, RunToEnd(few));

            // Ambiguous: fast flashes against jittered beeps.
            var amb = SeriesTaps(40);
            var flashFrames = new List<int>();
            for (int f = 12; f < 20 * Fps - 12; f += 12) flashFrames.Add(f);
            var rng = new Random(12345);
            var beepStarts = new List<double>();
            foreach (int f in flashFrames) beepStarts.Add(f * 1000.0 / Fps + (rng.NextDouble() * 200 - 100));
            var ambFrames = new List<(long, double)>();
            for (int i = 0; i < 20 * Fps; i++)
            {
                bool lit = false;
                foreach (int s in flashFrames) if (i >= s && i < s + 3) lit = true;
                ambFrames.Add((i * Tps / Fps, lit ? 235.0 : 16.0));
            }
            var ambAudio = new List<(long, float)>();
            for (int ms = 0; ms < 20_000; ms++)
            {
                bool beep = false;
                foreach (double b in beepStarts) if (ms >= b && ms < b + 10) beep = true;
                ambAudio.Add((ms * TicksPerMs, beep ? 0.5f : 0.002f));
            }
            amb.Frames = ambFrames;
            amb.Audio = ambAudio;
            Assert.Equal(UiText.SyncAmbiguous, RunToEnd(amb));
        }

        static string RunToEnd(FakeTaps taps)
        {
            var (check, sched, done, _) = Make(taps);
            check.Start();
            sched.Advance(SyncCheck.Duration);
            var f = Assert.Single(done);
            Assert.Null(f.Item2);
            return f.Item1;
        }

        [Fact]
        public void Copy_is_verbatim()
        {
            Assert.Equal("Checking A/V sync… 7 s", UiText.SyncChecking(7));
            Assert.Equal("Ambiguous — the video's flashes are too close together.", UiText.SyncAmbiguous);
        }
    }
}
