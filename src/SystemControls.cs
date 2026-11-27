using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
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

        static readonly Dictionary<string, Image> logoCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads an embedded PNG brand logo compiled from assets\logos\&lt;key&gt;.png.</summary>
        public static Image LogoFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Image cached;
            if (logoCache.TryGetValue(key, out cached)) return cached;
            Image img = null;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetSpeedBoss.Logos." + key + ".png"))
                    if (s != null) img = Image.FromStream(s);
            }
            catch { img = null; }
            logoCache[key] = img;
            return img;
        }

        public static bool IsWordmarkLogo(string key, Image logo)
        {
            if (logo == null) return false;
            switch (key)
            {
                case "acer": case "amd": case "asus": case "dell": case "hp": case "intel":
                case "lenovo": case "mediatek": case "micron": case "samsung": case "toshiba": case "wd":
                    return true;
                default:
                    return false;
            }
        }

        public static int BadgeWidth(BrandStyle b, int h, Font f)
        {
            if (b == null) return 0;
            Image logo = LogoFor(b.Key);
            int icoH = Math.Max(10, h - 6);
            int icoW = logo != null ? Math.Max(10, Math.Min(64, (int)Math.Round(icoH * (logo.Width / (float)logo.Height)))) : 0;
            if (IsWordmarkLogo(b.Key, logo)) return icoW + 16;
            Size ts = TextRenderer.MeasureText(b.Word, f);
            return ts.Width + (logo != null ? icoW + 10 : 6) + 6;
        }

        /// <summary>Draws a crisp 16x16 category glyph for a system or network spec row.</summary>
        public static void DrawCategoryIcon(Graphics g, RectangleF r, string label, string value, Color fg, Color accent)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = r.X, y = r.Y, w = r.Width, h = r.Height, cx = x + w * 0.5f, cy = y + h * 0.5f;
            string l = (label ?? "").ToLowerInvariant();
            string v = (value ?? "").ToLowerInvariant();
            using (Pen p = new Pen(fg, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (SolidBrush b = new SolidBrush(fg))
            {
                if (l.StartsWith("windows"))
                {
                    Image wl = LogoFor("windows");
                    if (wl != null)
                    {
                        using (System.Drawing.Imaging.ImageAttributes ia = TintMatrix(accent))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(wl, Rectangle.Round(RectangleF.Inflate(r, -1.5f, -1.5f)), 0, 0, wl.Width, wl.Height, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else
                    {
                        float gap = 1.6f, pw = (w - 3 - gap) * 0.5f, ph = (h - 3 - gap) * 0.5f;
                        using (SolidBrush wb = new SolidBrush(accent))
                        {
                            g.FillRectangle(wb, x + 1.5f, y + 1.5f, pw, ph);
                            g.FillRectangle(wb, x + 1.5f + pw + gap, y + 1.5f, pw, ph);
                            g.FillRectangle(wb, x + 1.5f, y + 1.5f + ph + gap, pw, ph);
                            g.FillRectangle(wb, x + 1.5f + pw + gap, y + 1.5f + ph + gap, pw, ph);
                        }
                    }
                }
                else if (l.StartsWith("processor"))
                {
                    g.DrawRectangle(p, x + 3.5f, y + 3.5f, w - 7f, h - 7f);
                    for (int i = 0; i < 2; i++)
                    {
                        float o = x + 5.5f + i * 4.5f;
                        g.DrawLine(p, o, y + 1f, o, y + 3.2f);
                        g.DrawLine(p, o, y + h - 3.2f, o, y + h - 1f);
                        g.DrawLine(p, x + 1f, o - x + y, x + 3.2f, o - x + y);
                        g.DrawLine(p, x + w - 3.2f, o - x + y, x + w - 1f, o - x + y);
                    }
                }
                else if (l.StartsWith("graphics"))
                {
                    g.DrawRectangle(p, x + 1.5f, y + 3f, w - 3f, h - 7f);
                    g.DrawEllipse(p, x + 4f, y + 5f, 5f, 5f);
                    g.DrawLine(p, x + 3f, y + h - 2.5f, x + 8f, y + h - 2.5f);
                }
                else if (l.StartsWith("memory"))
                {
                    g.DrawRectangle(p, x + 1.5f, y + 4f, w - 3f, h - 9f);
                    for (int i = 0; i < 4; i++) g.DrawLine(p, x + 3.5f + i * 3f, y + h - 5f, x + 3.5f + i * 3f, y + h - 2f);
                }
                else if (l.StartsWith("storage"))
                {
                    if (v.Contains("nvme"))
                    {
                        // slim M.2 NVMe stick glyph
                        g.DrawRectangle(p, x + 1.5f, y + 4.5f, w - 3f, h - 9f);
                        g.FillRectangle(b, x + 4f, y + 6.5f, 4f, 3f);
                        g.FillRectangle(b, x + 9.5f, y + 6.5f, 3f, 3f);
                    }
                    else
                    {
                        g.DrawRectangle(p, x + 2f, y + 4f, w - 4f, h - 8f);
                        g.FillEllipse(b, x + w - 5.5f, cy - 1.2f, 2.4f, 2.4f);
                    }
                }
                else if (l.StartsWith("connection") || l.StartsWith("network"))
                {
                    if (v.Contains("wi-fi") || v.Contains("wifi") || v.Contains("wireless"))
                    {
                        g.DrawArc(p, x + 1.5f, y + 2.5f, w - 3f, h - 3f, 225, 90);
                        g.DrawArc(p, x + 4f, y + 5.5f, w - 8f, h - 7f, 225, 90);
                        g.FillEllipse(b, cx - 1.4f, y + h - 4.2f, 2.8f, 2.8f);
                    }
                    else
                    {
                        // Ethernet RJ45 jack / port glyph
                        g.DrawRectangle(p, x + 2f, y + 2.5f, w - 4f, h - 5.5f);
                        g.DrawLine(p, x + 5f, y + 3f, x + 5f, y + 6f);
                        g.DrawLine(p, x + 8f, y + 3f, x + 8f, y + 6f);
                        g.DrawLine(p, x + 11f, y + 3f, x + 11f, y + 6f);
                        g.FillRectangle(b, cx - 2f, y + h - 5.2f, 4f, 2.2f);
                    }
                }
                else if (l.Contains("ip"))
                {
                    g.DrawEllipse(p, x + 2f, y + 2f, w - 4f, h - 4f);
                    g.DrawEllipse(p, x + 5f, y + 2f, w - 10f, h - 4f);
                    g.DrawLine(p, x + 2.5f, cy, x + w - 2.5f, cy);
                }
                else if (l.Contains("gateway") || l.Contains("dns"))
                {
                    g.DrawRectangle(p, x + 2f, y + 7f, w - 4f, h - 9.5f);
                    g.DrawLine(p, x + 5f, y + 2f, x + 5f, y + 7f);
                    g.DrawLine(p, x + w - 5f, y + 3f, x + w - 5f, y + 7f);
                    g.FillEllipse(b, x + 4.5f, y + h - 5.2f, 2f, 2f);
                }
                else if (l.StartsWith("isp"))
                {
                    g.DrawRectangle(p, x + 2.5f, y + 2f, w - 5f, 4.5f);
                    g.DrawRectangle(p, x + 2.5f, y + 9f, w - 5f, 4.5f);
                }
                else if (l.StartsWith("location"))
                {
                    g.DrawEllipse(p, x + 3.5f, y + 2f, w - 7f, w - 7f);
                    g.DrawLine(p, cx - 2.8f, y + 9.5f, cx, y + h - 1.5f);
                    g.DrawLine(p, cx + 2.8f, y + 9.5f, cx, y + h - 1.5f);
                }
                else if (l.StartsWith("installed"))
                {
                    g.DrawRectangle(p, x + 2f, y + 3.5f, w - 4f, h - 5.5f);
                    g.DrawLine(p, x + 2.5f, y + 7f, x + w - 2.5f, y + 7f);
                    g.DrawLine(p, x + 5.5f, y + 1.5f, x + 5.5f, y + 4f);
                    g.DrawLine(p, x + w - 5.5f, y + 1.5f, x + w - 5.5f, y + 4f);
                }
                else if (l.StartsWith("uptime"))
                {
                    g.DrawEllipse(p, x + 2f, y + 2f, w - 4f, h - 4f);
                    g.DrawLine(p, cx, cy, cx, y + 4.5f);
                    g.DrawLine(p, cx, cy, x + w - 4.5f, cy);
                }
                else if (l.StartsWith("bios"))
                {
                    g.DrawRectangle(p, x + 3f, y + 3f, w - 6f, h - 6f);
                    g.FillRectangle(b, cx - 2f, cy - 2f, 4f, 4f);
                }
                else if (l.StartsWith("motherboard"))
                {
                    g.DrawRectangle(p, x + 2f, y + 2f, w - 4f, h - 4f);
                    g.DrawRectangle(p, x + 5f, y + 5f, w - 10f, h - 10f);
                }
                else
                {
                    g.DrawRectangle(p, x + 1.5f, y + 2.5f, w - 3f, h - 6.5f);
                    g.DrawLine(p, cx - 3f, y + h - 2f, cx + 3f, y + h - 2f);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        /// <summary>Hardware package graphic (CPU chip, GPU cooler shroud, or RAM module) with vector brand logo.</summary>
        public static void DrawChip(Graphics g, Rectangle r, BrandStyle b, string series) { DrawHardware(g, r, b, series, "cpu"); }

        public static void DrawHardware(Graphics g, Rectangle r, BrandStyle b, string series, string kind)
        {
            if (b == null) b = Brands.Generic("?");
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pin = r.Width * 0.07f, gap = r.Width * 0.11f;
            RectangleF body = new RectangleF(r.Left + pin, r.Top + pin, r.Width - 2 * pin, r.Height - 2 * pin);

            if (kind == "ram")
            {
                body = new RectangleF(r.Left + 2, r.Top + r.Height * 0.14f, r.Width - 4, r.Height * 0.68f);
                using (SolidBrush gp = new SolidBrush(Color.FromArgb(228, 178, 56)))
                    for (int i = 0; i < 8; i++)
                        g.FillRectangle(gp, body.Left + 6 + i * ((body.Width - 12) / 8f), body.Bottom - 1, Math.Max(3f, body.Width * 0.07f), r.Height * 0.12f);
            }
            else if (kind == "gpu")
            {
                body = new RectangleF(r.Left + 2, r.Top + r.Height * 0.12f, r.Width - 4, r.Height * 0.72f);
                using (SolidBrush gp = new SolidBrush(Color.FromArgb(228, 178, 56)))
                    g.FillRectangle(gp, body.Left + body.Width * 0.16f, body.Bottom - 1, body.Width * 0.50f, r.Height * 0.10f);
            }
            else
            {
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
            }

            using (GraphicsPath p = Round(body, body.Width * 0.13f))
            using (LinearGradientBrush lg = new LinearGradientBrush(body, b.Back, b.Back2, 55f))
                g.FillPath(lg, p);
            RectangleF die = RectangleF.Inflate(body, -gap * 0.52f, -gap * 0.52f);
            using (GraphicsPath p = Round(die, die.Width * 0.09f))
            using (Pen pn = new Pen(Color.FromArgb(75, 255, 255, 255), Math.Max(1f, r.Width * 0.012f)))
                g.DrawPath(pn, p);

            Image logo = LogoFor(b.Key);
            bool hasSeries = !string.IsNullOrEmpty(series) && !series.Equals(b.Word, StringComparison.OrdinalIgnoreCase);

            if (logo != null)
            {
                float maxW = die.Width * 0.84f;
                float maxH = hasSeries ? die.Height * 0.44f : die.Height * 0.56f;
                float sc = Math.Min(maxW / logo.Width, maxH / logo.Height);
                float lw = logo.Width * sc, lh = logo.Height * sc;
                float ly = hasSeries ? die.Top + die.Height * 0.14f + (maxH - lh) * 0.5f : die.Top + (die.Height - lh) * 0.5f;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (b.Fore != Color.White)
                {
                    using (System.Drawing.Imaging.ImageAttributes ia = TintMatrix(b.Fore))
                        g.DrawImage(logo, new Rectangle((int)Math.Round(die.Left + (die.Width - lw) * 0.5f), (int)Math.Round(ly), Math.Max(1, (int)Math.Round(lw)), Math.Max(1, (int)Math.Round(lh))),
                            0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, ia);
                }
                else
                {
                    g.DrawImage(logo, die.Left + (die.Width - lw) * 0.5f, ly, lw, lh);
                }
                if (hasSeries)
                {
                    float ss = FitFont(g, series, die.Width * 0.9f, die.Height * 0.22f, false);
                    using (Font f2 = new Font("Segoe UI Semibold", ss, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (SolidBrush sb2 = new SolidBrush(Color.FromArgb(230, b.Fore)))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.DrawString(series, f2, sb2, new RectangleF(die.Left, die.Top + die.Height * 0.62f, die.Width, die.Height * 0.30f), sf);
                    }
                }
                return;
            }

            float fs = FitFont(g, b.Word, die.Width * 0.86f, hasSeries ? die.Height * 0.32f : die.Height * 0.44f, true);
            using (Font f = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush tb = new SolidBrush(b.Fore))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                RectangleF wr = hasSeries ? new RectangleF(die.Left, die.Top + die.Height * 0.14f, die.Width, die.Height * 0.44f) : die;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(b.Word, f, tb, wr, sf);
                if (hasSeries)
                {
                    float ss = FitFont(g, series, die.Width * 0.9f, die.Height * 0.2f, false);
                    using (Font f2 = new Font("Segoe UI Semibold", ss, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (SolidBrush sb2 = new SolidBrush(Color.FromArgb(225, b.Fore)))
                        g.DrawString(series, f2, sb2, new RectangleF(die.Left, die.Top + die.Height * 0.58f, die.Width, die.Height * 0.30f), sf);
                }
            }
        }

        static System.Drawing.Imaging.ImageAttributes TintMatrix(Color c)
        {
            System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix(new float[][] {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { c.R / 255f, c.G / 255f, c.B / 255f, 0, 1 }
            });
            System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(cm);
            return ia;
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

        /// <summary>Small pill badge with the embedded brand PNG (and text for symbol-only marks); returns its width.</summary>
        public static int DrawBadge(Graphics g, BrandStyle b, int x, int y, int h, Font f)
        {
            if (b == null) return 0;
            Image logo = LogoFor(b.Key);
            bool wordmark = IsWordmarkLogo(b.Key, logo);
            int icoH = Math.Max(10, h - 6);
            int icoW = logo != null ? Math.Max(10, Math.Min(64, (int)Math.Round(icoH * (logo.Width / (float)logo.Height)))) : 0;

            Rectangle r;
            if (wordmark)
            {
                r = new Rectangle(x, y, icoW + 16, h);
            }
            else
            {
                Size ts = TextRenderer.MeasureText(b.Word, f);
                int padLeft = logo != null ? icoW + 10 : 6;
                r = new Rectangle(x, y, ts.Width + padLeft + 6, h);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, h / 2f - 1))
            using (LinearGradientBrush lg = new LinearGradientBrush(r, b.Back, b.Back2, 90f))
                g.FillPath(lg, p);

            if (logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int lx = wordmark ? x + (r.Width - icoW) / 2 : x + 6;
                int ly = y + (h - icoH) / 2;
                if (b.Fore != Color.White)
                {
                    using (System.Drawing.Imaging.ImageAttributes ia = TintMatrix(b.Fore))
                        g.DrawImage(logo, new Rectangle(lx, ly, icoW, icoH), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, ia);
                }
                else
                {
                    g.DrawImage(logo, lx, ly, icoW, icoH);
                }
            }

            if (!wordmark)
            {
                int textLeft = logo != null ? x + icoW + 7 : x + 3;
                Rectangle tr = new Rectangle(textLeft, y, r.Right - textLeft - 3, h);
                TextRenderer.DrawText(g, b.Word, f, tr, b.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            return r.Width;
        }
    }

    /// <summary>Live usage card: brand chip, big percentage, two detail lines and a 2-minute history graph.</summary>
    public class UtilCard : Control
    {
        public string Title = "", Value = "--", Line1 = "", Line2 = "";
        public BrandStyle Brand;
        public string Series = "";
        public string Kind = "cpu";
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
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawCard(g, ClientRectangle, Theme.Card, Theme.Border, Accent, 8);

            int chip = ShowChip ? Math.Min(76, Height - 64) : 0;
            int x = 14;
            if (chip > 30) { BrandArt.DrawHardware(g, new Rectangle(x, 12, chip, chip), Brand, Series, Kind); x += chip + 14; }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, Title, fTitle, new Point(x, 10), Theme.Muted);
            TextRenderer.DrawText(g, Value, fValue, new Point(x - 3, 25), Theme.Ink);
            TextRenderer.DrawText(g, Line1, fLine, new Rectangle(x, 65, Width - x - 8, 16), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, Line2, fLine, new Rectangle(x, 81, Width - x - 8, 16), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            // history graph
            Rectangle gr = new Rectangle(10, Math.Max(102, 12 + chip + 8), Width - 20, 0);
            gr.Height = Height - gr.Top - 10;
            if (History == null || gr.Height < 16) return;
            using (Pen grid = new Pen(Theme.Grid))
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
            TextRenderer.DrawText(g, "2 min", fLine, new Point(gr.Left, gr.Bottom - 15), Theme.Muted);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fTitle.Dispose(); fValue.Dispose(); fLine.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Key-value list with optional brand badges.</summary>
    public class SpecList : Control
    {
        public string Heading = "About this system";
        public List<SpecRow> Rows = new List<SpecRow>();
        public string Loading = "Reading system information...";
        readonly Font fBadge = new Font("Segoe UI", 8f, FontStyle.Bold), fBold = new Font("Segoe UI Semibold", 9.5f);
        const int RowH = 27, Head = 32;

        public SpecList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = new Font("Segoe UI", 9f);
        }

        public int ContentHeight { get { return Head + Math.Max(1, Rows.Count) * RowH + 8; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawCard(g, ClientRectangle, Theme.Card, Theme.Border, null, 8);
            TextRenderer.DrawText(g, Heading, fBold, new Point(14, 8), Theme.Ink, TextFormatFlags.NoPrefix);
            using (Pen hp = new Pen(Theme.Grid)) g.DrawLine(hp, 1, Head - 1, Width - 2, Head - 1);
            if (Rows.Count == 0) { TextRenderer.DrawText(g, Loading, Font, new Point(14, Head + 6), Theme.Muted, TextFormatFlags.NoPrefix); return; }

            int labelW = Width < 540 ? 78 : 108;
            bool hasAnyBrand = false;
            int maxBrandW = 0;
            for (int i = 0; i < Rows.Count; i++)
            {
                labelW = Math.Max(labelW, TextRenderer.MeasureText(Rows[i].Label, Font, Size.Empty, TextFormatFlags.NoPrefix).Width + 8);
                if (Rows[i].Brand != null) { hasAnyBrand = true; maxBrandW = Math.Max(maxBrandW, BrandArt.BadgeWidth(Rows[i].Brand, RowH - 10, fBadge)); }
            }
            int brandX = 38 + labelW + 8;
            int brandCol = Width < 540 ? maxBrandW : Math.Max(82, maxBrandW);
            int valX = hasAnyBrand ? brandX + brandCol + 10 : brandX;

            for (int i = 0; i < Rows.Count; i++)
            {
                SpecRow r = Rows[i];
                int y = Head + i * RowH;
                if (i % 2 == 1) using (SolidBrush zb = new SolidBrush(Theme.RowAlt)) g.FillRectangle(zb, 1, y, Width - 2, RowH);
                BrandArt.DrawCategoryIcon(g, new RectangleF(14, y + (RowH - 16) * 0.5f, 16, 16), r.Label, r.Value, Theme.Muted, Theme.Accent);
                TextRenderer.DrawText(g, r.Label, Font, new Rectangle(38, y, labelW, RowH), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                if (r.Brand != null)
                {
                    BrandArt.DrawBadge(g, r.Brand, brandX, y + 5, RowH - 10, fBadge);
                    g.SmoothingMode = SmoothingMode.None;
                }
                TextRenderer.DrawText(g, r.Value, Font, new Rectangle(valX, y, Math.Max(0, Width - valX - 12), RowH), Theme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fBadge.Dispose(); fBold.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
