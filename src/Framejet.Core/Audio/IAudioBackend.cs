using System;
using System.Collections.Generic;
using Framejet.Core.Devices;

namespace Framejet.Core.Audio
{
    public interface IAudioBackend
    {
        IReadOnlyList<AudioInputInfo> ListInputs();
        IReadOnlyList<AudioOutputInfo> ListOutputs();
        AudioOutputInfo DefaultOutput();
        /// <summary>Starts playing the input to the output. Throws on failure.</summary>
        void Start(AudioInputInfo input, AudioOutputInfo output, float gain);
        void SetGain(float gain);
        void Stop();
        bool IsRunning { get; }
        /// <summary>Raised on any thread when playback fails after it started.</summary>
        event Action<string> Failed;
    }
}
