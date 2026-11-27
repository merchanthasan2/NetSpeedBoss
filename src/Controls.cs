using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeedBoss
{
    public class DbListView : ListView
    {
        int hoverIdx = -1;
        HeaderSkin headerSkin;

        class HeaderSkin : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0014 /*WM_ERASEBKGND*/)
                {
                    using (Graphics g = Graphics.FromHdc(m.WParam))
                    {
                        Rectangle r = new Rectangle(0, 0, 4096, 64);
                        using (SolidBrush b = new SolidBrush(Theme.CardAlt)) g.FillRectangle(b, r);
                    }
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        public DbListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            OwnerDraw = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            IntPtr hdr = Native.SendMessage(Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
            if (hdr != IntPtr.Zero)
            {
                if (headerSkin == null) headerSkin = new HeaderSkin();
                headerSkin.ReleaseHandle();
                headerSkin.AssignHandle(hdr);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (headerSkin != null) headerSkin.ReleaseHandle();
            base.OnHandleDestroyed(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            ListViewHitTestInfo ht = HitTest(e.Location);
            int idx = ht.Item != null ? ht.Item.Index : -1;
            if (idx != hoverIdx)
            {
                int prev = hoverIdx;
                hoverIdx = idx;
                if (prev >= 0 && prev < Items.Count) Invalidate(Items[prev].Bounds);
                if (hoverIdx >= 0 && hoverIdx < Items.Count) Invalidate(Items[hoverIdx].Bounds);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverIdx >= 0 && hoverIdx < Items.Count) { int p = hoverIdx; hoverIdx = -1; Invalidate(Items[p].Bounds); }
            else hoverIdx = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = false;
            using (SolidBrush bg = new SolidBrush(Theme.CardAlt))
                e.Graphics.FillRectangle(bg, e.Bounds);
            using (Pen border = new Pen(Theme.Border))
                e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using (Pen div = new Pen(Theme.Grid))
                e.Graphics.DrawLine(div, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
            TextFormatFlags flags = AlignFlag(e.Header.TextAlign) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            Rectangle tr = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, tr, Theme.Muted, flags);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            e.DrawDefault = false;
            Color bg = RowBg(e.Item, e.ItemIndex);
            Rectangle full = new Rectangle(0, e.Bounds.Y, Math.Max(ClientRectangle.Width, e.Bounds.Width), e.Bounds.Height);
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, full);
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = false;
            Color bg = RowBg(e.Item, e.ItemIndex);
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            Rectangle tr = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 12), e.Bounds.Height);
            if (e.ColumnIndex == 0 && SmallImageList != null && e.Item.ImageIndex >= 0 && e.Item.ImageIndex < SmallImageList.Images.Count)
            {
                int iy = e.Bounds.Y + Math.Max(0, (e.Bounds.Height - 16) / 2);
                SmallImageList.Draw(e.Graphics, e.Bounds.X + 6, iy, 16, 16, e.Item.ImageIndex);
                tr = new Rectangle(e.Bounds.X + 26, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 32), e.Bounds.Height);
            }

            Color fg = e.Item.Selected ? Theme.Ink : (e.Item.ForeColor == Theme.Muted ? Theme.Muted : Theme.Ink);
            TextFormatFlags flags = AlignFlag(e.Header.TextAlign) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, tr, fg, flags);
        }

        Color RowBg(ListViewItem item, int idx)
        {
            if (item.Selected) return Color.FromArgb(Theme.IsDark ? 48 : 30, Theme.Accent);
            if (idx == hoverIdx) return Theme.Hover;
            return idx % 2 == 1 ? Theme.RowAlt : Theme.Card;
        }

        static TextFormatFlags AlignFlag(HorizontalAlignment a)
        {
            if (a == HorizontalAlignment.Right) return TextFormatFlags.Right;
            if (a == HorizontalAlignment.Center) return TextFormatFlags.HorizontalCenter;
            return TextFormatFlags.Left;
        }
    }

    public class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly ModernMenuRenderer Instance = new ModernMenuRenderer();

        class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.Border; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color SeparatorDark { get { return Theme.Grid; } }
            public override Color SeparatorLight { get { return Theme.Grid; } }
        }

        public ModernMenuRenderer() : base(new Colors()) { RoundedEdges = true; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            Rectangle r = new Rectangle(4, 1, Math.Max(4, e.Item.Width - 8), Math.Max(4, e.Item.Height - 2));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = BrandArt.Round(r, 4))
            using (SolidBrush b = new SolidBrush(e.Item.Pressed ? Color.FromArgb(Theme.IsDark ? 55 : 35, Theme.Accent) : Theme.Hover))
                e.Graphics.FillPath(b, p);
            e.Graphics.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Grid)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = new Rectangle(e.ImageRectangle.X - 1, e.ImageRectangle.Y - 1, e.ImageRectangle.Width + 2, e.ImageRectangle.Height + 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = BrandArt.Round(r, 4))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(36, Theme.Accent)))
            using (Pen p = new Pen(Theme.Accent))
            {
                e.Graphics.FillPath(b, gp);
                e.Graphics.DrawPath(p, gp);
            }
            using (Pen cp = new Pen(Theme.Accent, 2f))
            {
                cp.StartCap = LineCap.Round; cp.EndCap = LineCap.Round; cp.LineJoin = LineJoin.Round;
                float cx = r.Left + r.Width * 0.25f, cy = r.Top + r.Height * 0.52f;
                e.Graphics.DrawLines(cp, new PointF[] {
                    new PointF(cx, cy),
                    new PointF(r.Left + r.Width * 0.45f, r.Top + r.Height * 0.72f),
                    new PointF(r.Left + r.Width * 0.76f, r.Top + r.Height * 0.30f)
                });
            }
            e.Graphics.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? Theme.Muted : Theme.Border;
            base.OnRenderArrow(e);
        }
    }

    public static class Theme
    {
        public static bool IsDark;
        public static Color Bg = Color.FromArgb(244, 246, 249);
        public static Color Card = Color.White;
        public static Color CardAlt = Color.FromArgb(249, 250, 252);
        public static Color Border = Color.FromArgb(224, 228, 234);
        public static Color Muted = Color.FromArgb(102, 110, 122);
        public static Color Ink = Color.FromArgb(24, 28, 35);
        public static Color Accent = Color.FromArgb(0, 120, 212);
        public static Color Grid = Color.FromArgb(234, 237, 242);
        public static Color Track = Color.FromArgb(234, 237, 242);
        public static Color Hover = Color.FromArgb(244, 247, 251);
        public static Color RowAlt = Color.FromArgb(249, 250, 252);
        public static Color TooltipBg = Color.FromArgb(240, 24, 28, 35);

        public static bool SystemIsDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static void Apply(DashTheme mode)
        {
            bool dark = mode == DashTheme.Dark || (mode == DashTheme.Auto && SystemIsDark());
            IsDark = dark;
            if (dark)
            {
                Bg = Color.FromArgb(20, 22, 26);
                Card = Color.FromArgb(29, 32, 38);
                CardAlt = Color.FromArgb(35, 39, 46);
                Border = Color.FromArgb(48, 53, 63);
                Muted = Color.FromArgb(150, 158, 170);
                Ink = Color.FromArgb(237, 240, 245);
                Accent = Color.FromArgb(76, 166, 255);
                Grid = Color.FromArgb(42, 47, 56);
                Track = Color.FromArgb(46, 52, 62);
                Hover = Color.FromArgb(38, 43, 52);
                RowAlt = Color.FromArgb(33, 37, 44);
                TooltipBg = Color.FromArgb(240, 14, 16, 20);
            }
            else
            {
                Bg = Color.FromArgb(244, 246, 249);
                Card = Color.White;
                CardAlt = Color.FromArgb(249, 250, 252);
                Border = Color.FromArgb(224, 228, 234);
                Muted = Color.FromArgb(102, 110, 122);
                Ink = Color.FromArgb(24, 28, 35);
                Accent = Color.FromArgb(0, 120, 212);
                Grid = Color.FromArgb(222, 227, 235);
                Track = Color.FromArgb(234, 237, 242);
                Hover = Color.FromArgb(244, 247, 251);
                RowAlt = Color.FromArgb(249, 250, 252);
                TooltipBg = Color.FromArgb(235, 33, 37, 43);
            }
        }

        public static void DrawCard(Graphics g, Rectangle bounds, Color fill, Color border, Color? leftAccent, int radius)
        {
            Rectangle r = new Rectangle(bounds.X, bounds.Y, Math.Max(2, bounds.Width - 1), Math.Max(2, bounds.Height - 1));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = BrandArt.Round(r, radius))
            using (SolidBrush fb = new SolidBrush(fill))
            using (Pen bp = new Pen(border))
            {
                g.FillPath(fb, p);
                if (leftAccent.HasValue)
                {
                    Region prev = g.Clip;
                    g.SetClip(p);
                    using (SolidBrush ab = new SolidBrush(leftAccent.Value))
                        g.FillRectangle(ab, bounds.X, bounds.Y, 4, bounds.Height);
                    g.Clip = prev;
                }
                g.DrawPath(bp, p);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
    }

    public class CardPanel : Panel
    {
        public int Radius = 8;
        public CardPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Padding = new Padding(1);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawCard(e.Graphics, ClientRectangle, BackColor, Theme.Border, null, Radius);
        }
    }

    /// <summary>Live line graph of download / upload history with hover read-out.</summary>
    public class GraphPanel : Control
    {
        public TrayApp App;
        public int Seconds = 120;
        int hoverX = -1;

        public GraphPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = new Font("Segoe UI", 8.5f);
        }

        protected override void OnMouseMove(MouseEventArgs e) { hoverX = e.X; Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverX = -1; Invalidate(); base.OnMouseLeave(e); }

        public static double NiceMax(double bytesPerSec, SpeedUnit unit)
        {
            bool bits = unit == SpeedUnit.Bits;
            double v = Math.Max(bytesPerSec * (bits ? 8 : 1), 1);
            double b = bits ? 1000 : 1024, scale = 1;
            while (v / scale >= b) scale *= b;
            double m = v / scale;
            double[] steps = { 1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 40, 50, 64, 80, 100, 128, 160, 200, 256, 400, 500, 512, 800, 1000, 1024 };
            double nice = 1024;
            foreach (double s in steps) if (s >= m) { nice = s; break; }
            return nice * scale / (bits ? 8 : 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (App == null) return;
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Settings s = App.S;
            int lblW = TextRenderer.MeasureText("888.8 Mbps", Font).Width + 8;
            Rectangle plot = new Rectangle(lblW, 28, Width - lblW - 14, Height - 28 - 24);
            if (plot.Width < 20 || plot.Height < 20) return;

            int n = Math.Max(10, Math.Min(App.HistRx.Length, Seconds * 1000 / Math.Max(250, s.IntervalMs)));
            double max = 0;
            for (int i = 0; i < n; i++) max = Math.Max(max, Math.Max(App.Hist(App.HistRx, i), App.Hist(App.HistTx, i)));
            max = NiceMax(Math.Max(max, 10 * 1024), s.Unit);
            List<KeyValuePair<TrayApp.GraphApp, double[]>> apps = new List<KeyValuePair<TrayApp.GraphApp, double[]>>();
            foreach (TrayApp.GraphApp ga in App.GraphApps)
            {
                double[] h;
                if (App.AppHistRx.TryGetValue(ga.Name, out h)) apps.Add(new KeyValuePair<TrayApp.GraphApp, double[]>(ga, h));
            }

            using (Pen grid = new Pen(Theme.Grid))
            {
                int yStep = plot.Height < 105 ? 2 : 1;
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Bottom - i * plot.Height / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    if (i % yStep == 0)
                        TextRenderer.DrawText(g, Fmt.Speed(max * i / 4, s.Unit), Font, new Rectangle(0, y - 8, lblW - 6, 16), Theme.Muted,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                for (int i = 0; i <= 4; i++)
                {
                    int x = plot.Right - i * plot.Width / 4;
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    double secs = (n - 1) * s.IntervalMs / 1000.0 * i / 4;
                    string lbl = i == 0 ? "now" : "-" + (secs >= 60 ? (secs / 60).ToString("0.#") + "m" : secs.ToString("0") + "s");
                    TextRenderer.DrawText(g, lbl, Font, new Rectangle(x - 30, plot.Bottom + 4, 60, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSeries(g, plot, App.HistRx, n, max, s.DownColor);
            DrawSeries(g, plot, App.HistTx, n, max, s.UpColor);
            foreach (KeyValuePair<TrayApp.GraphApp, double[]> kv in apps) DrawLine(g, plot, kv.Value, n, max, kv.Key.Color);
            g.SmoothingMode = SmoothingMode.None;

            // legend
            int lx = plot.Left;
            lx = Legend(g, lx, "Download  " + Fmt.Speed(App.CurRx, s.Unit), s.DownColor);
            lx = Legend(g, lx + 18, "Upload  " + Fmt.Speed(App.CurTx, s.Unit), s.UpColor);
            foreach (KeyValuePair<TrayApp.GraphApp, double[]> kv in apps)
            {
                string t = kv.Key.Name + "  " + Fmt.Speed(App.Hist(kv.Value, 0), s.Unit);
                if (lx + 36 + TextRenderer.MeasureText(t, Font).Width > Width - 130) break;   // keep clear of the range picker
                lx = Legend(g, lx + 18, t, kv.Key.Color);
            }

            // hover read-out
            if (hoverX >= plot.Left && hoverX <= plot.Right)
            {
                float step = plot.Width / (float)(n - 1);
                int ago = (int)Math.Round((plot.Right - hoverX) / step);
                ago = Math.Max(0, Math.Min(n - 1, ago));
                float x = plot.Right - ago * step;
                using (Pen p = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot })
                    g.DrawLine(p, x, plot.Top, x, plot.Bottom);

                double rxv = App.Hist(App.HistRx, ago), txv = App.Hist(App.HistTx, ago);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawHoverDot(g, x, (float)(plot.Bottom - Math.Min(max, rxv) / max * plot.Height), s.DownColor);
                DrawHoverDot(g, x, (float)(plot.Bottom - Math.Min(max, txv) / max * plot.Height), s.UpColor);
                foreach (KeyValuePair<TrayApp.GraphApp, double[]> kv in apps)
                    DrawHoverDot(g, x, (float)(plot.Bottom - Math.Min(max, App.Hist(kv.Value, ago)) / max * plot.Height), kv.Key.Color);
                g.SmoothingMode = SmoothingMode.None;

                string when = ago == 0 ? "now" : (ago * s.IntervalMs / 1000.0).ToString("0.#") + "s ago";
                string txt = "↓ " + Fmt.Speed(rxv, s.Unit) + "   ↑ " + Fmt.Speed(txv, s.Unit) + "   (" + when + ")";
                foreach (KeyValuePair<TrayApp.GraphApp, double[]> kv in apps)
                    txt += "\n" + kv.Key.Name + "  ↓ " + Fmt.Speed(App.Hist(kv.Value, ago), s.Unit);
                Size ts = TextRenderer.MeasureText(txt, Font);
                int bx = Math.Max(plot.Left + 4, (int)Math.Min(x + 10, plot.Right - ts.Width - 16));
                Rectangle box = new Rectangle(bx, plot.Top + 6, ts.Width + 14, ts.Height + 8);
                Theme.DrawCard(g, box, Theme.TooltipBg, Theme.Border, null, 6);
                TextRenderer.DrawText(g, txt, Font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        static void DrawHoverDot(Graphics g, float x, float y, Color c)
        {
            using (SolidBrush halo = new SolidBrush(Theme.Card))
            using (SolidBrush dot = new SolidBrush(c))
            {
                g.FillEllipse(halo, x - 4.5f, y - 4.5f, 9f, 9f);
                g.FillEllipse(dot, x - 3f, y - 3f, 6f, 6f);
            }
        }

        void DrawLine(Graphics g, Rectangle plot, double[] h, int n, double max, Color c)
        {
            PointF[] line = new PointF[n];
            float step = plot.Width / (float)(n - 1);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Min(max, App.Hist(h, n - 1 - i));
                line[i] = new PointF(plot.Left + i * step, (float)(plot.Bottom - v / max * plot.Height));
            }
            using (Pen p = new Pen(c, 2f)) g.DrawLines(p, line);
        }

        int Legend(Graphics g, int x, string text, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, x, 10, 14, 4);
            Size sz = TextRenderer.MeasureText(text, Font);
            TextRenderer.DrawText(g, text, Font, new Point(x + 18, 4), Theme.Ink);
            return x + 18 + sz.Width;
        }

        void DrawSeries(Graphics g, Rectangle plot, double[] h, int n, double max, Color c)
        {
            PointF[] line = new PointF[n];
            float step = plot.Width / (float)(n - 1);
            for (int i = 0; i < n; i++)
            {
                int ago = n - 1 - i;
                double v = Math.Min(max, App.Hist(h, ago));
                line[i] = new PointF(plot.Left + i * step, (float)(plot.Bottom - v / max * plot.Height));
            }
            PointF[] fill = new PointF[n + 2];
            Array.Copy(line, fill, n);
            fill[n] = new PointF(plot.Right, plot.Bottom);
            fill[n + 1] = new PointF(plot.Left, plot.Bottom);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(40, c))) g.FillPolygon(b, fill);
            using (Pen p = new Pen(c, 1.8f)) g.DrawLines(p, line);
        }
    }

    /// <summary>Top 5 downloading apps with tick boxes that add each app's line to the speed graph.</summary>
    public class TopAppsPanel : Control
    {
        public TrayApp App;
        public Func<string, Image> IconFor;
        readonly List<TrayApp.AppSeries> rows = new List<TrayApp.AppSeries>();
        Rectangle linkRect = Rectangle.Empty;
        int hover = -1;
        const int Head = 30, RowH = 26, Max = 5;
        readonly Font bold = new Font("Segoe UI Semibold", 9.5f);
        string flash;
        DateTime flashUntil;

        public TopAppsPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = new Font("Segoe UI", 9f);
        }

        public static int PreferredHeight { get { return Head + RowH * Max + 8; } }

        /// <summary>Re-rank: top apps by 5-second average download, plus any app already on the graph.</summary>
        public void RefreshRows()
        {
            rows.Clear();
            if (App == null || !App.Procs.EtwActive) { Invalidate(); return; }
            List<TrayApp.AppSeries> all = new List<TrayApp.AppSeries>(App.AppNow);
            Dictionary<string, double> score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (TrayApp.AppSeries a in all) score[a.Name] = App.AppAvgRx(a.Name, 5);
            all.Sort(delegate(TrayApp.AppSeries a, TrayApp.AppSeries b)
            {
                int r = score[b.Name].CompareTo(score[a.Name]);
                return r != 0 ? r : b.TotIn.CompareTo(a.TotIn);
            });
            // graphed apps keep their row so their tick box never disappears
            foreach (TrayApp.AppSeries a in all) if (App.IsGraphed(a.Name)) rows.Add(a);
            foreach (TrayApp.AppSeries a in all)
            {
                if (rows.Count >= Max) break;
                if (App.IsGraphed(a.Name) || (score[a.Name] <= 0 && a.TotIn == 0)) continue;
                rows.Add(a);
            }
            rows.Sort(delegate(TrayApp.AppSeries a, TrayApp.AppSeries b)
            {
                int r = score[b.Name].CompareTo(score[a.Name]);
                return r != 0 ? r : b.TotIn.CompareTo(a.TotIn);
            });
            Invalidate();
        }

        Rectangle RowRect(int i) { return new Rectangle(0, Head + i * RowH, Width, RowH); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < rows.Count; i++) if (RowRect(i).Contains(e.Location)) { h = i; break; }
            bool overLink = linkRect.Contains(e.Location);
            Cursor = h >= 0 || overLink ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || App == null) return;
            if (linkRect.Contains(e.Location)) { App.RestartElevated(); return; }
            for (int i = 0; i < rows.Count; i++)
                if (RowRect(i).Contains(e.Location))
                {
                    if (!App.ToggleGraphApp(rows[i].Name)) { flash = "Up to 5 apps can be on the graph at once"; flashUntil = DateTime.UtcNow.AddSeconds(3); }
                    Invalidate();
                    Control p = Parent;
                    while (p != null) { foreach (Control c in p.Controls) if (c is GraphPanel) c.Invalidate(); p = p.Parent; }
                    return;
                }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawCard(g, ClientRectangle, Theme.Card, Theme.Border, null, 8);
            if (App == null) return;
            Settings s = App.S;
            TextRenderer.DrawText(g, "Top downloading apps", bold, new Rectangle(10, 0, Width, Head), Theme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            string hint = flash != null && DateTime.UtcNow < flashUntil ? flash : "Tick an app to see it on the graph";
            TextRenderer.DrawText(g, hint, Font, new Rectangle(0, 0, Width - 12, Head), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            using (Pen hp = new Pen(Theme.Grid)) g.DrawLine(hp, 1, Head - 1, Width - 2, Head - 1);
            linkRect = Rectangle.Empty;

            if (!App.Procs.EtwActive)
            {
                Rectangle box = new Rectangle(10, Head + 6, Width - 20, Height - Head - 14);
                TextRenderer.DrawText(g, "Windows only shares per-app speeds with apps running as administrator.", Font,
                    new Point(box.Left + 2, box.Top + 8), Theme.Muted);
                string link = "Restart NetSpeed Boss as administrator \u2192";
                Size ls = TextRenderer.MeasureText(link, bold);
                linkRect = new Rectangle(box.Left + 2, box.Top + 34, ls.Width, ls.Height);
                TextRenderer.DrawText(g, link, bold, linkRect.Location, Theme.Accent);
                TextRenderer.DrawText(g, "Tip: Settings \u2192 \"Start as Administrator at sign-in\" skips this prompt at every sign-in.", Font,
                    new Point(box.Left + 2, box.Top + 62), Theme.Muted);
                return;
            }
            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "No apps are downloading right now.", Font, new Point(12, Head + 10), Theme.Muted);
                return;
            }

            double maxRx = 1;
            foreach (TrayApp.AppSeries a in rows) maxRx = Math.Max(maxRx, a.Rx);
            int speedW = TextRenderer.MeasureText("▼ 888.8 MB/s", Font).Width + 6;
            for (int i = 0; i < rows.Count; i++)
            {
                TrayApp.AppSeries a = rows[i];
                Rectangle r = RowRect(i);
                TrayApp.GraphApp ga = App.FindGraphed(a.Name);
                if (i == hover) using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillRectangle(hb, 1, r.Top, Width - 2, r.Height);

                int x = 10;
                CheckBoxRenderer.DrawCheckBox(g, new Point(x, r.Top + (RowH - 13) / 2),
                    ga != null ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                x += 22;
                Color c = ga != null ? ga.Color : Theme.Border;
                using (SolidBrush cb = new SolidBrush(c)) g.FillRectangle(cb, x, r.Top + RowH / 2 - 2, 12, 4);
                x += 18;
                Image ic = IconFor == null ? null : IconFor(a.Path);
                if (ic != null) g.DrawImage(ic, x, r.Top + (RowH - 16) / 2, 16, 16);
                x += 22;

                int right = Width - 10;
                Rectangle upR = new Rectangle(right - speedW, r.Top, speedW, RowH);
                Rectangle dnR = new Rectangle(upR.Left - speedW, r.Top, speedW, RowH);
                TextFormatFlags fr = TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, "▼ " + Fmt.Speed(a.Rx, s.Unit), Font, dnR, a.Rx > 0 ? Theme.Ink : Theme.Muted, fr);
                TextRenderer.DrawText(g, "▲ " + Fmt.Speed(a.Tx, s.Unit), Font, upR, Theme.Muted, fr);

                int nameW = Math.Min(220, Math.Max(80, dnR.Left - x - 120));
                TextRenderer.DrawText(g, a.Name, Font, new Rectangle(x, r.Top, nameW, RowH), Theme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

                // share-of-download bar
                int bx = x + nameW + 10, bw = dnR.Left - bx - 14;
                if (bw > 20)
                {
                    using (SolidBrush track = new SolidBrush(Theme.Track)) g.FillRectangle(track, bx, r.Top + RowH / 2 - 2, bw, 5);
                    int fw = (int)(bw * Math.Min(1, a.Rx / maxRx));
                    using (SolidBrush fill = new SolidBrush(ga != null ? ga.Color : s.DownColor)) g.FillRectangle(fill, bx, r.Top + RowH / 2 - 2, fw, 5);
                }
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) bold.Dispose(); base.Dispose(disposing); }
    }

    public class UsageAppRow
    {
        public string Name;
        public long Down, Up;
        public long Total { get { return Down + Up; } }
    }

    /// <summary>Ranked per-app data usage breakdown for the selected date range.</summary>
    public class UsageTopAppsPanel : Control
    {
        public TrayApp App;
        readonly List<UsageAppRow> rows = new List<UsageAppRow>();
        readonly Font bold = new Font("Segoe UI Semibold", 9.5f);
        readonly Font small = new Font("Segoe UI", 8f);
        string span = "";
        long rangeTotal;
        int hover = -1;
        Rectangle linkRect = Rectangle.Empty;
        const int Head = 30, RowH = 26;

        public UsageTopAppsPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = new Font("Segoe UI", 9f);
        }

        public void SetData(string spanText, long totalBytes, List<UsageAppRow> items)
        {
            span = spanText ?? "";
            rangeTotal = totalBytes;
            rows.Clear();
            if (items != null) rows.AddRange(items);
            Invalidate();
        }

        Rectangle RowRect(int i) { return new Rectangle(0, Head + i * RowH, Width, RowH); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            int maxRows = Math.Max(0, (Height - Head - 6) / RowH);
            for (int i = 0; i < Math.Min(rows.Count, maxRows); i++)
                if (RowRect(i).Contains(e.Location)) { h = i; break; }
            Cursor = linkRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && App != null && linkRect.Contains(e.Location))
                App.RestartElevated();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawCard(g, ClientRectangle, Theme.Card, Theme.Border, null, 8);

            TextRenderer.DrawText(g, "Top apps", bold, new Rectangle(10, 0, Width - 20, Head), Theme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (span.Length > 0)
                TextRenderer.DrawText(g, span, small, new Rectangle(90, 0, Width - 100, Head), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            using (Pen lp = new Pen(Theme.Grid)) g.DrawLine(lp, 1, Head - 1, Width - 2, Head - 1);
            linkRect = Rectangle.Empty;

            if (rows.Count == 0)
            {
                if (App != null && !App.Procs.EtwActive)
                {
                    TextRenderer.DrawText(g, "Per-app history is recorded while running as administrator.", Font,
                        new Point(12, Head + 14), Theme.Muted);
                    string link = "Restart NetSpeed Boss as administrator \u2192";
                    Size ls = TextRenderer.MeasureText(link, bold);
                    linkRect = new Rectangle(12, Head + 40, ls.Width, ls.Height);
                    TextRenderer.DrawText(g, link, bold, linkRect.Location, Theme.Accent);
                }
                else
                {
                    TextRenderer.DrawText(g, "No per-app usage recorded for these dates yet.", Font,
                        new Point(12, Head + 14), Theme.Muted);
                }
                return;
            }

            long maxTot = Math.Max(1, rows[0].Total);
            long denom = Math.Max(rangeTotal, maxTot);
            int maxRows = Math.Max(1, (Height - Head - 6) / RowH);
            int count = Math.Min(rows.Count, maxRows);
            Color dnCol = App != null ? App.S.DownColor : Theme.Accent;
            Color upCol = App != null ? App.S.UpColor : Color.FromArgb(255, 165, 60);
            int totW = TextRenderer.MeasureText("888.8 GB  100%", Font).Width + 4;
            int splitW = TextRenderer.MeasureText("▼ 888.8 GB  ▲ 888.8 GB", small).Width + 4;
            bool showSplit = Width >= 430;
            TextFormatFlags fr = TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine;

            for (int i = 0; i < count; i++)
            {
                UsageAppRow r = rows[i];
                Rectangle rr = RowRect(i);
                if (i == hover) using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillRectangle(hb, 1, rr.Top, Width - 2, rr.Height);

                int x = 10;
                TextRenderer.DrawText(g, (i + 1).ToString(), small, new Rectangle(x, rr.Top, 18, RowH), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                x += 20;

                int right = Width - 10;
                Rectangle totR = new Rectangle(right - totW, rr.Top, totW, RowH);
                int pct = denom > 0 ? (int)Math.Round(r.Total * 100.0 / denom) : 0;
                TextRenderer.DrawText(g, Fmt.Size(r.Total) + "  " + pct + "%", Font, totR, Theme.Ink, fr);

                int rightEdge = totR.Left - 6;
                if (showSplit)
                {
                    Rectangle spR = new Rectangle(rightEdge - splitW, rr.Top, splitW, RowH);
                    TextRenderer.DrawText(g, "\u25BC " + Fmt.Size(r.Down) + "  \u25B2 " + Fmt.Size(r.Up), small, spR, Theme.Muted, fr);
                    rightEdge = spR.Left - 8;
                }

                int nameW = Math.Min(150, Math.Max(75, (rightEdge - x) / 2));
                TextRenderer.DrawText(g, r.Name, Font, new Rectangle(x, rr.Top, nameW, RowH), Theme.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

                int bx = x + nameW + 8, bw = rightEdge - bx;
                if (bw > 24)
                {
                    using (SolidBrush track = new SolidBrush(Theme.Track)) g.FillRectangle(track, bx, rr.Top + RowH / 2 - 3, bw, 6);
                    int fw = (int)Math.Round(bw * Math.Min(1.0, (double)r.Total / maxTot));
                    int dw = r.Total > 0 ? (int)Math.Round(fw * ((double)r.Down / r.Total)) : 0;
                    if (dw > 0) using (SolidBrush db = new SolidBrush(dnCol)) g.FillRectangle(db, bx, rr.Top + RowH / 2 - 3, dw, 6);
                    if (fw - dw > 0) using (SolidBrush ub = new SolidBrush(upCol)) g.FillRectangle(ub, bx + dw, rr.Top + RowH / 2 - 3, fw - dw, 6);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { bold.Dispose(); small.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Stacked daily bars (download + upload) for the last N days.</summary>
    public class UsageChart : Control
    {
        public UsageStore Usage;
        public Settings S;
        public int Days = 30;
        int hover = -1;

        public UsageChart()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = new Font("Segoe UI", 8.5f);
        }

        Rectangle Plot()
        {
            int lblW = TextRenderer.MeasureText("888.8 GB", Font).Width + 8;
            return new Rectangle(lblW, 12, Width - lblW - 12, Height - 12 - 22);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Rectangle p = Plot();
            int h = -1;
            if (p.Width > 0 && e.X >= p.Left && e.X < p.Right) h = (e.X - p.Left) * Days / p.Width;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            if (Usage == null) return;
            Rectangle plot = Plot();
            if (plot.Width < 30 || plot.Height < 30) return;
            DateTime first = DateTime.Today.AddDays(-(Days - 1));
            long[][] v = new long[Days][];
            double max = 1024 * 1024;
            for (int i = 0; i < Days; i++) { v[i] = Usage.Day(first.AddDays(i)); max = Math.Max(max, v[i][0] + v[i][1]); }
            max = NiceSize(max);

            using (Pen grid = new Pen(Theme.Grid))
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Bottom - i * plot.Height / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, Fmt.Size(max * i / 4), Font, new Rectangle(0, y - 8, plot.Left - 6, 16), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }

            float slot = plot.Width / (float)Days;
            float bw = Math.Max(2, slot * 0.7f);
            using (SolidBrush bd = new SolidBrush(S.DownColor))
            using (SolidBrush bu = new SolidBrush(S.UpColor))
            using (SolidBrush hb = new SolidBrush(Theme.Hover))
                for (int i = 0; i < Days; i++)
                {
                    float x = plot.Left + i * slot + (slot - bw) / 2;
                    if (i == hover) g.FillRectangle(hb, plot.Left + i * slot, plot.Top, slot, plot.Height);
                    float hd = (float)(v[i][0] / max * plot.Height), hu = (float)(v[i][1] / max * plot.Height);
                    g.FillRectangle(bd, x, plot.Bottom - hd, bw, hd);
                    g.FillRectangle(bu, x, plot.Bottom - hd - hu, bw, hu);
                    DateTime d = first.AddDays(i);
                    if (i % 5 == 0 || i == Days - 1)
                        TextRenderer.DrawText(g, i == Days - 1 ? "Today" : d.ToString("d MMM"), Font,
                            new Rectangle((int)(x + bw / 2) - 30, plot.Bottom + 4, 60, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);
                }

            if (hover >= 0 && hover < Days)
            {
                DateTime d = first.AddDays(hover);
                string t = d.ToString("ddd d MMM") + ":  ↓ " + Fmt.Size(v[hover][0]) + "  ↑ " + Fmt.Size(v[hover][1]) +
                           "  = " + Fmt.Size(v[hover][0] + v[hover][1]);
                Size ts = TextRenderer.MeasureText(t, Font);
                int barCenter = (int)(plot.Left + (hover + 0.5f) * slot);
                int bx = Math.Max(plot.Left + 4, Math.Min(barCenter - (ts.Width + 14) / 2, plot.Right - ts.Width - 18));
                Rectangle box = new Rectangle(bx, plot.Top + 2, ts.Width + 14, ts.Height + 8);
                Theme.DrawCard(g, box, Theme.TooltipBg, Theme.Border, null, 6);
                TextRenderer.DrawText(g, t, Font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        static double NiceSize(double bytes)
        {
            double scale = 1;
            while (bytes / scale >= 1024) scale *= 1024;
            double m = bytes / scale;
            foreach (double s in new double[] { 1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 40, 50, 64, 80, 100, 128, 200, 256, 400, 500, 512, 800, 1024 })
                if (s >= m) return s * scale;
            return 1024 * scale;
        }
    }

    /// <summary>A rounded card with an accent stripe, title, value, and subtitle.</summary>
    public class StatCard : Panel
    {
        readonly Label title = new Label(), value = new Label(), sub = new Label();
        public Color AccentColor = Theme.Accent;

        public StatCard(string t)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Padding = new Padding(14, 10, 12, 8);
            Margin = new Padding(6);
            title.Text = t; title.ForeColor = Theme.Muted; title.BackColor = Color.Transparent; title.Font = new Font("Segoe UI", 8.5f); title.Dock = DockStyle.Top; title.Height = 18;
            value.Font = new Font("Segoe UI Semibold", 15.5f); value.ForeColor = Theme.Ink; value.BackColor = Color.Transparent; value.Dock = DockStyle.Top; value.Height = 32;
            sub.Font = new Font("Segoe UI", 8.5f); sub.ForeColor = Theme.Muted; sub.BackColor = Color.Transparent; sub.Dock = DockStyle.Top; sub.Height = 18;
            Controls.Add(sub); Controls.Add(value); Controls.Add(title);
            Height = 88;
        }

        public void Title(string t) { if (title.Text != t) title.Text = t; }

        public void Set(string v, string s) { if (value.Text != v) value.Text = v; if (sub.Text != s) sub.Text = s; }

        public void SyncTheme()
        {
            BackColor = Theme.Card;
            title.ForeColor = Theme.Muted;
            value.ForeColor = Theme.Ink;
            sub.ForeColor = Theme.Muted;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.DrawCard(e.Graphics, ClientRectangle, Theme.Card, Theme.Border, AccentColor, 8);
        }
    }
}
