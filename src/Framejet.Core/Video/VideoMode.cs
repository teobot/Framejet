using System;
using System.Globalization;

namespace Framejet.Core.Video
{
    public sealed class VideoMode : IEquatable<VideoMode>
    {
        public VideoMode(int width, int height, long frameInterval, PixelFormat format, Guid subtype = default)
        {
            Width = width;
            Height = height;
            FrameInterval = frameInterval;
            Format = format;
            Subtype = subtype;
        }

        public int Width { get; }
        public int Height { get; }
        /// <summary>In 100 ns units, like DirectShow's AvgTimePerFrame.</summary>
        public long FrameInterval { get; }
        public PixelFormat Format { get; }
        public Guid Subtype { get; }

        public double Fps => 1e7 / FrameInterval;
        public double RoundedFps => Math.Round(Fps, 2);

        public string Key => string.Format(CultureInfo.InvariantCulture, "{0}x{1}@{2:0.##}/{3}", Width, Height, RoundedFps, PixelFormats.Name(Format));
        public string Label => string.Format(CultureInfo.InvariantCulture, "{0}×{1} · {2:0.##} fps", Width, Height, RoundedFps);

        public bool Equals(VideoMode other)
            => other != null && Width == other.Width && Height == other.Height && RoundedFps == other.RoundedFps && Format == other.Format;

        public override bool Equals(object obj) => Equals(obj as VideoMode);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Width;
                h = h * 397 ^ Height;
                h = h * 397 ^ RoundedFps.GetHashCode();
                h = h * 397 ^ (int)Format;
                return h;
            }
        }

        public override string ToString() => Key;
    }
}
