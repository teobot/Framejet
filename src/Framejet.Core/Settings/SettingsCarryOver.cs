using System;
using System.IO;

namespace Framejet.Core.Settings
{
    public enum CarryOverResult { NotNeeded, Copied, Failed }

    /// <summary>Copies PlayThrough's settings.ini into the new Framejet folder, once. Never touches the old folder.</summary>
    public static class SettingsCarryOver
    {
        public static bool ShouldCopy(bool usingDefaultPath, bool framejetFolderExists, bool oldSettingsExist)
            => usingDefaultPath && !framejetFolderExists && oldSettingsExist;

        public static CarryOverResult Run(string appDataFolder, bool usingDefaultPath, out string error)
        {
            error = null;
            try
            {
                var oldFile = Path.Combine(appDataFolder, "PlayThrough", "settings.ini");
                var newDir = Path.Combine(appDataFolder, SettingsStore.FolderName);
                if (!ShouldCopy(usingDefaultPath, Directory.Exists(newDir), File.Exists(oldFile)))
                    return CarryOverResult.NotNeeded;
                Directory.CreateDirectory(newDir);
                File.Copy(oldFile, Path.Combine(newDir, "settings.ini"), false);
                return CarryOverResult.Copied;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                return CarryOverResult.Failed;
            }
        }
    }
}
