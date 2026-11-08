using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AFVReplay.Input;
using AFVReplay.VatSys;

namespace AFVReplay.UI
{
    /// <summary>
    /// Adds "Replay Last" and "Replay Coord" rows to vatSys's Keyboard Settings window, under Quick Text in the Comms column.
    /// It looks and behaves like the PTT buttons: left click to capture (keyboard, mouse or joystick),
    /// right click to capture without joysticks, middle click to clear.
    /// </summary>
    internal sealed class KeybindInjector : IDisposable
    {
        private const string WindowTypeName = "vatsys.KeyboardSettingsWindow";

        /// <summary>One key row: its label, the poller that listens for it, and where it's saved.</summary>
        internal sealed class Binding
        {
            public Binding(string label, string buttonName, InputPoller poller, Func<string> get, Action<string> set)
            {
                Label = label; ButtonName = buttonName; Poller = poller; Get = get; Set = set;
            }
            public string Label { get; }
            public string ButtonName { get; }
            public InputPoller Poller { get; }
            public Func<string> Get { get; }
            public Action<string> Set { get; }
        }

        private readonly Binding[] bindings;
        private readonly Action saveSettings;
        private System.Windows.Forms.Timer timer;
        private CancellationTokenSource capture;
        private bool includeJoysticks = true;
        private bool failedOnce;

        public KeybindInjector(Action saveSettings, params Binding[] bindings)
        {
            this.saveSettings = saveSettings;
            this.bindings = bindings;
        }

        /// <summary>Call on the UI thread.</summary>
        public void Start()
        {
            timer = new System.Windows.Forms.Timer { Interval = 300 };
            timer.Tick += (s, e) => Scan();
            timer.Start();
        }

        private void Scan()
        {
            foreach (Form form in Application.OpenForms)
            {
                if (form.GetType().FullName != WindowTypeName || form.IsDisposed) continue;
                if (form.Controls.Find(bindings[0].ButtonName, true).Length > 0) continue;
                try
                {
                    Inject(form);
                }
                catch (Exception ex)
                {
                    if (!failedOnce)
                    {
                        failedOnce = true;
                        Diag.UserError("Could not add the Replay keys to the Keyboard window.", ex);
                    }
                }
            }
        }

        private void Inject(Form form)
        {
            var table = GetField<TableLayoutPanel>(form, "tableLayoutPanel1");
            var templateButton = GetField<CheckBox>(form, "textToggleButton");
            var templateLabel = GetField<Label>(form, "textLabel28");
            if (table == null || templateButton == null || templateLabel == null)
            {
                throw new InvalidOperationException("Keyboard window layout not recognised");
            }

            int templateRow = table.GetRow(templateButton);
            var style = templateRow >= 0 && templateRow < table.RowStyles.Count ? table.RowStyles[templateRow] : null;
            table.SuspendLayout();
            foreach (var binding in bindings)
            {
                var label = (Label)Activator.CreateInstance(templateLabel.GetType(), true);
                CopyProperties(templateLabel, label, "Anchor", "AutoSize", "Font", "ForeColor", "BackColor", "Margin", "TextAlign", "HasBorder", "InteractiveText");
                label.Text = binding.Label;

                var button = (CheckBox)Activator.CreateInstance(templateButton.GetType(), true);
                CopyProperties(templateButton, button, "Appearance", "FlatStyle", "Font", "Size", "BackColor", "ForeColor", "TextAlign", "Margin", "Anchor", "SubFont", "UseVisualStyleBackColor");
                button.AutoCheck = false;
                button.Name = binding.ButtonName;
                button.Tag = binding;
                button.BackColor = vatsys.Colours.GetColour(vatsys.Colours.Identities.VSCSBackground);
                button.Text = InputBinding.Describe(binding.Get());
                button.MouseUp += Button_MouseUp;
                button.CheckedChanged += Button_CheckedChanged;
                button.LostFocus += (s, e) => { if (button.Checked) button.Checked = false; };

                int row = table.RowCount;
                table.RowCount = row + 1;
                table.RowStyles.Add(style != null ? new RowStyle(style.SizeType, style.Height) : new RowStyle());
                table.Controls.Add(label, 0, row);
                table.Controls.Add(button, 1, row);
            }
            table.ResumeLayout(true);

            // The table grows itself; make sure the window is tall enough to show the new row.
            var parent = table.Parent;
            if (parent != null)
            {
                int overflow = table.Bottom + table.Margin.Bottom - parent.ClientSize.Height;
                if (overflow > 0) form.Height += overflow;
            }
        }

        private void Button_MouseUp(object sender, MouseEventArgs e)
        {
            var button = (CheckBox)sender;
            switch (e.Button)
            {
                case MouseButtons.Middle:
                    if (button.Checked) button.Checked = false;
                    SetBinding((Binding)button.Tag, "");
                    button.Text = "";
                    break;
                case MouseButtons.Left:
                    includeJoysticks = true;
                    button.Checked = !button.Checked;
                    break;
                case MouseButtons.Right:
                    includeJoysticks = false;
                    button.Checked = !button.Checked;
                    break;
            }
        }

        private async void Button_CheckedChanged(object sender, EventArgs e)
        {
            var button = (CheckBox)sender;
            if (!button.Checked)
            {
                Cursor.Clip = Rectangle.Empty;
                capture?.Cancel();
                return;
            }
            if (capture != null) return; // already capturing

            var binding = (Binding)button.Tag;
            Cursor.Clip = button.RectangleToScreen(button.ClientRectangle);
            capture = new CancellationTokenSource();
            foreach (var b in bindings) b.Poller.Paused = true;
            string json = null;
            try
            {
                // vatSys's input loop has to be stopped while capturing; PTT is briefly unavailable,
                // exactly as when setting a PTT key.
                VatSysBridge.StopVatSysInput();
                json = await VatSysBridge.MapInputAsync(includeJoysticks, capture.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Diag.UserError("Could not capture the Replay key.", ex);
            }
            finally
            {
                try { VatSysBridge.StartVatSysInput(); }
                catch (Exception ex) { Diag.UserError("vatSys input could not be restarted. Reopen the Keyboard window and set any key to restore PTT.", ex); }
                foreach (var b in bindings) b.Poller.Paused = false;
                capture.Dispose();
                capture = null;
            }

            void Finish()
            {
                if (json != null && !InputBinding.Describe(json).Equals("")) SetBinding(binding, json);
                button.Text = InputBinding.Describe(binding.Get());
                Cursor.Clip = Rectangle.Empty;
                if (button.Checked) button.Checked = false;
            }
            if (button.IsDisposed) return;
            if (button.InvokeRequired) button.BeginInvoke((MethodInvoker)Finish); else Finish();
        }

        private void SetBinding(Binding binding, string json)
        {
            binding.Set(json ?? "");
            binding.Poller.SetBinding(binding.Get());
            saveSettings();
        }

        private static T GetField<T>(object owner, string name) where T : class =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(owner) as T;

        private static void CopyProperties(object from, object to, params string[] names)
        {
            foreach (var name in names)
            {
                try
                {
                    var p = from.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanRead && p.CanWrite) p.SetValue(to, p.GetValue(from));
                }
                catch { }
            }
        }

        public void Dispose()
        {
            timer?.Dispose();
            capture?.Cancel();
        }
    }
}
