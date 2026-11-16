using System;
using System.Globalization;

namespace Framejet.Core.Updates
{
    /// <summary>A parsed release version: 2-4 numbers plus an optional SemVer-style pre-release.</summary>
    public sealed class AppVersion : IComparable<AppVersion>
    {
        readonly int[] _numbers; // always 4 long, missing parts 0
        readonly string[] _pre;
        readonly string _shown;

        AppVersion(int[] numbers, string[] pre, string shown)
        { _numbers = numbers; _pre = pre; Prerelease = string.Join(".", pre); _shown = shown; }

        public string Prerelease { get; }

        public bool IsDev => _pre.Length > 0 && string.Equals(_pre[0], "dev", StringComparison.OrdinalIgnoreCase);

        public static bool TryParse(string text, out AppVersion version)
        {
            version = null;
            if (text == null) return false;
            text = text.Trim();
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text.Substring(1);
            int plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);
            string pre = "";
            int dash = text.IndexOf('-');
            if (dash >= 0) { pre = text.Substring(dash + 1); text = text.Substring(0, dash); if (pre.Length == 0) return false; }

            var parts = text.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            var nums = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!AllDigits(parts[i]) ||
                    !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
            }

            string[] ids = new string[0];
            if (pre.Length > 0)
            {
                ids = pre.Split('.');
                foreach (var id in ids)
                {
                    if (id.Length == 0) return false;
                    foreach (char c in id)
                        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-')) return false;
                }
            }

            version = new AppVersion(nums, ids, text + (pre.Length > 0 ? "-" + pre : ""));
            return true;
        }

        static bool AllDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        public int CompareTo(AppVersion other)
        {
            if (other == null) return 1;
            for (int i = 0; i < 4; i++)
            {
                int c = _numbers[i].CompareTo(other._numbers[i]);
                if (c != 0) return c;
            }
            if (_pre.Length == 0 && other._pre.Length == 0) return 0;
            if (_pre.Length == 0) return 1;
            if (other._pre.Length == 0) return -1;
            int n = Math.Min(_pre.Length, other._pre.Length);
            for (int i = 0; i < n; i++)
            {
                int c = CompareIdentifier(_pre[i], other._pre[i]);
                if (c != 0) return c;
            }
            return _pre.Length.CompareTo(other._pre.Length);
        }

        static int CompareIdentifier(string a, string b)
        {
            bool an = AllDigits(a), bn = AllDigits(b);
            if (an && bn)
            {
                string x = a.TrimStart('0'), y = b.TrimStart('0');
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                return Math.Sign(string.CompareOrdinal(x, y));
            }
            if (an) return -1;
            if (bn) return 1;
            return Math.Sign(string.CompareOrdinal(a, b));
        }

        public override string ToString() => _shown;
    }
}
