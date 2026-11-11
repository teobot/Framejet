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
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), out _);
          Assert.Equal(44100, p.WaveFormat.SampleRate); Assert.Equal(2, p.WaveFormat.Channels); Assert.True(Read(p, 44100).Max() >= 0.3f); }
        [Fact] public void Chain_maps_stereo_onto_eight_channels()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 8), out _); var s = Read(p, 8 * 4800);
          Assert.Equal(8, p.WaveFormat.Channels); Assert.True(Enumerable.Range(0, 4800).Max(i => s[i * 8]) >= 0.3f); Assert.All(Enumerable.Range(0, 4800), i => Assert.Equal(0f, s[i * 8 + 5])); }
        [Fact] public void Chain_mixes_stereo_down_to_mono()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), out _); Assert.Equal(1, p.WaveFormat.Channels); Assert.True(Read(p, 4800).Max() >= 0.3f); }
        [Fact] public void Volume_zero_is_silent()
        { var p = PlaythroughChain.Build(Sine(48000, 2, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), out var v); v.Volume = 0; Assert.All(Read(p, 9600), x => Assert.Equal(0f, x)); }
        [Fact] public void Pcm16_converts_to_float()
        { var src = BitConverter.GetBytes((short)16384); var dst = new byte[4];
          Assert.Equal(4, PlaythroughChain.ToFloat(src, 2, new WaveFormat(48000, 16, 1), dst)); Assert.Equal(0.5, (double)BitConverter.ToSingle(dst, 0), 3); }
    }
}
