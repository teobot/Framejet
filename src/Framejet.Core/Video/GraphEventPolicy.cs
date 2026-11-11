namespace Framejet.Core.Video
{
    public enum GraphEventKind { None, DeviceLost, TakenByAnotherApp }

    /// <summary>Decides which DirectShow graph events mean the capture device is gone or its stream has died.</summary>
    public static class GraphEventPolicy
    {
        public const int EC_COMPLETE = 0x01;
        public const int EC_ERRORABORT = 0x03;
        public const int EC_STREAM_ERROR_STOPPED = 0x06;
        public const int EC_DEVICE_LOST = 0x1F;
        public const int EC_ERRORABORTEX = 0x45;
        /// <summary>MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED: another app opened the same capture device and Windows gave it the card.</summary>
        public const int MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED = unchecked((int)0xC00D3EA3);

        public static GraphEventKind Classify(int eventCode, long param1, long param2)
        {
            if ((eventCode == EC_ERRORABORT || eventCode == EC_ERRORABORTEX) && (int)param1 == MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED)
                return GraphEventKind.TakenByAnotherApp;
            return MeansDeviceLost(eventCode, param2) ? GraphEventKind.DeviceLost : GraphEventKind.None;
        }

        /// <summary>EC_DEVICE_LOST counts only with param2 0 (removed); param2 1 means the device is available again.</summary>
        public static bool MeansDeviceLost(int eventCode, long param2)
        {
            switch (eventCode)
            {
                case EC_DEVICE_LOST: return param2 == 0;
                case EC_ERRORABORT:
                case EC_ERRORABORTEX:
                case EC_STREAM_ERROR_STOPPED: return true;
                default: return false;
            }
        }
    }
}
