using System;
using System.Collections.Generic;

namespace Framejet.Core.Tests.Fakes
{
    public sealed class RecordingLog : ILog
    {
        public List<string> Infos { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public void Info(string message) => Infos.Add(message);
        public void Error(string message, Exception error = null) => Errors.Add(message);
    }
}
