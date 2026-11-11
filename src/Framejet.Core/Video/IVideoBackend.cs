using System;
using System.Collections.Generic;
using Framejet.Core.Devices;

namespace Framejet.Core.Video
{
    public sealed class VideoStartException : Exception
    {
        public VideoStartException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Every member is called and raised on the UI thread.</summary>
    public interface IVideoBackend
    {
        IReadOnlyList<VideoDeviceInfo> ListDevices();
        /// <summary>Raw modes. May throw <see cref="VideoStartException"/>.</summary>
        IReadOnlyList<VideoMode> ListModes(VideoDeviceInfo device);
        /// <summary>Throws <see cref="VideoStartException"/>.</summary>
        void Start(VideoDeviceInfo device, VideoMode mode);
        /// <summary>Idempotent.</summary>
        void Stop();
        /// <summary>Frames delivered since the last successful Start; 0 when stopped. Cheap: no locks, no allocation.</summary>
        long FramesArrived { get; }
        event Action DeviceLost;
        /// <summary>Another app opened the device and Windows gave it the card. The graph is dead; reopening takes the card back.</summary>
        event Action DeviceTaken;
    }
}
