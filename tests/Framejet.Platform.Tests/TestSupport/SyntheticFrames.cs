using System;
using Framejet.Core.Video;

namespace Framejet.Platform.Tests.TestSupport
{
    /// <summary>Generated capture frames of exactly <see cref="CaptureFormat.FrameBytes"/> bytes.</summary>
    internal static class SyntheticFrames
    {
        /// <summary>Every pixel the same. For Rgb32, y/u/v are B/G/R.</summary>
        public static byte[] Solid(CaptureFormat f, byte y, byte u, byte v)
            => Build(f, x => y, u, v, (y, u, v));

        /// <summary>Left half luma <paramref name="yLeft"/>, right half <paramref name="yRight"/>, chroma 128. Rgb32 gets grey levels.</summary>
        public static byte[] SplitLeftRight(CaptureFormat f, byte yLeft, byte yRight)
        {
            int half = f.Width / 2;
            return Build(f, x => x < half ? yLeft : yRight, 128, 128, null);
        }

        /// <summary>Rgb32 only: the first N rows in memory are red (B0 G0 R255), the rest blue (B255 G0 R0).</summary>
        public static byte[] RowsTopBottom(CaptureFormat f, int firstRowsInMemoryRed)
        {
            if (f.Format != GrabberFormat.Rgb32) throw new ArgumentException("RowsTopBottom needs an Rgb32 format", nameof(f));
            var frame = new byte[f.FrameBytes];
            int stride = f.Width * 4;
            for (int row = 0; row < f.Height; row++)
            {
                bool red = row < firstRowsInMemoryRed;
                for (int i = row * stride; i < (row + 1) * stride; i += 4)
                {
                    frame[i] = red ? (byte)0 : (byte)255;
                    frame[i + 1] = 0;
                    frame[i + 2] = red ? (byte)255 : (byte)0;
                    frame[i + 3] = 255;
                }
            }
            return frame;
        }

        /// <summary>
        /// Fills a frame from a luma value per column and constant chroma. For Rgb32, <paramref name="bgr"/>
        /// gives every pixel's B/G/R, or when null each pixel is grey at its luma value.
        /// </summary>
        static byte[] Build(CaptureFormat f, Func<int, byte> lumaAt, byte u, byte v, (byte B, byte G, byte R)? bgr)
        {
            var frame = new byte[f.FrameBytes];
            int w = f.Width, h = f.Height;
            switch (f.Format)
            {
                case GrabberFormat.Nv12:
                    for (int row = 0; row < h; row++)
                        for (int x = 0; x < w; x++) frame[row * w + x] = lumaAt(x);
                    for (int i = w * h; i < frame.Length; i += 2) { frame[i] = u; frame[i + 1] = v; }
                    break;
                case GrabberFormat.Yuy2:
                    for (int row = 0; row < h; row++)
                        for (int x = 0; x < w; x += 2)
                        {
                            int i = (row * w + x) * 2;
                            frame[i] = lumaAt(x); frame[i + 1] = u; frame[i + 2] = lumaAt(x + 1); frame[i + 3] = v;
                        }
                    break;
                case GrabberFormat.Uyvy:
                    for (int row = 0; row < h; row++)
                        for (int x = 0; x < w; x += 2)
                        {
                            int i = (row * w + x) * 2;
                            frame[i] = u; frame[i + 1] = lumaAt(x); frame[i + 2] = v; frame[i + 3] = lumaAt(x + 1);
                        }
                    break;
                case GrabberFormat.Rgb32:
                    for (int row = 0; row < h; row++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = (row * w + x) * 4;
                            byte grey = lumaAt(x);
                            frame[i] = bgr?.B ?? grey; frame[i + 1] = bgr?.G ?? grey; frame[i + 2] = bgr?.R ?? grey; frame[i + 3] = 255;
                        }
                    break;
                default:
                    throw new ArgumentException("Unknown format " + f.Format, nameof(f));
            }
            return frame;
        }
    }
}
