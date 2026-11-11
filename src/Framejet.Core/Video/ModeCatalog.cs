using System.Collections.Generic;
using System.Linq;

namespace Framejet.Core.Video
{
    public static class ModeCatalog
    {
        public const double MinListedFps = 23.9;
        public const int MinListedLines = 480;

        public static IReadOnlyList<VideoMode> Build(IEnumerable<VideoMode> raw)
        {
            // One entry per size and rate; keep the best format (first seen on ties).
            var best = raw
                .GroupBy(m => (m.Width, m.Height, m.RoundedFps))
                .Select(g => g.Aggregate((a, b) => PixelFormats.Rank(b.Format) < PixelFormats.Rank(a.Format) ? b : a))
                .ToList();
            var listed = best.Where(m => m.RoundedFps >= MinListedFps && m.Height >= MinListedLines).ToList();
            if (listed.Count == 0) listed = best;
            return listed
                .OrderByDescending(m => (long)m.Width * m.Height)
                .ThenByDescending(m => m.RoundedFps)
                .ToList();
        }

        public static VideoMode PickDefault(IReadOnlyList<VideoMode> modes, int screenWidth, int screenHeight)
        {
            var fitting = modes.Where(m => m.Width <= screenWidth && m.Height <= screenHeight).ToList();
            var pool = fitting.Count > 0 ? fitting : modes;
            var tier = pool.Where(m => m.RoundedFps >= 59 && m.RoundedFps <= 61).ToList();
            if (tier.Count == 0) tier = pool.Where(m => m.RoundedFps >= 50).ToList();
            if (tier.Count == 0) tier = pool.ToList();
            return tier
                .OrderByDescending(m => (long)m.Width * m.Height)
                .ThenByDescending(m => m.RoundedFps)
                .FirstOrDefault();
        }

        public static VideoMode Resolve(IReadOnlyList<VideoMode> modes, string savedKey, int screenWidth, int screenHeight)
        {
            var exact = modes.FirstOrDefault(m => m.Key == savedKey);
            if (exact != null) return exact;
            // Saved format may be gone; same size and rate with whatever format is listed.
            if (!string.IsNullOrEmpty(savedKey))
            {
                var parts = savedKey.Split('/')[0].Split('@');
                var size = parts[0].Split('x');
                if (parts.Length == 2 && size.Length == 2
                    && int.TryParse(size[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var w)
                    && int.TryParse(size[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var h)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps))
                {
                    var same = modes.FirstOrDefault(m => m.Width == w && m.Height == h && m.RoundedFps == fps);
                    if (same != null) return same;
                }
            }
            return PickDefault(modes, screenWidth, screenHeight);
        }
    }
}
