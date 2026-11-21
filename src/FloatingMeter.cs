using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    /// <summary>Small borderless always-on-top window showing live download / upload speed.</summary>
    public class FloatingMeter : Form
    {
        readonly TrayApp app;
        double rx, tx;
        Font font;
        readonly ToolTip tip = new ToolTip();
        const int Pad = 6;

        public FloatingMeter(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "NetSpeed Boss Meter";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            ApplySettings();

            MouseDown += OnMouseDownDrag;
            MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) app.ShowDashboard(0); };
            MouseEnter += delegate
            {
                long[] t = app.Usage.Day(DateTime.Today);
                tip.SetToolTip(this, "Today:  ↓ " + Fmt.Size(t[0]) + "   ↑ " + Fmt.Size(t[1]) +
                                     "\nDouble-click for dashboard, right-click for options");
            };
            LocationChanged += delegate
            {
                if (!Visible) return;
                app.S.X = Left; app.S.Y = Top;
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                if (app != null && app.S.ClickThrough) cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT (form is layered via Opacity)
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4); } catch { }
        }

        public void ApplySettings()
        {
            Settings s = app.S;
            if (font != null) font.Dispose();
            font = new Font("Segoe UI Semibold", s.FontSize, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = s.BackColor;
            // keep the window layered (opacity < 1) so click-through works with WS_EX_TRANSPARENT
            Opacity = Math.Min(s.OpacityPct, 99) / 100.0;
            TopMost = s.AlwaysOnTop;
            ContextMenuStrip = app.Menu;

            Size sample = TextRenderer.MeasureText("▼ 888.8 MB/s", font);
            if (s.Unit == SpeedUnit.Bits) sample = TextRenderer.MeasureText("▼ 888.8 Mbps", font);
            int lineH = sample.Height;
            int w = sample.Width + Pad * 2;
            ClientSize = s.Horizontal ? new Size(w * 2 - Pad, lineH + Pad) : new Size(w, lineH * 2 + Pad);

            if (s.X == int.MinValue || !OnAnyScreen(new Rectangle(s.X, s.Y, Width, Height)))
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                s.X = wa.Right - Width - 12; s.Y = wa.Bottom - Height - 12;
            }
            Location = new Point(s.X, s.Y);
            Invalidate();
        }

        public void RecreateForClickThrough()
        {
            if (IsHandleCreated) RecreateHandle();
        }

        static bool OnAnyScreen(Rectangle r)
        {
            foreach (Screen sc in Screen.AllScreens)
                if (sc.Bounds.IntersectsWith(r)) return true;
            return false;
        }

        public void UpdateSpeed(double rxBps, double txBps)
        {
            rx = rxBps; tx = txBps;
            Invalidate();
            if (Visible && app.S.AlwaysOnTop)   // re-assert so we stay above the taskbar
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void OnMouseDownDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || app.S.LockPosition) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        }

        // Magnetic snapping to screen / work-area edges while dragging
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOVING)
            {
                Native.RECT r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                Screen sc = Screen.FromPoint(new Point(r.Left + w / 2, r.Top + h / 2));
                const int snap = 12;
                foreach (Rectangle a in new Rectangle[] { sc.WorkingArea, sc.Bounds })
                {
                    if (Math.Abs(r.Left - a.Left) < snap) { r.Left = a.Left; r.Right = a.Left + w; }
                    if (Math.Abs(r.Right - a.Right) < snap) { r.Right = a.Right; r.Left = a.Right - w; }
                    if (Math.Abs(r.Top - a.Top) < snap) { r.Top = a.Top; r.Bottom = a.Top + h; }
                    if (Math.Abs(r.Bottom - a.Bottom) < snap) { r.Bottom = a.Bottom; r.Top = a.Bottom - h; }
                }
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>Places the meter on the taskbar, just left of the notification area.</summary>
        public void SnapToTaskbar()
        {
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT tr;
            if (tb == IntPtr.Zero || !Native.GetWindowRect(tb, out tr)) return;
            Rectangle bar = Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom);
            int x, y;
            if (bar.Width >= bar.Height)
            {
                int rightEdge = bar.Right - 300;
                IntPtr notify = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
                Native.RECT nr;
                if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out nr) && nr.Right - nr.Left > 20 && nr.Left > bar.Left)
                    rightEdge = nr.Left;
                x = rightEdge - Width - 6;
                y = bar.Top + (bar.Height - Height) / 2;
            }
            else
            {
                x = bar.Left + (bar.Width - Width) / 2;
                y = bar.Bottom - 200 - Height;
            }
            app.S.AlwaysOnTop = true;
            TopMost = true;
            Location = new Point(x, y);
            app.S.X = x; app.S.Y = y;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Settings s = app.S;
            Rectangle rc = ClientRectangle;
            g.Clear(s.BackColor);

            if (s.ShowGraph) DrawSparkline(g, rc);

            string down = "▼", up = "▲";
            string dv = Fmt.Speed(rx, s.Unit), uv = Fmt.Speed(tx, s.Unit);
            Size arrowSz = TextRenderer.MeasureText(down, font, Size.Empty, TextFormatFlags.NoPadding);
            TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine;
            TextFormatFlags fr = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine;

            if (s.Horizontal)
            {
                int half = rc.Width / 2;
                Rectangle a = new Rectangle(Pad, 0, half - Pad, rc.Height);
                Rectangle b = new Rectangle(half, 0, half - Pad, rc.Height);
                DrawLine(g, a, down, dv, s.DownColor, arrowSz.Width, f, fr);
                DrawLine(g, b, up, uv, s.UpColor, arrowSz.Width, f, fr);
            }
            else
            {
                int h = (rc.Height - Pad) / 2;
                Rectangle a = new Rectangle(Pad, Pad / 2, rc.Width - Pad * 2, h);
                Rectangle b = new Rectangle(Pad, Pad / 2 + h, rc.Width - Pad * 2, h);
                DrawLine(g, a, down, dv, s.DownColor, arrowSz.Width, f, fr);
                DrawLine(g, b, up, uv, s.UpColor, arrowSz.Width, f, fr);
            }
        }

        void DrawLine(Graphics g, Rectangle r, string arrow, string value, Color c, int arrowW, TextFormatFlags f, TextFormatFlags fr)
        {
            TextRenderer.DrawText(g, arrow, font, r, c, f);
            Rectangle vr = new Rectangle(r.Left + arrowW + 4, r.Top, r.Width - arrowW - 4, r.Height);
            TextRenderer.DrawText(g, value, font, vr, app.S.TextColor, fr);
        }

        void DrawSparkline(Graphics g, Rectangle rc)
        {
            double[] hr = app.HistRx, ht = app.HistTx;
            int n = Math.Min(hr.Length, Math.Max(10, rc.Width / 3));
            double max = 1024;
            for (int i = 0; i < n; i++) { max = Math.Max(max, app.Hist(hr, i)); max = Math.Max(max, app.Hist(ht, i)); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillSeries(g, rc, hr, n, max, Color.FromArgb(55, app.S.DownColor));
            FillSeries(g, rc, ht, n, max, Color.FromArgb(55, app.S.UpColor));
            g.SmoothingMode = SmoothingMode.None;
        }

        void FillSeries(Graphics g, Rectangle rc, double[] h, int n, double max, Color c)
        {
            PointF[] pts = new PointF[n + 2];
            float step = rc.Width / (float)(n - 1);
            for (int i = 0; i < n; i++)
            {
                double v = app.Hist(h, n - 1 - i);   // oldest on the left
                pts[i] = new PointF(i * step, (float)(rc.Bottom - 1 - (v / max) * (rc.Height - 2)));
            }
            pts[n] = new PointF(rc.Right, rc.Bottom);
            pts[n + 1] = new PointF(0, rc.Bottom);
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (font != null) font.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
