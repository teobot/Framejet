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

        [Fact] public void The_profile_folder_is_written_as_a_placeholder()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Session(LogPath, "Settings " + Path.Combine(profile, "settings.ini"));
            FileLog.AppendError(LogPath, "Failed at " + Path.GetTempPath(), null); // the temp folder's 8.3 spelling, on most PCs
            var text = File.ReadAllText(LogPath);
            Assert.Contains(@"Settings %USERPROFILE%\settings.ini", text);
            Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName + @"\", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact] public void Keeping_four_old_logs_keeps_five_sessions()
        {
            foreach (var text in new[] { "first", "second", "third", "fourth", "fifth", "sixth" })
                using (var log = new FileLog(LogPath, keepOld: 4)) log.Info(text);
            Assert.Contains("sixth", File.ReadAllText(LogPath));
            Assert.Contains("fifth", File.ReadAllText(OldPath));
            Assert.Contains("fourth", File.ReadAllText(Path.Combine(dir, "Framejet.old2.log")));
            Assert.Contains("third", File.ReadAllText(Path.Combine(dir, "Framejet.old3.log")));
            Assert.Contains("second", File.ReadAllText(Path.Combine(dir, "Framejet.old4.log")));
            Assert.False(File.Exists(Path.Combine(dir, "Framejet.old5.log")));
            Assert.Equal(5, Directory.GetFiles(dir, "Framejet*.log").Length);
        }

        [Fact] public void By_default_only_one_old_log_is_kept()
        {
            Session(LogPath, "first"); Session(LogPath, "second"); Session(LogPath, "third");
            Assert.False(File.Exists(Path.Combine(dir, "Framejet.old2.log")));
        }

        [Fact] public void The_size_limit_is_an_option()
        {
            using (var log = new FileLog(LogPath, maxBytes: 200))
                for (int i = 0; i < 10; i++) log.Info(new string('x', 20));
            var text = File.ReadAllText(LogPath);
            Assert.True(new FileInfo(LogPath).Length < 260, "the log grew past its limit");
            Assert.EndsWith("INFO log full" + Environment.NewLine, text);
        }

        [Fact] public void A_locked_old_log_does_not_stop_logging()
        {
            Session(LogPath, "first"); Session(LogPath, "second");
            using (new FileStream(OldPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Session(LogPath, "third");
            Assert.Contains("third", File.ReadAllText(LogPath));
        }
    }
}
