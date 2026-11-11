using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Framejet.Platform.Audio
{
    public static class PlaythroughChain
    {
        public static WaveFormat FloatFormatFor(WaveFormat capture)
            => WaveFormat.CreateIeeeFloatWaveFormat(capture.SampleRate, capture.Channels);

        /// <summary>Converts captured bytes to 32-bit float bytes. Returns the number of bytes written.</summary>
        public static int ToFloat(byte[] source, int count, WaveFormat sourceFormat, byte[] destination)
        {
            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat
                || (sourceFormat is WaveFormatExtensible ext && ext.ToStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat))
            {
                Buffer.BlockCopy(source, 0, destination, 0, count);
                return count;
            }

            int bytesPer = sourceFormat.BitsPerSample / 8;
            int samples = count / bytesPer;
            for (int i = 0; i < samples; i++)
            {
                int o = i * bytesPer;
                float v;
                switch (bytesPer)
                {
                    case 2: v = BitConverter.ToInt16(source, o) / 32768f; break;
                    case 3: v = ((source[o] << 8) | (source[o + 1] << 16) | (source[o + 2] << 24)) / 2147483648f; break;
                    case 4: v = BitConverter.ToInt32(source, o) / 2147483648f; break;
                    default: throw new NotSupportedException("Unsupported PCM bit depth " + sourceFormat.BitsPerSample);
                }
                BitConverter.GetBytes(v).CopyTo(destination, i * 4);
            }
            return samples * 4;
        }

        public static ISampleProvider Build(IWaveProvider floatSource, WaveFormat outputMixFormat, out VolumeSampleProvider volume)
        {
            ISampleProvider p = floatSource.ToSampleProvider();
            if (p.WaveFormat.SampleRate != outputMixFormat.SampleRate)
                p = new WdlResamplingSampleProvider(p, outputMixFormat.SampleRate);

            int inCh = p.WaveFormat.Channels, outCh = outputMixFormat.Channels;
            if (inCh != outCh)
            {
                if (outCh == 1 && inCh >= 2)
                {
                    p = new StereoToMonoSampleProvider(p) { LeftVolume = 0.5f, RightVolume = 0.5f };
                }
                else
                {
                    var map = new int[outCh];
                    for (int c = 0; c < outCh; c++) map[c] = inCh == 1 ? 0 : (c < inCh ? c : -1);
                    p = new ChannelMapSampleProvider(p, map);
                }
            }

            volume = new VolumeSampleProvider(p);
            return volume;
        }

        /// <summary>
        /// Presents float samples as bytes under the device's own format. WASAPI rejects a plain WaveFormat
        /// for multichannel devices, and SampleToWaveProvider refuses an extensible one.
        /// </summary>
        public static IWaveProvider ToWaveProvider(ISampleProvider samples, WaveFormat deviceFormat)
            => new FloatBytesWaveProvider(samples, deviceFormat);

        sealed class FloatBytesWaveProvider : IWaveProvider
        {
            readonly ISampleProvider _source;
            float[] _scratch = new float[0];
            public FloatBytesWaveProvider(ISampleProvider source, WaveFormat format) { _source = source; WaveFormat = format; }
            public WaveFormat WaveFormat { get; }
            public int Read(byte[] buffer, int offset, int count)
            {
                int samples = count / 4;
                if (_scratch.Length < samples) _scratch = new float[samples];
                int got = _source.Read(_scratch, 0, samples);
                Buffer.BlockCopy(_scratch, 0, buffer, offset, got * 4);
                return got * 4;
            }
        }

        /// <summary>Routes input channels to output channels; a map entry of -1 leaves that output silent.</summary>
        sealed class ChannelMapSampleProvider : ISampleProvider
        {
            readonly ISampleProvider _source;
            readonly int[] _map;
            readonly int _inCh;
            float[] _scratch = new float[0];

            public ChannelMapSampleProvider(ISampleProvider source, int[] map)
            {
                _source = source; _map = map; _inCh = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, map.Length);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int outCh = _map.Length;
                int frames = count / outCh;
                if (_scratch.Length < frames * _inCh) _scratch = new float[frames * _inCh];
                int gotFrames = _source.Read(_scratch, 0, frames * _inCh) / _inCh;
                for (int f = 0; f < gotFrames; f++)
                    for (int c = 0; c < outCh; c++)
                        buffer[offset + f * outCh + c] = _map[c] < 0 ? 0f : _scratch[f * _inCh + _map[c]];
                return gotFrames * outCh;
            }
        }
    }
}
