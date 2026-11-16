using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DirectShowLib;
using Framejet.Core;
using Framejet.Core.Video;

namespace Framejet.Platform.Video
{
    /// <summary>
    /// Takes each frame from the Sample Grabber, on DirectShow's streaming thread, and publishes it to the slot the
    /// presenter draws from, stamped with the moment it arrived.
    /// </summary>
    [ComVisible(true)]
    internal sealed class GrabberCallback : ISampleGrabberCB
    {
        readonly LatestFrameSlot _slot;
        readonly CaptureFormat _format;
        readonly ILog _log;
        volatile Action<long, double> _lumaTap;
        bool _mismatchLogged;

        public GrabberCallback(LatestFrameSlot slot, CaptureFormat format, ILog log)
        {
            _slot = slot ?? throw new ArgumentNullException(nameof(slot));
            _format = format ?? throw new ArgumentNullException(nameof(format));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// While set, called on the streaming thread with each frame's arrival stamp and mean luma, after the frame is
        /// handed over. It must return at once.
        /// </summary>
        public Action<long, double> LumaTap { set => _lumaTap = value; }

        /// <summary>Unused, since the grabber is set to call <see cref="BufferCB"/>; releases the sample it was given.</summary>
        public int SampleCB(double sampleTime, IMediaSample sample)
        {
            if (sample != null) Marshal.ReleaseComObject(sample);
            return 0;
        }

        public int BufferCB(double sampleTime, IntPtr buffer, int bufferLength)
        {
            long arrived = Stopwatch.GetTimestamp();
            _slot.Publish(buffer, bufferLength, arrived);
            _lumaTap?.Invoke(arrived, LumaSampler.Mean(buffer, bufferLength, _format));
            if (bufferLength != _slot.FrameBytes && !_mismatchLogged)
            {
                // Once per start, after the frame is handed over so it is not delayed; the stats line counts the rest.
                _mismatchLogged = true;
                _log.Error($"Frame size mismatch: expected {_slot.FrameBytes} bytes, got {bufferLength}; the picture may be shifted");
            }
            return 0;
        }
    }
}
