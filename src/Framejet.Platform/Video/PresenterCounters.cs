namespace Framejet.Platform.Video
{
    /// <summary>The presenter's running totals, read without copying any timing samples: cheap enough to poll every second.</summary>
    public struct PresenterCounters
    {
        /// <summary>Frames published by capture since Start.</summary>
        public long Arrived;
        /// <summary>Frames presented since Start or the last reset.</summary>
        public long Drawn;
        /// <summary>Frames replaced or skipped since Start or the last reset, as in <see cref="PresenterStats.Dropped"/>.</summary>
        public long Dropped;
        /// <summary>Times the Direct3D device was lost and recreated.</summary>
        public int DeviceResets;
        /// <summary>Frames drawn per second over the last second.</summary>
        public double Fps;
    }
}
