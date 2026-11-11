using System;
using System.Collections.Generic;
using Framejet.Core.Devices;
using Framejet.Core.Video;

namespace Framejet.Core.Tests.Fakes
{
    public sealed class FakeVideoBackend : IVideoBackend
    {
        public List<VideoDeviceInfo> Devices { get; } = new List<VideoDeviceInfo>();
        public Dictionary<string, VideoMode[]> ModesByPath { get; } = new Dictionary<string, VideoMode[]>();
        public bool ListModesThrows { get; set; }
        public Queue<bool> StartResults { get; } = new Queue<bool>();
        public List<(VideoDeviceInfo device, VideoMode mode)> Starts { get; } = new List<(VideoDeviceInfo device, VideoMode mode)>();
        public Exception ListModesError { get; set; }
        public Exception StartError { get; set; }
        public int Stops { get; private set; }
        public long FramesArrived { get; set; }

        public event Action DeviceLost;
        public event Action DeviceTaken;

        public IReadOnlyList<VideoDeviceInfo> ListDevices() => Devices.ToArray();

        public IReadOnlyList<VideoMode> ListModes(VideoDeviceInfo device)
        {
            if (ListModesError != null) throw ListModesError;
            if (ListModesThrows) throw new VideoStartException("in use");
            return ModesByPath.TryGetValue(device.Path, out var m) ? m : new VideoMode[0];
        }

        public void Start(VideoDeviceInfo device, VideoMode mode)
        {
            Starts.Add((device, mode));
            if (StartError != null) throw StartError;
            if (StartResults.Count > 0 && !StartResults.Dequeue()) throw new VideoStartException("in use");
        }

        public void Stop() { Stops++; }

        public void RaiseDeviceLost() => DeviceLost?.Invoke();
        public void RaiseDeviceTaken() => DeviceTaken?.Invoke();
    }
}
