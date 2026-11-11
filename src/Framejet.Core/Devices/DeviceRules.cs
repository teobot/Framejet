using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Framejet.Core.Devices
{
    public static class DeviceRules
    {
        public static readonly string[] CaptureCardWords =
            { "ripsaw", "capture", "hdmi", "cam link", "elgato", "avermedia", "live gamer", "usb video", "game" };
        public static readonly string[] BrandWords =
            { "razer", "logi", "logitech", "elgato", "avermedia", "corsair", "asus", "msi", "nvidia", "microsoft", "steelseries", "sony" };
        public static readonly string[] GenericWords =
        {
            "usb", "hdmi", "video", "audio", "webcam", "camera", "cam", "capture", "card", "device", "digital", "interface",
            "game", "gamer", "live", "stream", "streaming", "link", "pro", "microphone", "mic", "virtual", "windows", "the"
        };

        public static readonly Guid NoContainer = new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

        public static bool LooksLikeCaptureCard(string name)
            => !string.IsNullOrEmpty(name)
               && CaptureCardWords.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>A blank saved path or name (a first run saves none) is no pick, so it never matches a device with a blank one.</summary>
        public static VideoPick PickVideo(IReadOnlyList<VideoDeviceInfo> devices, string savedPath, string savedName)
        {
            VideoDeviceInfo d;
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                d = devices.FirstOrDefault(x => x.Path == savedPath);
                if (d != null) return new VideoPick(d, VideoPickReason.SavedPath);
            }
            if (!string.IsNullOrWhiteSpace(savedName))
            {
                d = devices.FirstOrDefault(x => string.Equals(x.Name, savedName, StringComparison.OrdinalIgnoreCase));
                if (d != null) return new VideoPick(d, VideoPickReason.SavedName);
            }
            d = devices.FirstOrDefault(x => LooksLikeCaptureCard(x.Name));
            if (d != null) return new VideoPick(d, VideoPickReason.LooksLikeCard);
            return new VideoPick(null, VideoPickReason.None);
        }

        /// <summary>A video device with a blank name matches only by its Windows device group: a blank name is contained in every name.</summary>
        public static AudioInputMatch MatchAudioInput(VideoDeviceInfo video, Guid videoContainerId, IReadOnlyList<AudioInputInfo> inputs)
        {
            if (videoContainerId != Guid.Empty && videoContainerId != NoContainer)
            {
                var same = inputs.FirstOrDefault(i => i.ContainerId == videoContainerId);
                if (same != null) return new AudioInputMatch(same, AudioInputSource.SameDevice);
            }

            if (string.IsNullOrWhiteSpace(video.Name)) return new AudioInputMatch(null, AudioInputSource.None);

            var byName = inputs.FirstOrDefault(i => i.Name.IndexOf(video.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (byName != null) return new AudioInputMatch(byName, AudioInputSource.NameMatch);

            var words = DistinctiveWords(video.Name);
            var byWord = inputs.FirstOrDefault(i => Split(i.Name).Any(w => words.Contains(w)));
            if (byWord != null) return new AudioInputMatch(byWord, AudioInputSource.NameMatch);

            return new AudioInputMatch(null, AudioInputSource.None);
        }

        public static IReadOnlyList<string> DistinctiveWords(string deviceName)
            => Split(deviceName)
                .Where(w => w.Length >= 3 && !BrandWords.Contains(w) && !GenericWords.Contains(w))
                .ToList();

        static IEnumerable<string> Split(string s)
            => Regex.Split((s ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0);
    }
}
