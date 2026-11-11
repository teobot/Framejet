using System;
using System.Drawing;

namespace Framejet.Core.Video
{
    public static class Letterbox
    {
        /// <summary>Largest centred rectangle of the source aspect ratio that fits the target.</summary>
        public static Rectangle Fit(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            double scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
            int w = (int)Math.Round(sourceWidth * scale);
            int h = (int)Math.Round(sourceHeight * scale);
            return new Rectangle((targetWidth - w) / 2, (targetHeight - h) / 2, w, h);
        }
    }
}
