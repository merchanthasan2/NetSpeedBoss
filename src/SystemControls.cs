using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    public static class BrandArt
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Processor-package style graphic: pins around a brand-colored die with the brand name and series.</summary>
        public static void DrawChip(Graphics g, Rectangle r, BrandStyle b, string series)
        {
            if (b == null) b = Brands.Generic("?");
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pin = r.Width * 0.07f, gap = r.Width * 0.11f;
            RectangleF body = new RectangleF(r.Left + pin, r.Top + pin, r.Width - 2 * pin, r.Height - 2 * pin);
            using (SolidBrush pb = new SolidBrush(Color.FromArgb(178, 186, 196)))
                for (int i = 0; i < 5; i++)
                {
                    float o = body.Left + body.Width * 0.14f + i * (body.Width * 0.72f / 4) - pin * 0.35f;
                    float oy = body.Top + body.Height * 0.14f + i * (body.Height * 0.72f / 4) - pin * 0.35f;
                    g.FillRectangle(pb, o, r.Top, pin * 0.7f, pin + 1);
                    g.FillRectangle(pb, o, body.Bottom - 1, pin * 0.7f, pin + 1);
                    g.FillRectangle(pb, r.Left, oy, pin + 1, pin * 0.7f);
                    g.FillRectangle(pb, body.Right - 1, oy, pin + 1, pin * 0.7f);
                }
            using (GraphicsPath p = Round(body, body.Width * 0.14f))
            using (LinearGradientBrush lg = new LinearGradientBrush(body, b.Back, b.Back2, 55f))
                g.FillPath(lg, p);
            RectangleF die = RectangleF.Inflate(body, -gap * 0.55f, -gap * 0.55f);
            using (GraphicsPath p = Round(die, die.Width * 0.1f))
            using (Pen pn = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, r.Width * 0.012f)))
                g.DrawPath(pn, p);

            float fs = FitFont(g, b.Word, die.Width * 0.86f, die.Height * 0.30f, b.Key == "intel");
            using (Font f = new Font("Segoe UI", fs, b.Key == "intel" ? FontStyle.Bold : FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush tb = new SolidBrush(b.Fore))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                bool hasSeries = !string.IsNullOrEmpty(series) && !series.Equals(b.Word, StringComparison.OrdinalIgnoreCase);
                RectangleF wr = hasSeries ? new RectangleF(die.Left, die.Top + die.Height * 0.14f, die.Width, die.Height * 0.46f) : die;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(b.Word, f, tb, wr, sf);
                if (hasSeries)
                {
                    float ss = FitFont(g, series, die.Width * 0.9f, die.Height * 0.2f, false);
                    using (Font f2 = new Font("Segoe UI Semibold", ss, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (SolidBrush sb2 = new SolidBrush(Color.FromArgb(225, b.Fore)))
                        g.DrawString(series, f2, sb2, new RectangleF(die.Left, die.Top + die.Height * 0.56f, die.Width, die.Height * 0.32f), sf);
                }
            }
        }

        static float FitFont(Graphics g, string text, float maxW, float maxH, bool bold)
        {
            float size = maxH;
            while (size > 6)
            {
                using (Font f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    if (g.MeasureString(text, f).Width <= maxW) break;
                size -= 1;
            }
            return Math.Max(6, size);
        }

        /// <summary>Small pill badge with the brand name; returns its width.</summary>
        public static int DrawBadge(Graphics g, BrandStyle b, int x, int y, int h, Font f)
        {
            if (b == null) return 0;
            Size ts = TextRenderer.MeasureText(b.Word, f);
            Rectangle r = new Rectangle(x, y, ts.Width + 10, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, h / 2f - 1))
            using (LinearGradientBrush lg = new LinearGradientBrush(r, b.Back, b.Back2, 90f))
                g.FillPath(lg, p);
            TextRenderer.DrawText(g, b.Word, f, r, b.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return r.Width;
        }
    }

    /// <summary>Live usage card: brand chip, big percentage, two detail lines and a 2-minute history graph.</summary>
    public class UtilCard : Control
    {
        public string Title = "", Value = "--", Line1 = "", Line2 = "";
        public BrandStyle Brand;
        public string Series = "";
        public Color Accent = Theme.Accent;
        public Func<int, double> History;   // ago -> percent
        public int HistoryLength = 120;
        public bool ShowChip = true;
        readonly Font fTitle = new Font("Segoe UI", 9f), fValue = new Font("Segoe UI Semibold", 22f), fLine = new Font("Segoe UI", 8.5f);

        public UtilCard()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Margin = new Padding(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (SolidBrush ab = new SolidBrush(Accent)) g.FillRectangle(ab, 0, 0, 3, Height);

            int chip = ShowChip ? Math.Min(84, Height - 70) : 0;
            int x = 14;
            if (chip > 30) { BrandArt.DrawChip(g, new Rectangle(x, 12, chip, chip), Brand, Series); x += chip + 14; }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, Title, fTitle, new Point(x, 10), Theme.Muted);
            TextRenderer.DrawText(g, Value, fValue, new Point(x - 3, 26), Theme.Ink);
            TextRenderer.DrawText(g, Line1, fLine, new Rectangle(x, 68, Width - x - 8, 16), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, Line2, fLine, new Rectangle(x, 84, Width - x - 8, 16), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            // history graph
            Rectangle gr = new Rectangle(10, Math.Max(108, 12 + chip + 10), Width - 20, 0);
            gr.Height = Height - gr.Top - 10;
            if (History == null || gr.Height < 16) return;
            using (Pen grid = new Pen(Color.FromArgb(236, 238, 242)))
                for (int i = 0; i <= 2; i++) { int y = gr.Bottom - i * gr.Height / 2; g.DrawLine(grid, gr.Left, y, gr.Right, y); }
            int n = HistoryLength;
            PointF[] pts = new PointF[n + 2];
            float step = gr.Width / (float)(n - 1);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(0, Math.Min(100, History(n - 1 - i)));
                pts[i] = new PointF(gr.Left + i * step, (float)(gr.Bottom - v / 100.0 * gr.Height));
            }
            pts[n] = new PointF(gr.Right, gr.Bottom); pts[n + 1] = new PointF(gr.Left, gr.Bottom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush fb = new SolidBrush(Color.FromArgb(40, Accent))) g.FillPolygon(fb, pts);
            PointF[] line = new PointF[n];
            Array.Copy(pts, line, n);
            using (Pen lp = new Pen(Accent, 1.8f)) g.DrawLines(lp, line);
            TextRenderer.DrawText(g, "2 min", fLine, new Point(gr.Left, gr.Bottom - 15), Color.FromArgb(160, 166, 175));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fTitle.Dispose(); fValue.Dispose(); fLine.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>"About this system" list: label, brand badge, value.</summary>
    public class SpecList : Control
    {
        public List<SpecRow> Rows = new List<SpecRow>();
        public string Loading = "Reading system information...";
        readonly Font fBadge = new Font("Segoe UI", 8f, FontStyle.Bold), fBold = new Font("Segoe UI Semibold", 9.5f);
        const int RowH = 30, Head = 34;

        public SpecList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = new Font("Segoe UI", 9f);
        }

        public int ContentHeight { get { return Head + Math.Max(1, Rows.Count) * RowH + 10; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            using (Pen p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, "About this system", fBold, new Point(12, 9), Theme.Ink);
            if (Rows.Count == 0) { TextRenderer.DrawText(g, Loading, Font, new Point(12, Head + 6), Theme.Muted); return; }
            for (int i = 0; i < Rows.Count; i++)
            {
                SpecRow r = Rows[i];
                int y = Head + i * RowH;
                if (i % 2 == 0) using (SolidBrush zb = new SolidBrush(Color.FromArgb(249, 250, 252))) g.FillRectangle(zb, 1, y, Width - 2, RowH);
                TextRenderer.DrawText(g, r.Label, Font, new Rectangle(12, y, 120, RowH), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                int x = 140;
                int bw = BrandArt.DrawBadge(g, r.Brand, x, y + 6, RowH - 12, fBadge);
                g.SmoothingMode = SmoothingMode.None;
                if (bw > 0) x += bw + 8;
                TextRenderer.DrawText(g, r.Value, Font, new Rectangle(x, y, Width - x - 10, RowH), Theme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fBadge.Dispose(); fBold.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
