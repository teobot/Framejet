using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace Framejet.Platform.Tests.TestSupport
{
    internal static class Sta
    {
        public static void Run(Action body, int timeoutMs = 120000)
        {
            ExceptionDispatchInfo error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(timeoutMs))
                throw new TimeoutException("STA body did not finish within " + timeoutMs + " ms");
            error?.Throw();
        }

        public static void Pump(TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }
    }
}
