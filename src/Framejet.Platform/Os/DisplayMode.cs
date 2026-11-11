using System;
using System.Runtime.InteropServices;

namespace Framejet.Platform.Os
{
    /// <summary>
    /// The refresh rate a screen is set to, as Windows reports it for the current display mode. Unlike the DXGI frame
    /// statistics it doesn't wobble in fullscreen with no signal, so it is the reference for judging frame delay.
    /// </summary>
    public static class DisplayMode
    {
        const int EnumCurrentSettings = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode mode);

        /// <summary>
        /// The current mode's refresh in Hz for a screen's device name (such as <c>\.\DISPLAY1</c>, <c>Screen.DeviceName</c>).
        /// 0 when unknown: the call fails, or Windows reports 0 or 1, which mean "hardware default". Never throws.
        /// </summary>
        public static int CurrentRefreshHz(string deviceName)
        {
            try
            {
                var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf(typeof(DevMode)) };
                if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode)) return 0;
                return mode.dmDisplayFrequency <= 1 ? 0 : (int)mode.dmDisplayFrequency;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
