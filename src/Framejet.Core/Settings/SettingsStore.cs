using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Framejet.Core.Settings
{
    public sealed class SettingsStore
    {
        public string Path { get; }

        /// <summary>The settings folder's name under %AppData%.</summary>
        public const string FolderName = "Framejet";

        public SettingsStore(string path) { Path = path; }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName, "settings.ini");

        public AppSettings Load()
        {
            var s = new AppSettings();
            string[] lines;
            try { lines = File.ReadAllLines(Path, Encoding.UTF8); }
            catch (IOException) { return s; }
            catch (UnauthorizedAccessException) { return s; }

            int? x = null, y = null, w = null, h = null;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "video.path": s.VideoDevicePath = value; break;
                    case "video.name": s.VideoDeviceName = value; break;
                    case "mode": s.ModeKey = value; break;
                    case "audio.output.id": s.AudioOutputId = value; break;
                    case "audio.output.name": s.AudioOutputName = value; break;
                    case "audio.input.id": s.AudioInputId = value; break;
                    case "audio.input.name": s.AudioInputName = value; break;
                    case "volume":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !float.IsNaN(v) && !float.IsInfinity(v))
                            s.Volume = Math.Max(0f, Math.Min(1f, v));
                        break;
                    case "muted": if (bool.TryParse(value, out var m)) s.Muted = m; break;
                    case "vsync": if (bool.TryParse(value, out var vs)) s.VSync = vs; break;
                    case "window.x": x = ParseInt(value); break;
                    case "window.y": y = ParseInt(value); break;
                    case "window.width": w = ParseInt(value); break;
                    case "window.height": h = ParseInt(value); break;
                    case "window.maximized": if (bool.TryParse(value, out var mx)) s.Window.Maximized = mx; break;
                    case "window.fullscreen": if (bool.TryParse(value, out var fs)) s.Window.Fullscreen = fs; break;
                    case "window.screen": s.Window.ScreenName = value; break;
                }
            }

            if (x.HasValue && y.HasValue && w.HasValue && h.HasValue && w.Value > 0 && h.Value > 0)
            {
                s.Window.X = x.Value; s.Window.Y = y.Value; s.Window.Width = w.Value; s.Window.Height = h.Value;
                s.Window.HasBounds = true;
            }
            return s;
        }

        public void Save(AppSettings s)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            void Line(string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
            string B(bool b) => b ? "true" : "false";

            Line("video.path", s.VideoDevicePath);
            Line("video.name", s.VideoDeviceName);
            Line("mode", s.ModeKey);
            Line("audio.output.id", s.AudioOutputId);
            Line("audio.output.name", s.AudioOutputName);
            Line("audio.input.id", s.AudioInputId);
            Line("audio.input.name", s.AudioInputName);
            Line("volume", s.Volume.ToString("0.###", inv));
            Line("muted", B(s.Muted));
            Line("vsync", B(s.VSync));
            if (s.Window.HasBounds)
            {
                Line("window.x", s.Window.X.ToString(inv));
                Line("window.y", s.Window.Y.ToString(inv));
                Line("window.width", s.Window.Width.ToString(inv));
                Line("window.height", s.Window.Height.ToString(inv));
            }
            Line("window.maximized", B(s.Window.Maximized));
            Line("window.fullscreen", B(s.Window.Fullscreen));
            Line("window.screen", s.Window.ScreenName);

            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(Path)) File.Replace(tmp, Path, null);
            else File.Move(tmp, Path);
        }

        static int? ParseInt(string v) =>
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (int?)null;
    }
}
