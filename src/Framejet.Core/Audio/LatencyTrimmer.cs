using System;

namespace Framejet.Core.Audio
{
    public sealed class LatencyTrimmer
    {
        public static readonly TimeSpan DefaultTarget = TimeSpan.FromMilliseconds(15);
        public static readonly TimeSpan DefaultTrimAbove = TimeSpan.FromMilliseconds(60);

        readonly TimeSpan _target;
        readonly TimeSpan _trimAbove;

        public LatencyTrimmer(TimeSpan target, TimeSpan trimAbove)
        {
            _target = target;
            _trimAbove = trimAbove;
        }

        public int Trims { get; private set; }

        public int BytesToDrop(int bufferedBytes, int averageBytesPerSecond, int blockAlign)
        {
            if (bufferedBytes <= averageBytesPerSecond * _trimAbove.TotalSeconds) return 0;
            int drop = bufferedBytes - (int)(averageBytesPerSecond * _target.TotalSeconds);
            if (blockAlign > 0) drop = drop / blockAlign * blockAlign;
            if (drop <= 0) return 0;
            Trims++;
            return drop;
        }
    }
}
