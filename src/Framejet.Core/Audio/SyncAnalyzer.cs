using System;
using System.Collections.Generic;
using System.Linq;

namespace Framejet.Core.Audio
{
    public enum SyncOutcome { Ok, NoFlashes, NoBeeps, TooFewPairs, Ambiguous }

    public sealed class SyncResult
    {
        public SyncResult(SyncOutcome outcome, double cardGapMs = 0, double spreadMs = 0, int pairs = 0, double periodMs = 0)
        {
            Outcome = outcome;
            CardGapMs = cardGapMs;
            SpreadMs = spreadMs;
            Pairs = pairs;
            PeriodMs = periodMs;
        }

        public SyncOutcome Outcome { get; }
        public double CardGapMs { get; }
        public double SpreadMs { get; }
        public int Pairs { get; }
        public double PeriodMs { get; }
    }

    /// <summary>Pairs video flashes with audio beeps to measure the capture card's audio-after-picture gap.</summary>
    public static class SyncAnalyzer
    {
        const double MinLumaRange = 30;
        const double FlashDebounceMs = 100;
        const double BeepQuietMs = 50;
        const double MinBeepThreshold = 0.05;
        const double FloorMultiplier = 8;
        const double DefaultPeriodMs = 1000;
        const double MaxPairWindowMs = 500;
        const int MinPairs = 3;

        public static SyncResult Analyze(
            IReadOnlyList<(long Ticks, double Luma)> frames,
            IReadOnlyList<(long Ticks, float Peak)> audioBlocks,
            long ticksPerSecond)
        {
            double msPerTick = 1000.0 / ticksPerSecond;

            var flashes = FlashOnsets(frames, msPerTick);
            if (flashes == null || flashes.Count == 0) return new SyncResult(SyncOutcome.NoFlashes);

            var beeps = BeepOnsets(audioBlocks, msPerTick);
            if (beeps.Count == 0) return new SyncResult(SyncOutcome.NoBeeps);

            double period = DefaultPeriodMs;
            if (flashes.Count > 1)
            {
                var intervals = new List<double>();
                for (int i = 1; i < flashes.Count; i++) intervals.Add(flashes[i] - flashes[i - 1]);
                period = Median(intervals);
            }

            double window = Math.Min(period / 2, MaxPairWindowMs);
            var gaps = new List<double>();
            foreach (double flash in flashes)
            {
                double best = double.NaN;
                foreach (double beep in beeps)
                {
                    double gap = beep - flash;
                    if (Math.Abs(gap) <= window && (double.IsNaN(best) || Math.Abs(gap) < Math.Abs(best)))
                        best = gap;
                }
                if (!double.IsNaN(best)) gaps.Add(best);
            }

            if (gaps.Count < MinPairs)
                return new SyncResult(SyncOutcome.TooFewPairs, pairs: gaps.Count, periodMs: period);

            double spread = (gaps.Max() - gaps.Min()) / 2;
            var outcome = spread > period / 4 ? SyncOutcome.Ambiguous : SyncOutcome.Ok;
            return new SyncResult(outcome, Median(gaps), spread, gaps.Count, period);
        }

        // Flash onset times in ms; null when the video never varies enough to hold a flash.
        static List<double> FlashOnsets(IReadOnlyList<(long Ticks, double Luma)> frames, double msPerTick)
        {
            if (frames.Count == 0) return null;
            double min = frames.Min(f => f.Luma);
            double max = frames.Max(f => f.Luma);
            if (max - min < MinLumaRange) return null;

            double mid = (min + max) / 2;
            var onsets = new List<double>();
            for (int i = 1; i < frames.Count; i++)
            {
                if (!(frames[i - 1].Luma < mid && frames[i].Luma >= mid)) continue;
                double t = frames[i].Ticks * msPerTick;
                if (onsets.Count > 0 && t - onsets[onsets.Count - 1] < FlashDebounceMs) continue;
                onsets.Add(t);
            }
            return onsets;
        }

        static List<double> BeepOnsets(IReadOnlyList<(long Ticks, float Peak)> blocks, double msPerTick)
        {
            var onsets = new List<double>();
            if (blocks.Count == 0) return onsets;

            var sorted = blocks.Select(b => (double)b.Peak).OrderBy(p => p).ToList();
            double floor = sorted[(int)((sorted.Count - 1) * 0.2)];
            double threshold = Math.Max(MinBeepThreshold, FloorMultiplier * floor);

            double lastAbove = double.NegativeInfinity;
            foreach (var b in blocks)
            {
                if (b.Peak <= threshold) continue;
                double t = b.Ticks * msPerTick;
                if (t - lastAbove >= BeepQuietMs) onsets.Add(t);
                lastAbove = t;
            }
            return onsets;
        }

        static double Median(List<double> values)
        {
            var s = values.OrderBy(v => v).ToList();
            int n = s.Count;
            return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
        }
    }
}
