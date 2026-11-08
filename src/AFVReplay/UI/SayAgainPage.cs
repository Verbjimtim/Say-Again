using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AFVReplay.Audio;
using AFVReplay.VatSys;
using vatsys;
using Slider = VATSYSControls.Slider;

namespace AFVReplay.UI
{
    /// <summary>
    /// The Say Again page, shown in the VSCS's page area (where Setup, Coord and Text go).
    /// Left: a column of VSCS buttons (Next up, Play/Pause or status, Previous down, Settings).
    /// Right: Last Tx and Selected call-outs, then the calls list (or the settings), styled like
    /// the VSCS Text page's message panel.
    /// </summary>
    internal sealed class SayAgainPage : Panel
    {
        private const int W = VscsLayout.ButtonWidth, H = VscsLayout.ButtonHeight, P = VscsLayout.Padding;

        private static Color VscsBg => Colours.GetColour(Colours.Identities.VSCSBackground);
        private static Color WinBg => Colours.GetColour(Colours.Identities.WindowBackground);
        private static Color Heading => Colours.GetColour(Colours.Identities.GenericText);
        private static Color Interactive => Colours.GetColour(Colours.Identities.InteractiveText);
        private static Color NonInteractive => Colours.GetColour(Colours.Identities.NonInteractiveText);
        private static Color RowSelected => Colours.GetColour(Colours.Identities.WindowButtonSelected);
        private static Color Depressed => Colours.GetColour(Colours.Identities.WindowButtonDepressed);

        private readonly ReplayPlugin plugin;
        private readonly SayAgainPlaylist playlist;
        private readonly VscsButton older, playPause, newer, settingsButton;
        private readonly TextLabel lastTxLabel, lastTx, selectedLabel, selected;
        private readonly Panel listArea, settingsArea;
        private readonly ListView list;
        private readonly Panel listClip;
        private readonly VATSYSControls.ScrollBar listScroll;
        private readonly Timer scrollWatch;
        private bool syncingScroll;
        private int lastTopIndex = -1;
        private readonly ReplayProgressBar progress;
        private readonly TextLabel status;
        private readonly Font listFont, listBold, infoFont;
        private readonly Timer refresh;

        public SayAgainPage(ReplayPlugin plugin)
        {
            this.plugin = plugin;
            playlist = new SayAgainPlaylist(plugin);
            SuspendLayout(); // controls are positioned once they all exist
            BackColor = VscsBg;
            DoubleBuffered = true;
            listFont = MMI.eurofont_winsml;
            listBold = new Font(listFont, FontStyle.Bold | FontStyle.Underline);
            infoFont = new Font(MMI.eurofont_winverysml, FontStyle.Bold);

            // Next (newer, up the list) on top, Previous (older, down the list) under Play.
            newer = Button("", "", (s, e) => playlist.Newer(SelectedClip));
            newer.Arrow = VscsButton.Arrows.Up;
            playPause = Button("Play", "", (s, e) => playlist.PlayPause(SelectedClip));
            playPause.RightPressed += (s, e) => playlist.Stop();
            older = Button("", "", (s, e) => playlist.Older(SelectedClip));
            older.Arrow = VscsButton.Arrows.Down;
            settingsButton = Button("Settings", "", (s, e) => ShowSettings(!settingsArea.Visible));

            lastTxLabel = Info(false, "Last Tx:");
            lastTx = Info(true, "");
            selectedLabel = Info(false, "Selected:");
            selected = Info(true, "");

            // Calls list, in a panel like the Text page's message history.
            listArea = new InsetPanel { BackColor = WinBg, Padding = new Padding(3) };
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                OwnerDraw = true, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = WinBg, ForeColor = Interactive, Font = listFont,
            };
            list.Columns.Add("Ago", 42);
            list.Columns.Add("Freq", 72);
            list.Columns.Add("Callsign", 128);
            list.Columns.Add("Length", 62);
            list.DrawColumnHeader += List_DrawColumnHeader;
            list.DrawSubItem += List_DrawSubItem;
            list.DoubleClick += (s, e) => { if (SelectedClip != null) playlist.Play(SelectedClip); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && SelectedClip != null) playlist.Play(SelectedClip); };
            list.Resize += (s, e) => FitColumns();
            // Double-buffered so in-place updates don't flicker.
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(list, true);
            // The list's own Windows scroll bar is clipped off its right edge; vatSys's scroll bar
            // (as on the VSCS Text page) sits beside the panel instead and is kept in step with it.
            listClip = new Panel { Dock = DockStyle.Fill, BackColor = WinBg };
            listClip.Controls.Add(list);
            listClip.Resize += (s, e) => SizeList();
            listArea.Controls.Add(listClip);
            Controls.Add(listArea);

            listScroll = new VATSYSControls.ScrollBar
            {
                Orientation = ScrollOrientation.VerticalScroll,
                BackColor = Colours.GetColour(Colours.Identities.WindowButtonSelected),
                ForeColor = Colours.GetColour(Colours.Identities.VSCSBackground),
                PreferredHeight = 10, ActualHeight = 10, Change = 1, Value = 0,
                MinimumSize = new Size(0, 7),
            };
            listScroll.Scroll += ListScroll_Scroll;
            listScroll.Scrolling += ListScroll_Scroll; // raised while the thumb is being dragged
            listScroll.MouseWheel += (s, e) => ScrollRows(e.Delta > 0 ? -3 : 3);
            Controls.Add(listScroll);

            // ListView has no scroll event: watch its top row (wheel, keys, follow-playback) and move the bar.
            scrollWatch = new Timer { Interval = 100 };
            scrollWatch.Tick += (s, e) => UpdateScrollBar();
            scrollWatch.Start();

            // How far into the replay, under the list.
            progress = new ReplayProgressBar(plugin);
            Controls.Add(progress);

            settingsArea = BuildSettings(out status);
            settingsArea.Visible = false;
            Controls.Add(settingsArea);

            refresh = new Timer { Interval = 1000 };
            refresh.Tick += (s, e) => RefreshView();
            refresh.Start();
            foreach (var p in new[] { plugin.Player, plugin.CoordPlayer })
            {
                p.PlaybackStarted += Changed;
                p.PlaybackStopped += Changed;
                p.PausedChanged += Changed;
            }
            plugin.Store.Changed += Changed;
            plugin.CoordStore.Changed += Changed;
            MMI.SelectedTrackChanged += Changed;
            MMI.SelectedGroundTrackChanged += Changed;
            ResumeLayout(true);
            RefreshView();
        }

        private Clip SelectedClip => list.SelectedItems.Count > 0 ? (Clip)list.SelectedItems[0].Tag : null;

        internal SayAgainPlaylist Playlist => playlist;

        private void Changed(object sender, EventArgs e)
        {
            // From audio/input/network threads.
            if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)RefreshView);
        }

        // ------------------------------------------------------------------ layout

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (progress == null) return; // still being built
            settingsButton.Bounds = new Rectangle(0, VscsLayout.RowY(0), W, H);
            newer.Bounds = new Rectangle(0, VscsLayout.RowY(1), W, H);
            playPause.Bounds = new Rectangle(0, VscsLayout.RowY(2), W, H);
            older.Bounds = new Rectangle(0, VscsLayout.RowY(3), W, H);

            int x = W + 2 * P;
            int width = Math.Max(80, ClientSize.Width - x - P);
            // A little room above the call-outs. Values are cut short with "..." rather than running
            // off the page (very long callsigns or names).
            const int top = P + 4;
            lastTxLabel.Location = new Point(x, top);
            selectedLabel.Location = new Point(x, top + 18);
            foreach (var (value, y) in new[] { (lastTx, top), (selected, top + 18) })
            {
                value.AutoSize = false;
                value.AutoEllipsis = true;
                value.TextAlign = ContentAlignment.TopLeft;
                value.Bounds = new Rectangle(x + 80, y, Math.Max(40, width - 80), 16);
            }
            var area = new Rectangle(x, top + 40, width, Math.Max(60, ClientSize.Height - top - 40 - P));
            settingsArea.Bounds = area;
            // The list leaves room under it for the progress bar, and beside it for the scroll bar
            // (20 px, as on the Text page).
            const int bar = 18, scrollWidth = 20;
            listArea.Bounds = new Rectangle(area.X, area.Y, area.Width - scrollWidth - 3, area.Height - bar - 2);
            listScroll.Bounds = new Rectangle(listArea.Right + 3, listArea.Top, scrollWidth, listArea.Height);
            progress.Bounds = new Rectangle(area.X, area.Bottom - bar, area.Width, bar);
        }

        private void FitColumns()
        {
            // Fit to the visible (clipped) width, whether or not the hidden Windows scroll bar is showing.
            int fixedWidth = list.Columns[0].Width + list.Columns[1].Width + list.Columns[3].Width;
            int w = listClip.ClientSize.Width - fixedWidth;
            if (w > 60) list.Columns[2].Width = w; // Callsign takes the rest (no blank header end)
        }

        /// <summary>The list is wider than its clip panel by the Windows scroll bar, which is hidden off the edge.</summary>
        private void SizeList()
        {
            list.Bounds = new Rectangle(0, 0, listClip.ClientSize.Width + SystemInformation.VerticalScrollBarWidth, listClip.ClientSize.Height);
            FitColumns();
            UpdateScrollBar(force: true);
        }

        private int RowHeight => list.Items.Count > 0 ? Math.Max(1, list.GetItemRect(0).Height) : Math.Max(1, listFont.Height + 2);

        /// <summary>Rows fully visible under the header.</summary>
        private int VisibleRows
        {
            get
            {
                int header = list.Items.Count > 0 && list.TopItem != null ? list.GetItemRect(list.TopItem.Index).Top : RowHeight;
                return Math.Max(1, (list.ClientSize.Height - header) / RowHeight);
            }
        }

        /// <summary>Moves vatSys's scroll bar to match the list (sizes in rows, scaled to pixels).</summary>
        private void UpdateScrollBar(bool force = false)
        {
            if (listScroll == null || !list.IsHandleCreated) return;
            if (listScroll.Capture) return; // the user is holding the bar: it leads, the list follows
            int top = list.Items.Count > 0 && list.TopItem != null ? list.TopItem.Index : 0;
            if (!force && top == lastTopIndex && listScroll.PreferredHeight == list.Items.Count * RowHeight) return;
            lastTopIndex = top;
            syncingScroll = true;
            try
            {
                int row = RowHeight;
                listScroll.PreferredHeight = Math.Max(1, list.Items.Count * row);
                listScroll.ActualHeight = Math.Max(1, VisibleRows * row);
                listScroll.Change = row;
                listScroll.Value = top * row;
            }
            finally { syncingScroll = false; }
        }

        private void ListScroll_Scroll(object sender, EventArgs e)
        {
            if (syncingScroll || list.Items.Count == 0) return;
            int index = Math.Max(0, Math.Min(list.Items.Count - 1, (int)Math.Round(listScroll.Value / (double)RowHeight)));
            var item = list.Items[index];
            list.TopItem = item;
            list.TopItem = item; // set twice: WinForms quirk in Details view
            lastTopIndex = list.TopItem?.Index ?? index;
        }

        private void ScrollRows(int rows)
        {
            if (list.Items.Count == 0) return;
            int top = list.TopItem?.Index ?? 0;
            int index = Math.Max(0, Math.Min(list.Items.Count - 1, top + rows));
            list.TopItem = list.Items[index];
            list.TopItem = list.Items[index];
            UpdateScrollBar(force: true);
        }

        internal void ShowSettings(bool show)
        {
            settingsArea.Visible = show;
            listArea.Visible = !show;
            listScroll.Visible = !show;
            progress.Visible = !show;
            RefreshView();
        }

        internal bool SettingsShown => settingsArea.Visible;

        // ------------------------------------------------------------------ view

        internal void RefreshView()
        {
            if (IsDisposed) return;

            // Play / Pause, or who and where while a call plays.
            var current = playlist.Current;
            if (playlist.IsPlaying && current != null)
                playPause.Set(Who(current), Where(current), VscsButton.Faces.Receiving, true);
            else if (playlist.IsPaused && current != null)
                playPause.SetSymbol(VscsButton.Arrows.Play, Who(current), VscsButton.Faces.Depressed, true); // paused: play resumes
            else
                playPause.SetSymbol(VscsButton.Arrows.Play, "", VscsButton.Faces.Normal, false);
            settingsButton.Sunk = settingsArea.Visible;

            // Call-outs.
            var now = DateTime.UtcNow;
            var freq = plugin.Store.Snapshot();
            var last = freq.FirstOrDefault(c => !c.IsOwn);
            lastTx.Text = last == null ? "None" : $"{Who(last)} - {Where(last)} ({AgoLong(now - last.EndUtc)})";

            // A selected aircraft filters the list (and Next/Previous) to its calls.
            var (callsign, name) = SelectedPilot();
            selected.Text = callsign == null ? "No pilot selected" : string.IsNullOrWhiteSpace(name) ? callsign : $"{callsign}, {name}";
            playlist.Filter = callsign == null ? null
                : (Func<Clip, bool>)(c => c.Callsigns.Any(s => string.Equals(s, callsign, StringComparison.OrdinalIgnoreCase)));

            SyncList(playlist.Items(), now);

            // Follow the call playing, but only when a new one starts, so you can scroll and select
            // freely while it plays.
            bool active = playlist.IsPlaying || playlist.IsPaused;
            var playingNow = active ? current : null;
            if (playingNow != followed)
            {
                followed = playingNow;
                var item = FindItem(playingNow);
                if (item != null)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
                list.Invalidate(); // the playing row is drawn bold
            }
            status.Text = plugin.StatusText + Environment.NewLine + plugin.CoordStatusText;
        }

        private Clip followed;

        private ListViewItem FindItem(Clip c)
        {
            if (c == null) return null;
            foreach (ListViewItem i in list.Items) if (i.Tag == c) return i;
            return null;
        }

        private ListViewItem MakeItem(Clip c) =>
            new ListViewItem(new[] { "", Where(c), c.CallsignText + (c.Blocked ? " (blocked)" : ""), $"{c.Duration.TotalSeconds:0.0}s" }) { Tag = c };

        /// <summary>
        /// Brings the list in line with the calls kept, in place: new calls are inserted at the top,
        /// expired ones removed, nothing else touched. Selection is kept, and if you've scrolled down
        /// the same calls stay in view. "Ago" text is only rewritten when it changes (once a minute).
        /// </summary>
        internal void SyncList(List<Clip> items, DateTime now)
        {
            var wanted = new HashSet<Clip>(items);
            var topClip = list.Items.Count > 0 ? list.TopItem?.Tag as Clip : null;
            bool scrolled = list.Items.Count > 0 && list.TopItem != null && list.TopItem.Index > 0;
            bool changed = false;

            list.BeginUpdate();
            for (int i = list.Items.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains((Clip)list.Items[i].Tag)) { list.Items.RemoveAt(i); changed = true; }
            }
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                if (i < list.Items.Count && list.Items[i].Tag == c) continue;
                var existing = FindItem(c);
                if (existing != null) { list.Items.Remove(existing); list.Items.Insert(i, existing); }
                else list.Items.Insert(i, MakeItem(c));
                changed = true;
            }
            foreach (ListViewItem item in list.Items)
            {
                string ago = Ago(now - ((Clip)item.Tag).EndUtc);
                if (item.SubItems[0].Text != ago) item.SubItems[0].Text = ago;
            }
            list.EndUpdate();

            if (changed)
            {
                // Scrolled down: keep the same calls in view instead of shifting with each new call.
                if (scrolled && topClip != null)
                {
                    var keep = FindItem(topClip);
                    if (keep != null) { list.TopItem = keep; list.TopItem = keep; } // set twice: WinForms quirk in Details view
                }
                FitColumns();
            }
            UpdateScrollBar(force: changed);
        }

        /// <summary>The pilot selected in vatSys (air or ground track): callsign and name, or nulls.</summary>
        internal static (string callsign, string name) SelectedPilot()
        {
            if (PilotOverride != null) return PilotOverride();
            try
            {
                var pilot = (MMI.SelectedTrack ?? MMI.SelectedGroundTrack)?.GetPilot();
                if (pilot == null || string.IsNullOrEmpty(pilot.Callsign)) return (null, null);
                return (pilot.Callsign, pilot.RealName);
            }
            catch { return (null, null); }
        }

        /// <summary>For previews/tests: stands in for vatSys's selected track.</summary>
        internal static Func<(string callsign, string name)> PilotOverride;

        /// <summary>"&lt;1 min ago", "1 min ago", "3 mins ago".</summary>
        internal static string AgoLong(TimeSpan t)
        {
            int m = (int)t.TotalMinutes;
            return m < 1 ? "<1 min ago" : m == 1 ? "1 min ago" : $"{m} mins ago";
        }

        internal static string Who(Clip c) => c.IsCoord ? c.CallsignText : c.IsOwn ? "You" : c.Callsigns.FirstOrDefault() ?? "?";

        /// <summary>"118.500" for a frequency call (the last word of "Gingin T 118.500"), "Coord" for a line call.</summary>
        internal static string Where(Clip c)
        {
            if (c.IsCoord) return "Coord";
            var parts = (c.FrequencyName ?? "").Split(' ');
            return parts[parts.Length - 1];
        }

        /// <summary>Whole minutes, so it changes once a minute.</summary>
        internal static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "<1m" : $"{(int)t.TotalMinutes}m";

        // ------------------------------------------------------------------ list drawing

        private void List_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(WinBg)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, listFont, Inset(e.Bounds), Heading, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            // A rule under the header, across every column.
            using (var pen = new Pen(Heading)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        private void List_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var clip = (Clip)e.Item.Tag;
            bool playing = plugin.Player.PlayingClip == clip || plugin.CoordPlayer.PlayingClip == clip;
            var back = e.Item.Selected ? RowSelected : WinBg;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            var colour = clip.IsWiped ? NonInteractive : Interactive;
            if (e.Item.Selected) colour = Readable(WinBg, back);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, playing ? listBold : listFont, Inset(e.Bounds), colour,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static Color Readable(Color preferred, Color background)
        {
            double Lum(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            if (Math.Abs(Lum(preferred) - Lum(background)) >= 90) return preferred;
            return Lum(background) > 128 ? Color.Black : Color.White;
        }

        private static Rectangle Inset(Rectangle r) => new Rectangle(r.X + 3, r.Y, Math.Max(0, r.Width - 4), r.Height);

        // ------------------------------------------------------------------ settings

        private Panel BuildSettings(out TextLabel statusLabel)
        {
            var s = plugin.Settings;
            var area = new InsetPanel { BackColor = WinBg, Padding = new Padding(4) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = WinBg, AutoScroll = false };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddSetting(t, "Keep Calls", 1, 5, s.RetentionMinutes, v => v + " min", v => s.RetentionMinutes = v);
            AddSetting(t, "Keep Coord", 1, 10, s.CoordRetentionMinutes, v => v + " min", v => s.CoordRetentionMinutes = v);
            AddSetting(t, "Volume", 0, 200, s.VolumePercent, v => v + "%", v => s.VolumePercent = v, step: 5);
            AddSetting(t, "Speed", 100, 200, s.SpeedPercent, v => (v / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "x", v => s.SpeedPercent = v, step: 5);

            var toggle = VatSysBridge.CreateToggleButton();
            toggle.Size = new Size(90, 24);
            toggle.Font = MMI.eurofont_winverysml;
            toggle.BackColor = VscsBg;
            toggle.Margin = new Padding(2, 2, 2, 2);
            toggle.Anchor = AnchorStyles.Left;
            toggle.Checked = s.StopOnNewCall;
            toggle.Text = s.StopOnNewCall ? "On" : "Off";
            toggle.CheckedChanged += (o, e) =>
            {
                s.StopOnNewCall = toggle.Checked;
                toggle.Text = s.StopOnNewCall ? "On" : "Off";
                plugin.ApplySettings();
            };
            AddRow(t, SettingLabel("Stop On New"), toggle);

            statusLabel = new TextLabel(false) { AutoSize = true, TextAlign = ContentAlignment.TopLeft, Font = MMI.eurofont_winverysml, ForeColor = NonInteractive, BackColor = WinBg, Margin = new Padding(0, 6, 0, 0) };
            int row = AddRow(t, statusLabel, null);
            t.SetColumnSpan(statusLabel, 2);
            area.Controls.Add(t);
            return area;
        }

        private static int AddRow(TableLayoutPanel t, Control left, Control right)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(left, 0, row);
            if (right != null) t.Controls.Add(right, 1, row);
            return row;
        }

        private static TextLabel SettingLabel(string text) => new TextLabel(true)
        {
            Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Font = MMI.eurofont_winverysml, BackColor = WinBg, Margin = new Padding(0, 2, 6, 2),
        };

        // vatSys's Slider places its handle as Value/Maximum (it ignores Minimum), so each runs from 0
        // and the real minimum is added back.
        private void AddSetting(TableLayoutPanel t, string name, int min, int max, int value, Func<int, string> text, Action<int> set, int step = 1)
        {
            var slider = new Slider
            {
                Minimum = 0, Maximum = max - min, Value = Math.Max(min, Math.Min(max, value)) - min,
                Dock = DockStyle.Fill, Height = 28, BackColor = WinBg, ForeColor = Depressed, FontColor = Interactive,
                Font = MMI.eurofont_winverysml, CustomPaintText = true, Cursor = Cursors.NoMoveHoriz, Margin = new Padding(2, 1, 2, 1),
                SmallChange = step, LargeChange = step * 5,
            };
            slider.PaintText += (o, e) =>
            {
                using (var b = new SolidBrush(slider.FontColor))
                using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    e.Graphics.DrawString(text(slider.Value + min), slider.Font, b, e.ClipRectangle, f);
            };
            slider.Scroll += (o, e) =>
            {
                int v = slider.Value + min;
                if (step > 1)
                {
                    int snapped = (int)Math.Round(v / (double)step) * step;
                    if (snapped != v) { slider.Value = snapped - min; v = snapped; }
                }
                set(v);
                plugin.ApplySettings();
            };
            AddRow(t, SettingLabel(name), slider);
        }

        // ------------------------------------------------------------------ helpers

        private VscsButton Button(string text, string sub, EventHandler onPress)
        {
            var b = new VscsButton { Text = text, SubText = sub };
            b.Pressed += onPress;
            Controls.Add(b);
            return b;
        }

        private TextLabel Info(bool interactive, string text)
        {
            var l = new TextLabel(interactive) { Text = text, AutoSize = true, Font = infoFont, BackColor = VscsBg };
            l.ForeColor = interactive ? Interactive : Heading;
            Controls.Add(l);
            return l;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
                scrollWatch.Dispose();
                foreach (var p in new[] { plugin.Player, plugin.CoordPlayer })
                {
                    p.PlaybackStarted -= Changed;
                    p.PlaybackStopped -= Changed;
                    p.PausedChanged -= Changed;
                }
                plugin.Store.Changed -= Changed;
                plugin.CoordStore.Changed -= Changed;
                MMI.SelectedTrackChanged -= Changed;
                MMI.SelectedGroundTrackChanged -= Changed;
                listBold.Dispose();
                infoFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
