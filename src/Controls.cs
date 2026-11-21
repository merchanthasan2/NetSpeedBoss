using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    public class DbListView : ListView
    {
        public DbListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
        }
    }

    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(246, 247, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(222, 225, 230);
        public static readonly Color Muted = Color.FromArgb(105, 112, 122);
        public static readonly Color Ink = Color.FromArgb(30, 34, 40);
        public static readonly Color Accent = Color.FromArgb(0, 120, 212);
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
            g.Clear(BackColor);
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

            using (Pen grid = new Pen(Color.FromArgb(234, 236, 240)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Bottom - i * plot.Height / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
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
                using (Pen p = new Pen(Color.FromArgb(160, 160, 170)) { DashStyle = DashStyle.Dot })
                    g.DrawLine(p, x, plot.Top, x, plot.Bottom);
                string txt = "↓ " + Fmt.Speed(App.Hist(App.HistRx, ago), s.Unit) + "   ↑ " + Fmt.Speed(App.Hist(App.HistTx, ago), s.Unit) +
                             "   (" + (ago * s.IntervalMs / 1000.0).ToString("0.#") + "s ago)";
                foreach (KeyValuePair<TrayApp.GraphApp, double[]> kv in apps)
                    txt += "\n" + kv.Key.Name + "  ↓ " + Fmt.Speed(App.Hist(kv.Value, ago), s.Unit);
                Size ts = TextRenderer.MeasureText(txt, Font);
                int bx = (int)Math.Min(x + 8, plot.Right - ts.Width - 8);
                Rectangle box = new Rectangle(bx, plot.Top + 6, ts.Width + 8, ts.Height + 6);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 33, 37, 43))) g.FillRectangle(b, box);
                TextRenderer.DrawText(g, txt, Font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
            g.Clear(BackColor);
            if (App == null) return;
            Settings s = App.S;
            TextRenderer.DrawText(g, "Top downloading apps", bold, new Rectangle(8, 0, Width, Head), Theme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            string hint = flash != null && DateTime.UtcNow < flashUntil ? flash : "Tick an app to see it on the graph";
            TextRenderer.DrawText(g, hint, Font, new Rectangle(0, 0, Width - 10, Head), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            linkRect = Rectangle.Empty;

            if (!App.Procs.EtwActive)
            {
                Rectangle box = new Rectangle(8, Head + 6, Width - 16, Height - Head - 14);
                TextRenderer.DrawText(g, "Windows only shares per-app speeds with apps running as administrator.", Font,
                    new Point(box.Left + 2, box.Top + 8), Theme.Muted);
                string link = "Restart NetSpeed Boss as administrator →";
                Size ls = TextRenderer.MeasureText(link, bold);
                linkRect = new Rectangle(box.Left + 2, box.Top + 34, ls.Width, ls.Height);
                TextRenderer.DrawText(g, link, bold, linkRect.Location, Theme.Accent);
                TextRenderer.DrawText(g, "Tip: Settings → \"Start with administrator rights\" skips this prompt at every sign-in.", Font,
                    new Point(box.Left + 2, box.Top + 62), Theme.Muted);
                return;
            }
            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "No apps are downloading right now.", Font, new Point(10, Head + 10), Theme.Muted);
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
                if (i == hover) using (SolidBrush hb = new SolidBrush(Color.FromArgb(245, 247, 250))) g.FillRectangle(hb, r);

                int x = 10;
                CheckBoxRenderer.DrawCheckBox(g, new Point(x, r.Top + (RowH - 13) / 2),
                    ga != null ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                x += 22;
                Color c = ga != null ? ga.Color : Color.FromArgb(200, 205, 212);
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
                    using (SolidBrush track = new SolidBrush(Color.FromArgb(238, 240, 243))) g.FillRectangle(track, bx, r.Top + RowH / 2 - 2, bw, 4);
                    int fw = (int)(bw * Math.Min(1, a.Rx / maxRx));
                    using (SolidBrush fill = new SolidBrush(ga != null ? ga.Color : s.DownColor)) g.FillRectangle(fill, bx, r.Top + RowH / 2 - 2, fw, 4);
                }
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) bold.Dispose(); base.Dispose(disposing); }
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
            g.Clear(BackColor);
            if (Usage == null) return;
            Rectangle plot = Plot();
            if (plot.Width < 30 || plot.Height < 30) return;
            DateTime first = DateTime.Today.AddDays(-(Days - 1));
            long[][] v = new long[Days][];
            double max = 1024 * 1024;
            for (int i = 0; i < Days; i++) { v[i] = Usage.Day(first.AddDays(i)); max = Math.Max(max, v[i][0] + v[i][1]); }
            max = NiceSize(max);

            using (Pen grid = new Pen(Color.FromArgb(234, 236, 240)))
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
            using (SolidBrush hb = new SolidBrush(Color.FromArgb(18, 0, 0, 0)))
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
                Rectangle box = new Rectangle(plot.Left + 6, plot.Top, ts.Width + 10, ts.Height + 6);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 33, 37, 43))) g.FillRectangle(b, box);
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

    /// <summary>A white rounded-looking card with a title and a big value.</summary>
    public class StatCard : Panel
    {
        readonly Label title = new Label(), value = new Label(), sub = new Label();
        public Color AccentColor = Theme.Accent;

        public StatCard(string t)
        {
            BackColor = Theme.Card;
            Padding = new Padding(12, 8, 12, 8);
            Margin = new Padding(6);
            title.Text = t; title.ForeColor = Theme.Muted; title.Font = new Font("Segoe UI", 8.5f); title.Dock = DockStyle.Top; title.Height = 18;
            value.Font = new Font("Segoe UI Semibold", 15f); value.ForeColor = Theme.Ink; value.Dock = DockStyle.Top; value.Height = 32;
            sub.Font = new Font("Segoe UI", 8.5f); sub.ForeColor = Theme.Muted; sub.Dock = DockStyle.Top; sub.Height = 18;
            Controls.Add(sub); Controls.Add(value); Controls.Add(title);
            Height = 86;
        }

        public void Title(string t) { if (title.Text != t) title.Text = t; }

        public void Set(string v, string s) { if (value.Text != v) value.Text = v; if (sub.Text != s) sub.Text = s; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(AccentColor)) e.Graphics.FillRectangle(b, 0, 0, 3, Height);
        }
    }
}
