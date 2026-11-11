using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Framejet.Core;
using Framejet.Core.Settings;
using Framejet.Platform.Os;

namespace Framejet
{
    /// <summary>
    /// The last line of defence: an exception nothing else caught goes to the log instead of WinForms' dialog.
    /// In a normal run, a UI exception is logged and the app keeps running. A self-test fails at once: it writes a
    /// FAIL report and exits with code 1, without any dialog.
    /// </summary>
    static class UnhandledExceptions
    {
        static readonly object Sync = new object();
        static string _fallbackLogPath;
        static string _selfTestReport;
        static ILog _log;
        static bool _reported;

        /// <summary>The app's log while it is open; null before it opens and after it closes, when a file next to the settings is used.</summary>
        public static ILog Log
        {
            get => Volatile.Read(ref _log);
            set => Volatile.Write(ref _log, value);
        }

        /// <summary>Call on the UI thread before any window exists.</summary>
        public static void Install(CommandLine cmd)
        {
            _selfTestReport = cmd.SelfTestReport;
            try
            {
                _fallbackLogPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cmd.SettingsPath ?? SettingsStore.DefaultPath)), "Framejet.log");
            }
            catch (Exception)
            {
                _fallbackLogPath = null; // an unusable --settings path: there is no file to fall back to
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Handle("Unhandled exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Handle("Unhandled exception, Framejet is closing", e.ExceptionObject as Exception);
        }

        /// <summary>Raised on the UI thread for ThreadException, and on the failing thread for UnhandledException.</summary>
        static void Handle(string what, Exception error)
        {
            Record(what, error);
            if (_selfTestReport == null) return; // a UI exception is logged and the app runs on; any other ends the process anyway
            lock (Sync)
            {
                if (!_reported)
                {
                    _reported = true;
                    var message = error == null ? "unknown error" : error.Message.Replace("\r", " ").Replace("\n", " ");
                    SelfTestRunner.WriteFailure(_selfTestReport, "unhandled exception: " + message);
                }
            }
            Environment.Exit(1);
        }

        static void Record(string what, Exception error)
        {
            try
            {
                var log = Log;
                if (log != null) log.Error(what, error);
                else if (_fallbackLogPath != null) FileLog.AppendError(_fallbackLogPath, what, error);
            }
            catch (Exception)
            {
                // Nowhere left to report it.
            }
        }
    }
}
