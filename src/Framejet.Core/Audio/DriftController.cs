using System;

namespace Framejet.Core.Audio
{
    /// <summary>
    /// Steers the playback rate by up to +-0.5% so the playthrough buffer holds a small target,
    /// instead of letting capture-card and output clock drift build up and then cutting audio.
    /// <para>
    /// The target is <b>pre-packet headroom</b>: what is still buffered just before a capture packet lands (the
    /// lowest post-packet level over the last second, less the packet). It starts at one output period plus
    /// <see cref="HeadroomMargin"/>, enough for one output read to come before a packet instead of after it, and for
    /// the resampler's lookahead. Unlike a post-packet level, headroom can fall below its target, so a slow card's
    /// clock is corrected by slowing playback down.
    /// </para>
    /// </summary>
    public sealed class DriftController
    {
        /// <summary>The parameterless constructor's start target.</summary>
        public static readonly TimeSpan StartTarget = TimeSpan.FromMilliseconds(10);
        public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);
        /// <summary>The most the buffer may hold after a packet: headroom target plus one capture packet.</summary>
        public static readonly TimeSpan MaxBuffer = TimeSpan.FromMilliseconds(30);
        /// <summary>The parameterless constructor's highest target.</summary>
        public static readonly TimeSpan MaxTarget = MaxBuffer;
        public static readonly TimeSpan HardTrimAbove = TimeSpan.FromMilliseconds(80);
        public const double MaxCorrection = 0.005;

        /// <summary>
        /// Headroom beyond one output period: the sinc resampler's lookahead (32 samples, 0.67 ms at 48 kHz) and the
        /// capture packets' timing jitter (a 10 ms card's packets land 7-14 ms apart). Measured on the target card: 2 ms
        /// still underran, 3 ms ran clean.
        /// </summary>
        public static readonly TimeSpan HeadroomMargin = TimeSpan.FromTicks(30000); // 3 ms; FromMilliseconds rounds to whole ms on .NET Framework

        /// <summary>
        /// Within this of the target the correction holds still, so the rate doesn't wander on measurement noise.
        /// Outside it the correction is proportional to the error, plus an integral term that learns a steady clock
        /// offset (a card or output a fixed fraction of a percent off), so the headroom settles on the target itself.
        /// </summary>
        public static readonly TimeSpan HoldBand = TimeSpan.FromTicks(5000); // 0.5 ms

        /// <summary>
        /// How slowly the integral term catches up: an error held this long adds as much again as the proportional term.
        /// Slow enough that draining a start-up excess barely winds it up.
        /// </summary>
        public static readonly TimeSpan IntegralTime = TimeSpan.FromSeconds(10);

        // The level is smoothed over about this long (an exponential average).
        const double SmoothingMs = 250.0;

        // A level this far from the target gives the full correction. It is steep enough that a 0.4% clock offset
        // (seen on a virtual mixer's low-latency output) is matched within the headroom, before the integral learns it.
        const double FullScaleMs = 5.0;

        double _ema, _integral;
        bool _haveLevel;

        readonly TimeSpan _maxTarget;

        public DriftController() : this(StartTarget, MaxTarget, TimeSpan.Zero) { }

        /// <param name="capturePeriod">The capture packet length: prefill and the hard trim aim at the target plus one packet.</param>
        public DriftController(TimeSpan startTarget, TimeSpan maxTarget, TimeSpan capturePeriod)
        {
            _maxTarget = maxTarget < startTarget ? startTarget : maxTarget;
            CapturePeriod = capturePeriod;
            Target = startTarget;
            Ratio = 1.0;
        }

        /// <summary>
        /// A controller for these streams: headroom starts at one output period plus the margin, and may rise until
        /// headroom plus one capture packet reaches <see cref="MaxBuffer"/>.
        /// </summary>
        public static DriftController ForStreams(TimeSpan capturePeriod, TimeSpan outputPeriod)
            => new DriftController(outputPeriod + HeadroomMargin, MaxBuffer - capturePeriod, capturePeriod);

        /// <summary>The headroom to hold.</summary>
        public TimeSpan Target { get; private set; }

        public TimeSpan CapturePeriod { get; }

        /// <summary>The buffer level right after a packet when headroom is on target: where prefill opens and a trim cuts to.</summary>
        public TimeSpan FullLevel => Target + CapturePeriod;

        /// <summary>Above 1 consumes faster, below 1 slower.</summary>
        public double Ratio { get; private set; }

        public int Trims { get; private set; }

        /// <param name="levelMs">The headroom: the lowest level just before a packet landed, over the last second.</param>
        public double Update(double levelMs, double elapsedMs)
        {
            if (!_haveLevel)
            {
                _ema = levelMs;
                _haveLevel = true;
            }
            else
            {
                _ema += (levelMs - _ema) * Math.Min(1.0, elapsedMs / SmoothingMs);
            }
            double error = _ema - Target.TotalMilliseconds;
            double proportional = 0;
            if (Math.Abs(error) > HoldBand.TotalMilliseconds)
            {
                proportional = error / FullScaleMs * MaxCorrection;
                _integral = Clamp(_integral + proportional * elapsedMs / IntegralTime.TotalMilliseconds);
            }
            Ratio = 1.0 + Clamp(proportional + _integral);
            return Ratio;
        }

        /// <summary>Above <see cref="HardTrimAbove"/>, the bytes to drop to get back to <see cref="FullLevel"/>.</summary>
        static double Clamp(double correction) => Math.Max(-MaxCorrection, Math.Min(MaxCorrection, correction));

        public int BytesToTrim(int bufferedBytes, int bytesPerSecond, int blockAlign)
        {
            if (bufferedBytes <= bytesPerSecond * HardTrimAbove.TotalSeconds) return 0;
            int trim = bufferedBytes - (int)(bytesPerSecond * FullLevel.TotalSeconds);
            if (blockAlign > 0) trim = trim / blockAlign * blockAlign;
            if (trim <= 0) return 0;
            Trims++;
            return trim;
        }

        public void Underrun()
        {
            Target = Target + Step > _maxTarget ? _maxTarget : Target + Step;
        }
    }
}
