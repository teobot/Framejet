using System;
using System.Collections.Generic;
using Framejet.Core.Timing;

namespace Framejet.Core.Tests.Fakes
{
    public sealed class QueuedDispatcher : IDispatcher
    {
        readonly Queue<Action> queue = new Queue<Action>();
        public void Post(Action action) => queue.Enqueue(action);
        public void RunAll() { while (queue.Count > 0) queue.Dequeue()(); }
    }
}
