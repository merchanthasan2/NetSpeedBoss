using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetSpeedBoss
{
    public class TrayApp : ApplicationContext
    {
        public const string AppName = "NetSpeed Boss";
        public readonly Settings S;
        public readonly UsageStore Usage = new UsageStore();
        public readonly NetMonitor Net = new NetMonitor();
        public readonly ProcessMonitor Procs = new ProcessMonitor();
        public ContextMenuStrip Menu;
        public readonly bool IsAdmin;

        // speed history, ring buffer (bytes/sec)
        public readonly double[] HistRx = new double[600], HistTx = new double[600];
        int histPos;
        public double CurRx, CurTx, PeakRx, PeakTx, SessionRx, SessionTx;
        public readonly DateTime SessionStart = DateTime.Now;

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Control invoker = new Control();
        FloatingMeter meter;
        TaskbarMeter taskbarMeter;
        Dashboard dash;
        DateTime lastSave = DateTime.UtcNow;
        int iconState = -1;
        Icon currentIcon;
        bool hiddenForFullscreen;
        EventWaitHandle showSignal;

        public TrayApp(string[] args)
        {
            S = Settings.Load();
            Usage.Load();
            IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            invoker.CreateControl();
            IntPtr forceHandle = invoker.Handle;

            BuildMenu();
            meter = new FloatingMeter(this);
            if (S.ShowMeter) meter.Show();
            taskbarMeter = new TaskbarMeter(this);
            if (S.ShowOnTaskbar) taskbarMeter.Show();

            tray.Text = AppName;
            tray.ContextMenuStrip = Menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleMeter(); };
            tray.MouseDoubleClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDashboard(0); };
            UpdateTrayIcon(false, false);
            tray.Visible = true;

            Procs.StartEtw();   // works when elevated (or in the Performance Log Users group); otherwise sets a status hint

            Net.Sample(S.Adapter);       // prime counters
            timer.Interval = S.IntervalMs;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            StartShowSignalListener();
            if (!S.WelcomeShown)
            {
                S.WelcomeShown = true; S.Save();
                tray.ShowBalloonTip(12000, "NetSpeed Boss is running",
                    "Your live speed is now on the taskbar next to the clock. Drag it to move it, double-click for the dashboard, right-click for options.",
                    ToolTipIcon.Info);
            }
            Application.ApplicationExit += delegate { Shutdown(); };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (Array.IndexOf(args, "--dashboard") >= 0 || Array.IndexOf(args, "--restarted") >= 0) ShowDashboard(Array.IndexOf(args, "--restarted") >= 0 ? 1 : 0);
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) invoker.BeginInvoke((MethodInvoker)delegate { Net.Sample(S.Adapter); });
        }

        void StartShowSignalListener()
        {
            try
            {
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "NetSpeedBoss_ShowDashboard");
                Thread t = new Thread(delegate()
                {
                    while (true)
                    {
                        try { showSignal.WaitOne(); } catch { return; }
                        try { invoker.BeginInvoke((MethodInvoker)delegate { ShowDashboard(0); }); } catch { return; }
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        public double Hist(double[] arr, int ago)
        {
            int n = arr.Length;
            return arr[((histPos - 1 - ago) % n + n) % n];
        }

        void Tick()
        {
            Net.Sample(S.Adapter);
            CurRx = Net.RxBps; CurTx = Net.TxBps;
            SessionRx += Net.RxDelta; SessionTx += Net.TxDelta;
            if (CurRx > PeakRx) PeakRx = CurRx;
            if (CurTx > PeakTx) PeakTx = CurTx;
            HistRx[histPos] = CurRx; HistTx[histPos] = CurTx;
            histPos = (histPos + 1) % HistRx.Length;
            Usage.Add(Net.RxDelta, Net.TxDelta);

            // ETW totals accumulate all the time; the TCP-table-only mode is only polled while the dashboard is open
            bool dashOpen = dash != null && !dash.IsDisposed && dash.Visible;
            if (Procs.EtwActive || dashOpen) Procs.Sample();

            // floating meter + fullscreen auto-hide
            bool fs = S.HideOnFullscreen && IsFullscreenAppRunning();
            if (S.ShowMeter)
            {
                if (fs && meter.Visible) { meter.Hide(); hiddenForFullscreen = true; }
                else if (!fs && hiddenForFullscreen) { meter.Show(); hiddenForFullscreen = false; }
                if (meter.Visible) meter.UpdateSpeed(CurRx, CurTx);
            }
            if (S.ShowOnTaskbar) taskbarMeter.Tick(CurRx, CurTx, fs);

            // tray
            string tt = "↓ " + Fmt.Speed(CurRx, S.Unit) + "\n↑ " + Fmt.Speed(CurTx, S.Unit);
            long[] today = Usage.Day(DateTime.Today);
            tt += "\nToday " + Fmt.Size(today[0] + today[1]);
            if (tt.Length > 63) tt = tt.Substring(0, 63);
            tray.Text = tt;
            UpdateTrayIcon(CurRx > 2048, CurTx > 2048);

            if (dashOpen) dash.OnTick();

            CheckLimit();
            if ((DateTime.UtcNow - lastSave).TotalSeconds > 60) { Usage.Save(); S.Save(); lastSave = DateTime.UtcNow; }
        }

        static bool IsFullscreenAppRunning()
        {
            int st;
            try { if (Native.SHQueryUserNotificationState(out st) != 0) return false; }
            catch { return false; }
            return st == 2 || st == 3 || st == 4; // BUSY, D3D full screen, presentation mode
        }

        void CheckLimit()
        {
            if (S.MonthlyLimitGB <= 0) return;
            DateTime start = UsageStore.PeriodStart(S.MonthStartDay);
            string key = start.ToString("yyyy-MM-dd") + "@" + S.MonthlyLimitGB;
            if (S.LimitWarnedPeriod == key) return;
            long[] u = Usage.Range(start, DateTime.Today);
            double used = u[0] + u[1];
            if (used >= S.MonthlyLimitGB * 1024 * 1024 * 1024)
            {
                S.LimitWarnedPeriod = key;
                S.Save();
                tray.ShowBalloonTip(10000, "Data limit reached",
                    "You have used " + Fmt.Size(used) + " of your " + S.MonthlyLimitGB + " GB monthly limit.", ToolTipIcon.Warning);
            }
        }

        // ------------------------------------------------------------------ tray icon
        void UpdateTrayIcon(bool down, bool up)
        {
            int state = (down ? 1 : 0) | (up ? 2 : 0);
            if (state == iconState) return;
            iconState = state;
            Size sz = SystemInformation.SmallIconSize;
            using (Bitmap bmp = new Bitmap(sz.Width, sz.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float w = sz.Width, h = sz.Height;
                    Color idle = Color.FromArgb(150, 160, 170);
                    using (SolidBrush bd = new SolidBrush(down ? S.DownColor : idle))
                    using (SolidBrush bu = new SolidBrush(up ? S.UpColor : idle))
                    {
                        // down arrow (left)
                        g.FillRectangle(bd, w * 0.14f, h * 0.06f, w * 0.18f, h * 0.5f);
                        g.FillPolygon(bd, new PointF[] { new PointF(0, h * 0.5f), new PointF(w * 0.46f, h * 0.5f), new PointF(w * 0.23f, h * 0.96f) });
                        // up arrow (right)
                        g.FillRectangle(bu, w * 0.68f, h * 0.44f, w * 0.18f, h * 0.5f);
                        g.FillPolygon(bu, new PointF[] { new PointF(w * 0.54f, h * 0.5f), new PointF(w, h * 0.5f), new PointF(w * 0.77f, h * 0.04f) });
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                Icon ic = Icon.FromHandle(hIcon);
                tray.Icon = ic;
                if (currentIcon != null) { Native.DestroyIcon(currentIcon.Handle); currentIcon.Dispose(); }
                currentIcon = ic;
            }
        }

        // ------------------------------------------------------------------ menu
        void BuildMenu()
        {
            Menu = new ContextMenuStrip();
            Menu.Opening += delegate { RebuildMenu(); };
            RebuildMenu();
        }

        ToolStripMenuItem Item(string text, bool check, EventHandler click)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            i.Checked = check;
            i.Click += click;
            return i;
        }

        void RebuildMenu()
        {
            Menu.Items.Clear();
            ToolStripMenuItem dashItem = Item("Open dashboard", false, delegate { ShowDashboard(0); });
            dashItem.Font = new Font(dashItem.Font, FontStyle.Bold);
            Menu.Items.Add(dashItem);
            Menu.Items.Add(Item("Apps using the network", false, delegate { ShowDashboard(1); }));
            Menu.Items.Add(Item("Data usage", false, delegate { ShowDashboard(2); }));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Show speed on taskbar", S.ShowOnTaskbar, delegate { ToggleTaskbar(); }));
            Menu.Items.Add(Item("Bold taskbar text", S.TaskbarBold, delegate { S.TaskbarBold = !S.TaskbarBold; Changed(); }));
            Menu.Items.Add(Item("Show floating meter", S.ShowMeter, delegate { ToggleMeter(); }));

            ToolStripMenuItem units = new ToolStripMenuItem("Units");
            units.DropDownItems.Add(Item("Bytes  (KB/s, MB/s)", S.Unit == SpeedUnit.Bytes, delegate { S.Unit = SpeedUnit.Bytes; Changed(); }));
            units.DropDownItems.Add(Item("Bits  (Kbps, Mbps)", S.Unit == SpeedUnit.Bits, delegate { S.Unit = SpeedUnit.Bits; Changed(); }));
            Menu.Items.Add(units);

            ToolStripMenuItem ad = new ToolStripMenuItem("Network adapter");
            ad.DropDownItems.Add(Item("Automatic (internet adapters)", S.Adapter == "auto", delegate { S.Adapter = "auto"; Changed(); }));
            ad.DropDownItems.Add(Item("All adapters", S.Adapter == "all", delegate { S.Adapter = "all"; Changed(); }));
            ad.DropDownItems.Add(new ToolStripSeparator());
            foreach (AdapterInfo a in Net.Adapters)
            {
                string id = a.Id;
                ad.DropDownItems.Add(Item(a.Name + "  (" + a.Description + ")", S.Adapter == id, delegate { S.Adapter = id; Changed(); }));
            }
            Menu.Items.Add(ad);

            ToolStripMenuItem look = new ToolStripMenuItem("Meter appearance");
            look.DropDownItems.Add(Item("Two lines", !S.Horizontal, delegate { S.Horizontal = false; Changed(); }));
            look.DropDownItems.Add(Item("One line", S.Horizontal, delegate { S.Horizontal = true; Changed(); }));
            look.DropDownItems.Add(new ToolStripSeparator());
            look.DropDownItems.Add(Item("Background graph", S.ShowGraph, delegate { S.ShowGraph = !S.ShowGraph; Changed(); }));
            ToolStripMenuItem op = new ToolStripMenuItem("Opacity");
            foreach (int p in new int[] { 100, 90, 80, 70, 60, 50, 40 })
            {
                int pp = p;
                op.DropDownItems.Add(Item(p + "%", S.OpacityPct == p, delegate { S.OpacityPct = pp; Changed(); }));
            }
            look.DropDownItems.Add(op);
            ToolStripMenuItem fsz = new ToolStripMenuItem("Text size");
            foreach (float f in new float[] { 8f, 9f, 10f, 11f, 12f, 14f, 16f })
            {
                float ff = f;
                fsz.DropDownItems.Add(Item(f + " pt", Math.Abs(S.FontSize - f) < 0.01, delegate { S.FontSize = ff; Changed(); }));
            }
            look.DropDownItems.Add(fsz);
            Menu.Items.Add(look);

            ToolStripMenuItem iv = new ToolStripMenuItem("Update interval");
            foreach (int ms in new int[] { 500, 1000, 2000, 3000, 5000 })
            {
                int m = ms;
                iv.DropDownItems.Add(Item((ms / 1000.0) + " s", S.IntervalMs == ms, delegate { S.IntervalMs = m; timer.Interval = m; S.Save(); }));
            }
            Menu.Items.Add(iv);

            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Always on top", S.AlwaysOnTop, delegate { S.AlwaysOnTop = !S.AlwaysOnTop; Changed(); }));
            Menu.Items.Add(Item("Lock position", S.LockPosition, delegate { S.LockPosition = !S.LockPosition; S.Save(); }));
            Menu.Items.Add(Item("Click-through (ignore mouse)", S.ClickThrough, delegate { S.ClickThrough = !S.ClickThrough; S.Save(); meter.RecreateForClickThrough(); }));
            Menu.Items.Add(Item("Hide when a full-screen app runs", S.HideOnFullscreen, delegate { S.HideOnFullscreen = !S.HideOnFullscreen; S.Save(); }));
            Menu.Items.Add(Item("Reset positions", false, delegate { S.X = int.MinValue; S.TaskbarOffset = 6; Changed(); }));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Start with Windows", StartupEnabled, delegate { StartupEnabled = !StartupEnabled; }));
            if (!IsAdmin) Menu.Items.Add(Item("Restart as administrator (per-app speeds)", false, delegate { RestartElevated(); }));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Exit", false, delegate { ExitThread(); }));
        }

        public void Changed()
        {
            S.Save();
            meter.ApplySettings();
            taskbarMeter.ApplySettings();
            iconState = -1;
            UpdateTrayIcon(CurRx > 2048, CurTx > 2048);
            if (dash != null && !dash.IsDisposed) dash.SettingsChanged();
        }

        public void ToggleMeter()
        {
            S.ShowMeter = !S.ShowMeter;
            hiddenForFullscreen = false;
            if (S.ShowMeter) { meter.ApplySettings(); meter.Show(); } else meter.Hide();
            S.Save();
        }

        public void SetTimerInterval(int ms) { S.IntervalMs = ms; timer.Interval = ms; }

        public void ToggleTaskbar()
        {
            S.ShowOnTaskbar = !S.ShowOnTaskbar;
            if (S.ShowOnTaskbar) { taskbarMeter.ApplySettings(); taskbarMeter.Show(); taskbarMeter.Tick(CurRx, CurTx, false); }
            else taskbarMeter.Hide();
            S.Save();
            if (dash != null && !dash.IsDisposed) dash.SettingsChanged();
        }

        public void MeterClickThroughChanged() { meter.RecreateForClickThrough(); }

        public void ShowDashboard(int tab)
        {
            if (dash == null || dash.IsDisposed) dash = new Dashboard(this);
            dash.SelectTab(tab);
            if (!dash.Visible) dash.Show();
            if (dash.WindowState == FormWindowState.Minimized) dash.WindowState = FormWindowState.Normal;
            dash.Activate();
            dash.BringToFront();
        }

        // ------------------------------------------------------------------ startup / elevation
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public bool StartupEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue("NetSpeedBoss") != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue("NetSpeedBoss", "\"" + Application.ExecutablePath + "\" --startup");
                        else k.DeleteValue("NetSpeedBoss", false);
                    }
                }
                catch (Exception ex) { MessageBox.Show("Could not change startup setting: " + ex.Message, AppName); }
            }
        }

        public void RestartElevated()
        {
            S.Save(); Usage.Save();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--restarted");
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { return; } // user cancelled UAC
            ExitThread();
        }

        bool shutDown;
        void Shutdown()
        {
            if (shutDown) return;
            shutDown = true;
            timer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Procs.Dispose();
            Usage.Save();
            S.Save();
            tray.Visible = false;
            tray.Dispose();
        }

        protected override void ExitThreadCore()
        {
            Shutdown();
            if (dash != null && !dash.IsDisposed) dash.Close();
            meter.Close();
            taskbarMeter.Close();
            base.ExitThreadCore();
        }
    }
}
