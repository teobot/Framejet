using System;
using System.Windows.Forms;
using Framejet.Core.Timing;

namespace Framejet
{
    /// <summary>Posts actions to the UI thread of <c>control</c>, from any thread. Once its handle is gone they are dropped.</summary>
    sealed class ControlDispatcher : IDispatcher
    {
        readonly Control _control;

        public ControlDispatcher(Control control)
        {
            _control = control ?? throw new ArgumentNullException(nameof(control));
        }

        public void Post(Action action)
        {
            if (action == null || !_control.IsHandleCreated) return;
            try
            {
                _control.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The handle was destroyed between the check and the post: the window is closing.
            }
        }
    }
}
