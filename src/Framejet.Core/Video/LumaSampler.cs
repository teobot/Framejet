using System;

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
                int index;
                switch (format.Format)
                {
                    case GrabberFormat.Yuy2: index = 2 * p; break;
                    case GrabberFormat.Uyvy: index = 2 * p + 1; break;
                    case GrabberFormat.Rgb32: index = 4 * p + 1; break;
                    default: index = p; break;
                }
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
    }
}
