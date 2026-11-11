using System;

namespace Framejet.Core.Video
{
    /// <summary>Reports "no signal" once almost every sampled pixel has been darker than video black for <see cref="HoldTime"/>.</summary>
    public sealed class NoSignalDetector
    {
        public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(3);
        /// <summary>Share of samples at or below <see cref="LumaSampler.DarkLumaMax"/> that counts as a no-signal frame (the Ripsaw sends black plus a small logo).</summary>
        public const double DarkFractionMin = 0.95;

        TimeSpan? _darkSince;

        public bool NoSignal { get; private set; }

        /// <summary>Returns true when <see cref="NoSignal"/> changed.</summary>
        public bool Update(LumaStats stats, TimeSpan now)
        {
            bool previous = NoSignal;
            if (stats.DarkFraction >= DarkFractionMin)
            {
                if (_darkSince == null) _darkSince = now;
                if (now - _darkSince.Value >= HoldTime) NoSignal = true;
            }
            else
            {
                _darkSince = null;
                NoSignal = false;
            }
            return NoSignal != previous;
        }

        public void Reset() { _darkSince = null; NoSignal = false; }
    }
}
