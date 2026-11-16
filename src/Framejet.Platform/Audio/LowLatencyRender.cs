using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using Framejet.Core;
using Framejet.Platform.Audio.Interop;

namespace Framejet.Platform.Audio
{
    /// <summary>
    /// Shared-mode output at the engine's minimum period (IAudioClient3), in the endpoint's mix format, driven by the
    /// engine's event on its own thread. It keeps two periods queued: one being played, one in hand for a late wake.
    /// </summary>
    public sealed class LowLatencyRender : IDisposable
    {
        const string ThreadName = "Low-latency output";

        readonly LowLatencyClient _client;
        readonly IAudioRenderClient _render;
        readonly ILog _log;
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        readonly uint _queueFrames;
        IWaveProvider _source;
        Thread _thread;
        volatile bool _stopping;
        int _disposed;

        LowLatencyRender(LowLatencyClient client, IAudioRenderClient render, ILog log)
        {
            _client = client; _render = render; _log = log ?? NullLog.Instance;
            _queueFrames = Math.Min(client.BufferFrames, 2 * client.PeriodFrames);
        }

        /// <summary>Opens the endpoint in low-latency mode, or returns null and says why.</summary>
        public static LowLatencyRender TryOpen(string endpointId, out string whyNot, ILog log = null)
        {
            var client = LowLatencyClient.Open(endpointId, out whyNot);
            if (client == null) return null;
            try
            {
                return new LowLatencyRender(client, client.Service<IAudioRenderClient>(CoreAudio.IidAudioRenderClient), log);
            }
            catch (Exception ex)
            {
                whyNot = LowLatencyClient.Describe(ex);
                client.Dispose();
                return null;
            }
        }

        public WaveFormat MixFormat => _client.Format;
        public double PeriodMs => _client.PeriodMs;
        public double StreamLatencyMs => _client.StreamLatencyMs;
        public int BufferFrames => (int)_client.BufferFrames;

        /// <summary>Audio queued for the device, in frames. Any thread.</summary>
        public int CurrentPaddingFrames
        {
            get
            {
                _client.Client.GetCurrentPadding(out uint padding);
                return (int)padding;
            }
        }

        /// <summary>Raised on the output thread when the stream breaks (not on Dispose).</summary>
        public event Action<Exception> Stopped;

        /// <summary>The source must produce bytes in <see cref="MixFormat"/>.</summary>
        public void Init(IWaveProvider source) => _source = source ?? throw new ArgumentNullException(nameof(source));

        /// <summary>Queues one period of silence, then starts the stream and the thread that keeps it fed.</summary>
        public void Play()
        {
            if (_source == null) throw new InvalidOperationException("Call Init before Play.");
            uint period = _client.PeriodFrames;
            _render.GetBuffer(period, out _);
            _render.ReleaseBuffer(period, CoreAudio.BufferFlagsSilent);
            _client.Client.Start();
            _thread = LowLatencyClient.StartThread(ThreadName, Loop, _log);
        }

        void Loop()
        {
            var waits = new WaitHandle[] { _stop, _client.Event };
            int blockAlign = _client.Format.BlockAlign;
            var scratch = new byte[(int)_queueFrames * blockAlign];
            try
            {
                while (WaitHandle.WaitAny(waits) != 0)
                {
                    _client.Client.GetCurrentPadding(out uint padding);
                    if (padding >= _queueFrames) continue;
                    uint frames = _queueFrames - padding;
                    int bytes = (int)frames * blockAlign;
                    int got = Math.Max(0, _source.Read(scratch, 0, bytes));
                    if (got < bytes) Array.Clear(scratch, got, bytes - got);
                    _render.GetBuffer(frames, out IntPtr data);
                    Marshal.Copy(scratch, 0, data, bytes);
                    _render.ReleaseBuffer(frames, 0);
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
            CoreAudio.Release(_render);
            _client.Dispose(releaseEvent: exited);
            if (exited) _stop.Dispose();
        }
    }
}
