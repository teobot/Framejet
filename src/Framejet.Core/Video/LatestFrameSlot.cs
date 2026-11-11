using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Framejet.Core.Video
{
    /// <summary>
    /// Thread-safe single slot holding only the newest captured frame. The counters are read without the lock, so a
    /// status poll never waits behind a frame copy.
    /// </summary>
    public sealed class LatestFrameSlot
    {
        readonly object _lock = new object();
        readonly AutoResetEvent _ready = new AutoResetEvent(false);
        byte[] _pending;
        bool _hasPending;
        long _pendingTimestamp;
        long _published, _replaced;
        int _sizeMismatches, _firstMismatchBytes;

        public LatestFrameSlot(int frameBytes)
        {
            FrameBytes = frameBytes;
            _pending = new byte[frameBytes];
        }

        public int FrameBytes { get; }
        public WaitHandle FrameReady => _ready;
        public long Published => Interlocked.Read(ref _published);
        public long Replaced => Interlocked.Read(ref _replaced);
        /// <summary>Frames whose buffer was not <see cref="FrameBytes"/> long.</summary>
        public int SizeMismatches => Volatile.Read(ref _sizeMismatches);
        /// <summary>The length of the first such buffer; 0 while there has been none.</summary>
        public int FirstMismatchBytes => Volatile.Read(ref _firstMismatchBytes);

        /// <summary>
        /// Copies the frame in as the newest. A buffer of the wrong size is counted and still handed over: a longer one
        /// is cut to <see cref="FrameBytes"/>, and a shorter one leaves older bytes after its end.
        /// </summary>
        public void Publish(IntPtr data, int length, long arrivalTimestamp)
        {
            lock (_lock)
            {
                if (length != FrameBytes)
                {
                    if (_sizeMismatches == 0) Volatile.Write(ref _firstMismatchBytes, length);
                    Interlocked.Increment(ref _sizeMismatches);
                }
                int n = Math.Min(length, FrameBytes);
                if (n > 0) Marshal.Copy(data, _pending, 0, n);
                if (_hasPending) Interlocked.Increment(ref _replaced);
                _hasPending = true;
                _pendingTimestamp = arrivalTimestamp;
                Interlocked.Increment(ref _published);
            }
            _ready.Set();
        }

        /// <summary>Swaps the caller's buffer with the pending frame. Returns false when nothing new has arrived.</summary>
        public bool TryTake(ref byte[] buffer, out long arrivalTimestamp)
        {
            lock (_lock)
            {
                if (!_hasPending) { arrivalTimestamp = 0; return false; }
                var spare = buffer != null && buffer.Length == FrameBytes ? buffer : new byte[FrameBytes];
                buffer = _pending;
                _pending = spare;
                _hasPending = false;
                arrivalTimestamp = _pendingTimestamp;
                return true;
            }
        }
    }
}
