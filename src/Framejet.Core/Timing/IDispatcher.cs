using System;

namespace Framejet.Core.Timing
{
    public interface IDispatcher
    {
        void Post(Action action);
    }
}
