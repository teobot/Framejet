using System;
using System.Linq;
using Framejet.Core.Video;
using Xunit;

namespace Framejet.Core.Tests.Video
{
    public class TimingStatsTests
    {
        const long TicksPerSecond = 10_000_000;
        static long Ms(double ms) => (long)Math.Round(ms * TicksPerSecond / 1000.0);

        [Fact] public void Sixty_arrivals_16_683_ms_apart_are_59_94_fps()
        {
            var stamps = Enumerable.Range(0, 60).Select(i => Ms(5000) + i * Ms(16.683)).ToArray();
            Assert.Equal(59.94, TimingStats.RatePerSecond(stamps, stamps.Length, Ms(4999), TicksPerSecond), 2);
        }
        [Fact] public void Fewer_than_two_arrivals_are_0_fps()
        {
            Assert.Equal(0.0, TimingStats.RatePerSecond(new long[0], 0, 0, TicksPerSecond));
            Assert.Equal(0.0, TimingStats.RatePerSecond(new[] { Ms(10) }, 1, 0, TicksPerSecond));
            Assert.Equal(0.0, TimingStats.RatePerSecond(new[] { Ms(10), Ms(10) }, 2, 0, TicksPerSecond)); // no time between them
        }
        [Fact] public void Only_arrivals_after_the_window_start_count_in_any_order()
        {
            long[] ring = { Ms(1050), Ms(1000), Ms(950), Ms(100), Ms(50) }; // a ring buffer: not in time order, two before the window
            Assert.Equal(20.0, TimingStats.RatePerSecond(ring, ring.Length, Ms(500), TicksPerSecond), 6);
        }
        [Fact] public void Only_the_filled_part_of_the_ring_counts()
        {
            long[] ring = { Ms(100), Ms(200), Ms(999), 0 };                // the last slot is not filled yet
            Assert.Equal(2 / 0.899, TimingStats.RatePerSecond(ring, 3, 0, TicksPerSecond), 6);
        }
    }
}
