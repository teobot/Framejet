using System;
using Framejet.Core.Audio;
using Xunit;

namespace Framejet.Core.Tests.Audio
{
    public class LatencyTrimmerTests
    {
        const int Bps = 48000 * 2 * 4;   // float stereo, 384,000 bytes/s
        const int Block = 8;
        static int Bytes(double ms) => (int)(Bps * ms / 1000) / Block * Block;
        static LatencyTrimmer New() => new LatencyTrimmer(LatencyTrimmer.DefaultTarget, LatencyTrimmer.DefaultTrimAbove);

        [Fact] public void Defaults_are_15ms_and_60ms()
        {
            Assert.Equal(15, LatencyTrimmer.DefaultTarget.TotalMilliseconds);
            Assert.Equal(60, LatencyTrimmer.DefaultTrimAbove.TotalMilliseconds);
        }
        [Fact] public void Keeps_up_to_60ms()
        {
            var t = New();
            Assert.Equal(0, t.BytesToDrop(Bytes(60), Bps, Block));
            Assert.Equal(0, t.Trims);
        }
        [Fact] public void Trims_back_to_15ms_once_past_60ms()
        {
            var t = New();
            Assert.Equal(Bytes(61) - Bytes(15), t.BytesToDrop(Bytes(61), Bps, Block));
            Assert.Equal(1, t.Trims);
        }
        [Fact] public void Drops_whole_blocks_only()
            => Assert.Equal(0, New().BytesToDrop(Bytes(61) + 3, Bps, Block) % Block);
        [Fact] public void Card_clock_0_1_percent_fast_for_two_hours_never_exceeds_60ms()
        {
            var t = New(); int buffered = 0; double frameCarry = 0, maxMs = 0;
            for (int step = 0; step < 720_000; step++)           // 10 ms steps = 2 h
            {
                frameCarry += 480 * 1.001;                       // card delivers 480.48 frames per 10 ms
                int frames = (int)Math.Floor(frameCarry);        // only whole frames arrive
                frameCarry -= frames;
                buffered += frames * Block;
                buffered -= t.BytesToDrop(buffered, Bps, Block);
                maxMs = Math.Max(maxMs, buffered * 1000.0 / Bps);
                buffered = Math.Max(0, buffered - 480 * Block);  // output plays 480 frames
            }
            Assert.True(maxMs <= 60.0, $"max {maxMs:0.0} ms");
            Assert.True(t.Trims > 0);
        }
    }
}
