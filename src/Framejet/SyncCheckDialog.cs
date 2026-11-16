using System;
using System.Drawing;
using System.Windows.Forms;
using Framejet.Core;
using Framejet.Core.Audio;
using Framejet.Core.Ui;

namespace Framejet
{
    /// <summary>
    /// The Check A/V sync dialog: the prompt with Start and Cancel, then the countdown, then the result and the split
    /// with Close. Closing it while the check runs cancels the check.
    /// </summary>
    sealed class SyncCheckDialog : Form
    {
        readonly SyncCheck _check;
        readonly Func<bool> _canStart;
        readonly ILog _log;
        readonly Label _text, _split;
        readonly Button _start, _close;

        /// <param name="canStart">Whether the picture and the sound both run, checked each time Start is pressed.</param>
        public SyncCheckDialog(SyncCheck check, Func<bool> canStart, ILog log)
        {
            _check = check ?? throw new ArgumentNullException(nameof(check));
            _canStart = canStart ?? throw new ArgumentNullException(nameof(canStart));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            Text = UiText.SyncMenu.TrimEnd('…');
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = DarkTheme.Window;
            ForeColor = DarkTheme.Text;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(440, 150);

            _text = new Label { Text = UiText.SyncPrompt, ForeColor = DarkTheme.Text, Location = new Point(16, 16), Size = new Size(408, 54) };
            _split = new Label { ForeColor = DarkTheme.MutedText, Location = new Point(16, 74), Size = new Size(408, 22) };
            _start = DialogButton("Start", new Point(256, 108));
            _close = DialogButton("Cancel", new Point(344, 108));
            _start.Click += (s, e) => StartCheck();
            _close.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { _text, _split, _start, _close });
            AcceptButton = _start;
            CancelButton = _close;

            _check.SecondsLeft += OnSecondsLeft;
            _check.Finished += OnFinished;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTheme.DarkTitleBar(Handle);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _check.SecondsLeft -= OnSecondsLeft;
            _check.Finished -= OnFinished;
            _check.Cancel(); // nothing to do unless it is still running
            base.OnFormClosed(e);
        }

        void StartCheck()
        {
            _split.Text = "";
            if (!_canStart())
            {
                _text.Text = UiText.SyncSourceStopped;
                AcceptButton = _start;
                return;
            }
            _start.Enabled = false;
            _close.Text = "Cancel";
            AcceptButton = null; // Enter must not cancel a running check; Esc still does
            try
            {
                _check.Start();
            }
            catch (Exception ex)
            {
                _log.Error("The A/V sync check couldn't start", ex);
                _text.Text = UiText.SyncSourceStopped;
                _start.Enabled = true;
                AcceptButton = _start;
            }
        }

        void OnSecondsLeft(int seconds) => _text.Text = UiText.SyncChecking(seconds);

        void OnFinished(string headline, string split)
        {
            _text.Text = headline;
            _split.Text = split ?? "";
            _start.Enabled = true;
            _close.Text = "Close";
            AcceptButton = _close;
        }

        static Button DialogButton(string text, Point location)
        {
            var button = new Button { Text = text, Location = location, Size = new Size(80, 28), UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat };
            button.BackColor = DarkTheme.Control;
            button.ForeColor = DarkTheme.Text;
            button.FlatAppearance.BorderColor = DarkTheme.ControlBorder;
            button.FlatAppearance.MouseOverBackColor = DarkTheme.ControlBorder;
            button.FlatAppearance.MouseDownBackColor = DarkTheme.AccentBack;
            return button;
        }
    }
}
