using System;
using NAudio.Wave;
using Framejet.Core.Audio;
using Framejet.Platform.Audio;
using Xunit;

namespace Framejet.Platform.Tests.Audio
{
    public class CaptureIngestTests
    {
        static CaptureIngest Make(out BufferedWaveProvider buf, out LatencyTrimmer trimmer)
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            buf = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
            trimmer = new LatencyTrimmer(LatencyTrimmer.DefaultTarget, LatencyTrimmer.DefaultTrimAbove);
            return new CaptureIngest(buf, fmt, trimmer);
        }

        static byte[] Chunk(int ms) => new byte[48 * ms * 8];

        [Fact]
        public void Max_buffered_is_recorded_before_trimming()
        {
            var ingest = Make(out var buf, out var trimmer);
            ingest.Process(Chunk(50), 48 * 50 * 8);
            ingest.Process(Chunk(50), 48 * 50 * 8); // 100 ms buffered, then trimmed to about 15 ms

            var s = ingest.Snapshot(false);
            Assert.Equal(100.0, s.MaxBufferedMs, 3);
            Assert.True(s.BufferedMs <= 20, "current level is post-trim: " + s.BufferedMs);
            Assert.Equal(1, s.Trims);
            Assert.Equal(1, trimmer.Trims);
        }

        [Fact]
        public void Reset_clears_max_down_to_current_level()
        {
            var ingest = Make(out _, out _);
            ingest.Process(Chunk(50), 48 * 50 * 8);
            ingest.Process(Chunk(50), 48 * 50 * 8);
            ingest.Snapshot(true);
            var s = ingest.Snapshot(false);
            Assert.Equal(0, s.BytesCaptured);
            Assert.Equal(s.BufferedMs, s.MaxBufferedMs, 3);
            Assert.Equal(0, s.Trims);
        }
    }
}
