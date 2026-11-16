using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Framejet.Core.Video;

namespace Framejet.Core.Ui
{
    public static class StatusText
    {
        const string OneToOne = "1:1";

        /// <summary>"1440p · 59.9 fps · 1:1", then any warnings. The scale is left out when there is no area to draw on.</summary>
        public static string Format(VideoMode mode, double measuredFps, Size pictureArea, IEnumerable<string> warnings, ScalingMode scaling = ScalingMode.Fill)
        {
            var parts = new List<string>();
            if (mode != null)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}p · {1:0.0} fps", mode.Height, measuredFps));
                var scale = Scale(mode.Width, mode.Height, pictureArea, scaling);
                if (scale != null) parts.Add(scale);
            }
            if (warnings != null) parts.AddRange(warnings);
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// How a frame is drawn placed in an area by the scaling mode, as the renderer draws it: "1:1" when pixel for pixel, otherwise its
        /// size as a percentage of the frame's ("95%" for 1440p in a maximized window on a 1440p screen). A scaled picture
        /// never reads "100%". Null when there is no frame or no area.
        /// </summary>
        public static string Scale(int frameWidth, int frameHeight, Size area, ScalingMode scaling = ScalingMode.Fill)
        {
            if (frameWidth <= 0 || frameHeight <= 0 || area.Width <= 0 || area.Height <= 0) return null;
            var box = Scaling.Place(frameWidth, frameHeight, area.Width, area.Height, scaling);
            if (box.Width == frameWidth && box.Height == frameHeight) return OneToOne;
            var percent = (int)Math.Round(100.0 * box.Width / frameWidth, MidpointRounding.AwayFromZero);
            if (percent == 100) percent = box.Width < frameWidth || box.Height < frameHeight ? 99 : 101;
            return percent.ToString(CultureInfo.InvariantCulture) + "%";
        }
    }
}
