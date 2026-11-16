using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Framejet.Core
{
    /// <summary>
    /// Keeps the Windows user name out of the log, which people paste into bug reports: every occurrence of the
    /// user's profile folder (in any of its spellings, such as the long and the 8.3 short form) becomes
    /// <c>%USERPROFILE%</c>. Matching ignores case and stops at a name boundary, so a profile of <c>C:\Users\a</c>
    /// leaves <c>C:\Users\abc</c> alone.
    /// </summary>
    public static class LogScrub
    {
        public const string Placeholder = "%USERPROFILE%";

        public static string Scrub(string text, IEnumerable<string> profilePaths)
        {
            if (string.IsNullOrEmpty(text) || profilePaths == null) return text;
            // Longest first, so a longer spelling is replaced whole before a shorter one could match inside it.
            foreach (var profile in profilePaths
                         .Where(p => !string.IsNullOrEmpty(p))
                         .Select(p => p.TrimEnd('\\', '/'))
                         .Where(p => p.Length > 3) // never a bare drive such as C:\
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(p => p.Length))
                text = ReplaceAll(text, profile);
            return text;
        }

        static string ReplaceAll(string text, string profile)
        {
            int at = text.IndexOf(profile, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return text;
            var sb = new StringBuilder(text.Length);
            int from = 0;
            while (at >= 0)
            {
                int end = at + profile.Length;
                if (end == text.Length || !IsNameChar(text[end]))
                {
                    sb.Append(text, from, at - from).Append(Placeholder);
                    from = end;
                    at = text.IndexOf(profile, end, StringComparison.OrdinalIgnoreCase);
                }
                else at = text.IndexOf(profile, at + 1, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(text, from, text.Length - from);
            return sb.ToString();
        }

        static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '~' || c == '$';
    }
}
