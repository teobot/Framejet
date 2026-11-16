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

        /// <summary>The version a build gets when no release tag stamps one (Directory.Build.props, release.yml).</summary>
        public const string DevVersion = "0.0.0-dev";

        /// <summary>
        /// "Framejet 1.1.0": the name and the version without any +metadata suffix. A build from no release tag reads
        /// "Framejet (dev build)", and a blank version gives just the name.
        /// </summary>
        public static string NameAndVersion(string version)
        {
            if (version == null) return "Framejet";
            var plus = version.IndexOf('+');
            if (plus >= 0) version = version.Substring(0, plus);
            version = version.Trim();
            if (version == DevVersion) return "Framejet (dev build)";
            return version.Length == 0 ? "Framejet" : "Framejet " + version;
        }

        public static string Waiting(string device) => "Waiting for " + device + "… plug it in and it starts by itself";
        public static string Busy(string device) => $"Couldn't start {device} — retrying";
        public static string Taken(string device) => $"{device} is in use by another app — press Rescan to take it back";
        public static string ModeReverted(VideoMode failed, VideoMode back) => "Couldn't start " + failed.Label + " — back to " + back.Label;
        public static string OutputGone(string name) => name + " is gone — using " + DefaultOutput;
        public static string AudioError(string message) => "Audio error: " + message;
        public static string AudioRetrying(string message) => AudioError(message) + " — retrying";

        public const string SyncMenu = "Check A/V sync…";
        public const string SyncPrompt = "Play an A/V sync test video on the PS5 (a flash with a beep), then press Start.";
        public const string SyncNoFlashes = "No flashes seen — is the sync video playing?";
        public const string SyncNoBeeps = "No beeps heard — is the PS5's sound on?";
        public const string SyncTooFewPairs = "Not enough flash-beep pairs; try a longer run.";
        public const string SyncAmbiguous = "Ambiguous — the video's flashes are too close together.";
        public const string SyncSourceStopped = "The check stopped: the picture or sound went away.";

        public static string SyncChecking(int seconds) => $"Checking A/V sync… {seconds} s";
        public static string SyncResult(int ms, int pairs, int spread) =>
            $"Sound leaves the PC {ms} ms after the picture (from {pairs} flash-beep pairs, spread ±{spread} ms).";
        public static string SyncSplit(int card, int audio, int picture) =>
            $"card: {card} ms · Framejet audio: {audio} ms · Framejet picture: {picture} ms";

        public static string UpdateTo(string version) => "Update to " + version;
        public static string UpdateTooltip(string version) => "Open the Framejet " + version + " release page";
    }
}
