using System;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Framejet.Platform.Audio;
using Xunit;

namespace Framejet.Platform.Tests.Audio
{
    public class PlaythroughChainTests
    {
        static BufferedWaveProvider Sine(int rate, int channels, double seconds)
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels); var b = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromSeconds(seconds + 1), ReadFully = false };
            var bytes = new byte[(int)(rate * seconds) * channels * 4];
            for (int i = 0, n = 0; i < bytes.Length; i += 4 * channels, n++) for (int c = 0; c < channels; c++) BitConverter.GetBytes((float)(0.5 * Math.Sin(2 * Math.PI * 1000 * n / rate))).CopyTo(bytes, i + 4 * c);
            b.AddSamples(bytes, 0, bytes.Length); return b;
        }
        static float[] Read(ISampleProvider p, int count) { var buf = new float[count]; int n = p.Read(buf, 0, count); Assert.Equal(count, n); return buf; }

        [Fact] public void Chain_resamples_48k_to_44k1()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), out _, out _);
          Assert.Equal(44100, p.WaveFormat.SampleRate); Assert.Equal(2, p.WaveFormat.Channels); Assert.True(Read(p, 44100).Max() >= 0.3f); }
        [Fact] public void Chain_maps_stereo_onto_eight_channels()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 8), out _, out _); var s = Read(p, 8 * 4800);
          Assert.Equal(8, p.WaveFormat.Channels); Assert.True(Enumerable.Range(0, 4800).Max(i => s[i * 8]) >= 0.3f); Assert.All(Enumerable.Range(0, 4800), i => Assert.Equal(0f, s[i * 8 + 5])); }
        [Fact] public void Chain_mixes_stereo_down_to_mono()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), out _, out _); Assert.Equal(1, p.WaveFormat.Channels); Assert.True(Read(p, 4800).Max() >= 0.3f); }
        [Fact] public void Volume_zero_is_silent()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), out var v, out _); v.Volume = 0; Assert.All(Read(p, 9600), x => Assert.Equal(0f, x)); }
        [Fact] public void Variable_resampler_changes_consumption()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var src = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromSeconds(3), ReadFully = false };
            var bytes = new byte[48000 * 2 * 8];
            for (int i = 0; i < bytes.Length; i += 8) { BitConverter.GetBytes(0.5f).CopyTo(bytes, i); BitConverter.GetBytes(-0.25f).CopyTo(bytes, i + 4); }
            src.AddSamples(bytes, 0, bytes.Length);

            var p = PlaythroughChain.Build(src, WaveFormat.CreateIeeeFloatWaveFormat(96000, 8), out _, out var resampler);
            resampler.Ratio = 1.005;
            int before = src.BufferedBytes;
            var s = new float[960 * 8];
            for (int r = 0; r < 100; r++) // 1 s of output in 10 ms reads
            {
                Assert.Equal(s.Length, p.Read(s, 0, s.Length));
                if (r < 10) continue; // let the filter settle
                for (int f = 0; f < 960; f++)
                {
                    Assert.Equal(0.5, s[f * 8], 2);
                    Assert.Equal(-0.25, s[f * 8 + 1], 2);
                    for (int c = 2; c < 8; c++) Assert.Equal(0f, s[f * 8 + c]);
                }
            }
            double consumed = (before - src.BufferedBytes) / 8.0;
            Assert.InRange(consumed, 48240 * 0.999, 48240 * 1.001);
        }
        // Near-unity ratios sweep the resampler's fractional position slowly; a poor interpolator then makes the
        // high-frequency gain swing. The gain must stay flat block to block and close to the input's.
        [Theory]
        [InlineData(48000, 10000, 1.0001)] [InlineData(48000, 15000, 1.0001)]
        [InlineData(48000, 10000, 1.005)] [InlineData(48000, 15000, 1.005)]
        [InlineData(44100, 15000, 1.0001)] [InlineData(96000, 15000, 1.0001)]
        public void Variable_resampler_keeps_high_frequencies_flat(int outRate, int freq, double ratio)
        {
            const int inRate = 48000; const double amp = 0.5;
            var src = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(inRate, 1)) { BufferDuration = TimeSpan.FromSeconds(4), ReadFully = false };
            var bytes = new byte[inRate * 3 * 4];
            for (int i = 0; i < inRate * 3; i++) BitConverter.GetBytes((float)(amp * Math.Sin(2 * Math.PI * freq * i / inRate))).CopyTo(bytes, i * 4);
            src.AddSamples(bytes, 0, bytes.Length);

            var p = PlaythroughChain.Build(src, WaveFormat.CreateIeeeFloatWaveFormat(outRate, 1), out _, out var resampler);
            resampler.Ratio = ratio;
            int block = outRate / 100; var s = new float[block];
            double inputRms = amp / Math.Sqrt(2), min = double.MaxValue, max = double.MinValue;
            for (int b = 0; b < 180; b++)
            {
                Assert.Equal(block, p.Read(s, 0, block));
                if (b < 10) continue; // let the filter settle
                double db = 20 * Math.Log10(Math.Sqrt(s.Sum(x => (double)x * x) / block) / inputRms);
                min = Math.Min(min, db); max = Math.Max(max, db);
            }
            Assert.True(max - min <= 0.4, $"gain swings {min:0.00}..{max:0.00} dB");
            Assert.True(min >= -0.5 && max <= 0.5, $"gain {min:0.00}..{max:0.00} dB vs input");
        }

        // Each rate change rebuilds the sinc table when upsampling (0.2-1 ms), which a 2.67 ms period can't spare.
        [Fact] public void Variable_resampler_ignores_ratio_jitter_below_its_step()
        {
            var src = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { BufferDuration = TimeSpan.FromSeconds(2), ReadFully = true };
            var r = new VariableRateResampler(src.ToSampleProvider(), 96000); var s = new float[256];
            int Apply(double ratio) { r.Ratio = ratio; r.Read(s, 0, s.Length); return r.RateChanges; }
            Assert.Equal(0, Apply(1.00001));
            Assert.Equal(0, Apply(0.999985));
            Assert.Equal(1, Apply(1.00003));
            Assert.Equal(1, Apply(1.000045)); // within the step of the applied 1.00003
            Assert.Equal(1, Apply(1.000015));
            Assert.Equal(2, Apply(1.0)); // exact unity is always applied, so a held level gets exactly nominal speed
            Assert.Equal(2, Apply(1.0));
            Assert.Equal(3, Apply(0.99997));
        }

        [Fact] public void Pcm16_converts_to_float()
        { var src = BitConverter.GetBytes((short)16384); var dst = new byte[4];
          Assert.Equal(4, PlaythroughChain.ToFloat(src, 2, new WaveFormat(48000, 16, 1), dst)); Assert.Equal(0.5, (double)BitConverter.ToSingle(dst, 0), 3); }
    }
}
