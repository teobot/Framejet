using System;
using NAudio.Wave;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Platform.Audio;
using Xunit;

namespace Framejet.Platform.Tests.Audio
{
    public class CaptureIngestTests
    {
        static CaptureIngest Make(out BufferedWaveProvider buf, out DriftController drift)
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            buf = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            drift = new DriftController();
            return new CaptureIngest(buf, fmt, drift, NullLog.Instance);
        }

        static byte[] Chunk(int ms, float value = 0f)
        {
            var b = new byte[48 * ms * 8];
            if (value != 0f) for (int i = 0; i < b.Length; i += 4) BitConverter.GetBytes(value).CopyTo(b, i);
            return b;
        }

        static double LevelMs(BufferedWaveProvider buf) => buf.BufferedBytes * 1000.0 / buf.WaveFormat.AverageBytesPerSecond;

        [Fact]
        public void Prefill_holds_silence_until_target()
        {
            var ingest = Make(out var buf, out _);
            var chunk = Chunk(5, 0.5f);
            var read = new byte[48 * 5 * 8];

            ingest.Process(chunk, chunk.Length); // 5 ms: below the 10 ms target
            Assert.Equal(read.Length, ingest.Output.Read(read, 0, read.Length));
            Assert.All(read, b => Assert.Equal(0, b));
            Assert.Equal(5.0, LevelMs(buf), 3); // the silence was made up: nothing was taken from the ring
            Assert.Equal(0, ingest.Underruns);

            ingest.Process(chunk, chunk.Length); // 10 ms: the target is reached
            Assert.Equal(read.Length, ingest.Output.Read(read, 0, read.Length));
            Assert.Equal(0.5, (double)BitConverter.ToSingle(read, 0), 6);
            Assert.Equal(5.0, LevelMs(buf), 3);
        }

        [Fact]
        public void Stall_is_trimmed_once()
        {
            var ingest = Make(out var buf, out var drift);
            ingest.Process(Chunk(120), 48 * 120 * 8); // a stall's worth at once
            Assert.Equal(1, drift.Trims);
            Assert.Equal(10.0, LevelMs(buf), 3);

            ingest.Process(Chunk(10), 48 * 10 * 8);
            Assert.Equal(1, drift.Trims);
            Assert.Equal(1, ingest.Snapshot(false).Trims);
        }

        [Fact]
        public void Underrun_raises_the_target_only_while_capture_delivers()
        {
            var ingest = Make(out _, out var drift);
            var read = new byte[48 * 40 * 8];
            ingest.Process(Chunk(10), 48 * 10 * 8);
            ingest.Output.Read(read, 0, 48 * 20 * 8); // wants 20 ms, has 10
            Assert.Equal(1, ingest.Underruns);
            ingest.Process(Chunk(10), 48 * 10 * 8);
            Assert.Equal(15, drift.Target.TotalMilliseconds);

            System.Threading.Thread.Sleep(150); // the card goes quiet
            ingest.Output.Read(read, 0, read.Length);
            Assert.Equal(2, ingest.Underruns);
            ingest.Process(Chunk(10), 48 * 10 * 8);
            Assert.Equal(15, drift.Target.TotalMilliseconds);
        }

        [Fact]
        public void Pushes_the_ratio_to_the_resampler()
        {
            var ingest = Make(out _, out _);
            var resampler = new VariableRateResampler(new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).ToSampleProvider(), 48000);
            ingest.Resampler = resampler;
            ingest.Process(Chunk(50), 48 * 50 * 8); // 50 ms against a 10 ms target: full speed-up
            Assert.Equal(1.005, resampler.Ratio, 6);
        }

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
            var ingest = Make(out _, out var drift);
            ingest.Process(Chunk(50), 48 * 50 * 8);
            ingest.Process(Chunk(50), 48 * 50 * 8); // 100 ms buffered, then trimmed to the 10 ms target

            var s = ingest.Snapshot(false);
            Assert.Equal(100.0, s.MaxBufferedMs, 3);
            Assert.True(s.BufferedMs <= 20, "current level is post-trim: " + s.BufferedMs);
            Assert.Equal(1, s.Trims);
            Assert.Equal(1, drift.Trims);
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
        public void Buffer_level_stats_cover_every_packet()
        {
            var ingest = Make(out var buf, out _);
            for (int i = 0; i < 100; i++)
            {
                buf.ClearBuffer();
                int ms = i % 2 == 0 ? 10 : 12;
                ingest.Process(Chunk(ms), 48 * ms * 8);
            }

            var d = ingest.Snapshot(false).Delay;
            Assert.InRange(d.BufferMeanMs, 10.5, 11.5);
            Assert.InRange(d.BufferP95Ms, 11.5, 12.5);

            ingest.Snapshot(true);
            Assert.Equal(0, ingest.Snapshot(false).Delay.BufferMeanMs);
        }
    }
}
