using System;
using System.IO;
using Framejet.Core.Settings;
using Xunit;

namespace Framejet.Core.Tests.Settings
{
    public class SettingsCarryOverTests : IDisposable
    {
        readonly string tmp = Path.Combine(Path.GetTempPath(), "fj-tests-" + Guid.NewGuid().ToString("N"));
        string OldDir => Path.Combine(tmp, "PlayThrough");
        string Old => Path.Combine(OldDir, "settings.ini");
        string NewDir => Path.Combine(tmp, "Framejet");
        string Neu => Path.Combine(NewDir, "settings.ini");
        public void Dispose() { try { Directory.Delete(tmp, true); } catch { } }

        void WriteOld(string text) { Directory.CreateDirectory(OldDir); File.WriteAllText(Old, text); }

        [Theory]
        [InlineData(true, false, true, true)]
        [InlineData(false, false, true, false)]
        [InlineData(true, true, true, false)]
        [InlineData(true, false, false, false)]
        public void Copies_only_for_the_default_path_into_a_new_folder_from_an_existing_file(bool defaultPath, bool framejetFolder, bool oldFile, bool copy)
            => Assert.Equal(copy, SettingsCarryOver.ShouldCopy(defaultPath, framejetFolder, oldFile));

        [Fact]
        public void Copies_the_old_settings_into_a_new_Framejet_folder()
        {
            WriteOld("mode=2560x1440@60/NV12\r\nvolume=0.5\r\n");
            var before = File.ReadAllBytes(Old);
            Assert.Equal(CarryOverResult.Copied, SettingsCarryOver.Run(tmp, true, out var e));
            Assert.Null(e);
            Assert.Equal(before, File.ReadAllBytes(Neu));
            Assert.Equal(before, File.ReadAllBytes(Old));
        }

        [Fact]
        public void Leaves_an_existing_Framejet_folder_alone()
        {
            WriteOld("volume=0.5\r\n");
            Directory.CreateDirectory(NewDir);
            Assert.Equal(CarryOverResult.NotNeeded, SettingsCarryOver.Run(tmp, true, out _));
            Assert.False(File.Exists(Neu));
        }

        [Fact]
        public void Never_copies_for_a_custom_settings_path()
        {
            WriteOld("volume=0.5\r\n");
            Assert.Equal(CarryOverResult.NotNeeded, SettingsCarryOver.Run(tmp, false, out _));
            Assert.False(Directory.Exists(NewDir));
        }

        [Fact]
        public void Does_nothing_without_old_settings()
        {
            Assert.Equal(CarryOverResult.NotNeeded, SettingsCarryOver.Run(tmp, true, out _));
            Assert.False(Directory.Exists(NewDir));
        }

        [Fact]
        public void A_locked_old_file_fails_without_throwing()
        {
            WriteOld("volume=0.5\r\n");
            using (new FileStream(Old, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(CarryOverResult.Failed, SettingsCarryOver.Run(tmp, true, out var e));
                Assert.False(string.IsNullOrEmpty(e));
            }
            Assert.False(File.Exists(Neu));
        }
    }
}
