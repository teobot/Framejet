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
        public void Block_tap_reports_each_ms_of_a_packet_with_its_peak_and_a_stamp_counted_back_from_arrival()
        {
            var ingest = Make(out _, out _);
            var chunk = Chunk(10);
            BitConverter.GetBytes(-0.5f).CopyTo(chunk, (3 * 48 + 5) * 8 + 4); // right channel, block 3
            var calls = new System.Collections.Generic.List<(long Ticks, float Peak)>();
            ingest.BlockPeakTap = (t, p) => calls.Add((t, p));
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            ingest.Process(chunk, chunk.Length);
            long after = System.Diagnostics.Stopwatch.GetTimestamp();

            Assert.Equal(10, calls.Count);
            for (int b = 0; b < 10; b++) Assert.Equal(b == 3 ? 0.5 : 0.0, (double)calls[b].Peak, 6);
            long ms = System.Diagnostics.Stopwatch.Frequency / 1000;
            Assert.InRange(calls[0].Ticks, before - 10 * ms, after - 10 * ms);
            for (int b = 1; b < 10; b++) Assert.Equal(ms, calls[b].Ticks - calls[b - 1].Ticks);

            ingest.BlockPeakTap = null;
            ingest.Process(chunk, chunk.Length);
            Assert.Equal(10, calls.Count);
        }

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

        [Fact]
        public void Buffer_level_stats_cover_every_packet_and_count_what_was_waiting_before_it()
        {
            var ingest = Make(out var buf, out _);
            for (int i = 0; i < 100; i++)
            {
                buf.ClearBuffer();
                int waiting = i % 2 == 0 ? 2 : 4;
                buf.AddSamples(Chunk(waiting), 0, 48 * waiting * 8);
                ingest.Process(Chunk(10), 48 * 10 * 8);
            }

            var d = ingest.Snapshot(false).Delay;
            Assert.InRange(d.BufferMeanMs, 2.5, 3.5);
            Assert.InRange(d.BufferP95Ms, 3.5, 4.5);

            ingest.Snapshot(true);
            Assert.Equal(0, ingest.Snapshot(false).Delay.BufferMeanMs);
        }

        [Fact]
        public void Buffer_level_sample_is_the_level_before_the_packet_is_added()
        {
            var ingest = Make(out var buf, out _);
            buf.AddSamples(Chunk(3), 0, 48 * 3 * 8);
            ingest.Process(Chunk(10), 48 * 10 * 8);

            var s = ingest.Snapshot(false);
            Assert.Equal(3.0, s.Delay.BufferMeanMs, 3);
            Assert.Equal(13.0, s.BufferedMs, 3);    // the older figures stay post-add
            Assert.Equal(13.0, s.MaxBufferedMs, 3);
        }

        [Fact]
        public void Packet_length_is_the_mean_duration_of_the_packets_delivered()
        {
            var ingest = Make(out var buf, out _);
            for (int i = 0; i < 10; i++)
            {
                buf.ClearBuffer();
                int ms = i % 2 == 0 ? 10 : 12;
                ingest.Process(Chunk(ms), 48 * ms * 8);
            }

            Assert.Equal(11.0, ingest.Snapshot(true).PacketMeanMs, 3);
            Assert.Equal(0, ingest.Snapshot(false).PacketMeanMs);
        }
    }
}
