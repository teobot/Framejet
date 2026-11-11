using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Core.Devices;

namespace Framejet.Platform.Audio
{
    public sealed class WasapiAudioBackend : IAudioBackend, IDisposable
    {
        static readonly PropertyKey ContainerIdKey = new PropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

        readonly ILog _log;
        readonly object _sync = new object();
        Session _session;

        public WasapiAudioBackend(ILog log) { _log = log ?? NullLog.Instance; }

        public event Action<string> Failed;

        public bool IsRunning { get { lock (_sync) return _session != null; } }

        public IReadOnlyList<AudioInputInfo> ListInputs()
        {
            var list = new List<AudioInputInfo>();
            using (var e = new MMDeviceEnumerator())
            {
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    using (d) list.Add(new AudioInputInfo(d.ID, d.FriendlyName, ContainerOf(d)));
                }
            }
            return list;
        }

        public IReadOnlyList<AudioOutputInfo> ListOutputs()
        {
            var list = new List<AudioOutputInfo>();
            using (var e = new MMDeviceEnumerator())
            {
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (d) list.Add(new AudioOutputInfo(d.ID, d.FriendlyName));
                }
            }
            return list;
        }

        public AudioOutputInfo DefaultOutput()
        {
            using (var e = new MMDeviceEnumerator())
            {
                if (!e.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
                using (var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    return new AudioOutputInfo(d.ID, d.FriendlyName);
            }
        }

        static Guid ContainerOf(MMDevice d)
        {
            try
            {
                var v = d.Properties[ContainerIdKey].Value;
                if (v is Guid g) return g;
                if (v is string s && Guid.TryParse(s, out var parsed)) return parsed;
            }
            catch (Exception) { }
            return Guid.Empty;
        }

        public void Start(AudioInputInfo input, AudioOutputInfo output, float gain)
        {
            Stop();
            var session = new Session(this);
            try
            {
                session.Open(input.Id, output.Id, gain);
                lock (_sync) _session = session;
                session.Begin();
            }
            catch
            {
                lock (_sync) { if (ReferenceEquals(_session, session)) _session = null; }
                session.Dispose(_log);
                throw;
            }
        }

        public void SetGain(float gain)
        {
            Session s;
            lock (_sync) s = _session;
            if (s != null) s.Volume.Volume = gain;
        }

        public void Stop()
        {
            Session s;
            lock (_sync) { s = _session; _session = null; }
            if (s == null) return;
            try { s.Dispose(_log); }
            catch (Exception ex) { _log.Error("Audio stop failed", ex); }
        }

        public AudioStats GetStats(bool reset)
        {
            Session s;
            lock (_sync) s = _session;
            return s == null ? new AudioStats() : s.Snapshot(reset);
        }

        public void Dispose() => Stop();

        // Only the current session may report a failure; events from a stopped session are ignored.
        void RaiseFailed(Session from, string message)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_session, from)) return;
                _session = null;
            }
            // Dispose off this thread: tearing down a capture from inside its own callback can deadlock.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { from.Dispose(_log); } catch (Exception ex) { _log.Error("Audio cleanup after failure failed", ex); }
            });
            Failed?.Invoke(message);
        }

        sealed class Session
        {
            readonly WasapiAudioBackend _owner;
            readonly object _statsLock = new object();
            MMDeviceEnumerator _enumerator;
            MMDevice _inDevice, _outDevice;
            WasapiCapture _capture;
            WasapiOut _out;
            BufferedWaveProvider _buffer;
            WaveFormat _captureFormat;
            CaptureIngest _ingest;
            volatile bool _disposed;

            public VolumeSampleProvider Volume { get; private set; }

            public Session(WasapiAudioBackend owner) { _owner = owner; }

            public void Open(string inputId, string outputId, float gain)
            {
                _enumerator = new MMDeviceEnumerator();
                _inDevice = _enumerator.GetDevice(inputId);
                _outDevice = _enumerator.GetDevice(outputId);

                _capture = new WasapiCapture(_inDevice, true, 10);
                _captureFormat = _capture.WaveFormat;
                _buffer = new BufferedWaveProvider(PlaythroughChain.FloatFormatFor(_captureFormat))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                _ingest = new CaptureIngest(_buffer, _captureFormat, new LatencyTrimmer(LatencyTrimmer.DefaultTarget, LatencyTrimmer.DefaultTrimAbove));
                _capture.DataAvailable += (s, e) => { if (ReferenceEquals(s, _capture)) { if (!_disposed) _ingest.Process(e.Buffer, e.BytesRecorded); } };
                _capture.RecordingStopped += (s, e) => { if (e.Exception != null && ReferenceEquals(s, _capture)) _owner.RaiseFailed(this, e.Exception.Message); };

                _out = new WasapiOut(_outDevice, AudioClientShareMode.Shared, true, 20);
                _out.PlaybackStopped += (s, e) => { if (e.Exception != null && ReferenceEquals(s, _out)) _owner.RaiseFailed(this, e.Exception.Message); };

                VolumeSampleProvider volume;
                WaveFormat mix;
                using (var client = _outDevice.AudioClient) mix = client.MixFormat; // each AudioClient access activates a new client
                var chain = PlaythroughChain.Build(_buffer, mix, out volume);
                Volume = volume;
                volume.Volume = gain;
                _out.Init(PlaythroughChain.ToWaveProvider(chain, mix));
            }

            public void Begin()
            {
                _out.Play();
                _capture.StartRecording();
            }

            public AudioStats Snapshot(bool reset) => _ingest.Snapshot(reset);

            public void Dispose(ILog log)
            {
                lock (_statsLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                }
                Try(log, () => _capture?.StopRecording());
                Try(log, () => _out?.Stop());
                Try(log, () => _capture?.Dispose());
                Try(log, () => _out?.Dispose());
                Try(log, () => _inDevice?.Dispose());
                Try(log, () => _outDevice?.Dispose());
                Try(log, () => _enumerator?.Dispose());
            }

            static void Try(ILog log, Action a)
            {
                try { a(); } catch (Exception ex) { log.Error("Audio teardown step failed", ex); }
            }
        }
    }
}
