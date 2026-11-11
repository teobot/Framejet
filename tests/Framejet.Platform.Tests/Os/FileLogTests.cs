using System;
using System.IO;
using Framejet.Platform.Os;
using Xunit;

namespace Framejet.Platform.Tests.Os
{
    public class FileLogTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "fj-filelog-" + Guid.NewGuid().ToString("N"));
        string LogPath => Path.Combine(dir, "Framejet.log");
        string OldPath => Path.Combine(dir, "Framejet.old.log");
        public void Dispose() { try { Directory.Delete(dir, true); } catch (Exception) { } }

        static void Session(string path, string text) { using (var log = new FileLog(path)) log.Info(text); }

        [Fact] public void Opening_moves_the_previous_session_to_the_old_log()
        {
            Session(LogPath, "first");
            Session(LogPath, "second");
            Assert.Contains("first", File.ReadAllText(OldPath));
            Assert.DoesNotContain("first", File.ReadAllText(LogPath));
            Assert.Contains("second", File.ReadAllText(LogPath));
        }

        [Fact] public void A_third_session_replaces_the_old_log()
        {
            Session(LogPath, "first"); Session(LogPath, "second"); Session(LogPath, "third");
            Assert.Contains("second", File.ReadAllText(OldPath));
            Assert.DoesNotContain("first", File.ReadAllText(OldPath));
            Assert.Contains("third", File.ReadAllText(LogPath));
        }

        [Fact] public void The_first_ever_session_creates_only_the_log() { Session(LogPath, "first"); Assert.False(File.Exists(OldPath)); Assert.True(File.Exists(LogPath)); }

        [Fact] public void A_locked_old_log_does_not_stop_logging()
        {
            Session(LogPath, "first"); Session(LogPath, "second");
            using (new FileStream(OldPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Session(LogPath, "third");
            Assert.Contains("third", File.ReadAllText(LogPath));
        }
    }
}
