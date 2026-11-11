using System;

namespace Framejet.Core.Timing
{
    /// <summary>Schedules delayed actions on the UI thread. Disposing the result cancels the action.</summary>
    public interface IScheduler
    {
        TimeSpan Now { get; }
        IDisposable After(TimeSpan delay, Action action);
    }
}
