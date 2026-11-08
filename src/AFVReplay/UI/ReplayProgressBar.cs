using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AFVReplay.Audio;
using vatsys;

namespace AFVReplay.UI
{
    /// <summary>
    /// How far into the replay you are: a thin bar in the list's selection colour, with elapsed and
    /// total time (in the call's own time, whatever the replay speed). Updates ten times a second
    /// while a call plays; empty when nothing is loaded.
    /// </summary>
    internal sealed class ReplayProgressBar : Control
    {
        private readonly ReplayPlugin plugin;
        private readonly Timer timer = new Timer { Interval = 100 };
        private double shownFraction = -1;

        public ReplayProgressBar(ReplayPlugin plugin)
        {
            this.plugin = plugin;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            Font = MMI.eurofont_winverysml;
            timer.Tick += (s, e) =>
            {
                var (_, fraction) = Progress();
                if (Math.Abs(fraction - shownFraction) > 0.001) Invalidate();
            };
            timer.Start();
        }

        /// <summary>The clip loaded (playing or paused) and how far through it is, 0..1 (-1 when none).</summary>
        internal (Clip clip, double fraction) Progress()
        {
            foreach (var p in new[] { plugin.Player, plugin.CoordPlayer })
            {
                var clip = p.PlayingClip;
                if (clip != null) return (clip, p.Progress);
            }
            return (null, -1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            var back = Colours.GetColour(Colours.Identities.WindowBackground);
            var (clip, fraction) = Progress();
            shownFraction = fraction;
            // The white box always shows; with nothing loaded it stays empty (no track, no time).
            using (var b = new SolidBrush(back)) g.FillRectangle(b, r);
            if (clip == null) return;

            // Time on the right; the bar takes the rest.
            string time = "";
            if (clip != null)
            {
                double total = clip.Duration.TotalSeconds;
                time = (total * Math.Max(0, fraction)).ToString("0.0", CultureInfo.InvariantCulture) + " / "
                     + total.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            }
            int timeWidth = TextRenderer.MeasureText(g, "00.0 / 00.0s", Font).Width;
            var bar = new Rectangle(r.X + 2, r.Y + r.Height / 2 - 4, Math.Max(10, r.Width - timeWidth - 8), 8);

            // Track: sunk like an inset panel; fill: the list's selection colour.
            using (var track = new SolidBrush(ControlPaint.Dark(back, 0.05f))) g.FillRectangle(track, bar);
            ControlPaint.DrawBorder3D(g, bar, Border3DStyle.SunkenOuter);
            if (fraction > 0)
            {
                var fill = new Rectangle(bar.X + 1, bar.Y + 1, (int)((bar.Width - 2) * Math.Min(1, fraction)), bar.Height - 2);
                using (var b = new SolidBrush(Colours.GetColour(Colours.Identities.WindowButtonSelected))) g.FillRectangle(b, fill);
            }
            TextRenderer.DrawText(g, time, Font, new Rectangle(bar.Right + 4, r.Y, r.Right - bar.Right - 4, r.Height),
                Colours.GetColour(Colours.Identities.InteractiveText), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
