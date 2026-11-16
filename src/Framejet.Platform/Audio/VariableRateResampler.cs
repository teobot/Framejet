using System;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace Framejet.Platform.Audio
{
    /// <summary>
    /// Resamples to the output rate while consuming input at <see cref="Ratio"/> times the nominal rate, so playback
    /// can run a fraction of a percent fast or slow to hold the playthrough buffer steady.
    /// </summary>
    public sealed class VariableRateResampler : ISampleProvider
    {
        readonly ISampleProvider _source;
        readonly WdlResampler _resampler = new WdlResampler();
        readonly int _channels, _inRate, _outRate;
        long _ratioBits = BitConverter.DoubleToInt64Bits(1.0);
        double _appliedRatio = 1.0; // touched only on the reading thread

        public VariableRateResampler(ISampleProvider source, int outputRate)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            _inRate = source.WaveFormat.SampleRate;
            _outRate = outputRate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, _channels);

            // The quality settings NAudio's WdlResamplingSampleProvider uses.
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(false); // output driven
            _resampler.SetRates(_inRate, _outRate);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>
        /// Input consumed per nominal input sample: above 1 plays faster, below 1 slower. Any thread may set it without
        /// a lock; the reading thread applies it on its next read.
        /// </summary>
        public double Ratio
        {
            get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _ratioBits));
            set => Interlocked.Exchange(ref _ratioBits, BitConverter.DoubleToInt64Bits(value));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            double ratio = Ratio;
            if (ratio != _appliedRatio)
            {
                _resampler.SetRates(_inRate * ratio, _outRate);
                _appliedRatio = ratio;
            }

            int frames = count / _channels;
            int inNeeded = _resampler.ResamplePrepare(frames, _channels, out var inBuffer, out int inOffset);
            int inGot = _source.Read(inBuffer, inOffset, inNeeded * _channels) / _channels;
            return _resampler.ResampleOut(buffer, offset, inGot, frames, _channels) * _channels;
        }
    }
}
