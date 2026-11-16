using System;
using System.Runtime.InteropServices;

namespace Framejet.Core.Video
{
    public readonly struct LumaStats
    {
        public LumaStats(double mean, double stdDev, double darkFraction) { Mean = mean; StdDev = stdDev; DarkFraction = darkFraction; }
        public double Mean { get; }
        public double StdDev { get; }
        /// <summary>Fraction of sampled luma values at or below <see cref="LumaSampler.DarkLumaMax"/>.</summary>
        public double DarkFraction { get; }
    }

    /// <summary>Cheap brightness statistics from a sparse sample of one luma byte every <see cref="Step"/> pixels.</summary>
    public static class LumaSampler
    {
        public const int Step = 97;
        /// <summary>Darker than video black (Y=16): only a card's own black reaches this.</summary>
        public const int DarkLumaMax = 10;

        public static LumaStats Sample(byte[] frame, CaptureFormat format)
        {
            int pixels = format.Width * format.Height;
            double sum = 0, sumSq = 0; int n = 0, dark = 0;
            for (int p = 0; p < pixels; p += Step)
            {
                int index = LumaIndex(p, format.Format);
                if (index >= frame.Length) break;
                double v = frame[index];
                if (frame[index] <= DarkLumaMax) dark++;
                sum += v; sumSq += v * v; n++;
            }
            if (n == 0) return new LumaStats(0, 0, 0);
            double mean = sum / n;
            double variance = Math.Max(0, sumSq / n - mean * mean);
            return new LumaStats(mean, Math.Sqrt(variance), (double)dark / n);
        }

        /// <summary>
        /// The mean of the same samples as <see cref="Sample"/>, read straight from a frame buffer of
        /// <paramref name="length"/> bytes; 0 when it holds none.
        /// </summary>
        public static double Mean(IntPtr frame, int length, CaptureFormat format)
        {
            int pixels = format.Width * format.Height;
            long sum = 0; int n = 0;
            for (int p = 0; p < pixels; p += Step)
            {
                int index = LumaIndex(p, format.Format);
                if (index >= length) break;
                sum += Marshal.ReadByte(frame, index);
                n++;
            }
            return n == 0 ? 0 : (double)sum / n;
        }

        /// <summary>The byte that holds pixel <paramref name="pixel"/>'s luma (green for RGB32).</summary>
        static int LumaIndex(int pixel, GrabberFormat format)
        {
            switch (format)
            {
                case GrabberFormat.Yuy2: return 2 * pixel;
                case GrabberFormat.Uyvy: return 2 * pixel + 1;
                case GrabberFormat.Rgb32: return 4 * pixel + 1;
                default: return pixel;
            }
        }
    }
}
