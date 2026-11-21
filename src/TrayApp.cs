using System;
using System.Collections.Generic;
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
        public readonly UsageLog Log = new UsageLog();
        public readonly SystemMonitor Sys = new SystemMonitor();
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
            if (S.ShowMeter && S.ShowOnTaskbar) { S.ShowMeter = false; S.Save(); }   // only one speed display at a time
            meter = new FloatingMeter(this);
            if (S.ShowMeter) meter.Show();
            taskbarMeter = new TaskbarMeter(this);
            if (S.ShowOnTaskbar) taskbarMeter.Show();

            tray.Text = AppName;
            tray.ContextMenuStrip = Menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDashboard(0); };
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
            SystemEvents.SessionEnding += OnSessionEnding;   // Windows shutting down / signing out: write logs now

            if (Array.IndexOf(args, "--dashboard") >= 0 || Array.IndexOf(args, "--restarted") >= 0) ShowDashboard(Array.IndexOf(args, "--restarted") >= 0 ? 1 : 0);
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) invoker.BeginInvoke((MethodInvoker)delegate { Net.Sample(S.Adapter); });
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try { Log.Flush(); Usage.Save(); S.Save(); } catch { }
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

        // ------------------------------------------------------------------ per-app download history (grouped by app name)
        public class AppSeries { public string Name, Path; public double Rx, Tx; public long TotIn; }
        public class GraphApp { public string Name; public Color Color; }

        public readonly Dictionary<string, double[]> AppHistRx = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        public List<AppSeries> AppNow = new List<AppSeries>();
        public readonly List<GraphApp> GraphApps = new List<GraphApp>();
        public static readonly Color[] AppPalette = {
            Color.FromArgb(59, 130, 246), Color.FromArgb(168, 85, 247), Color.FromArgb(236, 72, 153),
            Color.FromArgb(20, 184, 166), Color.FromArgb(202, 138, 4) };
        int pruneTick;

        void RecordApps(int pos)
        {
            if (!Procs.EtwActive) { if (AppNow.Count > 0) AppNow = new List<AppSeries>(); return; }
            Dictionary<string, AppSeries> by = new Dictionary<string, AppSeries>(StringComparer.OrdinalIgnoreCase);
            List<ProcView> snap = Procs.Snapshot();
            Log.AddApps(snap);
            foreach (ProcView v in snap)
            {
                if (v.Pid == 0 || string.IsNullOrEmpty(v.Name)) continue;
                AppSeries a;
                if (!by.TryGetValue(v.Name, out a)) { a = new AppSeries(); a.Name = v.Name; by[v.Name] = a; }
                a.Rx += v.Rx; a.Tx += v.Tx; a.TotIn += v.TotIn;
                if (a.Path == null) a.Path = v.Path;
            }
            foreach (AppSeries a in by.Values)
            {
                double[] h;
                if (!AppHistRx.TryGetValue(a.Name, out h)) { h = new double[HistRx.Length]; AppHistRx[a.Name] = h; }
                h[pos] = a.Rx;
            }
            foreach (KeyValuePair<string, double[]> kv in AppHistRx) if (!by.ContainsKey(kv.Key)) kv.Value[pos] = 0;

            if (++pruneTick >= 120)    // drop histories of apps that have been silent for the whole window
            {
                pruneTick = 0;
                List<string> dead = new List<string>();
                foreach (KeyValuePair<string, double[]> kv in AppHistRx)
                {
                    if (IsGraphed(kv.Key)) continue;
                    bool any = false;
                    foreach (double d in kv.Value) if (d > 0) { any = true; break; }
                    if (!any) dead.Add(kv.Key);
                }
                foreach (string k in dead) AppHistRx.Remove(k);
            }
            AppNow = new List<AppSeries>(by.Values);
        }

        public double AppAvgRx(string name, int samples)
        {
            double[] h;
            if (!AppHistRx.TryGetValue(name, out h)) return 0;
            double s = 0;
            for (int i = 0; i < samples; i++) s += Hist(h, i);
            return s / samples;
        }

        public GraphApp FindGraphed(string name)
        {
            foreach (GraphApp g in GraphApps) if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g;
            return null;
        }
        public bool IsGraphed(string name) { return FindGraphed(name) != null; }

        /// <summary>Adds or removes an app's line on the speed graph. Returns false if all colors are taken.</summary>
        public bool ToggleGraphApp(string name)
        {
            GraphApp g = FindGraphed(name);
            if (g != null) { GraphApps.Remove(g); return true; }
            foreach (Color c in AppPalette)
            {
                bool used = false;
                foreach (GraphApp x in GraphApps) if (x.Color == c) { used = true; break; }
                if (used) continue;
                g = new GraphApp(); g.Name = name; g.Color = c;
                GraphApps.Add(g);
                return true;
            }
            return false;
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
            // ETW totals accumulate all the time; the TCP-table-only mode is only polled while the dashboard is open
            bool dashOpen = dash != null && !dash.IsDisposed && dash.Visible;
            if (Procs.EtwActive || dashOpen) Procs.Sample();

            HistRx[histPos] = CurRx; HistTx[histPos] = CurTx;
            RecordApps(histPos);
            histPos = (histPos + 1) % HistRx.Length;
            Usage.Add(Net.RxDelta, Net.TxDelta);
            Log.Add(Net.RxDelta, Net.TxDelta, CurRx, CurTx);
            Sys.Sample();   // CPU + memory history (cheap); GPU is sampled by the System tab

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
            Menu.Items.Add(Item("Show speed in a floating window instead", S.ShowMeter, delegate { ToggleMeter(); }));
            Menu.Items.Add(Item("Bold taskbar text", S.TaskbarBold, delegate { S.TaskbarBold = !S.TaskbarBold; Changed(); }));

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
            if (S.ShowMeter)
            {
                if (S.ShowOnTaskbar) { S.ShowOnTaskbar = false; taskbarMeter.Hide(); }
                meter.ApplySettings(); meter.Show();
            }
            else meter.Hide();
            S.Save();
            if (dash != null && !dash.IsDisposed) dash.SettingsChanged();
        }

        public void SetTimerInterval(int ms) { S.IntervalMs = ms; timer.Interval = ms; }

        public void ToggleTaskbar()
        {
            S.ShowOnTaskbar = !S.ShowOnTaskbar;
            if (S.ShowOnTaskbar)
            {
                if (S.ShowMeter) { S.ShowMeter = false; meter.Hide(); hiddenForFullscreen = false; }
                taskbarMeter.ApplySettings(); taskbarMeter.Show(); taskbarMeter.Tick(CurRx, CurTx, false);
            }
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

        // Elevated autostart: a scheduled task with "highest privileges" starts us as administrator at sign-in
        // without a UAC prompt (the Run key cannot do that). Creating/removing it requires being elevated.
        const string TaskName = "NetSpeedBoss";
        bool? elevatedStartupCache;

        static int RunSchtasks(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.CreateNoWindow = true; psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        public bool ElevatedStartupEnabled
        {
            get
            {
                if (!elevatedStartupCache.HasValue) elevatedStartupCache = RunSchtasks("/Query /TN " + TaskName) == 0;
                return elevatedStartupCache.Value;
            }
        }

        public bool SetElevatedStartup(bool on)
        {
            elevatedStartupCache = null;
            if (!on) return RunSchtasks("/Delete /TN " + TaskName + " /F") == 0;
            if (!IsAdmin) return false;
            string user = System.Security.SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = System.Security.SecurityElement.Escape(Application.ExecutablePath);
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Starts NetSpeed Boss with administrator rights so per-app speeds work.</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
"<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--startup</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetSpeedBoss-task.xml");
            try
            {
                System.IO.File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
                bool ok = RunSchtasks("/Create /TN " + TaskName + " /XML \"" + path + "\" /F") == 0;
                if (ok && StartupEnabled) StartupEnabled = false;   // avoid a second, non-elevated launch
                return ok;
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
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
            SystemEvents.SessionEnding -= OnSessionEnding;
            Procs.Dispose();
            Log.Flush();
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
