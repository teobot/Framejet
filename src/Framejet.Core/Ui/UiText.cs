using Framejet.Core.Video;

namespace Framejet.Core.Ui
{
    public static class UiText
    {
        public const string NoCard = "No capture card found";
        public const string NoPicture = "No picture — is the PS5 on?";
        public const string NoAudio = "No audio from the card";
        public const string DefaultOutput = "Default output";
        public const string Automatic = "Automatic";

        /// <summary>"Framejet 1.0.0": the name and the version without any +metadata suffix. A blank version gives just the name.</summary>
        public static string NameAndVersion(string version)
        {
            if (version == null) return "Framejet";
            var plus = version.IndexOf('+');
            if (plus >= 0) version = version.Substring(0, plus);
            version = version.Trim();
            return version.Length == 0 ? "Framejet" : "Framejet " + version;
        }

        public static string Waiting(string device) => "Waiting for " + device + "… plug it in and it starts by itself";
        public static string Busy(string device) => $"Couldn't start {device} — retrying";
        public static string Taken(string device) => $"{device} is in use by another app — press Rescan to take it back";
        public static string ModeReverted(VideoMode failed, VideoMode back) => "Couldn't start " + failed.Label + " — back to " + back.Label;
        public static string OutputGone(string name) => name + " is gone — using " + DefaultOutput;
        public static string AudioError(string message) => "Audio error: " + message;
        public static string AudioRetrying(string message) => AudioError(message) + " — retrying";
    }
}
