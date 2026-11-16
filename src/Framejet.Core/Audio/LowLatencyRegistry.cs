using System;
using System.Collections.Generic;

namespace Framejet.Core.Audio
{
    /// <summary>
    /// Remembers, for the life of the app, which audio endpoints can't run Windows low-latency shared mode:
    /// ones that failed to initialise (or have no IAudioClient3), and ones that underrun more than twice in 10 s.
    /// Underrun is called from the audio thread; the other calls come from the UI thread.
    /// </summary>
    public sealed class LowLatencyRegistry
    {
        public const int AllowedUnderruns = 2;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

        readonly object gate = new object();
        readonly HashSet<string> fallenBack = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, Queue<TimeSpan>> recent = new Dictionary<string, Queue<TimeSpan>>(StringComparer.Ordinal);

        /// <summary>Raised (outside the lock) once per endpoint, when underruns make it fall back.</summary>
        public event Action<string> FellBack;

        public bool ShouldTry(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId)) return false;
            lock (gate) return !fallenBack.Contains(endpointId);
        }

        /// <summary>Initialisation failed or the endpoint has no IAudioClient3.</summary>
        public void Unsupported(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId)) return;
            lock (gate)
            {
                fallenBack.Add(endpointId);
                recent.Remove(endpointId);
            }
        }

        /// <summary>True when this underrun makes more than 2 within 10 s; the endpoint is then fallen back.</summary>
        public bool Underrun(string endpointId, TimeSpan now)
        {
            if (string.IsNullOrEmpty(endpointId)) return false;
            lock (gate)
            {
                if (fallenBack.Contains(endpointId)) return false;
                if (!recent.TryGetValue(endpointId, out var times))
                    recent[endpointId] = times = new Queue<TimeSpan>();
                times.Enqueue(now);
                while (times.Count > AllowedUnderruns + 1) times.Dequeue();
                if (times.Count <= AllowedUnderruns || now - times.Peek() > Window) return false;
                fallenBack.Add(endpointId);
                recent.Remove(endpointId);
            }
            FellBack?.Invoke(endpointId);
            return true;
        }
    }
}
