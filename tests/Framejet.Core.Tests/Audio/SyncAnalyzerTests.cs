using System;
using System.Collections.Generic;
using Framejet.Core.Audio;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class SyncAnalyzerTests
    {
        const long Tps = 10_000_000;
        const long TicksPerMs = Tps / 1000;
        const int FramesPerSecond = 60;

        // 60 fps luma series: 16 dark, 235 for three frames from each flash start.
        static List<(long Ticks, double Luma)> Video(int seconds, IReadOnlyList<int> flashStartFrames, bool flat = false)
        {
            var frames = new List<(long, double)>();
            int total = seconds * FramesPerSecond;
            for (int i = 0; i < total; i++)
            {
                bool lit = false;
                if (!flat)
                    foreach (int s in flashStartFrames)
                        if (i >= s && i < s + 3) lit = true;
                frames.Add((i * Tps / FramesPerSecond, lit ? 235.0 : 16.0));
            }
            return frames;
        }

        // 1 ms blocks, near-silent, with a 10 ms beep starting at each given time.
        static List<(long Ticks, float Peak)> Audio(int seconds, IReadOnlyList<double> beepStartsMs)
        {
            var blocks = new List<(long, float)>();
            for (int ms = 0; ms < seconds * 1000; ms++)
            {
                bool beep = false;
                foreach (double b in beepStartsMs)
                    if (ms >= b && ms < b + 10) beep = true;
                blocks.Add((ms * TicksPerMs, beep ? 0.5f : 0.002f));
            }
            return blocks;
        }

        static List<int> EverySecondFlashes(int seconds)
        {
            var l = new List<int>();
            for (int s = 0; s < seconds; s++) l.Add(30 + s * FramesPerSecond);
            return l;
        }

        static List<double> BeepsAfterFlashes(IEnumerable<int> flashFrames, double offsetMs)
        {
            var l = new List<double>();
            foreach (int f in flashFrames) l.Add(f * 1000.0 / FramesPerSecond + offsetMs);
            return l;
        }

        [Fact]
        public void Finds_a_steady_40ms_gap()
        {
            var flashes = EverySecondFlashes(20);
            var r = SyncAnalyzer.Analyze(Video(20, flashes), Audio(20, BeepsAfterFlashes(flashes, 40)), Tps);
            Assert.Equal(SyncOutcome.Ok, r.Outcome);
            Assert.InRange(r.CardGapMs, 38.0, 42.0);
            Assert.True(r.Pairs >= 18);
        }

        [Fact]
        public void Negative_gap_when_sound_leads()
        {
            var flashes = EverySecondFlashes(20);
            var r = SyncAnalyzer.Analyze(Video(20, flashes), Audio(20, BeepsAfterFlashes(flashes, -30)), Tps);
            Assert.Equal(SyncOutcome.Ok, r.Outcome);
            Assert.InRange(r.CardGapMs, -32.0, -28.0);
        }

        [Fact]
        public void No_flashes_on_flat_video()
        {
            var flashes = EverySecondFlashes(20);
            var r = SyncAnalyzer.Analyze(Video(20, flashes, flat: true), Audio(20, BeepsAfterFlashes(flashes, 40)), Tps);
            Assert.Equal(SyncOutcome.NoFlashes, r.Outcome);
        }

        [Fact]
        public void No_beeps_in_silence()
        {
            var flashes = EverySecondFlashes(20);
            var r = SyncAnalyzer.Analyze(Video(20, flashes), Audio(20, new List<double>()), Tps);
            Assert.Equal(SyncOutcome.NoBeeps, r.Outcome);
        }

        [Fact]
        public void Too_few_pairs_with_two_flashes()
        {
            var flashes = new List<int> { 30, 90 };
            var r = SyncAnalyzer.Analyze(Video(5, flashes), Audio(5, BeepsAfterFlashes(flashes, 40)), Tps);
            Assert.Equal(SyncOutcome.TooFewPairs, r.Outcome);
        }

        [Fact]
        public void Fast_flashes_with_mismatched_beeps_are_ambiguous()
        {
            var flashes = new List<int>();
            for (int f = 12; f < 20 * FramesPerSecond - 12; f += 12) flashes.Add(f);
            var rng = new Random(12345);
            var beeps = new List<double>();
            foreach (int f in flashes)
                beeps.Add(f * 1000.0 / FramesPerSecond + (rng.NextDouble() * 200 - 100));
            var r = SyncAnalyzer.Analyze(Video(20, flashes), Audio(20, beeps), Tps);
            Assert.Equal(SyncOutcome.Ambiguous, r.Outcome);
        }
    }
}
