using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    /// <summary>
    /// Per-pixel-alpha Metal-style in-game overlay. Only the card background uses the user's
    /// transparency setting; all text, RAG indicators, core bars, and waveforms remain 100% opaque
    /// and crisp over games.
    /// </summary>
    public class GameHud : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; public PT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; public SZ(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref PT pos, ref SZ size, IntPtr src, ref PT srcPos, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        readonly TrayApp app;
        readonly Font fHead = new Font("Segoe UI Semibold", 9.5f);
        readonly Font fMono = new Font("Consolas", 9.5f, FontStyle.Bold);
        readonly Font fSmall = new Font("Consolas", 8f);
        public const int HudW = 376, Pad = 12;

        static readonly Color RagGreen = Color.FromArgb(74, 222, 128);
        static readonly Color RagAmber = Color.FromArgb(251, 191, 36);
        static readonly Color RagRed = Color.FromArgb(248, 113, 113);

        public GameHud(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "NetSpeed Boss Gaming HUD";
            ClientSize = new Size(HudW, ComputeHeight(app.S));

            MouseDown += OnMouseDownDrag;
            MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) app.ShowDashboard(4); };
            LocationChanged += delegate
            {
                if (!Visible) return;
                app.S.GameHudX = Left;
                app.S.GameHudY = Top;
            };
            Shown += delegate { RenderLayered(); };
            ApplySettings();
        }

        public static int ComputeHeight(Settings s)
        {
            int rows = 0;
            if (s.GameHudShowFps) rows++;
            if (s.GameHudShowGpu) rows++;
            if (s.GameHudShowCpu) rows++;
            if (s.GameHudShowMem) rows += 2; // VRAM + System RAM
            if (s.GameHudShowNet) rows++;
            int h = 40 + rows * 21 + (s.GameHudShowGraph ? 68 : 8);
            return Math.Max(82, h);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /*WS_EX_LAYERED*/ | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                if (app != null && app.S.GameHudClickThrough) cp.ExStyle |= 0x20;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void RecreateForClickThrough()
        {
            if (IsHandleCreated) { RecreateHandle(); if (Visible) RenderLayered(); }
        }

        public void ApplySettings()
        {
            Settings s = app.S;
            TopMost = true;
            ContextMenuStrip = app.Menu;
            int h = ComputeHeight(s);
            if (ClientSize.Height != h) ClientSize = new Size(HudW, h);

            if (s.GameHudX == int.MinValue || !OnAnyScreen(new Rectangle(s.GameHudX, s.GameHudY, HudW, h)))
                PlaceInCorner(s.GameHudCorner);
            else
                Location = new Point(s.GameHudX, s.GameHudY);
            if (Visible) RenderLayered();
        }

        public void PlaceInCorner(int corner)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int h = ComputeHeight(app.S);
            const int m = 16;
            int x = wa.Left + m, y = wa.Top + m;
            switch (corner)
            {
                case 1: x = wa.Right - HudW - m; y = wa.Top + m; break;
                case 2: x = wa.Left + m; y = wa.Bottom - h - m; break;
                case 3: x = wa.Right - HudW - m; y = wa.Bottom - h - m; break;
            }
            app.S.GameHudX = x; app.S.GameHudY = y;
            Location = new Point(x, y);
        }

        static bool OnAnyScreen(Rectangle r)
        {
            foreach (Screen sc in Screen.AllScreens)
                if (sc.Bounds.IntersectsWith(r)) return true;
            return false;
        }

        public void RefreshOverlay()
        {
            int h = ComputeHeight(app.S);
            if (ClientSize.Height != h) ClientSize = new Size(HudW, h);
            if (Visible)
            {
                RenderLayered();
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        void RenderLayered()
        {
            if (!IsHandleCreated) return;
            int w = HudW, h = ComputeHeight(app.S);
            int bgAlpha = Math.Max(1, Math.Min(255, (int)Math.Round(app.S.GameHudOpacity * 2.55)));
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    PaintHud(g, new Rectangle(0, 0, w, h), app, app.Game, fHead, fMono, fSmall, false, bgAlpha);
                }
                IntPtr screen = GetDC(IntPtr.Zero);
                IntPtr mem = CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr old = SelectObject(mem, hbmp);
                try
                {
                    PT pos = new PT(Left, Top), src = new PT(0, 0);
                    SZ size = new SZ(w, h);
                    BLEND bl = new BLEND(); bl.Op = 0; bl.Alpha = 255; bl.Format = 1; // AC_SRC_OVER, AC_SRC_ALPHA
                    UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref bl, 2 /*ULW_ALPHA*/);
                }
                finally
                {
                    SelectObject(mem, old);
                    DeleteObject(hbmp);
                    DeleteDC(mem);
                    ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        void OnMouseDownDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || app.S.GameHudClickThrough) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOVING)
            {
                Native.RECT r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                Screen sc = Screen.FromPoint(new Point(r.Left + w / 2, r.Top + h / 2));
                const int snap = 14;
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

        static Color RagHigherBetter(double val, double greenMin, double amberMin, Color fallback, bool rag)
        {
            if (!rag) return fallback;
            if (val >= greenMin) return RagGreen;
            if (val >= amberMin) return RagAmber;
            return RagRed;
        }

        static Color RagLowerBetter(double val, double greenMax, double amberMax, Color fallback, bool rag)
        {
            if (!rag) return fallback;
            if (val < greenMax) return RagGreen;
            if (val < amberMax) return RagAmber;
            return RagRed;
        }

        static Color RagLoad(double val, double amberAt, double redAt, Color normalColor, bool rag)
        {
            if (!rag) return normalColor;
            if (val >= redAt) return RagRed;
            if (val >= amberAt) return RagAmber;
            return RagGreen;
        }

        static void DrawTxt(Graphics g, string text, Font f, Rectangle r, Color c, StringAlignment align)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, c)))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, f, b, r, sf);
            }
        }

        public static void PaintHud(Graphics g, Rectangle rc, TrayApp app, GameTelemetry t,
            Font fHead, Font fMono, Font fSmall, bool useSampleIfIdle, int bgAlpha)
        {
            Settings s = app.S;
            bool sample = useSampleIfIdle && !t.GameActive;

            string gameTitle = sample ? "Cyberpunk2077 (Preview)"
                : (t.GameActive && t.GameName.Length > 0 ? t.GameName : (t.GpuName.Length > 0 ? t.GpuName : "Gaming HUD"));
            int scrW = t.ScreenW, scrH = t.ScreenH, hz = t.RefreshHz;
            double fps = sample ? 78.4 : t.Fps, fpsMin = sample ? 54 : t.FpsMin, fpsMax = sample ? 96 : t.FpsMax, fms = sample ? 12.8 : t.FrameMs;
            double gpuU = sample ? 94 : t.GpuUtil, gpuT = sample ? 74 : t.GpuTempC, gpuF = sample ? 66 : t.GpuFanPct, gpuP = sample ? 182 : t.GpuPowerW;
            int gpuClk = sample ? 2685 : t.GpuClockMhz;
            long vUsed = sample ? 7420 : t.VramUsedMB, vTot = sample ? 12282 : t.VramTotalMB;
            double cpuU = sample ? 48 : t.CpuUtil, cpuGhz = sample ? 4.6 : t.CpuGhz, cpuT = sample ? 66 : t.CpuTempC;
            double[] cores = sample ? new double[] { 64, 42, 84, 38, 72, 55, 29, 93, 46, 39, 68, 25, 58, 44, 32, 61 } : t.CoreUtils;
            double memU = sample ? 42 : t.MemUtil;
            long memUsed = sample ? 18500000000L : t.MemUsedBytes, memTotal = sample ? 68719476736L : t.MemTotalBytes;
            long gameMem = sample ? 4950000000L : t.GameMemBytes;
            double netRx = sample ? 840000 : t.GameRx, netTx = sample ? 145000 : t.GameTx;
            long pingMs = sample ? 32 : t.PingMs;
            string pingLbl = sample ? "Srv" : (t.PingLabel == "Game Srv" ? "Srv" : "Net");

            // ONLY the background uses bgAlpha; text & border stay bright and readable
            Color bg = Color.FromArgb(Math.Max(1, Math.Min(255, bgAlpha)), s.GameHudBackColor);
            Color fg = Color.FromArgb(255, s.GameHudTextColor);
            Color muted = Color.FromArgb(220, Math.Min(255, fg.R / 2 + 110), Math.Min(255, fg.G / 2 + 110), Math.Min(255, fg.B / 2 + 115));
            Color borderCol = Color.FromArgb(Math.Max(140, Math.Min(255, bgAlpha + 85)), s.GameHudBorderColor);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (GraphicsPath p = BrandArt.Round(new RectangleF(rc.X, rc.Y, rc.Width - 1, rc.Height - 1), 8))
            using (SolidBrush bb = new SolidBrush(bg))
            using (Pen bp = new Pen(borderCol))
            {
                g.FillPath(bb, p);
                g.DrawPath(bp, p);
            }

            int x = rc.X + Pad, w = rc.Width - Pad * 2, y = rc.Y + 8;
            string disp = "[" + scrW + "x" + scrH + " " + hz + "Hz]";
            int dispW = (int)Math.Ceiling(g.MeasureString(disp, fSmall).Width) + 8;
            DrawTxt(g, gameTitle, fHead, new Rectangle(x, y, w - dispW, 20), fg, StringAlignment.Near);
            DrawTxt(g, disp, fSmall, new Rectangle(x + w - dispW, y, dispW, 20), muted, StringAlignment.Far);
            y += 22;
            using (Pen div = new Pen(Color.FromArgb(140, s.GameHudBorderColor))) g.DrawLine(div, x, y, x + w, y);
            y += 6;

            int lblW = 48, rowH = 21;

            if (s.GameHudShowFps)
            {
                DrawTxt(g, "FPS:", fMono, new Rectangle(x, y, lblW, rowH), s.GameHudFpsColor, StringAlignment.Near);
                bool hasFps = fps > 0.5;
                Color fpsCol = hasFps ? RagHigherBetter(fps, s.GameHudFpsGreen, s.GameHudFpsAmber, fg, s.GameHudRag) : muted;
                if (hasFps && s.GameHudRag) DrawRagDot(g, x + lblW, y + 6, fpsCol);
                int valOff = (hasFps && s.GameHudRag) ? lblW + 12 : lblW;
                string fpsTxt = hasFps ? string.Format("{0:0.0}  [{1:0}  {2:0}]", fps, fpsMin, fpsMax)
                    : (app.IsAdmin ? "-- (idle)" : "-- (Admin)");
                string fmsTxt = fms > 0.1 ? fms.ToString("0.0") + " ms" : "";
                DrawTxt(g, fpsTxt, fMono, new Rectangle(x + valOff, y, w - valOff - 68, rowH), fpsCol, StringAlignment.Near);
                if (fmsTxt.Length > 0) DrawTxt(g, fmsTxt, fMono, new Rectangle(x + w - 68, y, 68, rowH), fpsCol, StringAlignment.Far);
                y += rowH;
            }

            if (s.GameHudShowGpu)
            {
                DrawTxt(g, "GPU:", fMono, new Rectangle(x, y, lblW, rowH), s.GameHudGpuColor, StringAlignment.Near);
                Color tempCol = gpuT >= 0 ? RagLowerBetter(gpuT, s.GameHudTempGreen, s.GameHudTempAmber, s.GameHudGpuColor, s.GameHudRag)
                    : (gpuU >= 0 ? RagLoad(gpuU, s.GameHudLoadAmber, s.GameHudLoadRed, s.GameHudGpuColor, s.GameHudRag) : muted);
                if (s.GameHudRag && (gpuT >= 0 || gpuU >= 0)) DrawRagDot(g, x + lblW, y + 6, tempCol);
                int valOff = s.GameHudRag && (gpuT >= 0 || gpuU >= 0) ? lblW + 12 : lblW;
                string gpuLeft = gpuU >= 0 ? gpuU.ToString("0") + "%" : "--";
                if (gpuT >= 0) gpuLeft += " " + gpuT.ToString("0") + "°C";
                if (gpuF >= 0) gpuLeft += " Fan " + gpuF.ToString("0") + "%";
                string gpuRight = "";
                if (gpuP >= 0) gpuRight += gpuP.ToString("0") + "W";
                if (gpuClk > 0) gpuRight += (gpuRight.Length > 0 ? " " : "") + gpuClk + "MHz";
                int gpuRw = gpuRight.Length > 0 ? (int)Math.Ceiling(g.MeasureString(gpuRight, fSmall).Width) + 8 : 0;
                DrawTxt(g, gpuLeft, fMono, new Rectangle(x + valOff, y, w - valOff - gpuRw, rowH), tempCol, StringAlignment.Near);
                if (gpuRight.Length > 0) DrawTxt(g, gpuRight, fSmall, new Rectangle(x + w - gpuRw, y, gpuRw, rowH), muted, StringAlignment.Far);
                y += rowH;
            }

            if (s.GameHudShowMem)
            {
                DrawTxt(g, "VRAM:", fMono, new Rectangle(x, y, lblW, rowH), s.GameHudGpuColor, StringAlignment.Near);
                double vramPct = (vUsed >= 0 && vTot > 0) ? 100.0 * vUsed / vTot : -1;
                Color vramCol = vramPct >= 0 ? RagLoad(vramPct, s.GameHudLoadAmber, s.GameHudLoadRed, fg, s.GameHudRag) : muted;
                if (vramPct >= 0 && s.GameHudRag) DrawRagDot(g, x + lblW, y + 6, vramCol);
                int valOff = (vramPct >= 0 && s.GameHudRag) ? lblW + 12 : lblW;
                string vramTxt = (vUsed >= 0 && vTot > 0)
                    ? string.Format("{0:0.0} / {1:0.0} GB ({2:0}%)", vUsed / 1024.0, vTot / 1024.0, vramPct)
                    : (vUsed >= 0 ? (vUsed / 1024.0).ToString("0.0") + " GB" : "--");
                DrawTxt(g, vramTxt, fMono, new Rectangle(x + valOff, y, w - valOff, rowH), vramCol, StringAlignment.Near);
                y += rowH;
            }

            if (s.GameHudShowCpu)
            {
                DrawTxt(g, "CPU:", fMono, new Rectangle(x, y, lblW, rowH), s.GameHudCpuColor, StringAlignment.Near);
                Color cpuCol = RagLoad(cpuU, s.GameHudLoadAmber, s.GameHudLoadRed, fg, s.GameHudRag);
                if (s.GameHudRag) DrawRagDot(g, x + lblW, y + 6, cpuCol);
                int valOff = s.GameHudRag ? lblW + 12 : lblW;
                string cpuTxt = cpuU.ToString("0") + "%" + (cpuGhz > 0 ? " " + cpuGhz.ToString("0.0") + "G" : "") +
                    (cpuT > 0 ? " " + cpuT.ToString("0") + "°C" : "");
                int coresW = cores != null && cores.Length > 0 ? 116 : 0;
                DrawTxt(g, cpuTxt, fMono, new Rectangle(x + valOff, y, w - valOff - coresW - 4, rowH), cpuCol, StringAlignment.Near);
                if (coresW > 0) DrawCoreBars(g, new Rectangle(x + w - coresW, y + 4, coresW, 13), cores, s);
                y += rowH;
            }

            if (s.GameHudShowMem)
            {
                DrawTxt(g, "MEM:", fMono, new Rectangle(x, y, lblW, rowH), s.GameHudMemColor, StringAlignment.Near);
                Color memCol = RagLoad(memU, s.GameHudLoadAmber, s.GameHudLoadRed, fg, s.GameHudRag);
                if (s.GameHudRag) DrawRagDot(g, x + lblW, y + 6, memCol);
                int valOff = s.GameHudRag ? lblW + 12 : lblW;
                string memTxt = Fmt.Size(memUsed) + (memTotal > 0 ? " / " + Fmt.Size(memTotal) : "") + " (" + memU.ToString("0") + "%)";
                string memRight = gameMem > 0 ? "[" + Fmt.Size(gameMem) + "]" : "";
                int memRw = memRight.Length > 0 ? (int)Math.Ceiling(g.MeasureString(memRight, fSmall).Width) + 8 : 0;
                DrawTxt(g, memTxt, fMono, new Rectangle(x + valOff, y, w - valOff - memRw, rowH), memCol, StringAlignment.Near);
                if (memRight.Length > 0) DrawTxt(g, memRight, fSmall, new Rectangle(x + w - memRw, y, memRw, rowH), muted, StringAlignment.Far);
                y += rowH;
            }

            if (s.GameHudShowNet)
            {
                DrawTxt(g, "NET:", fMono, new Rectangle(x, y, lblW, rowH), s.GameHudNetColor, StringAlignment.Near);
                string netTxt = "↓ " + Fmt.Speed(netRx, s.Unit) + "  ↑ " + Fmt.Speed(netTx, s.Unit);
                string pingTxt = pingMs >= 0 ? pingMs + " ms (" + pingLbl + ")" : "Ping --";
                Color pingCol = pingMs < 0 ? muted : RagLowerBetter(pingMs, s.GameHudPingGreen, s.GameHudPingAmber, s.GameHudNetColor, s.GameHudRag);
                int pingW = (int)Math.Ceiling(g.MeasureString(pingTxt, fMono).Width) + 8;
                DrawTxt(g, netTxt, fSmall, new Rectangle(x + lblW, y, w - lblW - pingW, rowH), fg, StringAlignment.Near);
                DrawTxt(g, pingTxt, fMono, new Rectangle(x + w - pingW, y, pingW, rowH), pingCol, StringAlignment.Far);
                y += rowH;
            }

            if (s.GameHudShowGraph)
            {
                y += 4;
                Rectangle plot = new Rectangle(x, y, w, rc.Bottom - y - 10);
                if (plot.Height >= 22)
                {
                    int plotAlpha = Math.Max(1, Math.Min(220, bgAlpha));
                    using (SolidBrush pbg = new SolidBrush(Color.FromArgb(plotAlpha, 8, 10, 14))) g.FillRectangle(pbg, plot);
                    using (Pen grid = new Pen(Color.FromArgb(140, s.GameHudBorderColor)))
                    {
                        g.DrawRectangle(grid, plot);
                        for (int i = 1; i < 6; i++)
                        {
                            int gx = plot.Left + i * plot.Width / 6;
                            g.DrawLine(grid, gx, plot.Top, gx, plot.Bottom);
                        }
                        g.DrawLine(grid, plot.Left, plot.Top + plot.Height / 2, plot.Right, plot.Top + plot.Height / 2);
                    }
                    double[] fh = t.FpsHist, mh = t.FrameMsHist;
                    int pos = t.HistPos;
                    if (sample)
                    {
                        fh = new double[60]; mh = new double[60]; pos = 0;
                        for (int i = 0; i < 60; i++) { fh[i] = 74 + Math.Sin(i / 4.0) * 16; mh[i] = 1000.0 / fh[i]; }
                    }
                    double maxFps = Math.Max(60.0, hz);
                    foreach (double v in fh) if (v > maxFps) maxFps = v;
                    DrawTrace(g, plot, fh, pos, maxFps, s.GameHudFpsColor);
                    DrawTrace(g, plot, mh, pos, 33.3, s.GameHudGpuColor);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        static void DrawRagDot(Graphics g, int x, int y, Color c)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, c))) g.FillEllipse(b, x, y, 7, 7);
        }

        static void DrawCoreBars(Graphics g, Rectangle r, double[] cores, Settings s)
        {
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(180, 26, 32, 42))) g.FillRectangle(bg, r);
            int n = cores.Length;
            float slot = r.Width / (float)n;
            float bw = Math.Max(2f, slot - 1.5f);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(0, Math.Min(100, cores[i]));
                int bh = Math.Max(1, (int)Math.Round(r.Height * (v / 100.0)));
                Color c = s.GameHudRag
                    ? (v >= s.GameHudLoadRed ? RagRed : (v >= s.GameHudLoadAmber ? RagAmber : s.GameHudCpuColor))
                    : s.GameHudCpuColor;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, c)))
                    g.FillRectangle(b, r.Left + i * slot + 0.5f, r.Bottom - bh, bw, bh);
            }
        }

        static void DrawTrace(Graphics g, Rectangle plot, double[] arr, int pos, double maxVal, Color c)
        {
            int n = arr.Length;
            PointF[] pts = new PointF[n];
            float step = plot.Width / (float)(n - 1);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                double v = arr[((pos + i) % n + n) % n];
                if (v > 0) any = true;
                double ratio = Math.Max(0, Math.Min(1.0, v / maxVal));
                pts[i] = new PointF(plot.Left + i * step, (float)(plot.Bottom - 2 - ratio * (plot.Height - 4)));
            }
            if (!any) return;
            using (Pen p = new Pen(Color.FromArgb(255, c), 1.6f)) g.DrawLines(p, pts);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fHead.Dispose(); fMono.Dispose(); fSmall.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Interactive live preview panel embedded inside the Gaming HUD dashboard tab.</summary>
    public class GameHudPreview : Control
    {
        public TrayApp App;
        readonly Font fHead = new Font("Segoe UI Semibold", 9.5f);
        readonly Font fMono = new Font("Consolas", 9.5f, FontStyle.Bold);
        readonly Font fSmall = new Font("Consolas", 8f);

        public GameHudPreview()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.IsDark ? Color.FromArgb(24, 28, 36) : Color.FromArgb(40, 48, 62));
            for (int y = 0; y < Height; y += 16)
                for (int x = 0; x < Width; x += 16)
                    if (((x / 16 + y / 16) & 1) == 0)
                        using (SolidBrush cb = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
                            g.FillRectangle(cb, x, y, 16, 16);

            if (App == null) return;
            int hudH = Math.Min(Height - 12, GameHud.ComputeHeight(App.S));
            int hudW = Math.Min(Width - 12, GameHud.HudW);
            Rectangle rc = new Rectangle((Width - hudW) / 2, Math.Max(6, (Height - hudH) / 2), hudW, hudH);
            int alpha = Math.Max(1, Math.Min(255, (int)Math.Round(App.S.GameHudOpacity * 2.55)));
            GameHud.PaintHud(g, rc, App, App.Game, fHead, fMono, fSmall, true, alpha);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fHead.Dispose(); fMono.Dispose(); fSmall.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
