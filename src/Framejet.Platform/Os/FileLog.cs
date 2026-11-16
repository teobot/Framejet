using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Framejet.Core;

namespace Framejet.Platform.Os
{
    /// <summary>
    /// The log of this session, with the ones before it kept beside it: on open an existing log moves to
    /// <c>Framejet.old.log</c>, and with more than one kept the older ones move up to <c>Framejet.old2.log</c> and so
    /// on (the oldest is deleted). Then each entry is one line such as
    /// <c>2026-11-09T19:33:15.123 INFO message</c> or <c>2026-11-09T19:33:15.123 ERROR message: exception</c>.
    /// Usable from any thread. Once the file holds its size limit (<see cref="MaxBytes"/> by default), it writes one <c>log full</c> line and
    /// stops. Logging never throws: if the file can't be opened or written, entries are dropped. The user's profile
    /// folder is written as <c>%USERPROFILE%</c>, so a pasted log doesn't give away the Windows user name.
    /// </summary>
    public sealed class FileLog : ILog, IDisposable
    {
        public const int MaxBytes = 524288;

        static readonly Encoding Utf8 = new UTF8Encoding(false);

        readonly object _sync = new object();
        readonly int _maxBytes;
        StreamWriter _writer;
        long _bytes;
        bool _full;

        /// <param name="path">The log file.</param>
        /// <param name="keepOld">How many earlier sessions' logs to keep.</param>
        /// <param name="maxBytes">The size limit of this session's log.</param>
        public FileLog(string path, int keepOld = 1, int maxBytes = MaxBytes)
        {
            _maxBytes = maxBytes;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Rotate(path, Math.Max(1, keepOld));
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, Utf8) { AutoFlush = true };
            }
            catch (Exception)
            {
                _writer = null; // no log is better than no app
            }
        }

        /// <summary>
        /// The name the <paramref name="n"/>th session back is kept under: <c>Framejet.log</c> becomes
        /// <c>Framejet.old.log</c> for the previous session, <c>Framejet.old2.log</c> for the one before, and so on.
        /// </summary>
        public static string OldLogPath(string path, int n = 1)
            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),
                            Path.GetFileNameWithoutExtension(path) + ".old" + (n > 1 ? n.ToString(CultureInfo.InvariantCulture) : "") + Path.GetExtension(path));

        static void Rotate(string path, int keepOld)
        {
            // Keeping old logs is a courtesy: each step is best effort, and the new log still replaces the current one below.
            for (int n = keepOld; n >= 1; n--)
            {
                try
                {
                    var from = n == 1 ? path : OldLogPath(path, n - 1);
                    if (!File.Exists(from)) continue;
                    var to = OldLogPath(path, n);
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
                catch (Exception) { }
            }
        }

        public void Info(string message) => Write("INFO", message);

        public void Error(string message, Exception error = null) => Write("ERROR", ErrorText(message, error));

        /// <summary>
        /// Adds one ERROR line, formatted like the log's, to the end of the file at <paramref name="path"/> without
        /// emptying it: for when no FileLog has the file open. Never throws.
        /// </summary>
        public static void AppendError(string path, string message, Exception error)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, Line("ERROR", ErrorText(message, error)), Utf8);
            }
            catch (Exception)
            {
                // Nowhere left to report it.
            }
        }

        static string ErrorText(string message, Exception error) => error == null ? message : message + ": " + error;

        void Write(string level, string text)
        {
            lock (_sync)
            {
                if (_writer == null || _full) return;
                string line = Line(level, text);
                int bytes = Utf8.GetByteCount(line);
                try
                {
                    if (_bytes + bytes > _maxBytes)
                    {
                        _full = true;
                        _writer.Write(Line("INFO", "log full"));
                        return;
                    }
                    _writer.Write(line);
                    _bytes += bytes;
                }
                catch (Exception)
                {
                    // A full disk or a lost drive must not take the app down with it.
                }
            }
        }

        static string Line(string level, string text)
            => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " "
               + LogScrub.Scrub(text, ProfilePaths) + "\r\n";

        /// <summary>The user's profile folder, long and 8.3 short form, which <see cref="LogScrub"/> keeps out of the log.</summary>
        static readonly string[] ProfilePaths = FindProfilePaths();

        static string[] FindProfilePaths()
        {
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(profile)) return new string[0];
                var shortPath = new StringBuilder(260);
                int length = GetShortPathName(profile, shortPath, shortPath.Capacity);
                return length > 0 && length < shortPath.Capacity
                    ? new[] { profile, shortPath.ToString() }
                    : new[] { profile };
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

        public void Dispose()
        {
            lock (_sync)
            {
                var writer = _writer;
                _writer = null;
                try { writer?.Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
