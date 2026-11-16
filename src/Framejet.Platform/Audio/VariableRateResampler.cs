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
        int _rateChanges;

        /// <summary>
        /// A new ratio is applied only once it is this far from the one in use (or is exactly 1). When upsampling, each
        /// change rebuilds the sinc table (0.2-1 ms), too much to spend on the controller's tiny jitter every period.
        /// </summary>
        public const double RatioStep = 2e-5;

        public VariableRateResampler(ISampleProvider source, int outputRate)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            _inRate = source.WaveFormat.SampleRate;
            _outRate = outputRate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputRate, _channels);

            // Windowed sinc, 64 taps. NAudio's WdlResamplingSampleProvider default (linear interpolation plus an IIR
            // lowpass) loses several dB at 10-15 kHz, and at near-unity ratios its high-frequency gain swings as
            // the fractional position sweeps; sinc stays flat.
            _resampler.SetMode(true, 0, true, 64, 32);
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

        /// <summary>How many times a new ratio was applied; read it on the reading thread (tests).</summary>
        internal int RateChanges => _rateChanges;

        public int Read(float[] buffer, int offset, int count)
        {
            double ratio = Ratio;
            if (ratio != _appliedRatio && (ratio == 1.0 || Math.Abs(ratio - _appliedRatio) >= RatioStep))
            {
                _resampler.SetRates(_inRate * ratio, _outRate);
                _appliedRatio = ratio;
                _rateChanges++;
            }

            int frames = count / _channels;
            int inNeeded = _resampler.ResamplePrepare(frames, _channels, out var inBuffer, out int inOffset);
            int inGot = _source.Read(inBuffer, inOffset, inNeeded * _channels) / _channels;
            return _resampler.ResampleOut(buffer, offset, inGot, frames, _channels) * _channels;
        }
    }
}
