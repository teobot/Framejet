using System;
using System.Collections.Generic;
using System.Linq;
using Framejet.Core.Audio;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class DriftControllerTests
    {
        const int Bps = 48000 * 2 * 4;   // float stereo, 384,000 bytes/s
        const int Block = 8;
        static int Bytes(double ms) => (int)(Bps * ms / 1000) / Block * Block;

        // Measurement noise within the band must not move the rate.
        [Fact] public void Holds_ratio_at_exactly_one_within_the_band()
        {
            var c = new DriftController();
            Assert.Equal(0.5, DriftController.HoldBand.TotalMilliseconds);
            for (int i = 0; i < 500; i++) c.Update(10.45, 10);
            Assert.Equal(1.0, c.Ratio);
            for (int i = 0; i < 500; i++) c.Update(9.55, 10);
            Assert.Equal(1.0, c.Ratio);
            for (int i = 0; i < 500; i++) c.Update(11, 10);
            Assert.True(c.Ratio > 1.0);
        }

        [Fact] public void Holds_ratio_at_one_on_target()
        {
            var c = new DriftController();
            double r = 0;
            for (int i = 0; i < 500; i++) r = c.Update(10, 10);
            Assert.Equal(1.0, r, 6);
            Assert.Equal(1.0, c.Ratio, 6);
        }

        [Fact] public void Speeds_up_when_buffer_is_above_target()
        {
            var c = new DriftController();
            double r = 0;
            for (int i = 0; i < 500; i++) r = c.Update(30, 10);   // 5 s
            Assert.Equal(1.005, r, 4);
        }

        [Fact] public void Correction_never_exceeds_half_a_percent()
        {
            var high = new DriftController();
            var low = new DriftController();
            for (int i = 0; i < 4; i++) low.Underrun();   // target 30 ms, so an empty buffer is 30 ms under it
            for (int i = 0; i < 500; i++) { high.Update(500, 10); low.Update(0, 10); }
            Assert.Equal(1.005, high.Ratio, 6);
            Assert.Equal(0.995, low.Ratio, 6);
        }

        static void Simulate(double cardRate, out double finalLevel, out double meanLate, out double p95Late, out int trims, out double target, double startAbove = 0)
            => Simulate(cardRate, out finalLevel, out meanLate, out p95Late, out trims, out target, out _, startAbove);

        static void Simulate(double cardRate, out double finalLevel, out double meanLate, out double p95Late, out int trims, out double target, out double minLevel, double startAbove)
        {
            var c = new DriftController();
            target = c.Target.TotalMilliseconds;
            double level = target + startAbove;
            minLevel = level;
            var late = new List<double>();
            const int steps = 60000;   // 10 min of 10 ms steps
            for (int i = 0; i < steps; i++)
            {
                c.Update(level, 10);
                level += 10 * cardRate - 10 * c.Ratio;
                minLevel = Math.Min(minLevel, level);
                if (i >= 1000) late.Add(level);
            }
            finalLevel = level;
            meanLate = late.Average();
            late.Sort();
            p95Late = late[(int)(late.Count * 0.95)];
            trims = c.Trims;
        }

        [Fact] public void Simulated_fast_card_settles_near_target()
        {
            Simulate(1.001, out double final, out double mean, out double p95, out int trims, out double target);
            Assert.InRange(final, target - 5, target + 5);
            Assert.True(p95 - mean <= 5, $"p95 {p95} - mean {mean}");
            Assert.Equal(0, trims);
        }

        [Fact] public void Simulated_slow_card_settles_near_target()
        {
            Simulate(0.999, out double final, out double mean, out double p95, out int trims, out double target);
            Assert.InRange(final, target - 5, target + 5);
            Assert.True(p95 - mean <= 5, $"p95 {p95} - mean {mean}");
            Assert.Equal(0, trims);
        }

        // P alone would hold a 0.43% offset only 4.3 ms below the target; the integral term takes that error away.
        [Theory, InlineData(0.9957), InlineData(1.0043)]
        public void Integral_learns_a_steady_clock_offset(double cardRate)
        {
            Simulate(cardRate, out double final, out double mean, out _, out int trims, out double target);
            Assert.InRange(final, target - 0.75, target + 0.75);
            Assert.InRange(mean, target - 0.75, target + 0.75);
            Assert.Equal(0, trims);
        }

        // Prefill can open with a packet's worth above the target. Draining it must not wind the integral up into an
        // overshoot below the target, where the ring runs dry.
        [Fact] public void A_start_up_excess_drains_without_overshoot()
        {
            Simulate(1.0, out double final, out _, out _, out _, out double target, out double min, startAbove: 10);
            Assert.True(min >= target - 1.5, $"fell to {min:0.00} ms against a {target} ms target"); // well within the 3 ms margin
            Assert.InRange(final, target - 0.75, target + 0.75);
        }

        // The output's clock 0.43% fast from the start: the level must not fall more than the start target's worth.
        [Fact] public void A_large_offset_from_the_start_stays_within_the_headroom()
        {
            Simulate(0.9957, out _, out _, out _, out _, out double target, out double min, startAbove: 0);
            Assert.True(min > 0, $"fell to {min:0.00} ms");
        }

        [Fact] public void For_streams_starts_at_one_output_period_plus_the_margin()
        {
            var c = DriftController.ForStreams(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(2));
            Assert.Equal(2 + DriftController.HeadroomMargin.TotalMilliseconds, c.Target.TotalMilliseconds, 6);
            Assert.Equal(10, c.CapturePeriod.TotalMilliseconds, 6);
            Assert.Equal(c.Target + c.CapturePeriod, c.FullLevel);
            for (int i = 0; i < 10; i++) c.Underrun();
            Assert.Equal(20, c.Target.TotalMilliseconds, 6); // headroom plus one 10 ms packet stays within 30 ms
        }

        [Fact] public void Underruns_raise_target_in_steps_up_to_30ms()
        {
            var c = new DriftController();
            Assert.Equal(10, c.Target.TotalMilliseconds);
            c.Underrun();
            Assert.Equal(15, c.Target.TotalMilliseconds);
            for (int i = 0; i < 9; i++) c.Underrun();
            Assert.Equal(30, c.Target.TotalMilliseconds);
        }

        [Fact] public void Hard_trim_only_above_80ms()
        {
            var c = new DriftController();
            Assert.Equal(0, c.BytesToTrim(Bytes(79), Bps, Block));
            Assert.Equal(0, c.Trims);
            int buffered = Bytes(81);
            int trim = c.BytesToTrim(buffered, Bps, Block);
            Assert.True(trim > 0);
            Assert.Equal(0, trim % Block);
            Assert.InRange(buffered - trim, Bytes(10) - Block, Bytes(10) + Block);
            Assert.Equal(1, c.Trims);
        }

        [Fact] public void Hard_trim_keeps_the_target_plus_one_capture_period()
        {
            var c = DriftController.ForStreams(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
            int buffered = Bytes(100);
            int trim = c.BytesToTrim(buffered, Bps, Block);
            Assert.InRange(buffered - trim, Bytes(c.FullLevel.TotalMilliseconds) - Block, Bytes(c.FullLevel.TotalMilliseconds) + Block);
        }
    }
}
