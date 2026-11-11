using System;
using Framejet.Core.Devices;

namespace Framejet.Core.Tests.Devices
{
    public static class TestDevices
    {
        public static readonly VideoDeviceInfo Ripsaw = new("Razer Ripsaw X", @"@device:pnp:\\?\usb#vid_1532&pid_0e6f&mi_00#a#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global");
        public static readonly VideoDeviceInfo RipsawOtherPort = new("Razer Ripsaw X", @"@device:pnp:\\?\usb#vid_1532&pid_0e6f&mi_00#b#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global");
        public static readonly VideoDeviceInfo Webcam = new("USB Webcam", @"@device:pnp:\\?\usb#vid_abcd&pid_0001&mi_00#c#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global");
        public static readonly VideoDeviceInfo PhoneCamera = new("Phone (Windows Virtual Camera)", "@device:sw:phone");
        public static readonly VideoDeviceInfo EffectsCamera = new("Camera (Virtual Effects)", "@device:sw:effects");
        public static readonly Guid RipsawContainer = new("A1B2C3D4-0000-4000-8000-000000000001");
        public static readonly Guid WebcamContainer = new("A1B2C3D4-0000-4000-8000-000000000002");
        public static readonly AudioInputInfo RipsawHdmi = new("{0.0.1.00000000}.{ripsaw}", "HDMI (Razer Ripsaw X)", RipsawContainer);
        public static readonly AudioInputInfo HeadsetMic = new("{0.0.1.00000000}.{headset}", "Microphone (USB Headset)", new Guid("A1B2C3D4-0000-4000-8000-000000000003"));
        public static readonly AudioInputInfo VirtualMic = new("{0.0.1.00000000}.{virtual}", "Microphone (Virtual Audio Device)", DeviceRules.NoContainer);
        public static readonly AudioInputInfo StreamingMic = new("{0.0.1.00000000}.{streaming}", "Microphone (Streaming Audio Device)", DeviceRules.NoContainer);
        public static readonly AudioOutputInfo Speakers = new("{0.0.0.00000000}.{speakers}", "Speakers (High Definition Audio Device)");
        public static readonly AudioOutputInfo Headphones = new("{0.0.0.00000000}.{headset}", "Headphones (USB Headset)");
        public static readonly AudioOutputInfo UsbSpeakers = new("{0.0.0.00000000}.{usb-speakers}", "Speakers (USB Audio Device)");
        public static AudioInputInfo[] AllInputs() => new[] { HeadsetMic, VirtualMic, StreamingMic, RipsawHdmi };
    }
}
