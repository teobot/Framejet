using System;

namespace Framejet.Core.Devices
{
    public sealed class VideoDeviceInfo
    {
        public VideoDeviceInfo(string name, string path) { Name = name; Path = path; }
        public string Name { get; }
        public string Path { get; }
    }

    public sealed class AudioInputInfo
    {
        public AudioInputInfo(string id, string name, Guid containerId) { Id = id; Name = name; ContainerId = containerId; }
        public string Id { get; }
        public string Name { get; }
        public Guid ContainerId { get; }
    }

    public sealed class AudioOutputInfo
    {
        public AudioOutputInfo(string id, string name) { Id = id; Name = name; }
        public string Id { get; }
        public string Name { get; }
    }

    public enum VideoPickReason { None, SavedPath, SavedName, LooksLikeCard }

    public sealed class VideoPick
    {
        public VideoPick(VideoDeviceInfo device, VideoPickReason reason) { Device = device; Reason = reason; }
        public VideoDeviceInfo Device { get; }
        public VideoPickReason Reason { get; }
    }

    public enum AudioInputSource { None, SameDevice, NameMatch, Override }

    public sealed class AudioInputMatch
    {
        public AudioInputMatch(AudioInputInfo input, AudioInputSource source) { Input = input; Source = source; }
        public AudioInputInfo Input { get; }
        public AudioInputSource Source { get; }
    }
}
