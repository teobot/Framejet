using System;
using Framejet.Core.Timing;

namespace Framejet.Core.Tests.Fakes
{
    public sealed class ImmediateDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }
}
