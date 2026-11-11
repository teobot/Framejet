using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Framejet.Core.Devices;
using Framejet.Core.Ui;

namespace Framejet
{
    /// <summary>The right-click menu of the picture. It is refilled each time it opens, so the checks and the submenus are current.</summary>
    static class VideoMenu
    {
        public static ContextMenuStrip Build(MainForm form)
        {
            if (form == null) throw new ArgumentNullException(nameof(form));
            var menu = new ContextMenuStrip
            {
                Renderer = DarkTheme.MenuRenderer(),
                BackColor = DarkTheme.MenuBack,
                ForeColor = DarkTheme.Text,
                ShowCheckMargin = true,
                ShowImageMargin = false,
            };
            menu.Opening += (s, e) => Fill(menu, form);
            Fill(menu, form);
            return menu;
        }

        public static string[] TopLevelTexts(ContextMenuStrip menu)
            => menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray();

        static void Fill(ContextMenuStrip menu, MainForm form)
        {
            var old = menu.Items.Cast<ToolStripItem>().ToList();
            menu.Items.Clear();
            foreach (var item in old) item.Dispose();

            var fullscreen = Item("Fullscreen", form.IsFullscreen, form.ToggleFullscreen);
            fullscreen.ShortcutKeyDisplayString = "F11";
            var mute = Item("Mute", form.Settings.Muted, () => form.Audio?.SetMuted(!form.Settings.Muted));
            mute.ShortcutKeyDisplayString = "M";

            menu.Items.Add(new ToolStripMenuItem(UiText.NameAndVersion(Application.ProductVersion)) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(fullscreen);
            menu.Items.Add(mute);
            menu.Items.Add(Item("V-Sync", form.Settings.VSync, form.ToggleVSync));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(VideoDevices(form));
            menu.Items.Add(Modes(form));
            menu.Items.Add(Outputs(form));
            menu.Items.Add(Inputs(form));
            menu.Items.Add(Item("Rescan devices", false, form.RescanDevices));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, form.Close));
            Style(menu.Items);
        }

        static ToolStripMenuItem VideoDevices(MainForm form)
        {
            var root = new ToolStripMenuItem("Video device");
            var video = form.Video;
            if (video?.Devices != null)
                foreach (var d in video.Devices)
                {
                    var device = d;
                    root.DropDownItems.Add(Item(Escape(device.Name), video.Device != null && video.Device.Path == device.Path, () => video.SelectDevice(device)));
                }
            return DisableIfEmpty(root);
        }

        static ToolStripMenuItem Modes(MainForm form)
        {
            var root = new ToolStripMenuItem("Mode");
            var video = form.Video;
            if (video?.Modes != null)
                foreach (var m in video.Modes)
                {
                    var mode = m;
                    root.DropDownItems.Add(Item(Escape(mode.Label), video.Mode != null && video.Mode.Key == mode.Key, () => video.SelectMode(mode)));
                }
            return DisableIfEmpty(root);
        }

        static ToolStripMenuItem Outputs(MainForm form)
        {
            var root = new ToolStripMenuItem("Audio output");
            var audio = form.Audio;
            if (audio == null || form.AudioBackend == null) return DisableIfEmpty(root);
            IReadOnlyList<AudioOutputInfo> outputs;
            try { outputs = form.AudioBackend.ListOutputs(); }
            catch (Exception ex)
            {
                form.Log.Error("Listing audio outputs failed", ex);
                return Unavailable(root);
            }
            root.DropDownItems.Add(Item(UiText.DefaultOutput, audio.OutputIsDefault, () => audio.SelectOutput(null)));
            foreach (var o in outputs)
            {
                var output = o;
                root.DropDownItems.Add(Item(Escape(output.Name), !audio.OutputIsDefault && audio.Output != null && audio.Output.Id == output.Id, () => audio.SelectOutput(output)));
            }
            return root;
        }

        static ToolStripMenuItem Inputs(MainForm form)
        {
            var overridden = !string.IsNullOrEmpty(form.Settings.AudioInputId);
            var name = overridden && !string.IsNullOrEmpty(form.Settings.AudioInputName) ? form.Settings.AudioInputName : UiText.Automatic;
            var root = new ToolStripMenuItem("Audio input (" + Escape(name) + ")");
            var audio = form.Audio;
            if (audio == null || form.AudioBackend == null) return DisableIfEmpty(root);
            IReadOnlyList<AudioInputInfo> inputs;
            try { inputs = form.AudioBackend.ListInputs(); }
            catch (Exception ex)
            {
                form.Log.Error("Listing audio inputs failed", ex);
                return Unavailable(root);
            }
            root.DropDownItems.Add(Item(UiText.Automatic, !overridden, () => audio.SelectInputOverride(null)));
            foreach (var i in inputs)
            {
                var input = i;
                root.DropDownItems.Add(Item(Escape(input.Name), overridden && form.Settings.AudioInputId == input.Id, () => audio.SelectInputOverride(input)));
            }
            return root;
        }

        /// <summary>An ampersand in a name would otherwise underline the next letter as a mnemonic.</summary>
        static string Escape(string text) => (text ?? "").Replace("&", "&&");

        static ToolStripMenuItem Unavailable(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();
            root.DropDownItems.Add(new ToolStripMenuItem("(unavailable)") { Enabled = false });
            return root;
        }

        static ToolStripMenuItem DisableIfEmpty(ToolStripMenuItem root)
        {
            if (root.DropDownItems.Count == 0) root.Enabled = false;
            return root;
        }

        static ToolStripMenuItem Item(string text, bool isChecked, Action click)
        {
            var item = new ToolStripMenuItem(text) { Checked = isChecked };
            item.Click += (s, e) => click();
            return item;
        }

        static void Style(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = item.Enabled ? DarkTheme.Text : DarkTheme.MutedText;
                item.BackColor = DarkTheme.MenuBack;
                if (item is ToolStripMenuItem mi) Style(mi.DropDownItems);
            }
        }
    }
}
