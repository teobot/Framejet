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
        static CaptureIngest Make(out BufferedWaveProvider buf, out DriftController drift, Func<long> clock = null)
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            buf = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            drift = new DriftController();
            return new CaptureIngest(buf, fmt, drift, NullLog.Instance, clock);
        }

        static byte[] Chunk(int ms, float value = 0f)
        {
            var b = new byte[48 * ms * 8];
            if (value != 0f) for (int i = 0; i < b.Length; i += 4) BitConverter.GetBytes(value).CopyTo(b, i);
            return b;
        }

        static double LevelMs(BufferedWaveProvider buf) => buf.BufferedBytes * 1000.0 / buf.WaveFormat.AverageBytesPerSecond;

        static TimeSpan Ms(double ms) => TimeSpan.FromTicks((long)(ms * 10000));

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
        public void Underrun_raises_the_target_only_after_settling_and_while_capture_delivers()
        {
            long now = 1, ms = System.Diagnostics.Stopwatch.Frequency / 1000;
            var ingest = Make(out var buf, out var drift, () => now);
            var read = new byte[48 * 40 * 8];
            ingest.Process(Chunk(10), 48 * 10 * 8);
            ingest.Output.Read(read, 0, 48 * 20 * 8); // wants 20 ms, has 10: a start-up catch-up read
            Assert.Equal(1, ingest.Underruns);
            now += 10 * ms; ingest.Process(Chunk(10), 48 * 10 * 8);
            Assert.Equal(10, drift.Target.TotalMilliseconds);

            for (int i = 0; i < 510; i++) { now += 10 * ms; buf.ClearBuffer(); ingest.Process(Chunk(10), 48 * 10 * 8); } // past the settle time
            ingest.Output.Read(read, 0, 48 * 20 * 8);
            Assert.Equal(2, ingest.Underruns);
            now += 10 * ms; ingest.Process(Chunk(10), 48 * 10 * 8);
            Assert.Equal(15, drift.Target.TotalMilliseconds);

            now += 150 * ms; // the card goes quiet
            ingest.Output.Read(read, 0, read.Length);
            Assert.Equal(3, ingest.Underruns);
            now += 10 * ms; ingest.Process(Chunk(10), 48 * 10 * 8);
            Assert.Equal(15, drift.Target.TotalMilliseconds);
        }

        [Fact]
        public void Counted_underruns_are_reported_only_after_settling_and_while_capture_delivers()
        {
            long now = 1, ms = System.Diagnostics.Stopwatch.Frequency / 1000;
            var ingest = Make(out var buf, out _, () => now);
            var counted = new System.Collections.Generic.List<long>();
            ingest.CountedUnderrun = t => counted.Add(t);
            var read = new byte[48 * 40 * 8];
            ingest.Process(Chunk(10), 48 * 10 * 8);
            ingest.Output.Read(read, 0, 48 * 20 * 8); // start-up catch-up read: inside the settle time
            Assert.Equal(1, ingest.Underruns);
            Assert.Empty(counted);

            for (int i = 0; i < 510; i++) { now += 10 * ms; buf.ClearBuffer(); ingest.Process(Chunk(10), 48 * 10 * 8); }
            ingest.Output.Read(read, 0, 48 * 20 * 8);
            Assert.Equal(new[] { now }, counted);

            now += 150 * ms; // the card goes quiet: a silent card never counts
            ingest.Output.Read(read, 0, read.Length);
            Assert.Single(counted);
        }

        [Fact]
        public void Prefill_rearms_after_a_stall()
        {
            var ingest = Make(out var buf, out var drift);
            var read = new byte[48 * 20 * 8];
            ingest.Process(Chunk(10, 0.5f), 48 * 10 * 8);

            System.Threading.Thread.Sleep(150); // the card stalls; playback drains the ring
            ingest.Output.Read(read, 0, read.Length);
            Assert.Equal(0, buf.BufferedBytes);
            ingest.Output.Read(read, 0, read.Length); // empty while the card is silent: back to prefill
            int underruns = ingest.Underruns;

            ingest.Process(Chunk(5, 0.5f), 48 * 5 * 8); // the card resumes, below target
            ingest.Output.Read(read, 0, read.Length);
            Assert.All(read, b => Assert.Equal(0, b));
            Assert.Equal(5.0, LevelMs(buf), 3);
            Assert.Equal(underruns, ingest.Underruns);

            ingest.Process(Chunk(5, 0.5f), 48 * 5 * 8); // target reached: plays again
            ingest.Output.Read(read, 0, 48 * 10 * 8);
            Assert.Equal(0.5, (double)BitConverter.ToSingle(read, 0), 6);
            ingest.Process(Chunk(10, 0.5f), 48 * 10 * 8);
            Assert.Equal(10, drift.Target.TotalMilliseconds);
            Assert.Equal(underruns, ingest.Underruns);
        }

        /// <summary>
        /// Capture and playback in lockstep on one engine clock, as with a card and an output on the same Windows
        /// audio engine: each 10 ms step a packet lands and playback reads 10 ms through the real chain. Every
        /// <paramref name="jitterEvery"/> steps a packet arrives before the read instead of after it. The controller
        /// is the backend's for 10 ms streams: 13 ms of headroom, so 23 ms right after a packet.
        /// </summary>
        static (double MeanMs, double FinalMs, int Underruns, double TargetMs) Lockstep(int standingPackets, int seconds, int jitterEvery)
        {
            long now = 1;
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var buf = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            var drift = DriftController.ForStreams(Ms(10), Ms(10));
            var ingest = new CaptureIngest(buf, fmt, drift, NullLog.Instance, () => now);
            var chain = PlaythroughChain.ToWaveProvider(PlaythroughChain.Build(ingest.Output, fmt, out _, out var resampler), fmt);
            ingest.Resampler = resampler;
            long tick = System.Diagnostics.Stopwatch.Frequency / 100;
            var packet = Chunk(10, 0.25f); var read = new byte[48 * 10 * 8];
            chain.Read(read, 0, read.Length); // playback starts before capture, as in the backend: a prefill read
            for (int i = 0; i < standingPackets; i++) ingest.Process(packet, packet.Length);
            double sum = 0; int n = 0, steps = seconds * 100;
            for (int i = 0; i < steps; i++)
            {
                now += tick;
                bool early = jitterEvery > 0 && i % jitterEvery == 0;
                if (early) ingest.Process(packet, packet.Length);
                chain.Read(read, 0, read.Length);
                if (!early) ingest.Process(packet, packet.Length);
                if (i >= steps / 2) { sum += ingest.Snapshot(false).BufferedMs; n++; }
            }
            return (sum / n, LevelMs(buf), ingest.Underruns, drift.Target.TotalMilliseconds);
        }

        [Fact]
        public void Locked_clocks_hold_the_headroom_without_underruns()
        {
            var r = Lockstep(standingPackets: 3, seconds: 60, jitterEvery: 50);
            Assert.Equal(0, r.Underruns);
            Assert.Equal(13, r.TargetMs, 3);
            Assert.True(r.MeanMs <= 23.5, $"mean level {r.MeanMs:0.00} ms");
        }

        [Fact]
        public void A_standing_excess_drains_into_the_band_without_underruns()
        {
            var r = Lockstep(standingPackets: 4, seconds: 40, jitterEvery: 50); // starts 10 ms above the target
            Assert.Equal(0, r.Underruns);
            Assert.Equal(13, r.TargetMs, 3);
            double band = DriftController.HoldBand.TotalMilliseconds;
            Assert.InRange(r.FinalMs, 23 - band - 0.25, 23 + band + 0.25);
        }

        /// <summary>
        /// Low-latency mode: 128-frame (2.67 ms) packets and 128-frame reads on one engine clock, through the real chain.
        /// Every 7th period the packet lands after the read instead of before it.
        /// </summary>
        [Fact]
        public void Short_periods_hold_their_headroom_without_underruns()
        {
            long now = 1;
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var buf = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            var drift = DriftController.ForStreams(Ms(128 / 48.0), Ms(128 / 48.0));
            var ingest = new CaptureIngest(buf, fmt, drift, NullLog.Instance, () => now);
            var chain = PlaythroughChain.ToWaveProvider(PlaythroughChain.Build(ingest.Output, fmt, out _, out var resampler), fmt);
            ingest.Resampler = resampler;
            long tick = System.Diagnostics.Stopwatch.Frequency * 128 / 48000;
            var packet = new byte[128 * 8]; var read = new byte[128 * 8];
            for (int i = 0; i < 128 * 2; i++) BitConverter.GetBytes(0.25f).CopyTo(packet, i * 4);
            int steps = 60 * 48000 / 128;
            for (int i = 0; i < steps; i++)
            {
                now += tick;
                bool late = i % 7 == 0;
                if (!late) ingest.Process(packet, packet.Length);
                chain.Read(read, 0, read.Length);
                if (late) ingest.Process(packet, packet.Length);
            }
            var d = ingest.Snapshot(false).Delay;
            Assert.Equal(0, ingest.Underruns);
            Assert.Equal(5.667, drift.Target.TotalMilliseconds, 3); // one 2.67 ms output period plus the 3 ms margin
            // The late packets set the minimum: on time, the ring holds one more period than that headroom before a packet.
            Assert.InRange(d.BufferMeanMs, 9.5, 11.5);
            Assert.InRange(d.BufferP95Ms, 9.5, 11.5);
        }

        [Fact]
        public void Pushes_the_ratio_to_the_resampler()
        {
            long now = 1;
            var ingest = Make(out _, out _, () => now);
            var resampler = new VariableRateResampler(new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).ToSampleProvider(), 48000);
            ingest.Resampler = resampler;
            ingest.Process(Chunk(50), 48 * 50 * 8); // primes with no headroom before it: full slow-down
            Assert.Equal(0.995, resampler.Ratio, 6);
            now += System.Diagnostics.Stopwatch.Frequency * 11 / 10; // past the 1 s window
            ingest.Process(Chunk(10), 48 * 10 * 8); // 50 ms of headroom against a 10 ms target: full speed-up
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
