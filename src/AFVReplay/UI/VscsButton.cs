using System;
using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace AFVReplay.UI
{
    /// <summary>
    /// A button drawn like the VSCS's own: profile VSCS colours, 2 px bevel (sunk when pressed or
    /// active), bold Terminus text with optional smaller subtext under it, text shrunk to fit.
    /// Left click raises <see cref="Pressed"/>, right click <see cref="RightPressed"/>.
    /// </summary>
    internal sealed class VscsButton : Control
    {
        /// <summary>Receiving = the colour a VSCS frequency button shows while receiving (used while a replay plays).</summary>
        public enum Faces { Normal, Selected, Depressed, Receiving }
        public enum Arrows { None, Up, Down, Play }

        private readonly Font mainFont = new Font(MMI.eurofont_winsml, FontStyle.Bold);
        private readonly Font subFont = new Font(MMI.eurofont_winverysml, FontStyle.Bold);
        private string subText = "";
        private Faces face = Faces.Normal;
        private bool sunk, disabled, mouseDown;
        private Arrows arrow = Arrows.None;

        /// <summary>A drawn symbol (up/down arrow, play) in place of the main text (the font has no reliable glyphs for them).</summary>
        public Arrows Arrow { get => arrow; set { if (arrow != value) { arrow = value; Invalidate(); } } }

        public VscsButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
        }

        public event EventHandler Pressed;
        public event EventHandler RightPressed;

        public string SubText { get => subText; set { if (subText != value) { subText = value ?? ""; Invalidate(); } } }
        public Faces Face { get => face; set { if (face != value) { face = value; Invalidate(); } } }
        public bool Sunk { get => sunk; set { if (sunk != value) { sunk = value; Invalidate(); } } }
        public bool Disabled { get => disabled; set { if (disabled != value) { disabled = value; Invalidate(); } } }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        /// <summary>Sets everything at once (one repaint).</summary>
        public void Set(string text, string sub, Faces f, bool isSunk)
        {
            if (Text == text && subText == (sub ?? "") && face == f && sunk == isSunk && arrow == Arrows.None) return;
            arrow = Arrows.None;
            base.Text = text;
            subText = sub ?? "";
            face = f;
            sunk = isSunk;
            Invalidate();
        }

        /// <summary>Shows a drawn symbol (with optional subtext) instead of text.</summary>
        public void SetSymbol(Arrows symbol, string sub, Faces f, bool isSunk)
        {
            if (arrow == symbol && Text == "" && subText == (sub ?? "") && face == f && sunk == isSunk) return;
            arrow = symbol;
            base.Text = "";
            subText = sub ?? "";
            face = f;
            sunk = isSunk;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            var id = face == Faces.Receiving ? Colours.Identities.VSCSReceivingFrequency
                   : face == Faces.Selected ? Colours.Identities.VSCSButtonSelected
                   : face == Faces.Depressed || mouseDown ? Colours.Identities.VSCSButtonDepressed
                   : Colours.Identities.VSCSBackground;
            using (var b = new SolidBrush(Colours.GetColour(id))) g.FillRectangle(b, r);

            var colour = Colours.GetColour(disabled ? Colours.Identities.NonInteractiveText : Colours.Identities.InteractiveText);
            if (arrow != Arrows.None)
            {
                // Symbol centred on its own; with subtext, in the top 55% and the subtext under it.
                bool withSub = !string.IsNullOrEmpty(subText);
                var top = withSub ? new Rectangle(r.X, r.Y, r.Width, (int)(r.Height * 0.55)) : r;
                int cx = top.X + top.Width / 2;
                int cy = withSub ? top.Bottom - 9 : top.Y + top.Height / 2;
                if (arrow == Arrows.Play) cy -= withSub ? 7 : 3; // room for its underline (and the subtext)
                const int s = 9; // half-size
                Point[] tri;
                switch (arrow)
                {
                    // Same size as the play symbol, turned: 18 wide, 14 tall.
                    case Arrows.Up: tri = new[] { new Point(cx - s, cy + 7), new Point(cx + s, cy + 7), new Point(cx, cy - 7) }; break;
                    case Arrows.Down: tri = new[] { new Point(cx - s, cy - 7), new Point(cx + s, cy - 7), new Point(cx, cy + 7) }; break;
                    default: tri = new[] { new Point(cx - s / 2 - 1, cy - s), new Point(cx - s / 2 - 1, cy + s), new Point(cx + s, cy) }; break; // play
                }
                using (var b = new SolidBrush(colour))
                {
                    g.FillPolygon(b, tri);
                    if (arrow == Arrows.Play) g.FillRectangle(b, cx - s, cy + s + 3, 2 * s, 2); // underlined play
                }
                if (withSub)
                {
                    var bottom = new Rectangle(r.X, top.Bottom, r.Width, r.Height - top.Height);
                    TextRenderer.DrawText(g, subText, Fit(g, subText, subFont, r.Width - 4), bottom, colour,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.Top);
                }
            }
            else if (string.IsNullOrEmpty(subText))
            {
                // Like the VSCS: one or two centred lines ("Speaker All").
                TextRenderer.DrawText(g, Text, Fit(g, Text, mainFont, r.Width - 4, wrap: true), r, colour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            else
            {
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                var top = new Rectangle(r.X, r.Y, r.Width, (int)(r.Height * 0.55));
                var bottom = new Rectangle(r.X, top.Bottom, r.Width, r.Height - top.Height);
                TextRenderer.DrawText(g, Text, Fit(g, Text, mainFont, r.Width - 4), top, colour, flags | TextFormatFlags.Bottom);
                TextRenderer.DrawText(g, subText, Fit(g, subText, subFont, r.Width - 4), bottom, colour, flags | TextFormatFlags.Top);
            }

            bool down = sunk || mouseDown;
            var bevel = Colours.GetColour(Colours.Identities.VSCSBackground);
            using (var lp = new Pen(down ? ControlPaint.DarkDark(bevel) : ControlPaint.LightLight(bevel), 2f))
            using (var dp = new Pen(down ? ControlPaint.LightLight(bevel) : ControlPaint.DarkDark(bevel), 2f))
            {
                float x0 = r.Left + 1, y0 = r.Top + 1, x1 = r.Right - 1, y1 = r.Bottom - 1;
                g.DrawLine(lp, x1, y0, x0, y0);
                g.DrawLine(lp, x0, y0, x0, y1);
                g.DrawLine(dp, x0, y1, x1, y1);
                g.DrawLine(dp, x1, y1, x1, y0);
            }
        }

        private Font fitted;
        private string fittedKey;

        /// <summary>Long callsigns (e.g. ML-BIK_CTR) shrink to fit rather than being cut off.</summary>
        private Font Fit(Graphics g, string text, Font font, int width, bool wrap = false)
        {
            if (string.IsNullOrEmpty(text)) return font;
            // Multi-word labels may wrap onto two lines at full size, as the VSCS does.
            int widest = 0;
            foreach (var word in wrap ? text.Split(' ') : new[] { text })
                widest = Math.Max(widest, TextRenderer.MeasureText(g, word, font, Size.Empty, TextFormatFlags.NoPadding).Width);
            if (widest <= width) return font;
            string key = text + "|" + width + "|" + font.Size;
            if (fittedKey == key && fitted != null) return fitted;
            fitted?.Dispose();
            fitted = null;
            for (float size = font.Size - 0.5f; size >= 5f; size -= 0.5f)
            {
                var f = new Font(font.FontFamily, size, font.Style, font.Unit);
                int w = 0;
                foreach (var word in wrap ? text.Split(' ') : new[] { text })
                    w = Math.Max(w, TextRenderer.MeasureText(g, word, f, Size.Empty, TextFormatFlags.NoPadding).Width);
                if (w <= width || size - 0.5f < 5f) { fitted = f; break; }
                f.Dispose();
            }
            fittedKey = key;
            return fitted ?? font;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !disabled) { mouseDown = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool inside = ClientRectangle.Contains(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                bool was = mouseDown;
                mouseDown = false;
                Invalidate();
                if (was && inside && !disabled) Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if (e.Button == MouseButtons.Right && inside)
            {
                RightPressed?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                mainFont.Dispose();
                subFont.Dispose();
                fitted?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
