using System;
using System.Collections.Generic;
using Framejet.Core.Audio;
using Framejet.Core.Devices;
using static Framejet.Core.Tests.Devices.TestDevices;

namespace Framejet.Core.Tests.Fakes
{
    public sealed class FakeAudioBackend : IAudioBackend
    {
        public List<AudioInputInfo> Inputs = new List<AudioInputInfo>(AllInputs());
        public List<AudioOutputInfo> Outputs = new List<AudioOutputInfo> { Speakers, Headphones, UsbSpeakers };
        public AudioOutputInfo Default = Speakers;
        public Exception ListInputsThrows;
        public Exception StartError;
        public List<(AudioInputInfo input, AudioOutputInfo output, float gain)> Starts = new List<(AudioInputInfo, AudioOutputInfo, float)>();
        public int Stops;
        public List<float> Gains = new List<float>();

        public IReadOnlyList<AudioInputInfo> ListInputs() { if (ListInputsThrows != null) throw ListInputsThrows; return Inputs.ToArray(); }
        public IReadOnlyList<AudioOutputInfo> ListOutputs() => Outputs.ToArray();
        public AudioOutputInfo DefaultOutput() => Default;
        public bool IsRunning { get; private set; }
        public event Action<string> Failed;

        public void Start(AudioInputInfo input, AudioOutputInfo output, float gain)
        {
            Starts.Add((input, output, gain));
            if (StartError != null) throw StartError;
            IsRunning = true;
        }
        public void SetGain(float gain) => Gains.Add(gain);
        public void Stop() { Stops++; IsRunning = false; }
        public void RaiseFailed(string message) => Failed?.Invoke(message);
    }
}
