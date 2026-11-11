using System;

namespace Framejet.Core
{
    public interface ILog
    {
        void Info(string message);
        void Error(string message, Exception error = null);
    }

    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        NullLog() { }
        public void Info(string message) { }
        public void Error(string message, Exception error = null) { }
    }
}
