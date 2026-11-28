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
        public readonly GameTelemetry Game = new GameTelemetry();
        public readonly OutageTracker Outages = new OutageTracker();
        public readonly LagDoctor LagDoc = new LagDoctor();
        public ContextMenuStrip Menu;
        public readonly bool IsAdmin;

        // speed history, ring buffer (bytes/sec)
        public readonly double[] HistRx = new double[600], HistTx = new double[600];
        public readonly bool[] HistSpike = new bool[600];
        int histPos;
        public double CurRx, CurTx, PeakRx, PeakTx, SessionRx, SessionTx;
        public readonly DateTime SessionStart = DateTime.Now;

        readonly Queue<long> pingWindow = new Queue<long>();
        public long LastPingMs = -2;
        public double PingLossPct, PingJitterMs;
        int prevWanCount = -1;
        DateTime lastWanAlertUtc = DateTime.MinValue;
        public string CurrentNetProfile = "Default";
        DateTime lastProfileCheckUtc = DateTime.MinValue;
        bool profileCheckBusy;
        string warnedProfileCapKey = "";
        int hogStreak;
        string hogLastApp = "";
        DateTime lastHogAlertUtc = DateTime.MinValue;
        readonly HashSet<string> alertedUnknownMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime lastLanWatchUtc = DateTime.MinValue;
        readonly LanScanner watchScanner = new LanScanner();
        DateTime lastNewAppToastUtc = DateTime.MinValue;

        class InvokerHost : Control
        {
            public Action<int> HotkeyPressed;
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_HOTKEY && HotkeyPressed != null) HotkeyPressed((int)m.WParam);
                base.WndProc(ref m);
            }
        }

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly InvokerHost invoker = new InvokerHost();
        FloatingMeter meter;
        TaskbarMeter taskbarMeter;
        GameHud gameHud;
        Dashboard dash;
        DateTime lastSave = DateTime.UtcNow;
        int iconState = -1;
        Icon currentIcon;
        bool hiddenForFullscreen;
        EventWaitHandle showSignal;
        public UpdateInfo LatestUpdate;
        bool updateChecking, updateInstalling, balloonIsUpdate;

        public TrayApp(string[] args)
        {
            S = Settings.Load();
            Theme.Apply(S.ThemeMode);
            Usage.Load();
            IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            invoker.CreateControl();
            invoker.HotkeyPressed = OnGlobalHotkey;
            IntPtr forceHandle = invoker.Handle;
            ApplyHotkeys();

            BuildMenu();
            if (S.ShowMeter && S.ShowOnTaskbar) { S.ShowMeter = false; S.Save(); }   // only one speed display at a time
            meter = new FloatingMeter(this);
            if (S.ShowMeter) meter.Show();
            taskbarMeter = new TaskbarMeter(this);
            if (S.ShowOnTaskbar) taskbarMeter.Show();
            gameHud = new GameHud(this);
            if (S.GameHudPinned) gameHud.Show();

            tray.Text = AppName;
            tray.ContextMenuStrip = Menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDashboard(0); };
            tray.BalloonTipClicked += delegate { if (balloonIsUpdate) { balloonIsUpdate = false; PromptAndInstallUpdate(); } };
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
                balloonIsUpdate = false;
                tray.ShowBalloonTip(12000, "NetSpeed Boss is running",
                    "Your live speed is now on the taskbar next to the clock. Drag it to move it, double-click for the dashboard, right-click for options.",
                    ToolTipIcon.Info);
            }
            if (S.AutoCheckUpdates && !Updater.IsStoreApp())
            {
                System.Windows.Forms.Timer ut = new System.Windows.Forms.Timer();
                ut.Interval = 4000;
                ut.Tick += delegate { ut.Stop(); ut.Dispose(); CheckForUpdatesAsync(false, null); };
                ut.Start();
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
                if (S.AlertNewNetworkApps && (a.Rx + a.Tx > 0 || a.TotIn > 0))
                {
                    bool seeded;
                    if (GuardStore.RecordAppAndCheckNew(a.Name, out seeded) && (DateTime.UtcNow - lastNewAppToastUtc).TotalSeconds >= 10)
                    {
                        lastNewAppToastUtc = DateTime.UtcNow;
                        balloonIsUpdate = false;
                        tray.ShowBalloonTip(7000, "New app connected",
                            a.Name + " connected to the network for the first time.", ToolTipIcon.Info);
                    }
                }
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

        public bool HistFlag(int ago)
        {
            int n = HistSpike.Length;
            return HistSpike[((histPos - 1 - ago) % n + n) % n];
        }

        public void RecordPingSample(long ms)
        {
            LastPingMs = ms;
            pingWindow.Enqueue(ms);
            while (pingWindow.Count > 20) pingWindow.Dequeue();
            int total = 0, lost = 0;
            List<long> ok = new List<long>();
            foreach (long v in pingWindow)
            {
                total++;
                if (v < 0) lost++;
                else ok.Add(v);
            }
            PingLossPct = total > 0 ? (100.0 * lost / total) : 0;
            double jitSum = 0;
            for (int i = 1; i < ok.Count; i++) jitSum += Math.Abs(ok[i] - ok[i - 1]);
            PingJitterMs = ok.Count >= 2 ? jitSum / (ok.Count - 1) : 0;

            bool spike = ms < 0;
            if (!spike && ok.Count >= 4)
            {
                ok.Sort();
                long med = ok[ok.Count / 2];
                if (ms > Math.Max(med * 2.2 + 30, 120)) spike = true;
            }
            int curSlot = ((histPos - 1) % HistSpike.Length + HistSpike.Length) % HistSpike.Length;
            if (spike) HistSpike[curSlot] = true;
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
            HistSpike[histPos] = false;
            RecordApps(histPos);
            histPos = (histPos + 1) % HistRx.Length;
            Usage.Add(Net.RxDelta, Net.TxDelta);
            Usage.AddProfileUsage(CurrentNetProfile, Net.RxDelta, Net.TxDelta);
            RefreshNetProfileAsync();
            Log.Add(Net.RxDelta, Net.TxDelta, CurRx, CurTx);
            Sys.Sample();   // CPU + memory history (cheap); GPU is sampled by the System tab

            CheckWanTransition();
            CheckHogAlert();
            Outages.TickProbe(Net.ActiveWanCount, S.TrackOutages);
            CheckLanWatchdog();

            // floating meter + fullscreen auto-hide
            bool fs = S.HideOnFullscreen && IsFullscreenAppRunning();
            if (S.ShowMeter)
            {
                if (fs && meter.Visible) { meter.Hide(); hiddenForFullscreen = true; }
                else if (!fs && hiddenForFullscreen) { meter.Show(); hiddenForFullscreen = false; }
                if (meter.Visible) meter.UpdateSpeed(CurRx, CurTx);
            }
            if (S.ShowOnTaskbar) taskbarMeter.Tick(CurRx, CurTx, fs);

            // game status HUD (auto-detected during games or pinned)
            if (S.GameHudAuto || S.GameHudPinned)
            {
                Game.Sample(this, S.GameHudPinned);
                bool wantHud = S.GameHudPinned || (S.GameHudAuto && Game.GameActive);
                if (wantHud)
                {
                    if (!gameHud.Visible) { gameHud.ApplySettings(); gameHud.Show(); }
                    gameHud.RefreshOverlay();
                }
                else if (gameHud.Visible) gameHud.Hide();
            }
            else
            {
                Game.EnsureFpsEtw(false);
                if (gameHud.Visible) gameHud.Hide();
            }

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

        void CheckWanTransition()
        {
            if (!S.AlertOnWanDrop) { prevWanCount = Net.ActiveWanCount; return; }
            if (prevWanCount < 0) { prevWanCount = Net.ActiveWanCount; return; }
            if (Net.ActiveWanCount != prevWanCount && (DateTime.UtcNow - lastWanAlertUtc).TotalSeconds >= 15)
            {
                if (prevWanCount >= 2 && Net.ActiveWanCount < prevWanCount)
                {
                    lastWanAlertUtc = DateTime.UtcNow;
                    balloonIsUpdate = false;
                    tray.ShowBalloonTip(7000, "Multi-WAN link change",
                        "Active internet links dropped from " + prevWanCount + " to " + Net.ActiveWanCount + ".", ToolTipIcon.Warning);
                }
                else if (prevWanCount <= 1 && Net.ActiveWanCount >= 2)
                {
                    lastWanAlertUtc = DateTime.UtcNow;
                    balloonIsUpdate = false;
                    tray.ShowBalloonTip(6000, "Multi-WAN restored",
                        Net.ActiveWanCount + " active WAN links are now load balancing.", ToolTipIcon.Info);
                }
            }
            prevWanCount = Net.ActiveWanCount;
        }

        void RefreshNetProfileAsync()
        {
            if (profileCheckBusy || (DateTime.UtcNow - lastProfileCheckUtc).TotalSeconds < 12) return;
            profileCheckBusy = true;
            lastProfileCheckUtc = DateTime.UtcNow;
            List<AdapterInfo> snap = Net.Adapters;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string prof = NetworkInfo.CurrentProfileKey(snap);
                CurrentNetProfile = string.IsNullOrEmpty(prof) ? "Default" : prof;
                profileCheckBusy = false;
            });
        }

        void CheckLanWatchdog()
        {
            if (!S.WatchNewLanDevices || watchScanner.Running || Net.ActiveWanCount <= 0) return;
            if (!GuardStore.HasAnyTrustedDevices()) return;
            if ((DateTime.UtcNow - lastLanWatchUtc).TotalSeconds < 75) return;
            lastLanWatchUtc = DateTime.UtcNow;
            string range = LanScanner.DefaultRange(Net.Adapters);
            watchScanner.Start(range, false, false,
                delegate(LanHostResult host, int d, int t)
                {
                    if (host == null || string.IsNullOrEmpty(host.Mac)) return;
                    if (GuardStore.GetTrustedLabel(host.Mac) != null) return;
                    if (!alertedUnknownMacs.Add(host.Mac)) return;
                    try
                    {
                        invoker.BeginInvoke((MethodInvoker)delegate
                        {
                            balloonIsUpdate = false;
                            tray.ShowBalloonTip(9000, "New LAN / Wi-Fi device detected",
                                host.Ip + " (" + host.Mac + ") is not in your trusted device list.", ToolTipIcon.Warning);
                        });
                    }
                    catch { }
                }, null);
        }

        void CheckHogAlert()
        {
            if (S.HogAlertMBps <= 0 || !Procs.EtwActive) { hogStreak = 0; return; }
            if (S.HogAlertDuringGame && !Game.GameActive) { hogStreak = 0; return; }
            double threshBps = S.HogAlertMBps * 1048576.0;
            AppSeries top = null;
            foreach (AppSeries a in AppNow)
            {
                if (Game.GameActive && !string.IsNullOrEmpty(Game.GameName) &&
                    a.Name.IndexOf(Game.GameName, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (a.Rx >= threshBps && (top == null || a.Rx > top.Rx)) top = a;
            }
            if (top != null)
            {
                hogStreak = string.Equals(hogLastApp, top.Name, StringComparison.OrdinalIgnoreCase) ? hogStreak + 1 : 1;
                hogLastApp = top.Name;
                if (hogStreak >= 4 && (DateTime.UtcNow - lastHogAlertUtc).TotalSeconds >= 90)
                {
                    lastHogAlertUtc = DateTime.UtcNow;
                    hogStreak = 0;
                    balloonIsUpdate = false;
                    tray.ShowBalloonTip(7000, "High background download",
                        top.Name + " is downloading at " + Fmt.Speed(top.Rx, S.Unit) + ".", ToolTipIcon.Info);
                }
            }
            else hogStreak = 0;
        }

        void CheckLimit()
        {
            DateTime start = UsageStore.PeriodStart(S.MonthStartDay);
            if (S.MonthlyLimitGB > 0)
            {
                string key = start.ToString("yyyy-MM-dd") + "@" + S.MonthlyLimitGB;
                if (S.LimitWarnedPeriod != key)
                {
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
            }
            double pCap = Usage.GetProfileCapGB(CurrentNetProfile);
            if (pCap > 0)
            {
                string pkey = CurrentNetProfile + "|" + start.ToString("yyyy-MM-dd") + "@" + pCap;
                if (warnedProfileCapKey != pkey)
                {
                    long[] pu = Usage.ProfileRange(CurrentNetProfile, start, DateTime.Today);
                    double pUsed = pu[0] + pu[1];
                    if (pUsed >= pCap * 1024 * 1024 * 1024)
                    {
                        warnedProfileCapKey = pkey;
                        tray.ShowBalloonTip(10000, "Network profile limit reached",
                            CurrentNetProfile + " used " + Fmt.Size(pUsed) + " (cap " + pCap + " GB).", ToolTipIcon.Warning);
                    }
                }
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
            Menu.Renderer = ModernMenuRenderer.Instance;
            Menu.ShowImageMargin = true;
            Menu.Opening += delegate { Theme.Apply(S.ThemeMode); RebuildMenu(); };
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
            if (LatestUpdate != null && LatestUpdate.UpdateAvailable)
            {
                ToolStripMenuItem upItem = Item("Update available: v" + LatestUpdate.Version + " (Download & install)...", false, delegate { PromptAndInstallUpdate(); });
                upItem.Font = new Font(upItem.Font, FontStyle.Bold);
                Menu.Items.Add(upItem);
                Menu.Items.Add(new ToolStripSeparator());
            }
            ToolStripMenuItem dashItem = Item("Open dashboard", false, delegate { ShowDashboard(0); });
            dashItem.Font = new Font(dashItem.Font, FontStyle.Bold);
            Menu.Items.Add(dashItem);
            Menu.Items.Add(Item("Apps using the network", false, delegate { ShowDashboard(1); }));
            Menu.Items.Add(Item("Network scanner & speed test", false, delegate { ShowDashboard(2); }));
            Menu.Items.Add(Item("Data usage", false, delegate { ShowDashboard(3); }));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Show speed on taskbar", S.ShowOnTaskbar, delegate { ToggleTaskbar(); }));
            Menu.Items.Add(Item("Auto-position taskbar meter (near Weather bar)", S.TaskbarAutoPlace, delegate { S.TaskbarAutoPlace = !S.TaskbarAutoPlace; Changed(); }));
            Menu.Items.Add(Item("Show speed in a floating window instead", S.ShowMeter, delegate { ToggleMeter(); }));
            Menu.Items.Add(Item("Show Game Status HUD", S.GameHudPinned, delegate { ToggleGameHud(); }));
            Menu.Items.Add(Item("Auto-show Game HUD in games", S.GameHudAuto, delegate { S.GameHudAuto = !S.GameHudAuto; Changed(); }));
            Menu.Items.Add(Item("Bold taskbar text", S.TaskbarBold, delegate { S.TaskbarBold = !S.TaskbarBold; Changed(); }));

            ToolStripMenuItem profMenu = new ToolStripMenuItem("Quick profile (" + S.ProfilePreset + ")");
            profMenu.DropDownItems.Add(Item("Standard (Balanced)", S.ProfilePreset == "Standard", delegate { ApplyProfilePreset("Standard"); }));
            profMenu.DropDownItems.Add(Item("Gaming (HUD + hog guard)", S.ProfilePreset == "Gaming", delegate { ApplyProfilePreset("Gaming"); }));
            profMenu.DropDownItems.Add(Item("Travel / Hotspot (Strict alerts)", S.ProfilePreset == "Travel", delegate { ApplyProfilePreset("Travel"); }));
            Menu.Items.Add(profMenu);

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
            ToolStripMenuItem fsz = new ToolStripMenuItem(S.ShowOnTaskbar ? "Taskbar text size" : "Text size");
            float curSize = S.ShowOnTaskbar ? S.TaskbarFontSize : S.FontSize;
            foreach (float f in new float[] { 8f, 9f, 10f, 11f, 12f, 14f, 16f })
            {
                float ff = f;
                fsz.DropDownItems.Add(Item(f + " pt", Math.Abs(curSize - f) < 0.01, delegate
                {
                    if (S.ShowOnTaskbar) S.TaskbarFontSize = ff; else S.FontSize = ff;
                    Changed();
                }));
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
            Menu.Items.Add(Item("Reset positions", false, delegate { S.X = int.MinValue; S.TaskbarAutoPlace = true; S.TaskbarOffset = 6; Changed(); }));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Start with Windows", StartupEnabled, delegate { StartupEnabled = !StartupEnabled; }));
            if (!IsAdmin) Menu.Items.Add(Item("Restart as administrator (per-app speeds)", false, delegate { RestartElevated(); }));
            if (!Updater.IsStoreApp()) Menu.Items.Add(Item("Check for updates...", false, delegate { CheckForUpdatesAsync(true, null); }));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(Item("Exit", false, delegate { ExitThread(); }));
        }

        public void CheckForUpdatesAsync(bool manual, Action<UpdateInfo> onDone)
        {
            if (Updater.IsStoreApp() || updateChecking) return;
            updateChecking = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = Updater.CheckLatest();
                try
                {
                    invoker.BeginInvoke((MethodInvoker)delegate
                    {
                        updateChecking = false;
                        if (info.Error.Length == 0)
                        {
                            LatestUpdate = info;
                            RebuildMenu();
                            if (dash != null && !dash.IsDisposed) dash.RefreshUpdateBar();
                        }
                        if (onDone != null) onDone(info);
                        if (info.UpdateAvailable)
                        {
                            if (manual) PromptAndInstallUpdate();
                            else if (dash == null || dash.IsDisposed || !dash.Visible)
                            {
                                balloonIsUpdate = true;
                                tray.ShowBalloonTip(12000, AppName + " v" + info.Version + " is available",
                                    "Click here to download, install, and restart automatically.", ToolTipIcon.Info);
                            }
                        }
                        else if (manual && onDone == null)
                        {
                            MessageBox.Show(info.Error.Length > 0
                                ? "Could not check for updates: " + info.Error
                                : AppName + " v" + info.CurrentVersion + " is up to date.",
                                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    });
                }
                catch { updateChecking = false; }
            });
        }

        public void PromptAndInstallUpdate()
        {
            if (LatestUpdate == null || !LatestUpdate.UpdateAvailable) return;
            ShowDashboard(0);
            DialogResult r = MessageBox.Show(
                AppName + " v" + LatestUpdate.Version + " is available (you have v" + LatestUpdate.CurrentVersion + ").\r\n\r\n" +
                "Download, install, and restart " + AppName + " automatically now?",
                AppName + " Update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes && dash != null && !dash.IsDisposed)
                dash.BeginUpdateInstall();
        }

        public void StartUpdateInstall(UpdateInfo info, Action<int> onProgress, Action<string> onError)
        {
            if (updateInstalling || info == null || !info.UpdateAvailable) return;
            updateInstalling = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Updater.DownloadAndInstall(info,
                        delegate(int pct) { try { invoker.BeginInvoke((MethodInvoker)delegate { if (onProgress != null) onProgress(pct); }); } catch { } },
                        delegate { try { invoker.BeginInvoke((MethodInvoker)delegate { ExitThread(); }); } catch { } });
                }
                catch (Exception ex)
                {
                    try
                    {
                        invoker.BeginInvoke((MethodInvoker)delegate
                        {
                            updateInstalling = false;
                            if (onError != null) onError(ex.Message);
                        });
                    }
                    catch { updateInstalling = false; }
                }
            });
        }

        public void Changed()
        {
            S.Save();
            Theme.Apply(S.ThemeMode);
            ApplyHotkeys();
            meter.ApplySettings();
            taskbarMeter.ApplySettings();
            if (gameHud != null) gameHud.ApplySettings();
            iconState = -1;
            UpdateTrayIcon(CurRx > 2048, CurTx > 2048);
            if (dash != null && !dash.IsDisposed) dash.SettingsChanged();
        }

        public void ApplyHotkeys()
        {
            try
            {
                if (!invoker.IsHandleCreated) return;
                for (int id = 1; id <= 3; id++) Native.UnregisterHotKey(invoker.Handle, id);
                if (!S.GlobalHotkeysEnabled) return;
                uint mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
                Native.RegisterHotKey(invoker.Handle, 1, mods, 0x48); // Ctrl+Shift+H -> Gaming HUD
                Native.RegisterHotKey(invoker.Handle, 2, mods, 0x4C); // Ctrl+Shift+L -> HUD Click-through
                Native.RegisterHotKey(invoker.Handle, 3, mods, 0x4D); // Ctrl+Shift+M -> Floating Meter
            }
            catch { }
        }

        void OnGlobalHotkey(int id)
        {
            switch (id)
            {
                case 1:
                    ToggleGameHud();
                    break;
                case 2:
                    S.GameHudClickThrough = !S.GameHudClickThrough;
                    S.Save();
                    GameHudClickThroughChanged();
                    if (dash != null && !dash.IsDisposed) dash.SettingsChanged();
                    break;
                case 3:
                    ToggleMeter();
                    break;
            }
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
        public void GameHudClickThroughChanged() { if (gameHud != null) gameHud.RecreateForClickThrough(); }

        public void ApplyProfilePreset(string name)
        {
            S.ProfilePreset = name;
            if (name == "Gaming")
            {
                S.GameHudAuto = true;
                S.HogAlertMBps = 5;
                S.HogAlertDuringGame = true;
                S.AlertOnWanDrop = true;
            }
            else if (name == "Travel")
            {
                S.GameHudAuto = false;
                S.HogAlertMBps = 2;
                S.HogAlertDuringGame = false;
                S.AlertOnWanDrop = true;
            }
            else
            {
                S.ProfilePreset = "Standard";
                S.GameHudAuto = true;
                S.HogAlertMBps = 0;
                S.HogAlertDuringGame = true;
                S.AlertOnWanDrop = true;
            }
            Changed();
        }

        public string BuildDiagnosticsReport()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("NetSpeed Boss Diagnostics Report");
            sb.AppendLine("================================");
            sb.AppendLine("Version:         v" + Updater.CurrentVersionString() + (Updater.IsStoreApp() ? " (Microsoft Store)" : " (Desktop)"));
            sb.AppendLine("Elevation:       " + (IsAdmin ? "Administrator" : "Standard user"));
            sb.AppendLine("OS:              " + Environment.OSVersion.VersionString + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
            sb.AppendLine("Quick Profile:   " + S.ProfilePreset + " | Network: " + CurrentNetProfile);
            sb.AppendLine("Per-App ETW:     " + (Procs.EtwActive ? "Active (" + Procs.EventsSeen + " events)" : "Inactive (" + Procs.Status + ")"));
            sb.AppendLine("DXGI/D3D9 FPS:   " + (Game.FpsEtwActive ? "Active" : "Standby/Admin required"));
            sb.AppendLine("Ping / Jitter:   " + (LastPingMs >= 0 ? LastPingMs + " ms" : "n/a") +
                          string.Format(" | Jitter ±{0:0.0} ms | Loss {1:0.0}%", PingJitterMs, PingLossPct));
            sb.AppendLine("Active WANs:     " + Net.ActiveWanCount + (Net.IsMultiWan ? " (Multi-WAN load balanced)" : ""));
            sb.AppendLine(string.Format("ISP Uptime:      {0:0.00}% today ({1} drop(s))", Outages.TodayUptimePct, Outages.TodayDropCount));
            if (LagDoc.LastResult != null)
                sb.AppendLine("Bufferbloat:     Grade " + LagDoc.LastResult.Grade + " (" + LagDoc.LastResult.Verdict + ")");
            foreach (AdapterInfo a in Net.Adapters)
                sb.AppendLine(string.Format("  - {0} [{1}] link={2} gw={3} down={4} up={5} selected={6}",
                    a.Name, a.Description, Fmt.LinkSpeed(a.LinkSpeed), a.Gateway, Fmt.Speed(a.RxBps, S.Unit), Fmt.Speed(a.TxBps, S.Unit), a.Selected));
            Sys.SampleGpu();
            sb.AppendLine("GPUs (" + Sys.Gpus.Count + "):");
            foreach (GpuInfo g in Sys.Gpus)
                sb.AppendLine(string.Format("  - {0} (vendor=0x{1:X4} util={2:0}% vram={3}/{4} MB)",
                    g.Name, g.VendorId, g.Util, g.DedicatedUsed / 1048576L, g.Vram / 1048576L));
            List<SpeedTestRecord> tests = SpeedTester.LoadHistory(1);
            if (tests.Count > 0)
            {
                SpeedTestRecord r = tests[0];
                sb.AppendLine(string.Format("Last Speedtest:  {0:yyyy-MM-dd HH:mm}  ↓ {1}  ↑ {2}  Ping {3:0} ms ({4} WANs)",
                    r.Timestamp, Fmt.Speed(r.DownloadBps, S.Unit), Fmt.Speed(r.UploadBps, S.Unit), r.PingMs, r.WanCount));
            }
            return sb.ToString();
        }

        public void ToggleGameHud()
        {
            S.GameHudPinned = !S.GameHudPinned;
            if (S.GameHudPinned)
            {
                Game.Sample(this, true);
                gameHud.ApplySettings();
                gameHud.Show();
                gameHud.RefreshOverlay();
            }
            else if (!S.GameHudAuto || !Game.GameActive) gameHud.Hide();
            S.Save();
            if (dash != null && !dash.IsDisposed) dash.SettingsChanged();
        }

        public void ShowDashboard(int tab)
        {
            if (dash == null || dash.IsDisposed) dash = new Dashboard(this);
            dash.SelectTab(tab);
            if (!dash.Visible) dash.Show();
            if (dash.WindowState == FormWindowState.Minimized) dash.WindowState = FormWindowState.Normal;
            dash.OnOpened();
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
            for (int id = 1; id <= 3; id++) try { Native.UnregisterHotKey(invoker.Handle, id); } catch { }
            LagDoc.Cancel();
            watchScanner.Cancel();
            Outages.FlushOpenOutage();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            Procs.Dispose();
            Game.Dispose();
            if (gameHud != null && !gameHud.IsDisposed) gameHud.Dispose();
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
