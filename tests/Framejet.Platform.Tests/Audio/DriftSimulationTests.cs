using System;
using NAudio.Wave;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Platform.Audio;
using Xunit;

namespace Framejet.Platform.Tests.Audio
{
    /// <summary>
    /// The card's clock against the output's, through the real ingest and chain: capture packets and output reads each
    /// on their own clock, so their order sweeps through every phase as the clocks drift apart.
    /// </summary>
    public class DriftSimulationTests
    {
        internal struct Result { public int Underruns, AllUnderruns, Trims; public double TargetMs, MeanPostMs, MinPreMs; }

        internal static Result Run(double cardPpm, double capturePeriodMs, double outputPeriodMs, int seconds, Func<DriftController> make)
        {
            const int Rate = 48000;
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
            var buf = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            var drift = make();
            long now = 1;
            var ingest = new CaptureIngest(buf, fmt, drift, NullLog.Instance, () => now);
            var chain = PlaythroughChain.ToWaveProvider(PlaythroughChain.Build(ingest.Output, fmt, out _, out var resampler), fmt);
            ingest.Resampler = resampler;

            int captureFrames = (int)Math.Round(Rate * capturePeriodMs / 1000), outputFrames = (int)Math.Round(Rate * outputPeriodMs / 1000);
            var packet = new byte[captureFrames * 8]; var read = new byte[outputFrames * 8];
            for (int i = 0; i < captureFrames * 2; i++) BitConverter.GetBytes(0.25f).CopyTo(packet, i * 4);
            double captureStep = captureFrames * 1000.0 / Rate / (1 + cardPpm * 1e-6), outputStep = outputFrames * 1000.0 / Rate;
            double nextCapture = 0.3, nextRead = 0, end = seconds * 1000.0;
            int settleUnderruns = -1; double sumPost = 0; int nPost = 0; double minPre = double.MaxValue;
            double bytesPerMs = fmt.AverageBytesPerSecond / 1000.0;
            while (nextRead < end)
            {
                bool capture = nextCapture <= nextRead;
                double t = capture ? nextCapture : nextRead;
                now = 1 + (long)(t * System.Diagnostics.Stopwatch.Frequency / 1000);
                if (capture)
                {
                    double pre = buf.BufferedBytes / bytesPerMs;
                    ingest.Process(packet, packet.Length);
                    nextCapture += captureStep;
                    if (t > end / 2) { sumPost += buf.BufferedBytes / bytesPerMs; nPost++; minPre = Math.Min(minPre, pre); }
                }
                else
                {
                    chain.Read(read, 0, read.Length);
                    nextRead += outputStep;
                }
                if (settleUnderruns < 0 && t > 10000) settleUnderruns = ingest.Underruns;
            }
            return new Result { Underruns = ingest.Underruns - Math.Max(0, settleUnderruns), AllUnderruns = ingest.Underruns, Trims = drift.Trims, TargetMs = drift.Target.TotalMilliseconds, MeanPostMs = sumPost / nPost, MinPreMs = minPre };
        }

        static DriftController Make(double capturePeriodMs, double outputPeriodMs)
            => DriftController.ForStreams(TimeSpan.FromTicks((long)(capturePeriodMs * 10000)), TimeSpan.FromTicks((long)(outputPeriodMs * 10000)));

        [Theory]
        [InlineData(-100, 10.0, 10.0)] [InlineData(100, 10.0, 10.0)]   // normal mode
        [InlineData(-100, 10.0, 2.0)] [InlineData(100, 10.0, 2.0)]     // low-latency output, normal capture
        [InlineData(-100, 2.6667, 2.6667)] [InlineData(100, 2.6667, 2.6667)] // both low-latency
        // An output whose clock runs 0.43% fast against the card: measured on a virtual mixer in low-latency mode.
        [InlineData(-4300, 10.0, 2.0)] [InlineData(-1000, 10.0, 10.0)] [InlineData(1000, 10.0, 10.0)]
        public void Drifting_card_is_held_without_underruns(double ppm, double capturePeriodMs, double outputPeriodMs)
        {
            var r = Run(ppm, capturePeriodMs, outputPeriodMs, 300, () => Make(capturePeriodMs, outputPeriodMs));
            Assert.True(r.AllUnderruns == 0, $"{r.AllUnderruns} underruns ({r.Underruns} after 10 s); target {r.TargetMs} ms, min pre-packet {r.MinPreMs:0.00} ms");
            Assert.Equal(0, r.Trims);
            Assert.Equal(outputPeriodMs + DriftController.HeadroomMargin.TotalMilliseconds, r.TargetMs, 3); // never stepped up
            Assert.True(r.MeanPostMs <= r.TargetMs + capturePeriodMs + 3, $"mean post-packet level {r.MeanPostMs:0.00} ms");
        }
    }
}
