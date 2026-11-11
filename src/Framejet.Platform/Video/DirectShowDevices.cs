using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using DirectShowLib;
using Framejet.Core.Devices;
using Framejet.Core.Video;

namespace Framejet.Platform.Video
{
    public static class DirectShowDevices
    {
        public static IReadOnlyList<VideoDeviceInfo> List()
        {
            var result = new List<VideoDeviceInfo>();
            DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            try
            {
                foreach (var d in devices)
                    result.Add(new VideoDeviceInfo(d.Name, d.DevicePath));
            }
            finally
            {
                foreach (var d in devices) d.Dispose();
            }
            return result;
        }

        public static IReadOnlyList<VideoMode> ListRawModes(VideoDeviceInfo device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            DsDevice[] devices = null;
            IFilterGraph2 graph = null;
            IBaseFilter source = null;
            IPin pin = null;
            IntPtr caps = IntPtr.Zero;
            try
            {
                devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
                DsDevice match = null;
                foreach (var d in devices)
                    if (string.Equals(d.DevicePath, device.Path, StringComparison.OrdinalIgnoreCase)) { match = d; break; }
                if (match == null) throw new VideoStartException(device.Name + ": device not found");

                graph = (IFilterGraph2)new FilterGraph();
                Check(graph.AddSourceFilterForMoniker(match.Mon, null, match.Name, out source), device, "open device");

                pin = DsFindPin.ByCategory(source, PinCategory.Capture, 0);
                if (pin == null) throw new VideoStartException(device.Name + ": no capture pin");
                var config = pin as IAMStreamConfig;
                if (config == null) throw new VideoStartException(device.Name + ": no stream configuration");
                var videoControl = source as IAMVideoControl;

                Check(config.GetNumberOfCapabilities(out int count, out int size), device, "count capabilities");
                caps = Marshal.AllocCoTaskMem(size);

                var modes = new List<VideoMode>();
                for (int i = 0; i < count; i++)
                {
                    AMMediaType mt = null;
                    try
                    {
                        if (config.GetStreamCaps(i, out mt, caps) != 0) continue;
                        if (!TryGetSize(mt, out int w, out int h)) continue;
                        var subtype = mt.subType;
                        var format = PixelFormats.FromFourCC(FourCC(subtype));
                        foreach (long interval in FrameIntervals(videoControl, pin, i, w, h, caps, size))
                            modes.Add(new VideoMode(w, h, interval, format, subtype));
                    }
                    finally
                    {
                        if (mt != null) DsUtils.FreeAMMediaType(mt);
                    }
                }
                return modes;
            }
            catch (VideoStartException) { throw; }
            catch (Exception ex) when (ex is COMException || ex is InvalidCastException || ex is InvalidOperationException)
            {
                throw new VideoStartException(device.Name + ": " + ex.Message, ex);
            }
            finally
            {
                if (caps != IntPtr.Zero) Marshal.FreeCoTaskMem(caps);
                Release(pin);
                Release(source);
                Release(graph);
                if (devices != null) foreach (var d in devices) d.Dispose();
            }
        }

        private static void Check(int hr, VideoDeviceInfo device, string what)
        {
            if (hr < 0) throw new VideoStartException(device.Name + ": " + what + " failed: 0x" + hr.ToString("X8"));
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        private static bool TryGetSize(AMMediaType mt, out int w, out int h)
        {
            w = h = 0;
            if (mt.formatPtr == IntPtr.Zero) return false;
            if (mt.formatType == FormatType.VideoInfo)
            {
                var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader));
                w = vih.BmiHeader.Width; h = Math.Abs(vih.BmiHeader.Height);
            }
            else if (mt.formatType == FormatType.VideoInfo2)
            {
                var vih = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2));
                w = vih.BmiHeader.Width; h = Math.Abs(vih.BmiHeader.Height);
            }
            else return false;
            return w > 0 && h > 0;
        }

        private static string FourCC(Guid subtype)
        {
            var b = subtype.ToByteArray();
            for (int i = 0; i < 4; i++)
                if (b[i] < 0x20 || b[i] > 0x7E) return "";
            return new string(new[] { (char)b[0], (char)b[1], (char)b[2], (char)b[3] });
        }

        private static List<long> FrameIntervals(IAMVideoControl control, IPin pin, int index, int w, int h, IntPtr caps, int capsSize)
        {
            var found = new List<long>();
            if (control != null)
            {
                IntPtr list = IntPtr.Zero;
                try
                {
                    if (control.GetFrameRateList(pin, index, new Size(w, h), out int n, out list) == 0 && list != IntPtr.Zero)
                    {
                        for (int k = 0; k < n; k++)
                        {
                            long v = Marshal.ReadInt64(list, k * 8);
                            if (v > 0 && !found.Contains(v)) found.Add(v);
                        }
                    }
                }
                finally
                {
                    if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list);
                }
            }
            if (found.Count == 0 && capsSize >= Marshal.SizeOf(typeof(VideoStreamConfigCaps)))
            {
                var vscc = (VideoStreamConfigCaps)Marshal.PtrToStructure(caps, typeof(VideoStreamConfigCaps));
                if (vscc.MinFrameInterval > 0) found.Add(vscc.MinFrameInterval);
            }
            return found;
        }
    }
}
