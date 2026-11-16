using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        readonly LowLatencyRegistry _lowLatency;
        readonly object _sync = new object();
        Session _session;
        SynchronizationContext _context;
        volatile Action<long, float> _blockPeakTap;

        /// <param name="lowLatency">Which endpoints can't run low-latency mode; the app keeps one for its whole run.</param>
        public WasapiAudioBackend(ILog log, LowLatencyRegistry lowLatency = null)
        {
            _log = log ?? NullLog.Instance;
            _lowLatency = lowLatency ?? new LowLatencyRegistry();
        }

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
            _context = SynchronizationContext.Current;
            var session = new Session(this, input, output);
            try
            {
                session.Open(gain);
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

        /// <summary>
        /// While set, called on the capture thread for each 1 ms block of captured sound with its capture stamp and peak.
        /// It must return at once and never touch the UI. It carries over to later starts until it is set back to null.
        /// </summary>
        public Action<long, float> BlockPeakTap
        {
            set
            {
                _blockPeakTap = value;
                Session s;
                lock (_sync) s = _session;
                s?.SetBlockPeakTap(value);
            }
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

        // From the audio thread: low-latency mode underran too often, and the registry has fallen back its endpoints.
        // Restart on the thread that started the session (the UI thread in the app), quietly: the user hears a short
        // gap, sees no warning, and the log gets one line.
        void FallBack(Session from)
        {
            var context = _context;
            SendOrPostCallback restart = _ => RestartInNormalMode(from);
            if (context != null) context.Post(restart, null);
            else ThreadPool.QueueUserWorkItem(_ => restart(null));
        }

        void RestartInNormalMode(Session from)
        {
            lock (_sync) { if (!ReferenceEquals(_session, from)) return; }
            _log.Info("Low-latency audio underran more than " + LowLatencyRegistry.AllowedUnderruns + " times in "
                + LowLatencyRegistry.Window.TotalSeconds + " s; restarting in normal mode");
            try { Start(from.Input, from.Output, from.Volume.Volume); }
            catch (Exception ex)
            {
                _log.Error("Restarting audio in normal mode failed", ex);
                Failed?.Invoke(ex.Message);
            }
        }

        sealed class Session
        {
            readonly WasapiAudioBackend _owner;
            readonly object _statsLock = new object();
            MMDeviceEnumerator _enumerator;
            MMDevice _inDevice, _outDevice;
            WasapiCapture _capture;
            WasapiOut _out;
            LowLatencyCapture _llCapture;
            LowLatencyRender _llRender;
            int _fellBack;
            BufferedWaveProvider _buffer;
            WaveFormat _captureFormat;
            CaptureIngest _ingest;
            OutputPaddingSampler _padding;
            double _captureMs, _capturePeriodMs, _outputPeriodMs, _outStreamMs;
            int _underrunBase;
            volatile bool _disposed;

            const int CaptureBufferMs = 10, OutputLatencyMs = 10;
            string _captureSource = "fallback", _outputSource = "fallback";

            public VolumeSampleProvider Volume { get; private set; }

            public Session(WasapiAudioBackend owner, AudioInputInfo input, AudioOutputInfo output) { _owner = owner; Input = input; Output = output; }

            public AudioInputInfo Input { get; }
            public AudioOutputInfo Output { get; }

            bool LowLatency => _llCapture != null;

            /// <summary>
            /// Opens both ends in low-latency mode, or neither. The buffer's 10 ms target covers one short capture packet
            /// plus the output's short reads; with one end at 10 ms periods it doesn't. A 10 ms capture packet feeding
            /// 2 ms output reads leaves the ring empty just before each packet, and it underran 7 times in 5 s.
            /// An end that can't is remembered by the registry, so later starts don't try again.
            /// </summary>
            void OpenLowLatency()
            {
                var registry = _owner._lowLatency;
                var log = _owner._log;
                if (!registry.ShouldTry(Input.Id) || !registry.ShouldTry(Output.Id)) return;
                var capture = LowLatencyCapture.TryOpen(Input.Id, out string whyNot, log);
                if (capture == null)
                {
                    registry.Unsupported(Input.Id);
                    log.Info("Low-latency audio unavailable on " + Input.Name + ": " + whyNot + "; using normal mode");
                    return;
                }
                var render = LowLatencyRender.TryOpen(Output.Id, out whyNot, log);
                if (render == null)
                {
                    capture.Dispose();
                    registry.Unsupported(Output.Id);
                    log.Info("Low-latency audio unavailable on " + Output.Name + ": " + whyNot + "; using normal mode");
                    return;
                }
                _llCapture = capture;
                _llRender = render;
            }

            public void Open(float gain)
            {
                _enumerator = new MMDeviceEnumerator();
                OpenLowLatency();

                if (LowLatency)
                {
                    _captureFormat = _llCapture.WaveFormat;
                }
                else
                {
                    _inDevice = _enumerator.GetDevice(Input.Id);
                    _capture = new WasapiCapture(_inDevice, true, CaptureBufferMs);
                    _captureFormat = _capture.WaveFormat;
                }
                _buffer = new BufferedWaveProvider(PlaythroughChain.FloatFormatFor(_captureFormat))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                _ingest = new CaptureIngest(_buffer, _captureFormat, new DriftController(), _owner._log)
                    { BlockPeakTap = _owner._blockPeakTap };
                if (LowLatency)
                {
                    _ingest.CountedUnderrun = OnCountedUnderrun;
                    _llCapture.DataAvailable += (buffer, count) => { if (!_disposed) _ingest.Process(buffer, count); };
                    _llCapture.Stopped += e => _owner.RaiseFailed(this, e.Message);
                }
                else
                {
                    _capture.DataAvailable += (s, e) => { if (ReferenceEquals(s, _capture)) { if (!_disposed) _ingest.Process(e.Buffer, e.BytesRecorded); } };
                    _capture.RecordingStopped += (s, e) => { if (e.Exception != null && ReferenceEquals(s, _capture)) _owner.RaiseFailed(this, e.Exception.Message); };
                }

                VolumeSampleProvider volume;
                WaveFormat mix;
                if (LowLatency)
                {
                    _llRender.Stopped += e => _owner.RaiseFailed(this, e.Message);
                    mix = _llRender.MixFormat;
                }
                else
                {
                    _outDevice = _enumerator.GetDevice(Output.Id);
                    _out = new WasapiOut(_outDevice, AudioClientShareMode.Shared, true, OutputLatencyMs);
                    _out.PlaybackStopped += (s, e) => { if (e.Exception != null && ReferenceEquals(s, _out)) _owner.RaiseFailed(this, e.Exception.Message); };
                    using (var client = _outDevice.AudioClient) mix = client.MixFormat; // each AudioClient access activates a new client
                }
                var chain = PlaythroughChain.Build(_ingest.Output, mix, out volume, out var resampler);
                _ingest.Resampler = resampler;
                Volume = volume;
                volume.Volume = gain;
                _padding = new OutputPaddingSampler(PlaythroughChain.ToWaveProvider(chain, mix), mix.SampleRate);
                if (LowLatency)
                {
                    _llRender.Init(_padding);
                    var render = _llRender;
                    _padding.Padding = () => render.CurrentPaddingFrames;
                    _outStreamMs = render.StreamLatencyMs;
                    _outputPeriodMs = render.PeriodMs;
                    _outputSource = "low-latency stream";
                }
                else
                {
                    _out.Init(_padding);
                    ReadOutputClient();
                }
            }

            public void Begin()
            {
                if (LowLatency)
                {
                    _llRender.Play();
                    _llCapture.Start();
                    _captureMs = _llCapture.PeriodMs + _llCapture.StreamLatencyMs;
                    _capturePeriodMs = _llCapture.PeriodMs;
                    _captureSource = "low-latency stream";
                }
                else
                {
                    _out.Play();
                    _capture.StartRecording();
                    ReadCaptureClient();
                }
                _owner._log.Info("Audio delay figures: capture=" + _captureSource + ", output=" + _outputSource);
                if (LowLatency)
                    _owner._log.Info(FormattableString.Invariant($"Audio low-latency mode: capture period {_capturePeriodMs:0.00} ms, output period {_outputPeriodMs:0.00} ms, output buffer {_llRender.BufferFrames} frames"));
            }

            // On the output thread, for each underrun that counts (capture delivering, past the settle time).
            void OnCountedUnderrun(long stopwatchTicks)
            {
                var now = TimeSpan.FromTicks((long)(stopwatchTicks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                // Either end may be the cause, and neither can run low-latency alone, so both are counted.
                var registry = _owner._lowLatency;
                bool fellBack = registry.Underrun(Input.Id, now);
                fellBack |= registry.Underrun(Output.Id, now);
                if (fellBack && Interlocked.Exchange(ref _fellBack, 1) == 0) _owner.FallBack(this);
            }

            public void SetBlockPeakTap(Action<long, float> tap) => _ingest.BlockPeakTap = tap;

            public AudioStats Snapshot(bool reset)
            {
                var s = _ingest.Snapshot(reset);
                var d = s.Delay;
                d.CaptureMs = _captureMs;
                d.OutputMs = _outStreamMs + _padding.MeanPaddingMs(reset);
                d.Mode = LowLatency ? "low-latency" : "normal";
                // Normal mode reports the capture period, as before; in low-latency mode the longer of the two ends'.
                d.PeriodMs = LowLatency ? Math.Max(_capturePeriodMs, _outputPeriodMs) : _capturePeriodMs;
                int u = _ingest.Underruns;
                d.Underruns = u - _underrunBase;
                if (reset) _underrunBase = u;
                return s;
            }

            // NAudio keeps the audio clients it initialised private, and a client taken from the device is a fresh,
            // uninitialised one that has no stream latency. So read the live clients by reflection; if that fails
            // (a different NAudio), fall back to the buffer durations passed to the constructors.
            void ReadCaptureClient()
            {
                _captureMs = CaptureBufferMs;
                var c = LiveClient(_capture, "audioClient");
                if (c == null) return;
                try { _captureMs += c.StreamLatency / 10000.0; _capturePeriodMs = c.DefaultDevicePeriod / 10000.0; _captureSource = "live client"; }
                catch (Exception ex) { _owner._log.Error("Capture latency unreadable", ex); }
            }

            void ReadOutputClient()
            {
                _outStreamMs = OutputLatencyMs;
                _outputPeriodMs = OutputLatencyMs;
                var c = LiveClient(_out, "audioClient");
                if (c == null) return;
                _padding.Padding = () => c.CurrentPadding;
                try { _outStreamMs = c.StreamLatency / 10000.0; _outputPeriodMs = c.DefaultDevicePeriod / 10000.0; _outputSource = "live client"; }
                catch (Exception ex) { _owner._log.Error("Output latency unreadable", ex); }
            }

            static AudioClient LiveClient(object owner, string field)
            {
                try
                {
                    return owner.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(owner) as AudioClient;
                }
                catch (Exception) { return null; }
            }

            public void Dispose(ILog log)
            {
                lock (_statsLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                }
                Try(log, () => _capture?.StopRecording());
                Try(log, () => _out?.Stop());
                Try(log, () => _llCapture?.Dispose());
                Try(log, () => _llRender?.Dispose());
                Try(log, () => _capture?.Dispose());
                Try(log, () => _out?.Dispose());
                Try(log, () => _inDevice?.Dispose());
                Try(log, () => _outDevice?.Dispose());
                Try(log, () => _enumerator?.Dispose());
            }

            /// <summary>Wraps the output; on each read it samples the stream's padding, the audio already queued for the device.</summary>
            sealed class OutputPaddingSampler : IWaveProvider
            {
                readonly IWaveProvider _source;
                readonly int _sampleRate;
                readonly object _lock = new object();
                double _sumMs; int _n;

                public OutputPaddingSampler(IWaveProvider source, int sampleRate) { _source = source; _sampleRate = sampleRate; }

                /// <summary>The stream's padding in frames.</summary>
                public Func<int> Padding { get; set; }
                public WaveFormat WaveFormat => _source.WaveFormat;

                public int Read(byte[] buffer, int offset, int count)
                {
                    int got = _source.Read(buffer, offset, count);
                    var padding = Padding;
                    if (padding != null)
                    {
                        try
                        {
                            double ms = padding() * 1000.0 / _sampleRate;
                            lock (_lock) { _sumMs += ms; _n++; }
                        }
                        catch (Exception) { }
                    }
                    return got;
                }

                public double MeanPaddingMs(bool reset)
                {
                    lock (_lock)
                    {
                        double m = _n == 0 ? 0 : _sumMs / _n;
                        if (reset) { _sumMs = 0; _n = 0; }
                        return m;
                    }
                }
            }

            static void Try(ILog log, Action a)
            {
                try { a(); } catch (Exception ex) { log.Error("Audio teardown step failed", ex); }
            }
        }
    }
}
