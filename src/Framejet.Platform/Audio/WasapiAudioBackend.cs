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

        // From an audio thread: an endpoint's low-latency stream underran too often, and the registry has fallen it back.
        // Restart on the thread that started the session (the UI thread in the app), quietly: the user hears a short
        // gap, sees no warning, and the log gets one line. The new session opens that endpoint in normal mode.
        void FallBack(Session from, string endpointName)
        {
            var context = _context;
            SendOrPostCallback restart = _ => RestartAfterFallBack(from, endpointName);
            if (context != null) context.Post(restart, null);
            else ThreadPool.QueueUserWorkItem(_ => restart(null));
        }

        void RestartAfterFallBack(Session from, string endpointName)
        {
            lock (_sync) { if (!ReferenceEquals(_session, from)) return; }
            _log.Info("Low-latency audio on " + endpointName + " underran more than " + LowLatencyRegistry.AllowedUnderruns + " times in "
                + LowLatencyRegistry.Window.TotalSeconds + " s; restarting it in normal mode");
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

            string Mode => _llCapture != null && _llRender != null ? "low-latency"
                : _llCapture != null ? "low-latency capture"
                : _llRender != null ? "low-latency output"
                : "normal";

            delegate T TryOpenStream<T>(string id, out string whyNot, ILog log);

            /// <summary>
            /// Opens one endpoint in low-latency mode unless the registry rules it out; null means use normal mode.
            /// Each end decides for itself: the buffer's headroom target follows the output period and its prefill the
            /// capture period, so a 10 ms end beside a low-latency one is fine.
            /// </summary>
            T TryLowLatency<T>(string id, string name, TryOpenStream<T> open) where T : class
            {
                var registry = _owner._lowLatency;
                if (!registry.ShouldTry(id)) return null;
                var stream = open(id, out string whyNot, _owner._log);
                if (stream == null)
                {
                    registry.Unsupported(id);
                    _owner._log.Info("Low-latency audio unavailable on " + name + ": " + whyNot + "; using normal mode");
                }
                return stream;
            }

            public void Open(float gain)
            {
                _enumerator = new MMDeviceEnumerator();
                _llCapture = TryLowLatency<LowLatencyCapture>(Input.Id, Input.Name, LowLatencyCapture.TryOpen);
                _llRender = TryLowLatency<LowLatencyRender>(Output.Id, Output.Name, LowLatencyRender.TryOpen);

                if (_llCapture != null)
                {
                    _captureFormat = _llCapture.WaveFormat;
                    _capturePeriodMs = _llCapture.PeriodMs;
                }
                else
                {
                    _inDevice = _enumerator.GetDevice(Input.Id);
                    _capture = new WasapiCapture(_inDevice, true, CaptureBufferMs);
                    _captureFormat = _capture.WaveFormat;
                    _capturePeriodMs = CaptureBufferMs; // its packets; the live client's period replaces this once it runs
                }

                WaveFormat mix;
                if (_llRender != null)
                {
                    mix = _llRender.MixFormat;
                    _outputPeriodMs = _llRender.PeriodMs;
                }
                else
                {
                    _outDevice = _enumerator.GetDevice(Output.Id);
                    _out = new WasapiOut(_outDevice, AudioClientShareMode.Shared, true, OutputLatencyMs);
                    using (var client = _outDevice.AudioClient) // each AudioClient access activates a new client
                    {
                        mix = client.MixFormat;
                        _outputPeriodMs = client.DefaultDevicePeriod / 10000.0;
                    }
                    if (_outputPeriodMs <= 0) _outputPeriodMs = OutputLatencyMs;
                }

                _buffer = new BufferedWaveProvider(PlaythroughChain.FloatFormatFor(_captureFormat))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };
                var drift = DriftController.ForStreams(TimeSpan.FromTicks((long)(_capturePeriodMs * 10000)), TimeSpan.FromTicks((long)(_outputPeriodMs * 10000)));
                _ingest = new CaptureIngest(_buffer, _captureFormat, drift, _owner._log)
                    { BlockPeakTap = _owner._blockPeakTap };
                _owner._log.Info(FormattableString.Invariant($"Audio buffer headroom target {drift.Target.TotalMilliseconds:0.00} ms (output period {_outputPeriodMs:0.00} ms + {DriftController.HeadroomMargin.TotalMilliseconds:0.00} ms), capture period {_capturePeriodMs:0.00} ms"));

                if (_llCapture != null || _llRender != null) _ingest.CountedUnderrun = OnCountedUnderrun;
                if (_llCapture != null)
                {
                    _llCapture.DataAvailable += (buffer, count) => { if (!_disposed) _ingest.Process(buffer, count); };
                    _llCapture.Stopped += e => _owner.RaiseFailed(this, e.Message);
                    _llCapture.Glitch += () => OnDeviceGlitch(Input.Id, Input.Name);
                }
                else
                {
                    _capture.DataAvailable += (s, e) => { if (ReferenceEquals(s, _capture)) { if (!_disposed) _ingest.Process(e.Buffer, e.BytesRecorded); } };
                    _capture.RecordingStopped += (s, e) => { if (e.Exception != null && ReferenceEquals(s, _capture)) _owner.RaiseFailed(this, e.Exception.Message); };
                }
                if (_llRender != null)
                {
                    _llRender.Stopped += e => _owner.RaiseFailed(this, e.Message);
                    _llRender.Glitch += () => OnDeviceGlitch(Output.Id, Output.Name);
                }
                else
                {
                    _out.PlaybackStopped += (s, e) => { if (e.Exception != null && ReferenceEquals(s, _out)) _owner.RaiseFailed(this, e.Exception.Message); };
                }

                var chain = PlaythroughChain.Build(_ingest.Output, mix, out var volume, out var resampler);
                _ingest.Resampler = resampler;
                Volume = volume;
                volume.Volume = gain;
                _padding = new OutputPaddingSampler(PlaythroughChain.ToWaveProvider(chain, mix), mix.SampleRate);
                if (_llRender != null)
                {
                    _llRender.Init(_padding);
                    var render = _llRender;
                    _padding.Padding = () => render.CurrentPaddingFrames;
                    _outStreamMs = render.StreamLatencyMs;
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
                if (_llRender != null) _llRender.Play(); else _out.Play();
                if (_llCapture != null)
                {
                    _llCapture.Start();
                    _captureMs = _llCapture.PeriodMs + _llCapture.StreamLatencyMs;
                    _captureSource = "low-latency stream";
                }
                else
                {
                    _capture.StartRecording();
                    ReadCaptureClient();
                }
                _owner._log.Info("Audio delay figures: capture=" + _captureSource + ", output=" + _outputSource);
                _owner._log.Info(FormattableString.Invariant($"Audio mode: {Mode}; capture {(_llCapture != null ? "low-latency" : "normal")} {_capturePeriodMs:0.00} ms, output {(_llRender != null ? "low-latency" : "normal")} {_outputPeriodMs:0.00} ms")
                    + (_llRender != null ? ", output buffer " + _llRender.BufferFrames + " frames" : ""));
            }

            // On a stream thread: the device itself glitched (an output wake with nothing queued, a capture discontinuity).
            // It counts against that endpoint only, under the same filter as ring underruns.
            void OnDeviceGlitch(string endpointId, string endpointName)
            {
                if (_ingest.CountsNow(out long now)) Count(endpointId, endpointName, now);
            }

            // On the output thread, for each ring underrun that counts (capture delivering, past the settle time). Either
            // low-latency end may be the cause, so each is counted.
            void OnCountedUnderrun(long stopwatchTicks)
            {
                if (_llCapture != null) Count(Input.Id, Input.Name, stopwatchTicks);
                if (_llRender != null) Count(Output.Id, Output.Name, stopwatchTicks);
            }

            void Count(string endpointId, string endpointName, long stopwatchTicks)
            {
                var now = TimeSpan.FromTicks((long)(stopwatchTicks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                if (_owner._lowLatency.Underrun(endpointId, now) && Interlocked.Exchange(ref _fellBack, 1) == 0)
                    _owner.FallBack(this, endpointName);
            }

            public void SetBlockPeakTap(Action<long, float> tap) => _ingest.BlockPeakTap = tap;

            public AudioStats Snapshot(bool reset)
            {
                var s = _ingest.Snapshot(reset);
                var d = s.Delay;
                d.CaptureMs = _captureMs;
                d.OutputMs = _outStreamMs + _padding.MeanPaddingMs(reset);
                d.Mode = Mode;
                // Normal mode reports the capture period, as before; otherwise the longer of the two ends'.
                d.PeriodMs = _llCapture == null && _llRender == null ? _capturePeriodMs : Math.Max(_capturePeriodMs, _outputPeriodMs);
                int u = _ingest.Underruns;
                d.Underruns = u - _underrunBase;
                d.StartupUnderruns = _underrunBase;
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
