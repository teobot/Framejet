using System;
using Framejet.Core.Timing;

namespace Framejet.Platform.Tests.TestSupport
{
    internal sealed class ImmediateTestDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }
}
