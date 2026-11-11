using System.Runtime.InteropServices;

namespace Framejet.Platform.Os
{
    /// <summary>
    /// Keeps the display and the system awake while video runs: the controller is on the PS5, so the PC sees no input.
    /// The request belongs to the calling thread, so always call it from the UI thread.
    /// </summary>
    public static class KeepAwake
    {
        const uint EsContinuous = 0x80000000;
        const uint EsSystemRequired = 0x00000001;
        const uint EsDisplayRequired = 0x00000002;

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);

        /// <summary>True: 0x80000003 (continuous, system and display required). False: 0x80000000 (continuous only), which releases it.</summary>
        public static void Set(bool awake)
            => SetThreadExecutionState(awake ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
    }
}
