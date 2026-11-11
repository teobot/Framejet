using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Framejet.Core.Timing;

namespace Framejet.Platform.Audio
{
    /// <summary>Reports audio device changes. Callbacks only post to the dispatcher.</summary>
    public sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
    {
        readonly IDispatcher _dispatcher;
        readonly MMDeviceEnumerator _enumerator = new MMDeviceEnumerator();
        volatile bool _disposed;

        public AudioDeviceWatcher(IDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        public event Action DevicesChanged;
        public event Action DefaultOutputChanged;

        void Changed() { if (!_disposed) _dispatcher.Post(() => DevicesChanged?.Invoke()); }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed();
        public void OnDeviceAdded(string pwstrDeviceId) => Changed();
        public void OnDeviceRemoved(string deviceId) => Changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia && !_disposed)
                _dispatcher.Post(() => DefaultOutputChanged?.Invoke());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch (Exception) { }
            _enumerator.Dispose();
        }
    }
}
