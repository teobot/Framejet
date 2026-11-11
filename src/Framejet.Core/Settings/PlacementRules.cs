using System;
using System.Collections.Generic;
using System.Drawing;

namespace Framejet.Core.Settings
{
    public sealed class ScreenInfo
    {
        public string Name { get; }
        public Rectangle Bounds { get; }
        public Rectangle WorkingArea { get; }
        public bool Primary { get; }

        public ScreenInfo(string name, Rectangle bounds, Rectangle workingArea, bool primary)
        {
            Name = name; Bounds = bounds; WorkingArea = workingArea; Primary = primary;
        }
    }

    public sealed class PlacementDecision
    {
        public Rectangle Bounds { get; set; }
        public bool Maximized { get; set; }
        public bool Fullscreen { get; set; }
        public ScreenInfo FullscreenScreen { get; set; }
    }

    public static class PlacementRules
    {
        public static readonly Size DefaultSize = new Size(1280, 720);

        public static bool IsUsable(Rectangle bounds, IReadOnlyList<ScreenInfo> screens)
        {
            var p = new Point(bounds.X + bounds.Width / 2, bounds.Y + 40);
            foreach (var s in screens)
                if (s.WorkingArea.Contains(p)) return true;
            return false;
        }

        public static PlacementDecision Restore(WindowPlacement saved, IReadOnlyList<ScreenInfo> screens)
        {
            var d = new PlacementDecision { Maximized = saved.Maximized };

            if (saved.HasBounds && IsUsable(saved.Bounds, screens))
            {
                d.Bounds = saved.Bounds;
            }
            else if (saved.HasBounds && ScreenToClampInto(saved, screens) is ScreenInfo target)
            {
                d.Bounds = ClampInto(saved.Bounds, target.WorkingArea);
            }
            else
            {
                var wa = Primary(screens).WorkingArea;
                d.Bounds = new Rectangle(
                    wa.X + (wa.Width - DefaultSize.Width) / 2,
                    wa.Y + (wa.Height - DefaultSize.Height) / 2,
                    DefaultSize.Width, DefaultSize.Height);
            }

            if (saved.Fullscreen && !string.IsNullOrEmpty(saved.ScreenName))
            {
                foreach (var s in screens)
                {
                    if (s.Name == saved.ScreenName) { d.Fullscreen = true; d.FullscreenScreen = s; break; }
                }
            }
            return d;
        }

        static ScreenInfo ScreenToClampInto(WindowPlacement saved, IReadOnlyList<ScreenInfo> screens)
        {
            if (!string.IsNullOrEmpty(saved.ScreenName))
                foreach (var s in screens)
                    if (s.Name == saved.ScreenName) return s;

            ScreenInfo best = null;
            long bestArea = 0;
            foreach (var s in screens)
            {
                var o = Rectangle.Intersect(s.Bounds, saved.Bounds);
                long area = (long)Math.Max(0, o.Width) * Math.Max(0, o.Height);
                if (area > bestArea) { bestArea = area; best = s; }
            }
            return best;
        }

        static Rectangle ClampInto(Rectangle r, Rectangle area)
        {
            int w = Math.Min(r.Width, area.Width);
            int h = Math.Min(r.Height, area.Height);
            int x = Math.Max(area.Left, Math.Min(r.X, area.Right - w));
            int y = Math.Max(area.Top, Math.Min(r.Y, area.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        public static ScreenInfo ScreenFor(Rectangle windowBounds, IReadOnlyList<ScreenInfo> screens)
        {
            var centre = new Point(windowBounds.X + windowBounds.Width / 2, windowBounds.Y + windowBounds.Height / 2);
            foreach (var s in screens)
                if (s.Bounds.Contains(centre)) return s;

            ScreenInfo best = null;
            long bestArea = 0;
            foreach (var s in screens)
            {
                var o = Rectangle.Intersect(s.Bounds, windowBounds);
                long area = (long)Math.Max(0, o.Width) * Math.Max(0, o.Height);
                if (area > bestArea) { bestArea = area; best = s; }
            }
            return best ?? Primary(screens);
        }

        static ScreenInfo Primary(IReadOnlyList<ScreenInfo> screens)
        {
            foreach (var s in screens)
                if (s.Primary) return s;
            return screens[0];
        }
    }
}
