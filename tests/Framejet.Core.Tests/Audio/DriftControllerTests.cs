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

        static void Simulate(double cardRate, out double finalLevel, out double meanLate, out double p95Late, out int trims, out double target)
        {
            var c = new DriftController();
            target = c.Target.TotalMilliseconds;
            double level = target;
            var late = new List<double>();
            const int steps = 60000;   // 10 min of 10 ms steps
            for (int i = 0; i < steps; i++)
            {
                c.Update(level, 10);
                level += 10 * cardRate - 10 * c.Ratio;
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
    }
}
