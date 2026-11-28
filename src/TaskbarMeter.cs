using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeedBoss
{
    /// <summary>
    /// Speed read-out docked on the Windows taskbar, just left of the notification area.
    /// Rendered as a per-pixel-alpha layered window so the taskbar shows through behind the text.
    /// </summary>
    public class TaskbarMeter : Form
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        const int Pad = 6;
        readonly TrayApp app;
        readonly ToolTip tip = new ToolTip();
        Font font;
        double rx, tx;
        bool hover, lightTheme = true, centeredWin11, widgetsEnabled = true, autoHidden, dragging;
        DateTime themeChecked = DateTime.MinValue;
        DateTime layoutCheckedUtc = DateTime.MinValue;
        bool layoutBusy, hasUiaLayout;
        int uiaWeatherRight, uiaIconsLeft, uiaIconsRight;
        int dragStartX, dragStartY, dragStartOffset, anchorEdge;
        bool vertical;
        IntPtr hook;
        readonly WinEventProc hookProc;
        readonly EventHandler displayChanged;

        public TaskbarMeter(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "NetSpeed Boss Taskbar";
            ContextMenuStrip = app.Menu;
            ApplySettings();

            // whenever another window (e.g. the taskbar itself) comes to the front, put us back on top
            hookProc = delegate { if (Visible) KeepOnTop(); };
            hook = SetWinEventHook(3 /*EVENT_SYSTEM_FOREGROUND*/, 3, IntPtr.Zero, hookProc, 0, 0, 0 /*OUTOFCONTEXT*/);
            displayChanged = delegate { themeChecked = DateTime.MinValue; layoutCheckedUtc = DateTime.MinValue; if (Visible) Render(); };
            SystemEvents.DisplaySettingsChanged += displayChanged;

            MouseEnter += delegate
            {
                hover = true; Render();
                long[] t = app.Usage.Day(DateTime.Today);
                tip.SetToolTip(this, "Today:  ↓ " + Fmt.Size(t[0]) + "   ↑ " + Fmt.Size(t[1]) +
                                     "\nDouble-click for dashboard · drag to move · right-click for options");
            };
            MouseLeave += delegate { hover = false; Render(); };
            MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) app.ShowDashboard(0); };
            MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || app.S.LockPosition) return;
                dragging = true; Capture = true;
                dragStartX = Cursor.Position.X; dragStartY = Cursor.Position.Y;
                if (app.S.TaskbarAutoPlace && IsHandleCreated)
                {
                    app.S.TaskbarAutoPlace = false;
                    app.S.TaskbarOffset = vertical ? Math.Max(0, anchorEdge - Bottom) : Math.Max(0, anchorEdge - Right);
                }
                dragStartOffset = app.S.TaskbarOffset;
            };
            MouseMove += delegate
            {
                if (!dragging) return;
                int d = vertical ? Cursor.Position.Y - dragStartY : Cursor.Position.X - dragStartX;
                app.S.TaskbarOffset = dragStartOffset - d;
                Render();
            };
            MouseUp += delegate { if (dragging) { dragging = false; Capture = false; app.Changed(); } };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /*LAYERED*/ | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void ApplySettings()
        {
            if (font != null) font.Dispose();
            string family = string.IsNullOrEmpty(app.S.TaskbarFontName) ? "Segoe UI" : app.S.TaskbarFontName;
            float size = Math.Max(6f, Math.Min(16f, app.S.TaskbarFontSize));
            FontStyle style = app.S.TaskbarBold ? FontStyle.Bold : FontStyle.Regular;
            try { font = new Font(family, size, style, GraphicsUnit.Point); }
            catch { font = new Font("Segoe UI", size, style, GraphicsUnit.Point); }
            themeChecked = DateTime.MinValue;
            if (Visible) Render();
        }

        /// <summary>Called every tick while taskbar mode is on.</summary>
        public void Tick(double rxBps, double txBps, bool suppress)
        {
            rx = rxBps; tx = txBps;
            if (suppress) { if (Visible) Hide(); return; }
            if (!Visible && !autoHidden) Show();
            Render();
            if (Visible) KeepOnTop();
        }

        void KeepOnTop()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        Size ContentSize()
        {
            string sample = app.S.Unit == SpeedUnit.Bits ? "▼ 888.8 Mbps" : "▼ 888.8 MB/s";
            Size t = TextRenderer.MeasureText(sample, font, Size.Empty, TextFormatFlags.NoPadding);
            return new Size(t.Width + Pad * 2 + 4, t.Height * 2 + 4);
        }

        bool ComputeBounds(out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT tr;
            if (tb == IntPtr.Zero || !Native.GetWindowRect(tb, out tr)) return false;
            Rectangle bar = Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom);
            Rectangle vis = Rectangle.Intersect(bar, Screen.FromRectangle(bar).Bounds);
            vertical = bar.Height > bar.Width;
            if (vertical ? vis.Width < bar.Width / 2 : vis.Height < bar.Height / 2) return false;   // taskbar auto-hidden

            IntPtr notify = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT nr = new Native.RECT();
            bool hasNotify = notify != IntPtr.Zero && Native.GetWindowRect(notify, out nr) && nr.Right > nr.Left && nr.Bottom > nr.Top;
            if (!hasNotify) nr = new Native.RECT();
            Size c = ContentSize();

            if (!vertical)
            {
                int w = c.Width, h = bar.Height;
                anchorEdge = hasNotify && nr.Left > bar.Left ? nr.Left : bar.Right - 320;
                ProbeLayoutAsync(tb, bar, anchorEdge);

                int left;
                if (app.S.TaskbarAutoPlace)
                {
                    int weatherEdge = (hasUiaLayout && uiaWeatherRight > bar.Left)
                        ? uiaWeatherRight + 8
                        : bar.Left + ((centeredWin11 && widgetsEnabled) ? 168 : 10);
                    int iconsStart = (hasUiaLayout && uiaIconsLeft > bar.Left && uiaIconsLeft < anchorEdge)
                        ? uiaIconsLeft
                        : bar.Left + (centeredWin11 ? Math.Max(220, bar.Width / 2 - 220) : 480);
                    int iconsEnd = (hasUiaLayout && uiaIconsRight > bar.Left && uiaIconsRight <= anchorEdge)
                        ? uiaIconsRight
                        : bar.Left + (centeredWin11 ? Math.Min(anchorEdge - 20, bar.Width / 2 + 220) : 500);

                    if (centeredWin11 && iconsStart - weatherEdge >= w + 8)
                        left = weatherEdge;                                  // right beside the weather/widgets bar
                    else if (anchorEdge - iconsEnd >= w + 12)
                        left = anchorEdge - w - 8;                           // fallback: open space left of system tray
                    else if (iconsStart - (bar.Left + 8) >= w + 6)
                        left = bar.Left + 8;                                 // fallback: left edge before icons
                    else
                        left = Math.Max(bar.Left + 4, anchorEdge - w - 6);
                    left = Math.Max(bar.Left, Math.Min(bar.Right - w, left));
                    bounds = new Rectangle(left, bar.Top, w, h);
                }
                else
                {
                    int right = anchorEdge - app.S.TaskbarOffset;
                    left = Math.Max(bar.Left, Math.Min(bar.Right - w, right - w));
                    bounds = new Rectangle(left, bar.Top, w, h);
                    app.S.TaskbarOffset = anchorEdge - bounds.Right;
                }
            }
            else
            {
                int w = bar.Width, h = c.Height + 8;
                anchorEdge = hasNotify && nr.Top > bar.Top ? nr.Top : bar.Bottom - 320;
                int bottom = anchorEdge - app.S.TaskbarOffset;
                int top = Math.Max(bar.Top, Math.Min(bar.Bottom - h, bottom - h));
                bounds = new Rectangle(bar.Left, top, w, h);
                app.S.TaskbarOffset = anchorEdge - bounds.Bottom;
            }
            return true;
        }

        void ProbeLayoutAsync(IntPtr tb, Rectangle bar, int trayLeft)
        {
            if (layoutBusy || !app.S.TaskbarAutoPlace || (DateTime.UtcNow - layoutCheckedUtc).TotalSeconds < 3) return;
            layoutBusy = true;
            layoutCheckedUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(tb);
                    if (root != null)
                    {
                        AutomationElementCollection btns = root.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        int wRight = bar.Left;
                        int iLeft = trayLeft;
                        int iRight = bar.Left;
                        foreach (AutomationElement el in btns)
                        {
                            try
                            {
                                System.Windows.Rect r = el.Current.BoundingRectangle;
                                if (r.IsEmpty || r.Width < 12 || r.Height < 14) continue;
                                if (r.Right <= bar.Left || r.Left >= trayLeft - 4) continue;
                                string aid = el.Current.AutomationId ?? "";
                                string name = el.Current.Name ?? "";
                                bool isWidget = aid.IndexOf("Widget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                name.IndexOf("Widget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                name.IndexOf("Weather", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                ((name.Contains("°") || name.Contains("°C") || name.Contains("°F")) && r.Left < bar.Left + bar.Width / 4.0);
                                if (isWidget)
                                {
                                    if ((int)Math.Ceiling(r.Right) > wRight) wRight = (int)Math.Ceiling(r.Right);
                                }
                                else
                                {
                                    int rl = (int)Math.Floor(r.Left);
                                    int rr = (int)Math.Ceiling(r.Right);
                                    if (rl < iLeft) iLeft = rl;
                                    if (rr > iRight) iRight = rr;
                                }
                            }
                            catch { }
                        }
                        uiaWeatherRight = wRight;
                        uiaIconsLeft = iLeft;
                        uiaIconsRight = iRight;
                        hasUiaLayout = iLeft < trayLeft || wRight > bar.Left;
                    }
                }
                catch { }
                finally { layoutBusy = false; }
            });
        }

        void CheckTheme()
        {
            if ((DateTime.UtcNow - themeChecked).TotalSeconds < 10) return;
            themeChecked = DateTime.UtcNow;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    lightTheme = v is int && (int)v == 1;
                }
                using (RegistryKey adv = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    object al = adv == null ? null : adv.GetValue("TaskbarAl");
                    centeredWin11 = !(al is int) || (int)al == 1;
                    object da = adv == null ? null : adv.GetValue("TaskbarDa");
                    widgetsEnabled = !(da is int) || (int)da != 0;
                }
            }
            catch { }
        }

        void Render()
        {
            if (!IsHandleCreated) return;
            Rectangle b;
            if (!ComputeBounds(out b))
            {
                if (Visible) { Hide(); autoHidden = true; }
                return;
            }
            if (autoHidden) { autoHidden = false; Show(); }
            if (!Visible) return;
            CheckTheme();

            Color fg = lightTheme ? Color.FromArgb(26, 26, 26) : Color.FromArgb(245, 245, 245);
            Color down = lightTheme ? Color.FromArgb(0, 140, 60) : app.S.DownColor;
            Color up = lightTheme ? Color.FromArgb(205, 100, 0) : app.S.UpColor;
            if (!app.S.TaskbarAutoColors) { fg = app.S.TaskbarTextColor; down = app.S.DownColor; up = app.S.UpColor; }

            using (Bitmap bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    // alpha 1 keeps the whole area clickable while looking fully transparent
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (hover || dragging)
                        using (GraphicsPath p = BrandArt.Round(new RectangleF(1, 3, b.Width - 3, b.Height - 7), 6))
                        using (SolidBrush hb = new SolidBrush(lightTheme ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255)))
                        using (Pen hp = new Pen(lightTheme ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(48, 255, 255, 255)))
                        {
                            g.FillPath(hb, p);
                            g.DrawPath(hp, p);
                        }

                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float lh = Math.Min(font.GetHeight(g), (b.Height - 2) / 2f);
                    float y0 = Math.Max(0f, (b.Height - lh * 2) / 2f);
                    using (StringFormat right = new StringFormat(StringFormatFlags.NoWrap))
                    using (SolidBrush fgB = new SolidBrush(fg), dB = new SolidBrush(down), uB = new SolidBrush(up))
                    {
                        right.Alignment = StringAlignment.Far;
                        RectangleF r1 = new RectangleF(Pad, y0, b.Width - Pad * 2, lh);
                        RectangleF r2 = new RectangleF(Pad, y0 + lh, b.Width - Pad * 2, lh);
                        g.DrawString("▼", font, dB, r1.Location);
                        g.DrawString("▲", font, uB, r2.Location);
                        g.DrawString(Fmt.Speed(rx, app.S.Unit), font, fgB, r1, right);
                        g.DrawString(Fmt.Speed(tx, app.S.Unit), font, fgB, r2, right);
                    }
                }

                Native.PresentLayered(Handle, b.X, b.Y, bmp);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            if (displayChanged != null) SystemEvents.DisplaySettingsChanged -= displayChanged;
            if (disposing) { if (font != null) font.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
