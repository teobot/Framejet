using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Framejet
{
    /// <summary>
    /// One Framejet per user session, so two copies never fight over the card. A second launch asks the running
    /// one to come forward with <see cref="ActivateMessage"/>.
    /// </summary>
    sealed class SingleInstance : IDisposable
    {
        const string MutexName = @"Local\Framejet.SingleInstance";
        static readonly IntPtr HwndBroadcast = new IntPtr(0xFFFF);
        const int AsfwAny = -1;

        /// <summary>Broadcast by a second launch; the running window restores and activates itself. 0 if registration failed.</summary>
        public static readonly int ActivateMessage = RegisterWindowMessage("Framejet.Activate");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllowSetForegroundWindow(int processId);

        readonly Mutex _mutex;

        SingleInstance(Mutex mutex) { _mutex = mutex; }

        /// <summary>Null when another Framejet already runs in this session.</summary>
        public static SingleInstance TryAcquire()
        {
            var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (createdNew) return new SingleInstance(mutex);
            mutex.Dispose();
            return null;
        }

        /// <summary>Asks the running Framejet to come forward. It may take the foreground, because this launch hands that right on.</summary>
        public static void ActivateExisting()
        {
            if (ActivateMessage == 0) return;
            AllowSetForegroundWindow(AsfwAny);
            PostMessage(HwndBroadcast, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
