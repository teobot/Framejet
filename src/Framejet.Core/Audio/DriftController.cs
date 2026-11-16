using System;

namespace Framejet.Core.Audio
{
    /// <summary>
    /// Steers the playback rate by up to +-0.5% so the playthrough buffer holds a small target,
    /// instead of letting capture-card and output clock drift build up and then cutting audio.
    /// </summary>
    public sealed class DriftController
    {
        public static readonly TimeSpan StartTarget = TimeSpan.FromMilliseconds(10);
        public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);
        public static readonly TimeSpan MaxTarget = TimeSpan.FromMilliseconds(30);
        public static readonly TimeSpan HardTrimAbove = TimeSpan.FromMilliseconds(80);
        public const double MaxCorrection = 0.005;

        /// <summary>
        /// Within this of the target the ratio is exactly 1. The level is taken right after a packet lands, so it can't
        /// fall below one packet; at a 10 ms target with 10 ms packets that floor is the target itself, and the
        /// slightest speed-up there starves a later read. Outside the band the correction is the full proportional one.
        /// </summary>
        public static readonly TimeSpan HoldBand = TimeSpan.FromTicks(5000); // 0.5 ms; FromMilliseconds rounds to whole ms on .NET Framework

        // A level this far from the target gives the full correction.
        const double FullScaleMs = 20.0;

        double _ema;
        bool _haveLevel;

        public DriftController()
        {
            Target = StartTarget;
            Ratio = 1.0;
        }

        public TimeSpan Target { get; private set; }

        /// <summary>Above 1 consumes faster, below 1 slower.</summary>
        public double Ratio { get; private set; }

        public int Trims { get; private set; }

        /// <param name="levelMs">The buffer level the target applies to (the caller says which, e.g. the lowest level right after a packet).</param>
        public double Update(double levelMs, double elapsedMs)
        {
            if (!_haveLevel)
            {
                _ema = levelMs;
                _haveLevel = true;
            }
            else
            {
                _ema += (levelMs - _ema) * Math.Min(1.0, elapsedMs / 1000.0);
            }
            double error = _ema - Target.TotalMilliseconds;
            if (Math.Abs(error) <= HoldBand.TotalMilliseconds)
            {
                Ratio = 1.0;
                return Ratio;
            }
            double correction = error / FullScaleMs * MaxCorrection;
            Ratio = 1.0 + Math.Max(-MaxCorrection, Math.Min(MaxCorrection, correction));
            return Ratio;
        }

        public int BytesToTrim(int bufferedBytes, int bytesPerSecond, int blockAlign)
        {
            if (bufferedBytes <= bytesPerSecond * HardTrimAbove.TotalSeconds) return 0;
            int trim = bufferedBytes - (int)(bytesPerSecond * Target.TotalSeconds);
            if (blockAlign > 0) trim = trim / blockAlign * blockAlign;
            if (trim <= 0) return 0;
            Trims++;
            return trim;
        }

        public void Underrun()
        {
            Target = Target + Step > MaxTarget ? MaxTarget : Target + Step;
        }
    }
}
