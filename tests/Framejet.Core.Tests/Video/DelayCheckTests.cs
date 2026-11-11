using System.Linq;
using Framejet.Core.Video;
using Xunit;

namespace Framejet.Core.Tests.Video
{
    public class DelayCheckTests
    {
        static TimingStats T(double mean, double p95) => new TimingStats(100, mean, p95, p95);   // test-only constructor (count, mean, p95, max)
        [Fact] public void Stats_use_nearest_rank_p95()
        { var t = TimingStats.From(Enumerable.Range(1, 100).Select(i => (double)i)); Assert.Equal(100, t.Count); Assert.Equal(50.5, t.MeanMs, 3); Assert.Equal(95, t.P95Ms, 3); Assert.Equal(100, t.MaxMs, 3); }
        [Fact] public void Spike_numbers_pass_at_165Hz() => Assert.True(DelayCheck.Evaluate(T(0.94, 1.27), T(3.91, 6.83), 164.85).Pass);
        [Fact] public void An_extra_refresh_fails()
        { var v = DelayCheck.Evaluate(T(0.94, 1.27), T(3.91, 13.0), 164.85); Assert.False(v.Pass); Assert.Contains("waited an extra refresh", v.Reason); }
        [Fact] public void A_slow_handoff_fails()
        { var v = DelayCheck.Evaluate(T(1.0, 3.1), T(3.9, 6.8), 164.85); Assert.False(v.Pass); Assert.Equal("arrival → Present p95 3.10 ms > 2.5 ms", v.Reason); }
        [Fact] public void Limits_scale_with_the_refresh_rate() => Assert.True(DelayCheck.Evaluate(T(1, 1.5), T(9.0, 17.5), 60).Pass);
        [Fact] public void No_samples_fails() => Assert.Equal("no frames measured", DelayCheck.Evaluate(TimingStats.Empty, TimingStats.Empty, 60).Reason);
    }
}
