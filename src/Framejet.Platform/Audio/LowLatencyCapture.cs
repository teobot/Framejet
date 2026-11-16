using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using Framejet.Core;
using Framejet.Platform.Audio.Interop;

namespace Framejet.Platform.Audio
{
    /// <summary>
    /// Shared-mode capture at the engine's minimum period (IAudioClient3; typically 128 frames, 2.67 ms at 48 kHz),
    /// in the endpoint's mix format, driven by the engine's event on its own thread.
    /// </summary>
    public sealed class LowLatencyCapture : IDisposable
    {
        const string ThreadName = "Low-latency capture";

        readonly LowLatencyClient _client;
        readonly IAudioCaptureClient _capture;
        readonly ILog _log;
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        Thread _thread;
        volatile bool _stopping;
        int _disposed;

        LowLatencyCapture(LowLatencyClient client, IAudioCaptureClient capture, ILog log)
        {
            _client = client; _capture = capture; _log = log ?? NullLog.Instance;
        }

        /// <summary>Opens the endpoint in low-latency mode, or returns null and says why.</summary>
        public static LowLatencyCapture TryOpen(string endpointId, out string whyNot, ILog log = null)
        {
            var client = LowLatencyClient.Open(endpointId, out whyNot);
            if (client == null) return null;
            try
            {
                return new LowLatencyCapture(client, client.Service<IAudioCaptureClient>(CoreAudio.IidAudioCaptureClient), log);
            }
            catch (Exception ex)
            {
                whyNot = LowLatencyClient.Describe(ex);
                client.Dispose();
                return null;
            }
        }

        public WaveFormat WaveFormat => _client.Format;
        public double PeriodMs => _client.PeriodMs;
        public double StreamLatencyMs => _client.StreamLatencyMs;

        /// <summary>Raised on the capture thread for each packet; the buffer is reused, so copy what you need before returning.</summary>
        public event Action<byte[], int> DataAvailable;

        /// <summary>Raised on the capture thread when the stream breaks (not on Dispose).</summary>
        public event Action<Exception> Stopped;

        public void Start()
        {
            _client.Client.Start();
            _thread = LowLatencyClient.StartThread(ThreadName, Loop, _log);
        }

        void Loop()
        {
            var waits = new WaitHandle[] { _stop, _client.Event };
            int blockAlign = _client.Format.BlockAlign;
            var buffer = new byte[Math.Max(1, (int)_client.BufferFrames) * blockAlign];
            try
            {
                while (WaitHandle.WaitAny(waits) != 0)
                {
                    while (!_stopping)
                    {
                        _capture.GetNextPacketSize(out uint next);
                        if (next == 0) break;
                        _capture.GetBuffer(out IntPtr data, out uint frames, out int flags, out _, out _);
                        int bytes = (int)frames * blockAlign;
                        if (buffer.Length < bytes) buffer = new byte[bytes];
                        if ((flags & CoreAudio.BufferFlagsSilent) != 0) Array.Clear(buffer, 0, bytes);
                        else Marshal.Copy(data, buffer, 0, bytes);
                        _capture.ReleaseBuffer(frames);
                        DataAvailable?.Invoke(buffer, bytes);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) Stopped?.Invoke(ex);
            }
        }

        /// <summary>Stops the thread (waiting up to 2 s for it) and releases the COM objects.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stopping = true;
            _stop.Set();
            bool exited = LowLatencyClient.Join(_thread, ThreadName, _log);
            CoreAudio.Release(_capture);
            _client.Dispose(releaseEvent: exited);
            if (exited) _stop.Dispose();
        }
    }
}
