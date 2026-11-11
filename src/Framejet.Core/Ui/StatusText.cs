using System.Collections.Generic;
using System.Globalization;
using Framejet.Core.Video;

namespace Framejet.Core.Ui
{
    public static class StatusText
    {
        public static string Format(VideoMode mode, double measuredFps, IEnumerable<string> warnings)
        {
            var parts = new List<string>();
            if (mode != null)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}p · {1:0.0} fps", mode.Height, measuredFps));
            if (warnings != null) parts.AddRange(warnings);
            return string.Join(" · ", parts);
        }
    }
}
