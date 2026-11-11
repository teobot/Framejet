using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Framejet.Core.Audio;
using Framejet.Core.Devices;
using Framejet.Core.Ui;
using Framejet.Core.Video;

namespace Framejet
{
    /// <summary>
    /// The floating toolbar over the bottom of the picture. It is its own borderless window owned by the main window,
    /// because the Direct3D picture can cover ordinary child controls. UI thread only.
    /// </summary>
    sealed class ToolbarWindow : Form
    {
        const int WmDpiChanged = 0x02E0;
        const int WsExToolWindow = 0x00000080;
        const int WsExNoActivate = 0x08000000;
        const double MaxOpacity = 0.92;
        const double FadeInMs = 150;
        const double FadeOutMs = 300;
        const int InsetPx = 8;
        const int HeightPx = 36;
        const int PadPx = 6;
        const int GapPx = 6;
        const int ControlHeightPx = 24;
        const string RescanGlyph = "⟳";
        const string FullscreenGlyph = "⛶";
        const string SpeakerGlyph = "\U0001F508";
        const string MutedGlyph = "\U0001F507";

        readonly MainForm _owner;
        readonly ToolTip _tips = new ToolTip { ShowAlways = true }; // the window is never active
        readonly Timer _fade = new Timer { Interval = 15 };
        readonly Stopwatch _fadeClock = new Stopwatch();
        readonly Font _glyphFont = new Font("Segoe UI Symbol", 10f);
        readonly ComboBox _devices, _modes, _outputs;
        readonly Button _rescan, _mute, _vsync, _fullscreen;
        readonly DarkSlider _volume;
        readonly Panel _separatorA, _separatorB;
        readonly Label _status;
        readonly List<Control> _items; // left to right, the status label last
        string _deviceKeys, _modeKeys, _outputKeys;
        List<Entry> _outputEntries = new List<Entry> { new Entry("", UiText.DefaultOutput, null) };
        bool _outputsListed = true;
        double _target;
        long _lastFadeMs;
        int _openDropDowns;
        bool _hover, _refreshing, _held;

        public ToolbarWindow(MainForm owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = DarkTheme.Toolbar;
            Opacity = 0;
            Font = SystemFonts.MessageBoxFont;

            _devices = Combo(150);
            _modes = Combo(150);
            _rescan = GlyphButton(RescanGlyph);
            _separatorA = Separator();
            _outputs = Combo(170);
            _mute = GlyphButton(SpeakerGlyph);
            _volume = new DarkSlider();
            _separatorB = Separator();
            _vsync = new Button { Text = "V-Sync", TabStop = false, UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat };
            _fullscreen = GlyphButton(FullscreenGlyph);
            _status = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = DarkTheme.MutedText,
                BackColor = DarkTheme.Toolbar,
                AutoEllipsis = true,
                UseMnemonic = false,
            };
            _items = new List<Control> { _devices, _modes, _rescan, _separatorA, _outputs, _mute, _volume, _separatorB, _vsync, _fullscreen, _status };
            StyleButton(_vsync, false);
            Controls.AddRange(_items.ToArray());

            _tips.SetToolTip(_rescan, "Rescan devices");
            _tips.SetToolTip(_mute, "Mute (M)");
            _tips.SetToolTip(_volume, "Volume");
            _tips.SetToolTip(_fullscreen, "Fullscreen (F11)");

            foreach (var combo in new[] { _devices, _modes, _outputs })
            {
                combo.DropDown += (s, e) => { _openDropDowns++; UpdateHeld(); };
                combo.DropDownClosed += (s, e) => { _openDropDowns = Math.Max(0, _openDropDowns - 1); UpdateHover(); }; // the pointer may still be over the toolbar
            }
            _devices.SelectionChangeCommitted += (s, e) => OnDeviceCommitted();
            _modes.SelectionChangeCommitted += (s, e) => OnModeCommitted();
            _outputs.SelectionChangeCommitted += (s, e) => OnOutputCommitted();
            _rescan.Click += (s, e) => _owner.RescanDevices();
            _mute.Click += (s, e) => _owner.Audio?.SetMuted(!_owner.Settings.Muted);
            _volume.ValueChanged += (s, e) => { if (!_refreshing) _owner.Audio?.SetVolume(_volume.Value); };
            _vsync.Click += (s, e) => { _owner.ToggleVSync(); ApplyToggles(); };
            _fullscreen.Click += (s, e) => _owner.ToggleFullscreen();

            HookHover(this);
            // Spec 4.6: the right-click menu works everywhere, so the toolbar and every control on it show the window's
            // menu at the pointer. While it is open the main window holds the toolbar up (as for a dropdown).
            UseMenu(this, owner.VideoContextMenu);
            owner.VideoContextMenu.Closed += OnMenuClosed;
            _fade.Tick += (s, e) => OnFadeTick();
            owner.Move += OnOwnerMoved;
            owner.Resize += OnOwnerMoved;
            LayoutControls();
        }

        /// <summary>True while the pointer is over the toolbar or one of its dropdowns is open.</summary>
        public bool Held => _held;

        /// <summary>Raised when <see cref="Held"/> changes.</summary>
        public event EventHandler HeldChanged;

        /// <summary>True when the window is not on screen at all (never shown, or faded out).</summary>
        public bool FullyHidden => !Visible;

        /// <summary>How many times the window has been brought on screen.</summary>
        public int ShownCount { get; private set; }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExNoActivate | WsExToolWindow;
                return cp;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            => _owner.HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

        float DpiScale => Math.Max(1, _owner.DeviceDpi) / 96f;

        int Px(int value) => (int)Math.Round(value * DpiScale);

        bool OwnerHidesToolbar => _owner.IsDisposed || _owner.WindowState == FormWindowState.Minimized || _owner.IsFullscreen;

        /// <summary>Puts the toolbar over the bottom of the main window's picture: 8 px in from the left, right and bottom, 36 px high.</summary>
        public void Reposition()
        {
            if (IsDisposed || _owner.IsDisposed || !_owner.IsHandleCreated || _owner.WindowState == FormWindowState.Minimized) return;
            var client = _owner.ClientRectangle;
            var origin = _owner.PointToScreen(client.Location);
            int inset = Px(InsetPx), height = Px(HeightPx);
            var width = Math.Max(1, client.Width - 2 * inset);
            var bounds = new Rectangle(origin.X + inset, origin.Y + client.Height - inset - height, width, height);
            if (Bounds != bounds) Bounds = bounds;
            LayoutControls();
        }

        public void ShowAnimated()
        {
            if (IsDisposed || OwnerHidesToolbar || !_owner.IsHandleCreated) return;
            if (!Visible)
            {
                RefreshContent();
                Reposition();
                Opacity = 0;
                ShownCount++;
                Show(); // ShowWithoutActivation: the main window keeps the focus
            }
            StartFade(MaxOpacity);
        }

        public void HideAnimated()
        {
            if (IsDisposed || !Visible) return;
            StartFade(0);
        }

        /// <summary>Hides at once, without fading: fullscreen and minimising.</summary>
        public void HideNow()
        {
            if (IsDisposed) return;
            _fade.Stop();
            _target = 0;
            Opacity = 0;
            if (Visible) Hide();
            _hover = false;
            UpdateHeld();
        }

        /// <summary>The text on the right: the mode, the frame rate and the warnings.</summary>
        public void SetStatus(string text)
        {
            if (IsDisposed || _status.Text == text) return;
            _status.Text = text ?? "";
        }

        /// <summary>
        /// Repopulates the lists from the sessions and selects the current items. It does not run the selection actions.
        /// </summary>
        public void RefreshContent() => RefreshContent(true);

        /// <summary>
        /// With <paramref name="enumerateOutputs"/> false the audio outputs are not listed again (a volume or mute change
        /// does not need the COM enumeration); the last list is used, and listed again if the current output is not in it.
        /// </summary>
        public void RefreshContent(bool enumerateOutputs)
        {
            if (IsDisposed || _refreshing) return;
            _refreshing = true;
            try
            {
                RefreshVideo();
                RefreshAudio(enumerateOutputs);
                ApplyToggles();
            }
            finally
            {
                _refreshing = false;
            }
        }

        void RefreshVideo()
        {
            var video = _owner.Video;
            var devices = video?.Devices ?? new List<VideoDeviceInfo>();
            Fill(_devices, ref _deviceKeys, devices.Select(d => new Entry(d.Path, d.Name, d)).ToList(),
                e => video.Device != null && ((VideoDeviceInfo)e.Value).Path == video.Device.Path);
            var modes = video?.Modes ?? new List<VideoMode>();
            Fill(_modes, ref _modeKeys, modes.Select(m => new Entry(m.Key, m.Label, m)).ToList(),
                e => video.Mode != null && ((VideoMode)e.Value).Key == video.Mode.Key);
        }

        bool CurrentOutputListed(AudioSession audio)
            => audio == null || audio.OutputIsDefault || audio.Output == null
               || _outputEntries.Any(e => e.Value != null && ((AudioOutputInfo)e.Value).Id == audio.Output.Id);

        void RefreshAudio(bool enumerateOutputs)
        {
            var audio = _owner.Audio;
            if (!enumerateOutputs && !CurrentOutputListed(audio)) enumerateOutputs = true;
            var entries = _outputEntries;
            bool listed = _outputsListed;
            if (enumerateOutputs)
            {
                entries = new List<Entry> { new Entry("", UiText.DefaultOutput, null) };
                listed = true;
                if (audio != null && _owner.AudioBackend != null)
                {
                    try
                    {
                        foreach (var o in _owner.AudioBackend.ListOutputs()) entries.Add(new Entry(o.Id, o.Name, o));
                    }
                    catch (Exception ex)
                    {
                        listed = false;
                        _owner.Log.Error("Listing audio outputs failed", ex);
                    }
                }
                _outputEntries = entries;
                _outputsListed = listed;
            }
            Fill(_outputs, ref _outputKeys, entries, e =>
                e.Value == null ? audio == null || audio.OutputIsDefault
                : !audio.OutputIsDefault && audio.Output != null && ((AudioOutputInfo)e.Value).Id == audio.Output.Id,
                fallbackFirst: true);
            _outputs.Enabled = audio != null && listed;
            _mute.Enabled = _volume.Enabled = audio != null;
            if (audio != null && !_volume.Dragging) _volume.Value = _owner.Settings.Volume; // never fight a drag
        }

        /// <summary>Repopulates only when the entries changed (a dropdown may be open), then selects the current one.</summary>
        void Fill(ComboBox combo, ref string keys, List<Entry> entries, Func<Entry, bool> isCurrent, bool fallbackFirst = false)
        {
            var newKeys = string.Join("\n", entries.Select(e => e.Key));
            if (newKeys != keys)
            {
                keys = newKeys;
                combo.BeginUpdate();
                combo.Items.Clear();
                foreach (var e in entries) combo.Items.Add(e);
                combo.EndUpdate();
            }
            combo.Enabled = entries.Count > 0;
            int index = entries.FindIndex(e => isCurrent(e));
            if (index < 0 && fallbackFirst && entries.Count > 0) index = 0;
            if (combo.SelectedIndex != index) combo.SelectedIndex = index;
        }

        /// <summary>Shows the current mute and V-Sync state.</summary>
        public void RefreshToggles() => ApplyToggles();

        void ApplyToggles()
        {
            _mute.Text = _owner.Settings.Muted ? MutedGlyph : SpeakerGlyph;
            StyleButton(_vsync, _owner.Settings.VSync);
        }

        void OnDeviceCommitted()
        {
            if (_refreshing) return;
            if (_devices.SelectedItem is Entry entry && _owner.Video != null) _owner.Video.SelectDevice((VideoDeviceInfo)entry.Value);
            RefreshContent();
        }

        void OnModeCommitted()
        {
            if (_refreshing) return;
            if (_modes.SelectedItem is Entry entry && _owner.Video != null) _owner.Video.SelectMode((VideoMode)entry.Value);
            RefreshContent();
        }

        void OnOutputCommitted()
        {
            if (_refreshing) return;
            if (_outputs.SelectedItem is Entry entry && _owner.Audio != null) _owner.Audio.SelectOutput((AudioOutputInfo)entry.Value);
            RefreshContent();
        }

        // ---- fade ----

        void StartFade(double target)
        {
            if (_target == target && (_fade.Enabled || Opacity == target)) return;
            _target = target;
            _lastFadeMs = 0;
            _fadeClock.Restart();
            _fade.Start();
        }

        void OnFadeTick()
        {
            if (IsDisposed) { _fade.Stop(); return; }
            var now = _fadeClock.ElapsedMilliseconds;
            var dt = Math.Max(1, now - _lastFadeMs);
            _lastFadeMs = now;
            var opacity = Opacity;
            if (_target > opacity) opacity = Math.Min(_target, opacity + dt * MaxOpacity / FadeInMs);
            else opacity = Math.Max(_target, opacity - dt * MaxOpacity / FadeOutMs);
            Opacity = opacity;
            if (opacity != _target) return;
            _fade.Stop();
            if (_target == 0)
            {
                if (Visible) Hide();
                _hover = false;
                UpdateHeld();
            }
        }

        // ---- following the main window ----

        void OnOwnerMoved(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (OwnerHidesToolbar)
            {
                HideNow();
                return;
            }
            if (Visible) Reposition();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutControls();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WmDpiChanged) Reposition();
        }

        void LayoutControls()
        {
            if (_items == null) return;
            var scale = DpiScale;
            var widths = new[] { 150, 150, 28, 0, 170, 28, 80, 0, 56, 28 };
            int x = Px(PadPx), gap = Px(GapPx), height = Px(ControlHeightPx), top = (ClientSize.Height - height) / 2;
            for (int i = 0; i < widths.Length; i++)
            {
                var c = _items[i];
                bool separator = widths[i] == 0;
                int w = separator ? Math.Max(1, (int)Math.Round(scale)) : Px(widths[i]);
                if (c is ComboBox combo)
                {
                    combo.ItemHeight = Math.Max(10, height - 6);
                    combo.DropDownWidth = Px(300);
                    c.SetBounds(x, top + (height - c.Height) / 2, w, c.Height);
                }
                else c.SetBounds(x, top, w, height);
                x += w + gap;
            }
            _status.SetBounds(x, top, Math.Max(0, ClientSize.Width - x - Px(PadPx)), height);
        }

        // ---- holding ----

        void HookHover(Control control)
        {
            control.MouseEnter += (s, e) => UpdateHover();
            control.MouseLeave += (s, e) => UpdateHover();
            foreach (Control child in control.Controls) HookHover(child);
        }

        static void UseMenu(Control control, ContextMenuStrip menu)
        {
            control.ContextMenuStrip = menu;
            foreach (Control child in control.Controls) UseMenu(child, menu);
        }

        /// <summary>The pointer may have left the toolbar while the menu was open, without a MouseLeave.</summary>
        void OnMenuClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            if (!IsDisposed) UpdateHover();
        }

        void UpdateHover()
        {
            _hover = Visible && Bounds.Contains(Cursor.Position);
            UpdateHeld();
        }

        void UpdateHeld()
        {
            var held = _hover || _openDropDowns > 0;
            if (held == _held) return;
            _held = held;
            HeldChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- building ----

        ComboBox Combo(int width)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed,
                BackColor = DarkTheme.Control,
                ForeColor = DarkTheme.Text,
                TabStop = false,
                Width = width,
                IntegralHeight = false,
                MaxDropDownItems = 12,
            };
            combo.DrawItem += DrawComboItem;
            return combo;
        }

        static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            var combo = (ComboBox)sender;
            bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
            bool selected = inList && (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? DarkTheme.AccentBack : DarkTheme.Control))
                e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index < 0) return;
            var text = combo.Items[e.Index].ToString();
            var bounds = Rectangle.Inflate(e.Bounds, -4, 0);
            TextRenderer.DrawText(e.Graphics, text, combo.Font, bounds, DarkTheme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        Button GlyphButton(string glyph)
        {
            var button = new Button { Text = glyph, Font = _glyphFont, TabStop = false, UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat };
            StyleButton(button, false);
            return button;
        }

        static void StyleButton(Button button, bool on)
        {
            button.BackColor = on ? DarkTheme.AccentBack : DarkTheme.Control;
            button.ForeColor = DarkTheme.Text;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = on ? DarkTheme.AccentBorder : DarkTheme.ControlBorder;
            button.FlatAppearance.MouseOverBackColor = on ? DarkTheme.AccentBack : DarkTheme.ControlBorder;
            button.FlatAppearance.MouseDownBackColor = DarkTheme.AccentBack;
            button.Padding = Padding.Empty;
        }

        static Panel Separator() => new Panel { BackColor = DarkTheme.ControlBorder, TabStop = false };

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(DarkTheme.ControlBorder))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owner.Move -= OnOwnerMoved;
                _owner.Resize -= OnOwnerMoved;
                _owner.VideoContextMenu.Closed -= OnMenuClosed;
                _fade.Stop();
                _fade.Dispose();
                _tips.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) _glyphFont.Dispose();
        }

        /// <summary>A list entry that shows its text.</summary>
        sealed class Entry
        {
            public Entry(string key, string text, object value)
            {
                Key = key ?? "";
                Text = text ?? "";
                Value = value;
            }

            public string Key { get; }
            public string Text { get; }
            public object Value { get; }
            public override string ToString() => Text;
        }
    }
}
