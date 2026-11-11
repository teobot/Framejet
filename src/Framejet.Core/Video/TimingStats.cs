using System;
using System.Collections.Generic;
using System.Linq;

namespace Framejet.Core.Video
{
    public sealed class TimingStats
    {
        public static readonly TimingStats Empty = new TimingStats(0, 0, 0, 0);

        internal TimingStats(int count, double meanMs, double p95Ms, double maxMs)
        {
            Count = count; MeanMs = meanMs; P95Ms = p95Ms; MaxMs = maxMs;
        }

        public int Count { get; }
        public double MeanMs { get; }
        public double P95Ms { get; }
        public double MaxMs { get; }

        public static TimingStats From(IEnumerable<double> samplesMs)
        {
            var sorted = samplesMs.OrderBy(x => x).ToArray();
            if (sorted.Length == 0) return Empty;
            int rank = (int)Math.Ceiling(0.95 * sorted.Length);
            return new TimingStats(sorted.Length, sorted.Average(), sorted[rank - 1], sorted[sorted.Length - 1]);
        }

        /// <summary>
        /// The rate, per second, of the events stamped after <paramref name="windowStart"/>: the n − 1 intervals between
        /// the oldest and the newest of them over the time they span, so a steady 59.94 fps reads 59.94 rather than a
        /// whole count. 0 with fewer than two. Reads the first <paramref name="count"/> stamps in any order (a ring
        /// buffer) and allocates nothing.
        /// </summary>
        public static double RatePerSecond(long[] stamps, int count, long windowStart, long ticksPerSecond)
        {
            int n = 0;
            long oldest = long.MaxValue, newest = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                long t = stamps[i];
                if (t <= windowStart) continue;
                n++;
                if (t < oldest) oldest = t;
                if (t > newest) newest = t;
            }
            return n < 2 || newest <= oldest ? 0 : (n - 1) * (double)ticksPerSecond / (newest - oldest);
        }
    }
}
