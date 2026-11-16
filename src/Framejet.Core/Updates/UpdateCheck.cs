using System;

namespace Framejet.Core.Updates
{
    public sealed class UpdateOffer
    {
        public UpdateOffer(string version, string pageUrl) { Version = version; PageUrl = pageUrl; }
        public string Version { get; }
        public string PageUrl { get; }
    }

    /// <summary>When to look for a newer release, and whether a fetched release is worth offering.</summary>
    public static class UpdateCheck
    {
        public const string LatestReleaseApi = "https://api.github.com/repos/teobot/Framejet/releases/latest";
        public const string ReleasePagePrefix = "https://github.com/teobot/Framejet/releases/";
        public const string ExeAsset = "Framejet.exe";
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10), Timeout = TimeSpan.FromSeconds(10);

        public static bool ShouldCheck(string runningVersion, bool enabled, bool selfTesting)
            => enabled && !selfTesting && AppVersion.TryParse(runningVersion, out var v) && !v.IsDev;

        public static UpdateOffer Evaluate(string runningVersion, ReleaseInfo latest)
        {
            if (latest == null) return null;
            if (!AppVersion.TryParse(runningVersion, out var running)) return null;
            if (!AppVersion.TryParse(latest.Tag, out var tag)) return null;
            if (tag.CompareTo(running) <= 0) return null;
            if (latest.PageUrl == null || !latest.PageUrl.StartsWith(ReleasePagePrefix, StringComparison.Ordinal)) return null;
            bool hasExe = false;
            foreach (var n in latest.AssetNames)
                if (string.Equals(n, ExeAsset, StringComparison.OrdinalIgnoreCase)) { hasExe = true; break; }
            return hasExe ? new UpdateOffer(tag.ToString(), latest.PageUrl) : null;
        }

        public static string UserAgent(string runningVersion)
            => AppVersion.TryParse(runningVersion, out var v) ? "Framejet/" + v : "Framejet";
    }
}
