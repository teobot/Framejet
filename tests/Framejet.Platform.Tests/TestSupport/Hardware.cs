using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Framejet.Core.Devices;
using Framejet.Platform.Video;
using Xunit;

namespace Framejet.Platform.Tests.TestSupport
{
    internal static class Hardware
    {
        private const string FileName = "hardware.local.ini";
        private static readonly Lazy<IReadOnlyDictionary<string, string>> File = new Lazy<IReadOnlyDictionary<string, string>>(Load);

        public static VideoDeviceInfo Ripsaw() => Find("Ripsaw");
        public static VideoDeviceInfo Webcam() => Find(Require("webcam"));
        public static Guid RipsawContainer() => Guid.Parse(Require("ripsaw.container"));
        public static Guid WebcamContainer() => Guid.Parse(Require("webcam.container"));
        public static string Microphone() => Get("microphone");

        internal static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                if (key.Length == 0 || value.Length == 0) continue;
                keys[key] = value;
            }
            return keys;
        }

        private static IReadOnlyDictionary<string, string> Load()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            return Parse(System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path) : new string[0]);
        }

        private static string Get(string key) => File.Value.TryGetValue(key, out var value) ? value : null;

        private static string Require(string key)
        {
            var value = Get(key);
            Skip.If(value == null, key + " isn't set in hardware.local.ini (see hardware.example.ini)");
            return value;
        }

        private static VideoDeviceInfo Find(string fragment)
        {
            var device = DirectShowDevices.List()
                .FirstOrDefault(d => d.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
            Skip.If(device == null, fragment + " not connected");
            return device;
        }
    }
}
