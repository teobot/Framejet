using System;

namespace Framejet.Core.Video
{
    /// <summary>Size, pixel layout and orientation of frames delivered by the capture graph.</summary>
    public sealed class CaptureFormat
    {
        public CaptureFormat(int width, int height, GrabberFormat format, bool bottomUp)
        {
            Width = width; Height = height; Format = format; BottomUp = bottomUp;
        }

        public int Width { get; }
        public int Height { get; }
        public GrabberFormat Format { get; }
        public bool BottomUp { get; }

        public int FrameBytes
        {
            get
            {
                switch (Format)
                {
                    case GrabberFormat.Nv12: return Width * Height * 3 / 2;
                    case GrabberFormat.Yuy2:
                    case GrabberFormat.Uyvy: return Width * Height * 2;
                    case GrabberFormat.Rgb32: return Width * Height * 4;
                    default: throw new InvalidOperationException("Unknown format " + Format);
                }
            }
        }
    }
}
