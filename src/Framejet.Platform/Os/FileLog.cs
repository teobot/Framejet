using System;
using System.Globalization;
using System.IO;
using System.Text;
using Framejet.Core;

namespace Framejet.Platform.Os
{
    /// <summary>
    /// The log of this session, with the one before it kept beside it: on open an existing log moves to
    /// <c>Framejet.old.log</c> (replacing any older one), then each entry is one line such as
    /// <c>2026-11-09T19:33:15.123 INFO message</c> or <c>2026-11-09T19:33:15.123 ERROR message: exception</c>.
    /// Usable from any thread. Once the file holds <see cref="MaxBytes"/>, it writes one <c>log full</c> line and
    /// stops. Logging never throws: if the file can't be opened or written, entries are dropped.
    /// </summary>
    public sealed class FileLog : ILog, IDisposable
    {
        public const int MaxBytes = 524288;

        static readonly Encoding Utf8 = new UTF8Encoding(false);

        readonly object _sync = new object();
        StreamWriter _writer;
        long _bytes;
        bool _full;

        public FileLog(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Rotate(path);
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, Utf8) { AutoFlush = true };
            }
            catch (Exception)
            {
                _writer = null; // no log is better than no app
            }
        }

        /// <summary>The name the previous session's log is kept under: <c>Framejet.log</c> becomes <c>Framejet.old.log</c>.</summary>
        public static string OldLogPath(string path)
            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path) + ".old" + Path.GetExtension(path));

        static void Rotate(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var old = OldLogPath(path);
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            catch (Exception)
            {
                // Keeping the old log is a courtesy: the new log still replaces it below.
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
                    if (_bytes + bytes > MaxBytes)
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
            => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " " + text + "\r\n";

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
