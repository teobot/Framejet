using System;
using System.Collections.Generic;
using System.Drawing;

namespace Framejet.Core.Video
{
    public enum ScalingMode { Fill, WholeStepsCrisp, WholeStepsSmooth, OneToOne }

    public enum ScaleFilter { Bilinear, CatmullRom, Point }

    public static class Scaling
    {
        public static readonly IReadOnlyList<ScalingMode> All = new[]
        {
            ScalingMode.Fill, ScalingMode.WholeStepsCrisp, ScalingMode.WholeStepsSmooth, ScalingMode.OneToOne,
        };

        /// <summary>Where the picture goes in the target. The box may be larger than the target (1:1 crops, centred).</summary>
        public static Rectangle Place(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, ScalingMode mode)
        {
            switch (mode)
            {
                case ScalingMode.WholeStepsCrisp:
                case ScalingMode.WholeStepsSmooth:
                    int n = Math.Min(targetWidth / sourceWidth, targetHeight / sourceHeight);
                    if (n < 1) return Letterbox.Fit(sourceWidth, sourceHeight, targetWidth, targetHeight);
                    int w = sourceWidth * n, h = sourceHeight * n;
                    return new Rectangle((targetWidth - w) / 2, (targetHeight - h) / 2, w, h);
                case ScalingMode.OneToOne:
                    return new Rectangle((targetWidth - sourceWidth) / 2, (targetHeight - sourceHeight) / 2, sourceWidth, sourceHeight);
                default:
                    return Letterbox.Fit(sourceWidth, sourceHeight, targetWidth, targetHeight);
            }
        }

        /// <summary>Point only for crisp upscaling, Catmull-Rom for other upscaling, bilinear otherwise.</summary>
        public static ScaleFilter Filter(ScalingMode mode, Rectangle box, int sourceWidth, int sourceHeight)
        {
            if (box.Width > sourceWidth || box.Height > sourceHeight)
                return mode == ScalingMode.WholeStepsCrisp ? ScaleFilter.Point : ScaleFilter.CatmullRom;
            return ScaleFilter.Bilinear;
        }

        public static string Key(ScalingMode mode)
        {
            switch (mode)
            {
                case ScalingMode.WholeStepsCrisp: return "crisp";
                case ScalingMode.WholeStepsSmooth: return "smooth";
                case ScalingMode.OneToOne: return "1to1";
                default: return "fill";
            }
        }

        public static ScalingMode Parse(string key)
        {
            foreach (var mode in All)
                if (string.Equals(Key(mode), key, StringComparison.OrdinalIgnoreCase)) return mode;
            return ScalingMode.Fill;
        }
    }
}
