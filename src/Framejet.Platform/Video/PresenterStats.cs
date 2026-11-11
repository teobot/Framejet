using Framejet.Core.Video;

namespace Framejet.Platform.Video
{
    /// <summary>A snapshot of the presenter's counters and timings since it started or was last reset.</summary>
    public sealed class PresenterStats
    {
        /// <summary>Frames published by capture.</summary>
        public long Arrived { get; set; }
        /// <summary>Frames presented.</summary>
        public long Drawn { get; set; }
        /// <summary>Frames replaced by a newer one before drawing, or skipped (paused, no window area, device lost).</summary>
        public long Dropped { get; set; }
        /// <summary>Frames drawn per second over the last second: the intervals between them over the time they span (59.94, not 60).</summary>
        public double MeasuredFps { get; set; }
        /// <summary>Display refresh rate from the vblank counter; 0 until measured.</summary>
        public double RefreshHz { get; set; }
        /// <summary>Frame arrival to the Present call; the last 2,000 frames.</summary>
        public TimingStats ArrivalToPresent { get; set; } = TimingStats.Empty;
        /// <summary>Present call to on screen, from exact DXGI samples while measuring; tearing presents are left out.</summary>
        public TimingStats PresentToScreen { get; set; } = TimingStats.Empty;
        /// <summary>Presents shown by direct scanout (overlay) while measuring.</summary>
        public int Overlay { get; set; }
        /// <summary>Presents composed by the desktop compositor while measuring.</summary>
        public int Composed { get; set; }
        /// <summary>Times the Direct3D device was lost and recreated.</summary>
        public int DeviceResets { get; set; }
        /// <summary>Frames since Start whose buffer was not <see cref="ExpectedFrameBytes"/> long; never reset. Such a frame draws a shifted picture.</summary>
        public int SizeMismatches { get; set; }
        /// <summary>The bytes a frame of the running format has.</summary>
        public int ExpectedFrameBytes { get; set; }
        /// <summary>The length of the first mismatched buffer; 0 when there was none.</summary>
        public int MismatchedFrameBytes { get; set; }
    }
}
