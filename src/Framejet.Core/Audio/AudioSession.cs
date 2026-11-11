using System;
using System.Linq;
using System.Threading;
using Framejet.Core.Devices;
using Framejet.Core.Settings;
using Framejet.Core.Timing;
using Framejet.Core.Ui;

namespace Framejet.Core.Audio
{
    /// <summary>
    /// Decides which input plays to which output, and rides out device changes and failures. A failure (the running
    /// playback breaking, a start that throws, or listing the devices failing) is tried again every 3 s until audio
    /// plays: real failures, such as another app taking the device exclusively, a format change or an audio-service
    /// restart, come with no device event to recover on. UI thread only, apart from the backend's Failed event.
    /// </summary>
    public sealed class AudioSession : IDisposable
    {
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

        readonly IAudioBackend backend;
        readonly AppSettings settings;
        readonly IDispatcher dispatcher;
        readonly IScheduler scheduler;
        readonly Action settingsChanged;
        readonly ILog log;
        readonly Action<string> onBackendFailed;

        VideoDeviceInfo video;
        Guid videoContainerId;
        AudioInputInfo runningInput;
        AudioOutputInfo runningOutput;
        int generation;
        IDisposable retry, recoveryCheck;
        // From the first failure until audio has played again for RetryDelay: only those two moments are logged, so a
        // device that stays broken (or keeps breaking right after it starts) doesn't write a line every 3 s.
        bool failing;
        // The last input/output pair written to the log as playing, so only a different pair gets a new line.
        string loggedPlaying;
        bool disposed;

        public AudioSession(IAudioBackend backend, AppSettings settings, IDispatcher dispatcher, IScheduler scheduler, Action settingsChanged, ILog log)
        {
            this.backend = backend;
            this.settings = settings;
            this.dispatcher = dispatcher;
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.settingsChanged = settingsChanged ?? (() => { });
            this.log = log ?? NullLog.Instance;
            onBackendFailed = message =>
            {
                // Capture the generation on the raising thread, so a failure from an older playback is recognisable later.
                int g = Volatile.Read(ref generation);
                dispatcher.Post(() => OnFailed(g, message));
            };
            backend.Failed += onBackendFailed;
        }

        public AudioInputInfo Input { get; private set; }
        public AudioInputSource InputSource { get; private set; }
        public AudioOutputInfo Output { get; private set; }
        public bool OutputIsDefault { get; private set; } = true;
        public string Warning { get; private set; }
        public event Action Changed;

        public void Follow(VideoDeviceInfo video, Guid videoContainerId)
        {
            this.video = video;
            this.videoContainerId = videoContainerId;
            Reconcile();
        }

        public void DevicesChanged() => Reconcile();

        public void DefaultOutputChanged()
        {
            // Follows Windows when nothing is chosen, or when the chosen output is missing and we fell back to the default.
            if (OutputIsDefault) Reconcile();
        }

        public void SelectOutput(AudioOutputInfo output)
        {
            settings.AudioOutputId = output?.Id;
            settings.AudioOutputName = output?.Name;
            settingsChanged();
            Reconcile();
        }

        public void SelectInputOverride(AudioInputInfo input)
        {
            settings.AudioInputId = input?.Id;
            settings.AudioInputName = input?.Name;
            settingsChanged();
            Reconcile();
        }

        public void SetVolume(float slider)
        {
            settings.Volume = slider;
            ApplyGain();
        }

        public void SetMuted(bool muted)
        {
            settings.Muted = muted;
            ApplyGain();
        }

        /// <summary>Stops the audio and any retry, and stops listening to the backend.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CancelTimers();
            backend.Failed -= onBackendFailed;
            StopBackend();
        }

        float Gain() => settings.Muted ? 0f : VolumeCurve.ToGain(settings.Volume);

        void ApplyGain()
        {
            settingsChanged();
            try { if (backend.IsRunning) backend.SetGain(Gain()); }
            catch (Exception e) { log.Error("Setting audio gain failed", e); }
            Changed?.Invoke();
        }

        void OnFailed(int failedGeneration, string message)
        {
            if (disposed) return;
            if (failedGeneration != Volatile.Read(ref generation))
            {
                log.Info("Ignoring stale audio failure: " + message);
                return;
            }
            StopBackend();
            FailAndRetry("Audio backend failed: " + message, null, message);
            Changed?.Invoke();
        }

        /// <summary>Shows the error and tries again in 3 s. Only the first failure in a row is logged.</summary>
        void FailAndRetry(string logText, Exception error, string message)
        {
            if (!failing) log.Error(logText, error);
            failing = true;
            Warning = UiText.AudioRetrying(message);
            CancelTimers();
            retry = scheduler.After(RetryDelay, Reconcile);
        }

        /// <summary>A state with nothing to retry (no input, no output): the failure, if any, is over.</summary>
        void NotFailing()
        {
            failing = false;
            CancelTimers();
        }

        /// <summary>Audio plays again after failures: once it has kept playing for 3 s, log the recovery.</summary>
        void ConfirmRecovery()
        {
            recoveryCheck = null;
            if (!failing || runningInput == null || !backend.IsRunning) return;
            failing = false;
            log.Info("Audio recovered: playing " + runningInput.Name + " to " + runningOutput.Name);
        }

        void LogPlaying(AudioInputInfo input, AudioOutputInfo output)
        {
            string pair = input.Id + "|" + output.Id;
            if (pair == loggedPlaying) return;
            loggedPlaying = pair;
            log.Info("Audio playing: " + input.Name + " → " + output.Name);
        }

        void CancelTimers()
        {
            retry?.Dispose();
            retry = null;
            recoveryCheck?.Dispose();
            recoveryCheck = null;
        }

        void StopBackend()
        {
            Interlocked.Increment(ref generation);
            bool wasRunning = runningInput != null || backend.IsRunning;
            runningInput = null;
            runningOutput = null;
            if (!wasRunning) return;
            try { backend.Stop(); }
            catch (Exception e) { log.Error("Stopping audio failed", e); }
        }

        void Reconcile()
        {
            if (disposed) return;
            // A retry, or any explicit change, replaces the pending retry; failing again schedules a new one.
            retry?.Dispose();
            retry = null;

            string warning = null;

            AudioInputInfo input = null;
            var source = AudioInputSource.None;
            AudioOutputInfo output = null;
            bool isDefault = true;
            try
            {
                // 1. Input
                var inputs = backend.ListInputs();
                if (!string.IsNullOrEmpty(settings.AudioInputId) || !string.IsNullOrEmpty(settings.AudioInputName))
                {
                    input = inputs.FirstOrDefault(i => i.Id == settings.AudioInputId)
                            ?? inputs.FirstOrDefault(i => string.Equals(i.Name, settings.AudioInputName, StringComparison.OrdinalIgnoreCase));
                    if (input != null) source = AudioInputSource.Override;
                }
                if (input == null && video != null)
                {
                    var match = DeviceRules.MatchAudioInput(video, videoContainerId, inputs);
                    input = match.Input;
                    source = match.Source;
                }

                // 2. Output
                if (!string.IsNullOrEmpty(settings.AudioOutputId) || !string.IsNullOrEmpty(settings.AudioOutputName))
                {
                    var outputs = backend.ListOutputs();
                    output = outputs.FirstOrDefault(o => o.Id == settings.AudioOutputId)
                             ?? outputs.FirstOrDefault(o => string.Equals(o.Name, settings.AudioOutputName, StringComparison.OrdinalIgnoreCase));
                    if (output != null) isDefault = false;
                    else warning = UiText.OutputGone(settings.AudioOutputName);
                }
                if (output == null) output = backend.DefaultOutput();
            }
            catch (Exception e)
            {
                Input = null;
                InputSource = AudioInputSource.None;
                StopBackend();
                FailAndRetry("Enumerating audio devices failed", e, e.Message);
                Changed?.Invoke();
                return;
            }

            Input = input;
            InputSource = source;
            Output = output;
            OutputIsDefault = isDefault;

            // 3. No input
            if (input == null)
            {
                StopBackend();
                NotFailing();
                Warning = video != null ? UiText.NoAudio : null;
                Changed?.Invoke();
                return;
            }

            if (output == null)
            {
                StopBackend();
                NotFailing();
                Warning = UiText.AudioError("no audio output device");
                Changed?.Invoke();
                return;
            }

            // 4. Nothing changed
            if (runningInput != null && runningOutput != null && backend.IsRunning
                && runningInput.Id == input.Id && runningOutput.Id == output.Id)
            {
                Warning = warning;
                Changed?.Invoke();
                return;
            }

            // 5. Restart
            StopBackend();
            try
            {
                backend.Start(input, output, Gain());
                runningInput = input;
                runningOutput = output;
                Warning = warning;
                LogPlaying(input, output);
                if (failing)
                {
                    recoveryCheck?.Dispose();
                    recoveryCheck = scheduler.After(RetryDelay, ConfirmRecovery);
                }
            }
            catch (Exception e)
            {
                StopBackend();
                FailAndRetry("Starting audio failed", e, e.Message);
            }
            Changed?.Invoke();
        }
    }
}
