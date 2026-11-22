using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AFVReplay.Audio;
using AFVReplay.VatSys;

namespace AFVReplay.UI
{
    /// <summary>The VSCS's button grid: 80 x 60 buttons, 3 px padding all round.</summary>
    internal static class VscsLayout
    {
        public const int ButtonWidth = 80, ButtonHeight = 60, Padding = 3;
        public static int ColumnX(int column) => Padding + column * (ButtonWidth + 2 * Padding);
        public static int RowY(int row) => Padding + row * (ButtonHeight + 2 * Padding);

        /// <summary>How many button rows fit in the VSCS panel.</summary>
        public static int Rows(int panelHeight) => Math.Max(1, (panelHeight - Padding - ButtonHeight) / (ButtonHeight + 2 * Padding) + 1);

        /// <summary>The VSCS's menu column (Setup, Coord, Text, Group): just right of the frequency columns.</summary>
        public static int MenuColumn(int frequencyCount, int panelHeight)
        {
            int rows = Rows(panelHeight);
            int lastFreqColumn = frequencyCount <= 0 ? 0 : (frequencyCount - 1) / rows;
            return lastFreqColumn + 1;
        }

        public static Rectangle Cell(int column, int row) => new Rectangle(ColumnX(column), RowY(row), ButtonWidth, ButtonHeight);
    }

    /// <summary>
    /// Puts Say Again on the VSCS and shows the Say Again page in the VSCS's page area.
    /// <list type="bullet">
    /// <item>Say Again is a fifth menu button under Group; the VSCS window's minimum height is raised
    /// so it always fits.</item>
    /// <item>While a call replays and the page isn't open, Say Again shows who/where in the VSCS receiving colour;
    /// right click stops it.</item>
    /// <item>The page closes only when vatSys changes page (Setup, Coord, Text or a frequency button);
    /// clicks on blank VSCS space leave it open.</item>
    /// </list>
    /// Only adds controls on top of the VSCS; changes nothing of vatSys's.
    /// </summary>
    internal sealed class VscsInjector : IDisposable
    {
        private readonly ReplayPlugin plugin;
        private readonly Timer timer = new Timer { Interval = 200 };
        private Control panel;
        private VscsButton menuButton;
        private VscsButton handoffButton;
        private VscsButton afkButton;
        private Form sizedWindow;
        private Size? originalMinimum;
        private SayAgainPage page;
        private bool pageOpen;
        private string pageOpenedOn;
        private bool failed;

        public VscsInjector(ReplayPlugin plugin)
        {
            this.plugin = plugin;
            timer.Tick += (s, e) => Sync();
            foreach (var p in new[] { plugin.Player, plugin.CoordPlayer })
            {
                p.PlaybackStarted += PlayerChanged;
                p.PlaybackStopped += PlayerChanged;
                p.PausedChanged += PlayerChanged;
            }
        }

        public void Start() { timer.Start(); Sync(); }

        internal bool PageOpen => pageOpen;

        /// <summary>For tests: why the VSCS page switched itself off, if it did.</summary>
        internal Exception LastError { get; private set; }
        internal SayAgainPage Page => page;

        private void PlayerChanged(object sender, EventArgs e)
        {
            var p = panel;
            if (p != null && p.IsHandleCreated && !p.IsDisposed) p.BeginInvoke((MethodInvoker)Sync);
        }

        /// <summary>Opens the Say Again page (and brings the VSCS window up).</summary>
        public void OpenPage()
        {
            var window = VatSysBridge.VscsWindow;
            if (window != null && !window.IsDisposed)
            {
                if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
                if (!window.Visible) window.Show();
                window.Activate();
            }
            Sync();
            if (panel == null) return;
            // Park vatSys on its Monitor page underneath: none of its menu buttons (Setup, Coord, Text)
            // show pressed there, so only Say Again looks selected. Its Monitor buttons are covered.
            try { VatSysBridge.SetVscsPage(panel, "Monitor"); } catch { }
            pageOpen = true;
            pageOpenedOn = VatSysBridge.VscsPageName(panel);
            Sync();
        }

        public void ClosePage()
        {
            if (!pageOpen) return;
            pageOpen = false;
            Sync();
        }

        internal void Sync()
        {
            if (failed) return;
            try
            {
                var p = VatSysBridge.VscsPanel;
                if (p == null || p.IsDisposed) return;
                if (p != panel || page == null || page.IsDisposed || page.Parent != p) Attach(p);

                EnsureRoomForSayAgain();
                int menuCol = VscsLayout.MenuColumn(FrequencyCount(), panel.ClientSize.Height);
                string vatsysPage = VatSysBridge.VscsPageName(panel);

                // Leaving the page: vatSys switched page underneath (a frequency or menu button was used).
                if (pageOpen && vatsysPage != pageOpenedOn) pageOpen = false;

                // Status shown on Say Again while a call replays and the page isn't open.
                var clip = plugin.Player.PlayingClip ?? plugin.CoordPlayer.PlayingClip;
                bool paused = plugin.Player.IsPaused || plugin.CoordPlayer.IsPaused;

                void ShowSayAgain(VscsButton b)
                {
                    if (clip != null && !pageOpen)
                        b.Set(SayAgainPage.Who(clip), paused ? "Paused" : SayAgainPage.Where(clip),
                              paused ? VscsButton.Faces.Depressed : VscsButton.Faces.Receiving, false);
                    else
                        b.Set("Say\nAgain", "", VscsButton.Faces.Normal, pageOpen);
                }

                // Say Again: a fifth menu button under Group, like Setup / Coord / Text / Group.
                menuButton.Visible = true;
                menuButton.Bounds = VscsLayout.Cell(menuCol, 4);
                ShowSayAgain(menuButton);
                menuButton.BringToFront();

                // Handoff Alert's on/off button on vatSys's Setup page, where Matt's plugin had it.
                handoffButton.Bounds = HandoffButtonBounds(menuCol);
                afkButton.Bounds = AfkButtonBounds(menuCol);
                ShowHandoffButton(bringToFront: true);

                page.Visible = pageOpen;
                if (pageOpen)
                {
                    int x = VscsLayout.ColumnX(menuCol + 1);
                    page.Bounds = new Rectangle(x, 0, Math.Max(200, panel.ClientSize.Width - x), panel.ClientSize.Height);
                    page.BringToFront(); // over vatSys's own page controls (e.g. the Text page)
                }
            }
            catch (Exception ex)
            {
                // Cosmetic only: never let it affect anything else.
                failed = true;
                timer.Stop();
                Diag.Warn("VSCS page unavailable: " + ex.GetType().Name);
                LastError = ex;
            }
        }

        private void Attach(Control p)
        {
            Detach();
            panel = p;
            menuButton = new VscsButton { Text = "Say\nAgain", Visible = false };
            menuButton.Pressed += (s, e) => OpenPage();
            menuButton.RightPressed += (s, e) => plugin.StopAll(); // dismiss a replay
            panel.Controls.Add(menuButton);
            handoffButton = new VscsButton { Text = "Handoff Alert", Visible = false };
            handoffButton.Pressed += (s, e) => { plugin.ToggleHandoffAlert(); ShowHandoffButton(); };
            panel.Controls.Add(handoffButton);
            afkButton = new VscsButton { Text = "AFK", Visible = false };
            afkButton.Pressed += (s, e) => { plugin.Afk.Toggle(); ShowHandoffButton(); };
            panel.Controls.Add(afkButton);
            panel.Invalidated += PanelInvalidated; // shows/hides it as soon as vatSys changes page
            page = new SayAgainPage(plugin) { Visible = false };
            panel.Controls.Add(page);
            // No click handling on the VSCS itself: blank space does nothing. The page closes only when
            // vatSys changes page (Setup, Coord, Text or a frequency button), checked in Sync.
        }

        private void Detach()
        {
            RestoreMinimumHeight();
            if (panel != null && !panel.IsDisposed) panel.Invalidated -= PanelInvalidated;
            foreach (var c in new Control[] { menuButton, handoffButton, afkButton, page })
            {
                if (c == null || c.IsDisposed) continue;
                try { if (c.InvokeRequired) c.BeginInvoke((MethodInvoker)c.Dispose); else c.Dispose(); } catch { }
            }
            menuButton = null;
            handoffButton = null;
            afkButton = null;
            page = null;
            panel = null;
            pageOpen = false;
        }

        /// <summary>
        /// Second row of the Setup page, under Mute Input (the fourth column after the menu column), one pixel
        /// bigger all round: the same place and size as Matt's Handoff Alert plugin.
        /// </summary>
        internal static Rectangle HandoffButtonBounds(int menuColumn) =>
            new Rectangle(VscsLayout.ColumnX(menuColumn + 4) - 1, VscsLayout.RowY(1) - 1, VscsLayout.ButtonWidth + 2, VscsLayout.ButtonHeight + 2);

        /// <summary>AFK: the next cell right of Handoff Alert, the same size.</summary>
        internal static Rectangle AfkButtonBounds(int menuColumn)
        {
            var r = HandoffButtonBounds(menuColumn);
            r.X = VscsLayout.ColumnX(menuColumn + 5) - 1;
            return r;
        }

        internal VscsButton HandoffButton => handoffButton;
        internal VscsButton AfkButton => afkButton;

        private void PanelInvalidated(object sender, InvalidateEventArgs e)
        {
            try { ShowHandoffButton(); } catch { }
        }

        /// <summary>Handoff Alert and AFK: only on vatSys's Setup page; pressed while on.</summary>
        // Not brought to front from Invalidated: changing the order repaints the panel, which would loop.
        private void ShowHandoffButton(bool bringToFront = false)
        {
            var b = handoffButton;
            if (b == null || b.IsDisposed || panel == null || panel.IsDisposed) return;
            bool visible = !pageOpen && VatSysBridge.IsVscsSetupPage(panel);
            b.Set("Handoff Alert", "", VscsButton.Faces.Normal, plugin.Settings.HandoffAlert);
            if (b.Visible != visible) b.Visible = visible;
            if (visible && bringToFront) b.BringToFront();
            var a = afkButton;
            if (a == null || a.IsDisposed) return;
            a.Set("AFK", "", VscsButton.Faces.Normal, plugin.Afk.On);
            if (a.Visible != visible) a.Visible = visible;
            if (visible && bringToFront) a.BringToFront();
        }

        /// <summary>Rows the VSCS panel needs so Say Again fits under Group.</summary>
        internal const int RowsNeeded = 5;

        /// <summary>Panel height for <see cref="RowsNeeded"/> rows of VSCS buttons.</summary>
        internal static int PanelHeightNeeded => VscsLayout.RowY(RowsNeeded - 1) + VscsLayout.ButtonHeight + VscsLayout.Padding;

        /// <summary>
        /// Raises the VSCS window's minimum height (and its height, if shorter) so the panel always has
        /// room for a fifth row. The original minimum is put back when the plugin unloads.
        /// </summary>
        private void EnsureRoomForSayAgain()
        {
            var window = panel.FindForm();
            if (window == null || window.IsDisposed) return;
            if (sizedWindow != window)
            {
                RestoreMinimumHeight();
                sizedWindow = window;
                originalMinimum = window.MinimumSize;
            }
            // The panel stretches with the window, so the window needs this much more than the panel.
            int chrome = window.Height - panel.Height;
            int minHeight = PanelHeightNeeded + chrome;
            if (window.MinimumSize.Height < minHeight)
                window.MinimumSize = new Size(window.MinimumSize.Width, minHeight); // also grows a shorter window
            if (window.Height < minHeight && window.WindowState == FormWindowState.Normal)
                window.Height = minHeight;
        }

        private void RestoreMinimumHeight()
        {
            try
            {
                if (sizedWindow != null && !sizedWindow.IsDisposed && originalMinimum.HasValue)
                    sizedWindow.MinimumSize = originalMinimum.Value;
            }
            catch { }
            sizedWindow = null;
            originalMinimum = null;
        }

        private static int FrequencyCount()
        {
            try { return vatsys.Audio.VSCSFrequencies?.Count ?? 0; }
            catch { return 0; } // vatSys audio not started yet
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            foreach (var p in new[] { plugin.Player, plugin.CoordPlayer })
            {
                p.PlaybackStarted -= PlayerChanged;
                p.PlaybackStopped -= PlayerChanged;
                p.PausedChanged -= PlayerChanged;
            }
            Detach();
        }
    }
}
