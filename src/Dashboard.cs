using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    class ModernTabControl : TabControl
    {
        public ModernTabControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x1328 /*TCM_ADJUSTRECT*/ && !DesignMode)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }

    public partial class Dashboard : Form
    {
        readonly TrayApp app;
        readonly ModernTabControl tabs = new ModernTabControl();
        readonly Font uiFont = new Font("Segoe UI", 9f);
        readonly Font navFont = new Font("Segoe UI Semibold", 9.5f);
        readonly ToolTip tip = new ToolTip();
        Panel navBar;
        readonly List<Button> navBtns = new List<Button>();
        Button btnThemeToggle;
        Label lblNavVer;

        // live
        StatCard cDown, cUp, cPeakDown, cPeakUp, cSession;
        GraphPanel graph;
        TopAppsPanel topApps;
        ComboBox rangeBox;
        Label lblPing, lblWanMode, lblSpeedResult;
        Button btnSpeedTest, btnSpeedHistory;
        readonly SpeedTester speedTester = new SpeedTester();
        DateTime lastPingAt = DateTime.MinValue;
        bool pingBusy;
        long lastPingMs = -2;
        DbListView adapterList;
        DateTime lastAdapterRefresh = DateTime.MinValue;

        // apps
        DbListView procList, epList, portsList;
        SplitContainer appsSplit;
        Label appsStatus;
        TextBox txtAppSearch;
        Button btnElevate;
        CheckBox chkActiveOnly, chkResolve, chkAlertNewApps, chkPortsMode;
        DateTime lastPortsRefresh = DateTime.MinValue;
        readonly ImageList icons = new ImageList();
        readonly Dictionary<string, int> iconIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int procSortCol = -1; bool procSortAsc;
        int selectedPid = -1;
        static readonly Dictionary<string, string> dnsCache = new Dictionary<string, string>();

        // usage
        StatCard uToday, uYesterday, uPeriod, u30, uAll;
        UsageChart usageChart;
        DbListView usageList;
        ProgressBar limitBar;
        Label limitLabel;
        DateTime lastUsageRefresh = DateTime.MinValue;

        // settings
        bool loadingSettings;
        CheckBox sElevated;
        CheckBox sTaskbar, sShowMeter, sTop, sLock, sFull, sOneLine, sGraph, sStartup, sClickThrough, sAutoUpdate, sWanDrop, sHogGameOnly;
        ComboBox sUnit, sInterval, sAdapter, sThemeMode, sProfilePreset;
        TrackBar sOpacity;
        NumericUpDown sFont, sLimit, sStartDay, sHogAlert;
        Button bDown, bUp, bBack, bText, bTbText, btnCheckUpdates;
        Label lblUpdateStatus;
        ComboBox tFont;
        NumericUpDown tSize;
        CheckBox tBold, tAuto, tAutoPlace;
        CheckBox sGameAuto, sGameShow, sGameClickThrough, sGameRag, sGameStrip, sGameHotkeys, sGameLogSess;
        CheckBox sHudFps, sHudGpu, sHudCpu, sHudMem, sHudNet, sHudGraph;
        ComboBox sGameCorner, sGamePreset;
        TrackBar sGameOpacity;
        Label lblGameOpacity;
        NumericUpDown nFpsG, nFpsA, nTempG, nTempA, nPingG, nPingA, nLoadA, nLoadR;
        Button bHudBg, bHudBorder, bHudText, bHudFps, bHudGpu, bHudCpu, bHudMem, bHudNet;
        GameHudPreview hudPreview;
        SpecList gameDiagList;

        static Icon cachedAppIcon, cachedSmallIcon, cachedBigIcon;
        public static Icon GetAppIcon()
        {
            if (cachedAppIcon != null) return cachedAppIcon;
            try
            {
                using (Stream st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("NetSpeedBoss.app.ico"))
                {
                    if (st != null) { cachedAppIcon = new Icon(st); return cachedAppIcon; }
                }
            }
            catch { }
            try { cachedAppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            return cachedAppIcon;
        }

        void ApplyWindowIcons()
        {
            Icon ic = GetAppIcon();
            if (ic == null) return;
            Icon = ic;
            if (!IsHandleCreated) return;
            try
            {
                if (cachedSmallIcon == null) cachedSmallIcon = new Icon(ic, SystemInformation.SmallIconSize);
                if (cachedBigIcon == null) cachedBigIcon = new Icon(ic, SystemInformation.IconSize);
                Native.SendMessage(Handle, Native.WM_SETICON, new IntPtr(0), cachedSmallIcon.Handle);
                Native.SendMessage(Handle, Native.WM_SETICON, new IntPtr(1), cachedBigIcon.Handle);
            }
            catch { }
        }

        public Dashboard(TrayApp app)
        {
            this.app = app;
            Theme.Apply(app.S.ThemeMode);
            Text = TrayApp.AppName + (app.IsAdmin ? " (Administrator)" : "");
            Font = uiFont;
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(wa.Width, Math.Max(940, app.S.DashW)), Math.Min(wa.Height, Math.Max(780, app.S.DashH)));
            MinimumSize = new Size(840, 640);
            ApplyWindowIcons();
            icons.ColorDepth = ColorDepth.Depth32Bit;
            icons.ImageSize = new Size(16, 16);

            tabs.Dock = DockStyle.Fill;
            tabs.Margin = Padding.Empty;
            tabs.TabPages.Add(BuildLive());
            tabs.TabPages.Add(BuildApps());
            tabs.TabPages.Add(BuildNetwork());
            tabs.TabPages.Add(BuildUsage());
            tabs.TabPages.Add(BuildSystem());
            tabs.TabPages.Add(BuildGamingHud());
            tabs.TabPages.Add(BuildDisplays());
            tabs.TabPages.Add(BuildSettings());
            tabs.TabPages.Add(BuildAbout());
            tabs.SelectedIndexChanged += delegate { SyncNavButtons(); lastUsageRefresh = DateTime.MinValue; OnTick(); };
            Controls.Add(tabs);
            Controls.Add(BuildModeBar());
            Controls.Add(BuildUpdateBar());
            Controls.Add(BuildNavBar());

            HandleCreated += delegate { ApplyDwmFrame(); ApplyWindowIcons(); };
            FormClosing += delegate
            {
                if (WindowState == FormWindowState.Normal) { app.S.DashW = Width; app.S.DashH = Height; }
                app.S.Save();
            };
            LoadSettingsIntoUi();
            ApplyTheme();
            RefreshUpdateBar();
            OnTick();
        }

        public void OnOpened()
        {
            if (publicIp == null || !publicIp.Ok || (DateTime.Now - publicIp.CheckedAt).TotalSeconds >= 60)
                RequestPublicIp();
            if (app.S.AutoCheckUpdates && !Updater.IsStoreApp() && app.LatestUpdate == null)
                app.CheckForUpdatesAsync(false, null);
        }

        Panel updateBar, modeBar;
        Label updateMsg, modeMsg;
        Button btnUpdateNow, btnUpdateClose, btnModeAction, btnModeClose;
        LinkLabel lnkUpdateNotes;
        bool updateDismissed, updateBusy;

        Panel BuildNavBar()
        {
            navBar = new Panel(); navBar.Dock = DockStyle.Top; navBar.Height = 46; navBar.Padding = new Padding(12, 0, 12, 0);
            navBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, navBar.Height - 1, navBar.Width, navBar.Height - 1);
            };

            FlowLayoutPanel left = new FlowLayoutPanel(); left.Dock = DockStyle.Left; left.AutoSize = true; left.WrapContents = false;
            left.Padding = new Padding(0, 4, 0, 0);
            string[] names = { "Live", "Apps", "Network", "Usage", "System", "Gaming HUD", "Displays", "Settings", "About" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                Button b = new Button();
                b.Text = names[i];
                b.Tag = idx;
                b.AutoSize = true;
                b.MinimumSize = new Size(46, 38);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.Font = navFont;
                b.Cursor = Cursors.Hand;
                b.Margin = new Padding(0, 0, 2, 0);
                b.Paint += delegate(object s, PaintEventArgs e)
                {
                    if (tabs.SelectedIndex != idx) return;
                    using (SolidBrush ab = new SolidBrush(Theme.Accent))
                        e.Graphics.FillRectangle(ab, 10, b.Height - 4, b.Width - 20, 3);
                };
                b.Click += delegate { tabs.SelectedIndex = idx; };
                navBtns.Add(b);
                left.Controls.Add(b);
            }

            FlowLayoutPanel right = new FlowLayoutPanel(); right.Dock = DockStyle.Right; right.AutoSize = true; right.WrapContents = false;
            right.Padding = new Padding(0, 9, 0, 0);

            btnThemeToggle = new Button();
            btnThemeToggle.AutoSize = true; btnThemeToggle.Height = 28;
            btnThemeToggle.FlatStyle = FlatStyle.Flat; btnThemeToggle.FlatAppearance.BorderSize = 1;
            btnThemeToggle.Font = new Font("Segoe UI", 8.5f); btnThemeToggle.Cursor = Cursors.Hand;
            btnThemeToggle.Padding = new Padding(8, 2, 8, 2); btnThemeToggle.Margin = new Padding(0, 0, 8, 0);
            btnThemeToggle.Click += delegate
            {
                app.S.ThemeMode = (DashTheme)(((int)app.S.ThemeMode + 1) % 3);
                app.S.Save();
                LoadSettingsIntoUi();
                ApplyTheme();
            };

            lblNavVer = new Label();
            lblNavVer.Text = "v" + Updater.CurrentVersionString();
            lblNavVer.AutoSize = true;
            lblNavVer.Font = new Font("Segoe UI", 8.5f);
            lblNavVer.Margin = new Padding(2, 6, 2, 0);

            right.Controls.Add(btnThemeToggle);
            right.Controls.Add(lblNavVer);
            navBar.Controls.Add(left);
            navBar.Controls.Add(right);
            return navBar;
        }

        void SyncNavButtons()
        {
            for (int i = 0; i < navBtns.Count; i++)
            {
                Button b = navBtns[i];
                bool sel = i == tabs.SelectedIndex;
                b.BackColor = sel ? Theme.Hover : Theme.Card;
                b.ForeColor = sel ? Theme.Ink : Theme.Muted;
                b.FlatAppearance.MouseOverBackColor = Theme.Hover;
                b.Invalidate();
            }
        }

        void ApplyDwmFrame()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = Theme.IsDark ? 1 : 0;
                Native.DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
                int corner = 2 /*DWMWCP_ROUND*/;
                Native.DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref corner, 4);
            }
            catch { }
        }

        void ApplyTheme()
        {
            Theme.Apply(app.S.ThemeMode);
            BackColor = Theme.Bg;
            ForeColor = Theme.Ink;
            ApplyDwmFrame();

            if (navBar != null)
            {
                navBar.BackColor = Theme.Card;
                SyncNavButtons();
                if (btnThemeToggle != null)
                {
                    btnThemeToggle.BackColor = Theme.CardAlt;
                    btnThemeToggle.ForeColor = Theme.Ink;
                    btnThemeToggle.FlatAppearance.BorderColor = Theme.Border;
                    btnThemeToggle.Text = app.S.ThemeMode == DashTheme.Auto ? "Theme: Auto"
                        : (app.S.ThemeMode == DashTheme.Dark ? "Theme: Dark" : "Theme: Light");
                }
                if (lblNavVer != null) lblNavVer.ForeColor = Theme.Muted;
                navBar.Invalidate();
            }

            if (modeBar != null)
            {
                bool admin = app.IsAdmin;
                Color bg = Theme.IsDark
                    ? (admin ? Color.FromArgb(18, 42, 34) : Color.FromArgb(46, 35, 18))
                    : (admin ? Color.FromArgb(236, 253, 245) : Color.FromArgb(255, 248, 231));
                Color accent = admin ? Color.FromArgb(16, 185, 129) : Color.FromArgb(217, 119, 6);
                Color fg = Theme.IsDark
                    ? (admin ? Color.FromArgb(167, 243, 208) : Color.FromArgb(253, 230, 138))
                    : (admin ? Color.FromArgb(6, 95, 70) : Color.FromArgb(120, 53, 15));
                modeBar.BackColor = bg;
                modeBar.Tag = accent;
                if (modeMsg != null) { modeMsg.BackColor = bg; modeMsg.ForeColor = fg; }
                foreach (Control c in modeBar.Controls) if (c is FlowLayoutPanel) c.BackColor = bg;
                if (btnModeAction != null && admin) { btnModeAction.BackColor = Theme.Card; btnModeAction.ForeColor = fg; }
                if (btnModeClose != null) { btnModeClose.BackColor = bg; btnModeClose.ForeColor = fg; }
                modeBar.Invalidate();
            }

            if (updateBar != null)
            {
                Color bg = Theme.IsDark ? Color.FromArgb(21, 36, 58) : Color.FromArgb(239, 246, 255);
                Color fg = Theme.IsDark ? Color.FromArgb(191, 219, 254) : Color.FromArgb(30, 58, 138);
                Color accent = Theme.Accent;
                updateBar.BackColor = bg;
                if (updateMsg != null) { updateMsg.BackColor = bg; updateMsg.ForeColor = fg; }
                foreach (Control c in updateBar.Controls) if (c is FlowLayoutPanel) c.BackColor = bg;
                if (lnkUpdateNotes != null) { lnkUpdateNotes.BackColor = bg; lnkUpdateNotes.LinkColor = accent; lnkUpdateNotes.ActiveLinkColor = fg; }
                if (btnUpdateClose != null) { btnUpdateClose.BackColor = bg; btnUpdateClose.ForeColor = fg; }
                updateBar.Invalidate();
            }

            lastAdapterRefresh = DateTime.MinValue;
            lastUsageRefresh = DateTime.MinValue;
            foreach (TabPage tp in tabs.TabPages) ApplyThemeTo(tp);
            if (btnSpeedTest != null) btnSpeedTest.BackColor = Theme.Accent;
            if (lblPing != null) lblPing.Invalidate();
            Invalidate(true);
        }

        void ApplyThemeTo(Control c)
        {
            if (c is TabPage || c is SplitContainer || c is SplitterPanel)
            {
                c.BackColor = Theme.Bg;
                c.ForeColor = Theme.Ink;
            }
            else if (c is StatCard)
            {
                ((StatCard)c).SyncTheme();
            }
            else if (c is CardPanel)
            {
                c.BackColor = Theme.Card;
                c.ForeColor = Theme.Ink;
                c.Invalidate();
            }
            else if (c is DbListView)
            {
                c.BackColor = Theme.Card;
                c.ForeColor = Theme.Ink;
                c.Invalidate();
            }
            else if (c is GraphPanel || c is TopAppsPanel || c is UsageTopAppsPanel || c is UsageChart || c is UtilCard || c is SpecList || c is GameHudPreview)
            {
                c.BackColor = Theme.Card;
                c.ForeColor = Theme.Ink;
                c.Invalidate();
            }
            else if (c is Button)
            {
                Button b = (Button)c;
                b.UseMnemonic = false;
                if (!object.Equals(b.Tag, "primary"))
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Theme.Border;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.MouseOverBackColor = Theme.Hover;
                    b.BackColor = Theme.CardAlt;
                    b.ForeColor = Theme.Ink;
                    b.Cursor = Cursors.Hand;
                }
            }
            else if (c is ComboBox || c is NumericUpDown || c is TextBox)
            {
                c.BackColor = Theme.CardAlt;
                c.ForeColor = Theme.Ink;
            }
            else if (c is LinkLabel)
            {
                LinkLabel ll = (LinkLabel)c;
                ll.LinkColor = Theme.Accent;
                ll.ActiveLinkColor = Theme.Ink;
            }
            else if (c is Label || c is CheckBox)
            {
                if (c is Label) ((Label)c).UseMnemonic = false;
                if (c is CheckBox) ((CheckBox)c).UseMnemonic = false;
                c.BackColor = Color.Transparent;
                if (!object.Equals(c.Tag, "keepcolor"))
                    c.ForeColor = object.Equals(c.Tag, "muted") ? Theme.Muted : Theme.Ink;
            }
            else if (c is Panel || c is TableLayoutPanel || c is FlowLayoutPanel)
            {
                if (object.Equals(c.Tag, "card")) c.BackColor = Theme.Card;
                else if (c.Parent is CardPanel) c.BackColor = Theme.Card;
                else c.BackColor = Theme.Bg;
                c.ForeColor = Theme.Ink;
            }

            foreach (Control child in c.Controls) ApplyThemeTo(child);
        }

        Panel BuildUpdateBar()
        {
            Color bg = Color.FromArgb(239, 246, 255);
            Color accent = Color.FromArgb(37, 99, 235);
            Color fg = Color.FromArgb(30, 58, 138);

            updateBar = new Panel(); updateBar.Dock = DockStyle.Top; updateBar.Height = 38; updateBar.BackColor = bg;
            updateBar.Padding = new Padding(10, 4, 10, 4); updateBar.Visible = false;
            updateBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 0, 0, 4, updateBar.Height);
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, updateBar.Height - 1, updateBar.Width, updateBar.Height - 1);
            };

            FlowLayoutPanel right = new FlowLayoutPanel(); right.Dock = DockStyle.Right; right.AutoSize = true; right.WrapContents = false;
            right.FlowDirection = FlowDirection.LeftToRight; right.BackColor = bg; right.Padding = new Padding(0, 1, 0, 0);

            lnkUpdateNotes = new LinkLabel(); lnkUpdateNotes.Text = "Release notes"; lnkUpdateNotes.AutoSize = true;
            lnkUpdateNotes.LinkColor = accent; lnkUpdateNotes.ActiveLinkColor = fg; lnkUpdateNotes.BackColor = bg;
            lnkUpdateNotes.Margin = new Padding(0, 6, 10, 0);
            lnkUpdateNotes.LinkClicked += delegate
            {
                if (app.LatestUpdate != null && app.LatestUpdate.ReleaseUrl.Length > 0)
                    try { Process.Start(app.LatestUpdate.ReleaseUrl); } catch { }
            };

            btnUpdateNow = new Button(); btnUpdateNow.Text = "Download & Update Now"; btnUpdateNow.AutoSize = true;
            btnUpdateNow.FlatStyle = FlatStyle.Flat; btnUpdateNow.FlatAppearance.BorderSize = 0;
            btnUpdateNow.BackColor = accent; btnUpdateNow.ForeColor = Color.White;
            btnUpdateNow.Font = new Font("Segoe UI Semibold", 8.5f); btnUpdateNow.Cursor = Cursors.Hand;
            btnUpdateNow.Padding = new Padding(10, 2, 10, 2); btnUpdateNow.Margin = new Padding(0, 1, 4, 0);
            btnUpdateNow.Click += delegate { BeginUpdateInstall(); };

            btnUpdateClose = new Button(); btnUpdateClose.Text = "×"; btnUpdateClose.Width = 24; btnUpdateClose.Height = 24;
            btnUpdateClose.FlatStyle = FlatStyle.Flat; btnUpdateClose.FlatAppearance.BorderSize = 0;
            btnUpdateClose.BackColor = bg; btnUpdateClose.ForeColor = fg; btnUpdateClose.Cursor = Cursors.Hand;
            btnUpdateClose.Margin = new Padding(0, 1, 0, 0);
            btnUpdateClose.Click += delegate { updateDismissed = true; updateBar.Visible = false; };

            right.Controls.Add(lnkUpdateNotes); right.Controls.Add(btnUpdateNow); right.Controls.Add(btnUpdateClose);

            updateMsg = new Label(); updateMsg.Dock = DockStyle.Fill; updateMsg.TextAlign = ContentAlignment.MiddleLeft;
            updateMsg.ForeColor = fg; updateMsg.BackColor = bg; updateMsg.Padding = new Padding(4, 0, 0, 0);
            updateMsg.Font = new Font("Segoe UI Semibold", 9f);

            updateBar.Controls.Add(updateMsg); updateBar.Controls.Add(right);
            return updateBar;
        }

        public void RefreshUpdateBar()
        {
            if (updateBar == null || IsDisposed) return;
            UpdateInfo u = app.LatestUpdate;
            bool show = !updateDismissed && u != null && u.UpdateAvailable;
            updateBar.Visible = show;
            if (show && !updateBusy)
            {
                updateMsg.Text = "Version " + u.Version + " is available (installed: " + u.CurrentVersion + ") — ready to download and install.";
                btnUpdateNow.Text = "Download & Update Now";
                btnUpdateNow.Enabled = true;
                btnUpdateClose.Enabled = true;
            }
        }

        public void BeginUpdateInstall()
        {
            UpdateInfo u = app.LatestUpdate;
            if (updateBusy || u == null || !u.UpdateAvailable) return;
            updateDismissed = false;
            updateBusy = true;
            updateBar.Visible = true;
            btnUpdateNow.Enabled = false;
            btnUpdateClose.Enabled = false;
            btnUpdateNow.Text = "Downloading...";
            updateMsg.Text = "Downloading NetSpeed Boss v" + u.Version + "...";
            app.StartUpdateInstall(u,
                delegate(int pct)
                {
                    if (IsDisposed) return;
                    btnUpdateNow.Text = pct >= 100 ? "Installing & restarting..." : "Downloading " + pct + "%";
                    updateMsg.Text = pct >= 100
                        ? "Installing v" + u.Version + " and restarting automatically..."
                        : "Downloading NetSpeed Boss v" + u.Version + " (" + pct + "%)...";
                },
                delegate(string err)
                {
                    if (IsDisposed) return;
                    updateBusy = false;
                    btnUpdateNow.Enabled = true;
                    btnUpdateClose.Enabled = true;
                    btnUpdateNow.Text = "Retry Update";
                    updateMsg.Text = "Update download failed: " + err;
                });
        }

        Panel BuildModeBar()
        {
            bool admin = app.IsAdmin;
            Color bg = admin ? Color.FromArgb(236, 253, 245) : Color.FromArgb(255, 248, 231);
            Color accent = admin ? Color.FromArgb(16, 185, 129) : Color.FromArgb(217, 119, 6);
            Color fg = admin ? Color.FromArgb(6, 95, 70) : Color.FromArgb(120, 53, 15);

            modeBar = new Panel(); modeBar.Dock = DockStyle.Top; modeBar.Height = 36; modeBar.BackColor = bg; modeBar.Padding = new Padding(10, 4, 10, 4);
            modeBar.Tag = accent;
            modeBar.Paint += delegate(object s, PaintEventArgs e)
            {
                Color ac = modeBar.Tag is Color ? (Color)modeBar.Tag : accent;
                using (SolidBrush b = new SolidBrush(ac)) e.Graphics.FillRectangle(b, 0, 0, 4, modeBar.Height);
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, modeBar.Height - 1, modeBar.Width, modeBar.Height - 1);
            };

            FlowLayoutPanel right = new FlowLayoutPanel(); right.Dock = DockStyle.Right; right.AutoSize = true; right.WrapContents = false;
            right.FlowDirection = FlowDirection.LeftToRight; right.BackColor = bg; right.Padding = new Padding(0, 1, 0, 0);

            if (!admin)
            {
                btnModeAction = new Button(); btnModeAction.Text = "Restart as Administrator"; btnModeAction.AutoSize = true;
                btnModeAction.FlatStyle = FlatStyle.Flat; btnModeAction.FlatAppearance.BorderSize = 0;
                btnModeAction.BackColor = accent; btnModeAction.ForeColor = Color.White;
                btnModeAction.Font = new Font("Segoe UI Semibold", 8.5f); btnModeAction.Cursor = Cursors.Hand;
                btnModeAction.Padding = new Padding(8, 1, 8, 1); btnModeAction.Margin = new Padding(0, 1, 4, 0);
                btnModeAction.Click += delegate { app.RestartElevated(); };

                btnModeClose = new Button(); btnModeClose.Text = "×"; btnModeClose.Width = 24; btnModeClose.Height = 24;
                btnModeClose.FlatStyle = FlatStyle.Flat; btnModeClose.FlatAppearance.BorderSize = 0;
                btnModeClose.BackColor = bg; btnModeClose.ForeColor = fg; btnModeClose.Cursor = Cursors.Hand;
                btnModeClose.Margin = new Padding(0, 1, 0, 0);
                btnModeClose.Click += delegate { modeBar.Visible = false; };
                right.Controls.Add(btnModeAction); right.Controls.Add(btnModeClose);
            }
            else
            {
                btnModeAction = new Button();
                btnModeAction.AutoSize = true; btnModeAction.FlatStyle = FlatStyle.Flat;
                btnModeAction.FlatAppearance.BorderColor = accent; btnModeAction.FlatAppearance.BorderSize = 1;
                btnModeAction.BackColor = Color.White; btnModeAction.ForeColor = fg;
                btnModeAction.Font = new Font("Segoe UI Semibold", 8.5f); btnModeAction.Cursor = Cursors.Hand;
                btnModeAction.Padding = new Padding(8, 1, 8, 1); btnModeAction.Margin = new Padding(0, 1, 0, 0);
                Action syncAuto = delegate
                {
                    bool on = app.ElevatedStartupEnabled;
                    btnModeAction.Text = on ? "✔ Auto-starts as Admin" : "Auto-start as Admin (no prompt)";
                };
                syncAuto();
                btnModeAction.Click += delegate
                {
                    if (!app.SetElevatedStartup(!app.ElevatedStartupEnabled))
                        MessageBox.Show("Could not update the elevated sign-in task.", TrayApp.AppName);
                    syncAuto();
                    LoadSettingsIntoUi();
                };
                right.Controls.Add(btnModeAction);
            }

            modeMsg = new Label(); modeMsg.Dock = DockStyle.Fill; modeMsg.TextAlign = ContentAlignment.MiddleLeft;
            modeMsg.ForeColor = fg; modeMsg.BackColor = bg; modeMsg.Padding = new Padding(4, 0, 0, 0);
            modeMsg.Text = admin
                ? "✔ Administrator mode active  ·  Live per-app speeds and hourly app logs are enabled."
                : "Standard mode  ·  Per-app live speeds and hourly app logs require Administrator rights.";

            modeBar.Controls.Add(modeMsg); modeBar.Controls.Add(right);
            return modeBar;
        }

        public void SelectTab(int i) { if (i >= 0 && i < tabs.TabPages.Count) tabs.SelectedIndex = i; }

        static TabPage Page(string t)
        {
            TabPage p = new TabPage(t);
            p.BackColor = Theme.Bg;
            p.Padding = new Padding(10);
            return p;
        }

        static ColumnHeader Col(ListView lv, string t, int w, HorizontalAlignment a)
        {
            ColumnHeader c = lv.Columns.Add(t, w);
            c.TextAlign = a;
            return c;
        }

        // =================================================================== LIVE
        TabPage BuildLive()
        {
            TabPage p = Page("Live");
            TableLayoutPanel cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top; cards.Height = 100; cards.ColumnCount = 5; cards.RowCount = 1;
            for (int i = 0; i < 5; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            cDown = new StatCard("Download"); cDown.AccentColor = app.S.DownColor;
            cUp = new StatCard("Upload"); cUp.AccentColor = app.S.UpColor;
            cPeakDown = new StatCard("Peak download"); cPeakDown.AccentColor = app.S.DownColor;
            cPeakUp = new StatCard("Peak upload"); cPeakUp.AccentColor = app.S.UpColor;
            cSession = new StatCard("This session");
            foreach (StatCard c in new StatCard[] { cDown, cUp, cPeakDown, cPeakUp, cSession }) { c.Dock = DockStyle.Fill; cards.Controls.Add(c); }

            Panel graphHost = new Panel();
            graphHost.Dock = DockStyle.Fill; graphHost.Padding = new Padding(6);
            CardPanel graphCard = new CardPanel(); graphCard.Dock = DockStyle.Fill; graphCard.Padding = new Padding(6, 4, 6, 6);
            graph = new GraphPanel(); graph.App = app; graph.Dock = DockStyle.Fill;
            Panel graphBar = new Panel(); graphBar.Dock = DockStyle.Top; graphBar.Height = 32; graphBar.Tag = "card";
            Label gl = new Label(); gl.Text = "Speed history"; gl.Font = new Font("Segoe UI Semibold", 9.5f); gl.AutoSize = true; gl.Location = new Point(6, 7);
            lblWanMode = new Label(); lblWanMode.AutoSize = true; lblWanMode.Tag = "keepcolor"; lblWanMode.Location = new Point(108, 8);
            lblSpeedResult = new Label(); lblSpeedResult.AutoSize = false; lblSpeedResult.Height = 20; lblSpeedResult.AutoEllipsis = true;
            lblSpeedResult.TextAlign = ContentAlignment.MiddleLeft; lblSpeedResult.Tag = "keepcolor"; lblSpeedResult.ForeColor = Theme.Muted;

            btnSpeedTest = new Button(); btnSpeedTest.Text = "Speedtest"; btnSpeedTest.Size = new Size(88, 24);
            btnSpeedTest.Tag = "primary";
            btnSpeedTest.FlatStyle = FlatStyle.Flat; btnSpeedTest.FlatAppearance.BorderSize = 0;
            btnSpeedTest.BackColor = Theme.Accent; btnSpeedTest.ForeColor = Color.White; btnSpeedTest.Cursor = Cursors.Hand;
            btnSpeedTest.Font = new Font("Segoe UI Semibold", 8.5f);
            btnSpeedTest.Click += delegate { ToggleSpeedTest(); };
            tip.SetToolTip(btnSpeedTest, "Run multi-stream speed test (measures combined load-balanced bandwidth)");

            btnSpeedHistory = new Button(); btnSpeedHistory.Text = "History"; btnSpeedHistory.Size = new Size(64, 24);
            btnSpeedHistory.Font = new Font("Segoe UI", 8.5f);
            btnSpeedHistory.Click += delegate { ShowSpeedHistoryDialog(); };
            tip.SetToolTip(btnSpeedHistory, "View saved Speedtest history");

            lblPing = new Label(); lblPing.AutoSize = false; lblPing.Size = new Size(134, 22); lblPing.Tag = "keepcolor"; lblPing.Cursor = Cursors.Hand;
            lblPing.Paint += PaintPingPill;
            lblPing.Click += delegate { CheckPingAsync(true); };
            tip.SetToolTip(lblPing, "Latency, jitter & packet loss to 1.1.1.1 (click to re-test)");
            rangeBox = new ComboBox(); rangeBox.DropDownStyle = ComboBoxStyle.DropDownList; rangeBox.Width = 104;
            rangeBox.Items.AddRange(new object[] { "1 minute", "2 minutes", "5 minutes", "10 minutes" });
            rangeBox.SelectedIndex = 1;
            rangeBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            rangeBox.SelectedIndexChanged += delegate { graph.Seconds = new int[] { 60, 120, 300, 600 }[rangeBox.SelectedIndex]; graph.Invalidate(); };
            graphBar.Controls.Add(gl); graphBar.Controls.Add(lblWanMode); graphBar.Controls.Add(lblSpeedResult);
            graphBar.Controls.Add(btnSpeedTest); graphBar.Controls.Add(btnSpeedHistory); graphBar.Controls.Add(lblPing); graphBar.Controls.Add(rangeBox);
            Action layoutGraphBar = delegate
            {
                rangeBox.Location = new Point(graphBar.Width - rangeBox.Width - 4, 4);
                lblPing.Location = new Point(rangeBox.Left - lblPing.Width - 8, 5);
                btnSpeedHistory.Location = new Point(lblPing.Left - btnSpeedHistory.Width - 6, 4);
                btnSpeedTest.Location = new Point(btnSpeedHistory.Left - btnSpeedTest.Width - 6, 4);
                lblWanMode.Location = new Point(gl.Right + 10, 8);
                int resLeft = lblWanMode.Right + 12;
                lblSpeedResult.Location = new Point(resLeft, 6);
                lblSpeedResult.Width = Math.Max(0, btnSpeedTest.Left - resLeft - 8);
            };
            graphBar.Resize += delegate { layoutGraphBar(); };
            lblWanMode.TextChanged += delegate { layoutGraphBar(); };
            lblWanMode.SizeChanged += delegate { layoutGraphBar(); };
            graphCard.Controls.Add(graph); graphCard.Controls.Add(graphBar);
            graphHost.Controls.Add(graphCard);

            adapterList = new DbListView(); adapterList.Dock = DockStyle.Fill;
            Col(adapterList, "Adapter", 140, HorizontalAlignment.Left);
            Col(adapterList, "Download", 88, HorizontalAlignment.Right);
            Col(adapterList, "Upload", 84, HorizontalAlignment.Right);
            Col(adapterList, "Link speed", 78, HorizontalAlignment.Right);
            Col(adapterList, "IPv4", 104, HorizontalAlignment.Left);
            Col(adapterList, "Gateway", 104, HorizontalAlignment.Left);
            Col(adapterList, "Received", 92, HorizontalAlignment.Right);
            Col(adapterList, "Sent", 84, HorizontalAlignment.Right);
            Col(adapterList, "Measured", 82, HorizontalAlignment.Center);

            Panel appsHost = new Panel(); appsHost.Dock = DockStyle.Bottom; appsHost.Padding = new Padding(6, 0, 6, 6);
            appsHost.Height = TopAppsPanel.PreferredHeight + 6;
            topApps = new TopAppsPanel(); topApps.App = app; topApps.Dock = DockStyle.Fill;
            topApps.IconFor = delegate(string path) { int i = IconFor(path); return i >= 0 ? icons.Images[i] : null; };
            appsHost.Controls.Add(topApps);

            Panel adapterHost = new Panel(); adapterHost.Dock = DockStyle.Bottom; adapterHost.Height = 136; adapterHost.Padding = new Padding(6);
            CardPanel adapterCard = new CardPanel(); adapterCard.Dock = DockStyle.Fill; adapterCard.Padding = new Padding(8, 6, 8, 6);
            Label al = new Label(); al.Text = "Network adapters"; al.Font = new Font("Segoe UI Semibold", 9.5f); al.Dock = DockStyle.Top; al.Height = 24;
            adapterCard.Controls.Add(adapterList); adapterCard.Controls.Add(al);
            adapterHost.Controls.Add(adapterCard);

            p.Controls.Add(graphHost);
            p.Controls.Add(appsHost);
            p.Controls.Add(adapterHost);
            p.Controls.Add(cards);
            return p;
        }

        void TickLive()
        {
            Settings s = app.S;
            bool multi = app.Net.IsMultiWan;
            if (multi)
            {
                cDown.Title("Total Download (" + app.Net.ActiveWanCount + " WANs)");
                cUp.Title("Total Upload (" + app.Net.ActiveWanCount + " WANs)");
                List<string> dnParts = new List<string>(), upParts = new List<string>();
                foreach (AdapterInfo a in app.Net.Adapters)
                {
                    if (!a.Selected || !a.HasGateway) continue;
                    string shortN = a.Name.Length > 10 ? a.Name.Substring(0, 10) : a.Name;
                    dnParts.Add(shortN + " " + Fmt.Speed(a.RxBps, s.Unit));
                    upParts.Add(shortN + " " + Fmt.Speed(a.TxBps, s.Unit));
                }
                cDown.Set(Fmt.Speed(app.CurRx, s.Unit), string.Join(" + ", dnParts.ToArray()));
                cUp.Set(Fmt.Speed(app.CurTx, s.Unit), string.Join(" + ", upParts.ToArray()));
                if (lblWanMode != null)
                {
                    lblWanMode.Text = "● " + app.Net.ActiveWanCount + " WANs · Load balanced";
                    lblWanMode.ForeColor = Theme.IsDark ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 124, 16);
                }
            }
            else
            {
                cDown.Title("Download");
                cUp.Title("Upload");
                cDown.Set(Fmt.Speed(app.CurRx, s.Unit), "↓ receiving");
                cUp.Set(Fmt.Speed(app.CurTx, s.Unit), "↑ sending");
                if (lblWanMode != null)
                {
                    lblWanMode.Text = app.Net.ActiveWanCount == 1 ? "Single WAN" : "No gateway";
                    lblWanMode.ForeColor = Theme.Muted;
                }
            }
            cPeakDown.Set(Fmt.Speed(app.PeakRx, s.Unit), "since " + app.SessionStart.ToString("t"));
            cPeakUp.Set(Fmt.Speed(app.PeakTx, s.Unit), "since " + app.SessionStart.ToString("t"));
            TimeSpan up = DateTime.Now - app.SessionStart;
            cSession.Title("This session (" +
                (up.TotalHours >= 1 ? ((int)up.TotalHours) + "h " + up.Minutes + "m" : up.Minutes + "m " + up.Seconds + "s") + ")");
            cSession.Set(Fmt.Size(app.SessionRx + app.SessionTx),
                "↓ " + Fmt.Size(app.SessionRx) + "  ↑ " + Fmt.Size(app.SessionTx));
            graph.Invalidate();
            topApps.RefreshRows();
            if ((DateTime.UtcNow - lastPingAt).TotalSeconds >= 3) CheckPingAsync(false);

            if ((DateTime.UtcNow - lastAdapterRefresh).TotalSeconds >= 1)
            {
                lastAdapterRefresh = DateTime.UtcNow;
                List<AdapterInfo> ads = app.Net.Adapters;
                adapterList.BeginUpdate();
                while (adapterList.Items.Count > ads.Count) adapterList.Items.RemoveAt(adapterList.Items.Count - 1);
                for (int i = 0; i < ads.Count; i++)
                {
                    AdapterInfo a = ads[i];
                    if (a.IPv4 == null) a.IPv4 = NetMonitor.GetIPv4(a.Id);
                    ListViewItem it = i < adapterList.Items.Count ? adapterList.Items[i] : adapterList.Items.Add("");
                    double totRate = app.CurRx + app.CurTx;
                    string wanCol = a.Selected ? "✔" : "";
                    if (a.Selected && a.HasGateway && multi)
                    {
                        double pct = totRate > 512 ? Math.Round(100.0 * (a.RxBps + a.TxBps) / totRate) : 0;
                        wanCol = pct > 0 ? pct.ToString("0") + "% · ✔ WAN" : "✔ WAN";
                    }
                    SetRow(it, new string[] {
                        a.Name,
                        Fmt.Speed(a.RxBps, s.Unit),
                        Fmt.Speed(a.TxBps, s.Unit),
                        Fmt.LinkSpeed(a.LinkSpeed),
                        a.IPv4,
                        a.Gateway,
                        Fmt.Size(a.BytesIn),
                        Fmt.Size(a.BytesOut),
                        wanCol
                    });
                    Color fc = a.Selected ? Theme.Ink : Theme.Muted;
                    if (it.ForeColor != fc) it.ForeColor = fc;
                }
                adapterList.EndUpdate();
            }
        }

        // =================================================================== APPS
        TabPage BuildApps()
        {
            TabPage p = Page("Apps");
            CardPanel bar = new CardPanel(); bar.Radius = 8; bar.Dock = DockStyle.Top; bar.Height = 42; bar.Padding = new Padding(10, 7, 10, 7);
            appsStatus = new Label(); appsStatus.AutoSize = false; appsStatus.Dock = DockStyle.Fill; appsStatus.TextAlign = ContentAlignment.MiddleLeft;
            appsStatus.AutoEllipsis = true; appsStatus.Tag = "keepcolor";
            FlowLayoutPanel right = new FlowLayoutPanel(); right.Dock = DockStyle.Right; right.AutoSize = true; right.WrapContents = false;
            right.FlowDirection = FlowDirection.LeftToRight;
            txtAppSearch = new TextBox(); txtAppSearch.Width = 140; txtAppSearch.BorderStyle = BorderStyle.FixedSingle; txtAppSearch.Margin = new Padding(0, 3, 6, 0);
            txtAppSearch.HandleCreated += delegate { Native.SendMessage(txtAppSearch.Handle, 0x1501 /*EM_SETCUEBANNER*/, 1, "Search apps..."); };
            txtAppSearch.TextChanged += delegate { TickApps(); };
            chkActiveOnly = new CheckBox(); chkActiveOnly.Text = "Active only"; chkActiveOnly.AutoSize = true; chkActiveOnly.Margin = new Padding(6, 5, 6, 0);
            chkActiveOnly.CheckedChanged += delegate { TickApps(); };
            chkResolve = new CheckBox(); chkResolve.Text = "Resolve host names"; chkResolve.Checked = true; chkResolve.AutoSize = true; chkResolve.Margin = new Padding(6, 5, 6, 0);
            chkAlertNewApps = new CheckBox(); chkAlertNewApps.Text = "Alert new apps"; chkAlertNewApps.AutoSize = true; chkAlertNewApps.Margin = new Padding(6, 5, 6, 0);
            chkAlertNewApps.CheckedChanged += delegate { if (!loadingSettings) { app.S.AlertNewNetworkApps = chkAlertNewApps.Checked; app.S.Save(); } };
            chkPortsMode = new CheckBox(); chkPortsMode.Text = "Listening ports"; chkPortsMode.AutoSize = true; chkPortsMode.Margin = new Padding(6, 5, 6, 0);
            chkPortsMode.CheckedChanged += delegate
            {
                bool pmOn = chkPortsMode.Checked;
                appsSplit.Visible = !pmOn;
                portsList.Visible = pmOn;
                lastPortsRefresh = DateTime.MinValue;
                TickApps();
            };
            Button reset = new Button(); reset.Text = "Reset totals"; reset.AutoSize = true; reset.Click += delegate { app.Procs.ResetTotals(); };
            btnElevate = new Button(); btnElevate.Text = "Restart as administrator"; btnElevate.AutoSize = true;
            btnElevate.Click += delegate { app.RestartElevated(); };
            btnElevate.Visible = !app.IsAdmin;
            right.Controls.Add(txtAppSearch); right.Controls.Add(chkPortsMode); right.Controls.Add(chkActiveOnly); right.Controls.Add(chkResolve); right.Controls.Add(chkAlertNewApps); right.Controls.Add(reset); right.Controls.Add(btnElevate);
            bar.Controls.Add(appsStatus); bar.Controls.Add(right);

            appsSplit = new SplitContainer();
            appsSplit.Dock = DockStyle.Fill; appsSplit.Orientation = Orientation.Horizontal; appsSplit.SplitterWidth = 6;
            appsSplit.BackColor = Theme.Bg;

            portsList = new DbListView(); portsList.Dock = DockStyle.Fill; portsList.SmallImageList = icons; portsList.MultiSelect = false; portsList.Visible = false;
            Col(portsList, "Port", 78, HorizontalAlignment.Right);
            Col(portsList, "Proto", 62, HorizontalAlignment.Center);
            Col(portsList, "Application", 190, HorizontalAlignment.Left);
            Col(portsList, "PID", 66, HorizontalAlignment.Right);
            Col(portsList, "Bind Address", 180, HorizontalAlignment.Left);
            Col(portsList, "Path", 340, HorizontalAlignment.Left);
            ContextMenuStrip ptm = new ContextMenuStrip(); ptm.Renderer = ModernMenuRenderer.Instance;
            ptm.Items.Add("Open http://localhost:<port>", null, delegate
            {
                if (portsList.SelectedItems.Count > 0) try { Process.Start("http://localhost:" + portsList.SelectedItems[0].Text); } catch { }
            });
            ptm.Items.Add("Copy IP:Port", null, delegate
            {
                if (portsList.SelectedItems.Count > 0)
                    Clipboard.SetText(portsList.SelectedItems[0].SubItems[4].Text + ":" + portsList.SelectedItems[0].Text);
            });
            ptm.Items.Add("Kill process locking port...", null, delegate
            {
                if (portsList.SelectedItems.Count == 0) return;
                int pid;
                if (!int.TryParse(portsList.SelectedItems[0].SubItems[3].Text, out pid)) return;
                string appN = portsList.SelectedItems[0].SubItems[2].Text;
                if (MessageBox.Show("Kill " + appN + " (PID " + pid + ") holding port " + portsList.SelectedItems[0].Text + "?",
                    TrayApp.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try { Process.GetProcessById(pid).Kill(); lastPortsRefresh = DateTime.MinValue; TickApps(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
            });
            portsList.ContextMenuStrip = ptm;

            procList = new DbListView(); procList.Dock = DockStyle.Fill; procList.SmallImageList = icons; procList.MultiSelect = false;
            Col(procList, "Application", 210, HorizontalAlignment.Left);
            Col(procList, "PID", 60, HorizontalAlignment.Right);
            Col(procList, "Download", 95, HorizontalAlignment.Right);
            Col(procList, "Upload", 95, HorizontalAlignment.Right);
            Col(procList, "Downloaded", 95, HorizontalAlignment.Right);
            Col(procList, "Uploaded", 95, HorizontalAlignment.Right);
            Col(procList, "TCP conns", 70, HorizontalAlignment.Right);
            Col(procList, "Path", 320, HorizontalAlignment.Left);
            procList.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                if (procSortCol == e.Column) procSortAsc = !procSortAsc;
                else { procSortCol = e.Column; procSortAsc = e.Column == 0 || e.Column == 7; }
                TickApps();
            };
            procList.SelectedIndexChanged += delegate
            {
                if (procList.SelectedItems.Count > 0) { selectedPid = (int)procList.SelectedItems[0].Tag; TickApps(); }
            };
            ContextMenuStrip pm = new ContextMenuStrip();
            pm.Renderer = ModernMenuRenderer.Instance;
            pm.Items.Add("Open file location", null, delegate
            {
                ProcView v = SelectedProc();
                if (v != null && v.Path != null) Process.Start("explorer.exe", "/select,\"" + v.Path + "\"");
            });
            pm.Items.Add("Copy name", null, delegate { ProcView v = SelectedProc(); if (v != null) Clipboard.SetText(v.Name); });
            pm.Items.Add(new ToolStripSeparator());
            pm.Items.Add("Block outbound in Windows Firewall...", null, delegate { SetFirewallBlock(SelectedProc(), true); });
            pm.Items.Add("Remove NetSpeed Boss Firewall block", null, delegate { SetFirewallBlock(SelectedProc(), false); });
            pm.Items.Add(new ToolStripSeparator());
            pm.Items.Add("End process...", null, delegate
            {
                ProcView v = SelectedProc();
                if (v == null) return;
                if (MessageBox.Show("End " + v.Name + " (PID " + v.Pid + ")? Unsaved work in it will be lost.", TrayApp.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try { Process.GetProcessById(v.Pid).Kill(); } catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
            });
            procList.ContextMenuStrip = pm;

            epList = new DbListView(); epList.Dock = DockStyle.Fill;
            Col(epList, "Protocol", 65, HorizontalAlignment.Left);
            Col(epList, "Remote endpoint", 330, HorizontalAlignment.Left);
            Col(epList, "State", 90, HorizontalAlignment.Left);
            Col(epList, "Download", 95, HorizontalAlignment.Right);
            Col(epList, "Upload", 95, HorizontalAlignment.Right);
            Col(epList, "Downloaded", 95, HorizontalAlignment.Right);
            Col(epList, "Uploaded", 95, HorizontalAlignment.Right);
            ContextMenuStrip em = new ContextMenuStrip();
            em.Renderer = ModernMenuRenderer.Instance;
            em.Items.Add("Copy remote address", null, delegate { if (epList.SelectedItems.Count > 0) Clipboard.SetText((string)epList.SelectedItems[0].Tag); });
            epList.ContextMenuStrip = em;

            Label epTitle = new Label(); epTitle.Text = "Connections of the selected app"; epTitle.Dock = DockStyle.Top; epTitle.Height = 22;
            epTitle.Font = new Font("Segoe UI Semibold", 9.5f); epTitle.Padding = new Padding(2, 4, 0, 0);
            appsSplit.Panel1.Controls.Add(procList);
            appsSplit.Panel2.Controls.Add(epList); appsSplit.Panel2.Controls.Add(epTitle);
            p.Controls.Add(appsSplit); p.Controls.Add(portsList); p.Controls.Add(bar);
            p.Layout += delegate { try { if (appsSplit.Height > 200 && appsSplit.SplitterDistance < 100) appsSplit.SplitterDistance = (int)(appsSplit.Height * 0.58); } catch { } };
            return p;
        }

        ProcView SelectedProc()
        {
            if (procList.SelectedItems.Count == 0) return null;
            int pid = (int)procList.SelectedItems[0].Tag;
            foreach (ProcView v in app.Procs.Snapshot()) if (v.Pid == pid) return v;
            return null;
        }

        void SetFirewallBlock(ProcView v, bool block)
        {
            if (v == null || string.IsNullOrEmpty(v.Path))
            {
                MessageBox.Show("No executable path available for this process.", TrayApp.AppName);
                return;
            }
            string rule = "NetSpeedBoss Block - " + v.Name;
            if (block && MessageBox.Show("Block all outbound internet access for:\n" + v.Path + "\n\n(Rule: \"" + rule + "\")",
                TrayApp.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                string args = block
                    ? string.Format("advfirewall firewall add rule name=\"{0}\" dir=out action=block program=\"{1}\" enable=yes", rule, v.Path)
                    : string.Format("advfirewall firewall delete rule name=\"{0}\" program=\"{1}\"", rule, v.Path);
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                if (!app.IsAdmin) { psi.Verb = "runas"; psi.UseShellExecute = true; }
                else { psi.CreateNoWindow = true; psi.UseShellExecute = false; }
                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(8000);
                    MessageBox.Show(p != null && p.ExitCode == 0
                        ? (block ? "Blocked outbound firewall rule added for " + v.Name : "Removed firewall block for " + v.Name)
                        : "Windows Firewall did not apply the change (Administrator rights required).", TrayApp.AppName);
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
        }

        int IconFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            int idx;
            if (iconIndex.TryGetValue(path, out idx)) return idx;
            idx = -1;
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(path))
                    if (ic != null) { icons.Images.Add(new Bitmap(ic.ToBitmap(), 16, 16)); idx = icons.Images.Count - 1; }
            }
            catch { }
            iconIndex[path] = idx;
            return idx;
        }

        string Host(string endpoint)
        {
            if (!chkResolve.Checked) return endpoint;
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0) return endpoint;
            string ip = endpoint.Substring(0, colon).Trim('[', ']');
            string port = endpoint.Substring(colon + 1);
            string host;
            lock (dnsCache)
            {
                if (!dnsCache.TryGetValue(ip, out host))
                {
                    dnsCache[ip] = null;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string h = "";
                        try { h = Dns.GetHostEntry(ip).HostName; if (h == ip) h = ""; } catch { }
                        lock (dnsCache) dnsCache[ip] = h;
                    });
                }
            }
            if (string.IsNullOrEmpty(host)) return endpoint;
            return host + ":" + port + "   (" + ip + ")";
        }

        int CompareProc(ProcView a, ProcView b)
        {
            int r;
            switch (procSortCol)
            {
                case 0: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: r = a.Pid.CompareTo(b.Pid); break;
                case 2: r = a.Rx.CompareTo(b.Rx); break;
                case 3: r = a.Tx.CompareTo(b.Tx); break;
                case 4: r = a.TotIn.CompareTo(b.TotIn); break;
                case 5: r = a.TotOut.CompareTo(b.TotOut); break;
                case 6: r = a.Conns.CompareTo(b.Conns); break;
                case 7: r = string.Compare(a.Path ?? "", b.Path ?? "", StringComparison.OrdinalIgnoreCase); break;
                default:
                    r = (a.Rx + a.Tx).CompareTo(b.Rx + b.Tx);
                    if (r == 0) r = (a.TotIn + a.TotOut).CompareTo(b.TotIn + b.TotOut);
                    if (r == 0) r = a.Conns.CompareTo(b.Conns);
                    if (r == 0) r = -string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return -r;
            }
            return procSortAsc ? r : -r;
        }

        static void SetRow(ListViewItem it, string[] vals)
        {
            while (it.SubItems.Count < vals.Length) it.SubItems.Add("");
            for (int i = 0; i < vals.Length; i++) if (it.SubItems[i].Text != vals[i]) it.SubItems[i].Text = vals[i];
        }

        void TickApps()
        {
            if (chkPortsMode != null && chkPortsMode.Checked)
            {
                if ((DateTime.UtcNow - lastPortsRefresh).TotalSeconds < 2) return;
                lastPortsRefresh = DateTime.UtcNow;
                string q = txtAppSearch != null ? txtAppSearch.Text.Trim() : "";
                List<ListeningPortInfo> ports = app.Procs.ReadListeners();
                if (q.Length > 0)
                    ports.RemoveAll(delegate(ListeningPortInfo lp)
                    {
                        return lp.Port.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                               lp.ProcessName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                               lp.LocalAddress.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0;
                    });
                appsStatus.Text = "● " + ports.Count + " listening TCP/UDP port(s) — right-click a port to copy, open, or kill locking process";
                appsStatus.ForeColor = Theme.IsDark ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 124, 16);
                portsList.BeginUpdate();
                portsList.Items.Clear();
                foreach (ListeningPortInfo lp in ports)
                {
                    ListViewItem it = new ListViewItem(new string[] {
                        lp.Port.ToString(), lp.Proto, lp.ProcessName, lp.Pid.ToString(), lp.LocalAddress, lp.Path
                    });
                    it.ImageIndex = IconFor(lp.Path);
                    portsList.Items.Add(it);
                }
                portsList.EndUpdate();
                return;
            }
            Settings s = app.S;
            ProcessMonitor pm = app.Procs;
            List<ProcView> list = pm.Snapshot();
            bool etw = pm.EtwActive;
            if (chkActiveOnly.Checked) list.RemoveAll(delegate(ProcView v) { return v.Rx + v.Tx <= 0 && v.Conns == 0; });
            string query = txtAppSearch != null ? txtAppSearch.Text.Trim() : "";
            if (query.Length > 0)
                list.RemoveAll(delegate(ProcView v)
                {
                    return (v.Name == null || v.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) &&
                           (v.Path == null || v.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0);
                });
            list.Sort(CompareProc);

            double sumRx = 0, sumTx = 0;
            foreach (ProcView v in list) { sumRx += v.Rx; sumTx += v.Tx; }
            if (etw && pm.EventsSeen == 0 && app.SessionRx + app.SessionTx > 5 * 1024 * 1024)
                pm.Diagnose("Kernel trace started but no network events were received - per-app speeds unavailable.");
            string status = etw
                ? "● Live per-app tracking  ·  " + list.Count + " apps  ·  ↓ " + Fmt.Speed(sumRx, s.Unit) + "  ↑ " + Fmt.Speed(sumTx, s.Unit)
                : "⚠ " + pm.Status + "  Showing TCP connections only.";
            if (appsStatus.Text != status) appsStatus.Text = status;
            appsStatus.ForeColor = etw
                ? (Theme.IsDark ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 124, 16))
                : (Theme.IsDark ? Color.FromArgb(251, 191, 36) : Color.FromArgb(157, 93, 0));

            procList.BeginUpdate();
            // reuse items where possible to avoid flicker
            Dictionary<int, ListViewItem> existing = new Dictionary<int, ListViewItem>();
            foreach (ListViewItem it in procList.Items) existing[(int)it.Tag] = it;
            List<ListViewItem> ordered = new List<ListViewItem>();
            foreach (ProcView v in list)
            {
                ListViewItem it;
                if (!existing.TryGetValue(v.Pid, out it)) { it = new ListViewItem(); it.Tag = v.Pid; }
                string[] vals = {
                    v.Name, v.Pid.ToString(),
                    etw ? Fmt.Speed(v.Rx, s.Unit) : "-", etw ? Fmt.Speed(v.Tx, s.Unit) : "-",
                    etw ? Fmt.Size(v.TotIn) : "-", etw ? Fmt.Size(v.TotOut) : "-",
                    v.Conns.ToString(), v.Path ?? "" };
                SetRow(it, vals);
                int ic = IconFor(v.Path);
                if (it.ImageIndex != ic) it.ImageIndex = ic;
                bool active = etw ? v.Rx + v.Tx > 0 : v.Conns > 0;
                Color fc = active ? Theme.Ink : Theme.Muted;
                if (it.ForeColor != fc) it.ForeColor = fc;
                ordered.Add(it);
            }
            bool same = ordered.Count == procList.Items.Count;
            for (int i = 0; same && i < ordered.Count; i++) if (procList.Items[i] != ordered[i]) same = false;
            if (!same)
            {
                procList.Items.Clear();
                procList.Items.AddRange(ordered.ToArray());
                foreach (ListViewItem it in ordered) if ((int)it.Tag == selectedPid) { it.Selected = true; break; }
            }
            procList.EndUpdate();

            // endpoints of the selected process
            ProcView sel = null;
            foreach (ProcView v in list) if (v.Pid == selectedPid) { sel = v; break; }
            epList.BeginUpdate();
            if (sel == null) epList.Items.Clear();
            else
            {
                sel.Eps.Sort(delegate(EpView a, EpView b)
                {
                    int r = (b.Rx + b.Tx).CompareTo(a.Rx + a.Tx);
                    if (r == 0) r = (b.TotIn + b.TotOut).CompareTo(a.TotIn + a.TotOut);
                    if (r == 0) r = string.Compare(a.Remote, b.Remote, StringComparison.Ordinal);
                    return r;
                });
                while (epList.Items.Count > sel.Eps.Count) epList.Items.RemoveAt(epList.Items.Count - 1);
                for (int i = 0; i < sel.Eps.Count; i++)
                {
                    EpView e = sel.Eps[i];
                    ListViewItem it = i < epList.Items.Count ? epList.Items[i] : epList.Items.Add("");
                    it.Tag = e.Remote;
                    SetRow(it, new string[] { e.Proto, Host(e.Remote), e.State,
                        etw ? Fmt.Speed(e.Rx, s.Unit) : "-", etw ? Fmt.Speed(e.Tx, s.Unit) : "-",
                        etw ? Fmt.Size(e.TotIn) : "-", etw ? Fmt.Size(e.TotOut) : "-" });
                    Color fc = (etw ? e.Rx + e.Tx > 0 : e.State == "Established") ? Theme.Ink : Theme.Muted;
                    if (it.ForeColor != fc) it.ForeColor = fc;
                }
            }
            epList.EndUpdate();
        }

        // =================================================================== USAGE
        TabPage BuildUsage()
        {
            TabPage p = Page("Usage");
            TableLayoutPanel cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top; cards.Height = 100; cards.ColumnCount = 5;
            for (int i = 0; i < 5; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            uToday = new StatCard("Today"); uYesterday = new StatCard("Yesterday"); uPeriod = new StatCard("This month");
            u30 = new StatCard("Last 30 days"); uAll = new StatCard("All time");
            foreach (StatCard c in new StatCard[] { uToday, uYesterday, uPeriod, u30, uAll }) { c.Dock = DockStyle.Fill; cards.Controls.Add(c); }

            Panel limitPanel = new Panel(); limitPanel.Dock = DockStyle.Top; limitPanel.Height = 34; limitPanel.Padding = new Padding(6, 4, 6, 4);
            limitLabel = new Label(); limitLabel.Dock = DockStyle.Left; limitLabel.Width = 420; limitLabel.TextAlign = ContentAlignment.MiddleLeft;
            Button profileCapBtn = new Button(); profileCapBtn.Text = "Profile cap..."; profileCapBtn.Dock = DockStyle.Right; profileCapBtn.Width = 96;
            profileCapBtn.Click += delegate { PromptProfileCap(); };
            limitBar = new ProgressBar(); limitBar.Dock = DockStyle.Fill; limitBar.Maximum = 1000;
            limitPanel.Controls.Add(limitBar); limitPanel.Controls.Add(profileCapBtn); limitPanel.Controls.Add(limitLabel);

            Panel chartHost = new Panel(); chartHost.Dock = DockStyle.Fill; chartHost.Padding = new Padding(6);
            CardPanel chartCard = new CardPanel(); chartCard.Dock = DockStyle.Fill; chartCard.Padding = new Padding(8, 6, 8, 6);
            usageChart = new UsageChart(); usageChart.Usage = app.Usage; usageChart.S = app.S; usageChart.Dock = DockStyle.Fill;
            Label ct = new Label(); ct.Text = "Daily usage, last 30 days"; ct.Font = new Font("Segoe UI Semibold", 9.5f);
            ct.Dock = DockStyle.Top; ct.Height = 24; ct.Padding = new Padding(2, 2, 0, 0);
            chartCard.Controls.Add(usageChart); chartCard.Controls.Add(ct);
            chartHost.Controls.Add(chartCard);

            CardPanel listCard = new CardPanel(); listCard.Dock = DockStyle.Fill; listCard.Padding = new Padding(8, 6, 8, 6);
            Panel listHost = new Panel(); listHost.Dock = DockStyle.Fill; listHost.Padding = new Padding(0, 0, 4, 0);
            usageList = new DbListView(); usageList.Dock = DockStyle.Fill; usageList.MultiSelect = false;
            Col(usageList, "Date", 130, HorizontalAlignment.Left);
            Col(usageList, "Downloaded", 95, HorizontalAlignment.Right);
            Col(usageList, "Uploaded", 95, HorizontalAlignment.Right);
            Col(usageList, "Total", 95, HorizontalAlignment.Right);
            usageList.SelectedIndexChanged += delegate
            {
                if (usageList.SelectedItems.Count == 0 || !(usageList.SelectedItems[0].Tag is DateTime)) return;
                DateTime day = (DateTime)usageList.SelectedItems[0].Tag;
                rangeLoading = true; rFrom.Value = day; rTo.Value = day; rangeLoading = false;
                UpdateRange();
            };
            FlowLayoutPanel btns = new FlowLayoutPanel(); btns.Dock = DockStyle.Bottom; btns.Height = 32; btns.FlowDirection = FlowDirection.RightToLeft;
            Button reset = new Button(); reset.Text = "Reset usage data..."; reset.AutoSize = true;
            reset.Click += delegate
            {
                if (MessageBox.Show("Delete all recorded data usage history?", TrayApp.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                { app.Usage.Reset(); lastUsageRefresh = DateTime.MinValue; TickUsage(); }
            };
            Button export = new Button(); export.Text = "Export CSV..."; export.AutoSize = true;
            export.Click += delegate
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Filter = "CSV files|*.csv"; d.FileName = "network-usage-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv";
                    if (d.ShowDialog(this) == DialogResult.OK)
                        try { app.Usage.ExportCsv(d.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
                }
            };
            btns.Controls.Add(reset); btns.Controls.Add(export);
            listCard.Controls.Add(usageList); listCard.Controls.Add(btns);
            listHost.Controls.Add(listCard);

            usageTopApps = new UsageTopAppsPanel(); usageTopApps.App = app; usageTopApps.Dock = DockStyle.Fill;
            Panel topAppsHost = new Panel(); topAppsHost.Dock = DockStyle.Fill; topAppsHost.Padding = new Padding(4, 0, 0, 0);
            topAppsHost.Controls.Add(usageTopApps);

            TableLayoutPanel bottomHost = new TableLayoutPanel();
            bottomHost.Dock = DockStyle.Bottom; bottomHost.Height = 204; bottomHost.Padding = new Padding(6, 2, 6, 6);
            bottomHost.ColumnCount = 2; bottomHost.RowCount = 1;
            bottomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bottomHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bottomHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottomHost.Controls.Add(listHost, 0, 0);
            bottomHost.Controls.Add(topAppsHost, 1, 0);

            p.Controls.Add(chartHost); p.Controls.Add(bottomHost); p.Controls.Add(BuildRangePanel()); p.Controls.Add(limitPanel); p.Controls.Add(cards);
            return p;
        }

        // ----- date range report -----
        DateTimePicker rFrom, rTo;
        Label rSummary;
        UsageTopAppsPanel usageTopApps;
        bool rangeLoading;

        Control BuildRangePanel()
        {
            Panel host = new Panel(); host.Dock = DockStyle.Top; host.Height = 114; host.Padding = new Padding(6, 4, 6, 4);
            CardPanel card = new CardPanel(); card.Dock = DockStyle.Fill; card.Padding = new Padding(12, 8, 12, 8);

            FlowLayoutPanel row = new FlowLayoutPanel(); row.Dock = DockStyle.Top; row.Height = 62; row.WrapContents = true;
            Label t = new Label(); t.Text = "Date range"; t.Font = new Font("Segoe UI Semibold", 9.5f); t.AutoSize = true; t.Margin = new Padding(0, 7, 12, 0);
            Label lf = new Label(); lf.Text = "From"; lf.AutoSize = true; lf.Margin = new Padding(0, 7, 4, 0);
            rFrom = new DateTimePicker(); rFrom.Format = DateTimePickerFormat.Short; rFrom.Width = 110; rFrom.Margin = new Padding(0, 3, 10, 0);
            Label lt = new Label(); lt.Text = "to"; lt.AutoSize = true; lt.Margin = new Padding(0, 7, 4, 0);
            rTo = new DateTimePicker(); rTo.Format = DateTimePickerFormat.Short; rTo.Width = 110; rTo.Margin = new Padding(0, 3, 14, 0);
            rFrom.ValueChanged += delegate { if (!rangeLoading) UpdateRange(); };
            rTo.ValueChanged += delegate { if (!rangeLoading) UpdateRange(); };
            row.Controls.AddRange(new Control[] { t, lf, rFrom, lt, rTo });

            DateTime today = DateTime.Today;
            DateTime monthStart = new DateTime(today.Year, today.Month, 1);
            AddPreset(row, "Today", today, today);
            AddPreset(row, "7 days", today.AddDays(-6), today);
            AddPreset(row, "30 days", today.AddDays(-29), today);
            AddPreset(row, "This month", monthStart, today);
            AddPreset(row, "Last month", monthStart.AddMonths(-1), monthStart.AddDays(-1));

            Button exp = new Button(); exp.Text = "Export range..."; exp.AutoSize = true; exp.Margin = new Padding(10, 2, 4, 0);
            exp.Click += delegate { ExportRange(); };
            Button logs = new Button(); logs.Text = "Open log folder"; logs.AutoSize = true; logs.Margin = new Padding(4, 2, 0, 0);
            logs.Click += delegate { OpenLogFolder(); };
            row.Controls.Add(exp); row.Controls.Add(logs);

            rSummary = new Label(); rSummary.Dock = DockStyle.Top; rSummary.Height = 28; rSummary.TextAlign = ContentAlignment.MiddleLeft;
            rSummary.Font = new Font("Segoe UI Semibold", 9.5f); rSummary.ForeColor = Theme.Ink; rSummary.AutoEllipsis = true;

            card.Controls.Add(rSummary); card.Controls.Add(row);
            host.Controls.Add(card);

            rangeLoading = true;
            rFrom.Value = monthStart; rTo.Value = today;
            rangeLoading = false;
            return host;
        }

        void AddPreset(FlowLayoutPanel row, string text, DateTime from, DateTime to)
        {
            LinkLabel l = new LinkLabel(); l.Text = text; l.AutoSize = true; l.Margin = new Padding(0, 7, 8, 0);
            l.LinkColor = Theme.Accent; l.ActiveLinkColor = Theme.Accent; l.LinkBehavior = LinkBehavior.HoverUnderline;
            l.LinkClicked += delegate
            {
                rangeLoading = true; rFrom.Value = from; rTo.Value = to; rangeLoading = false;
                UpdateRange();
            };
            row.Controls.Add(l);
        }

        void RangeDates(out DateTime from, out DateTime to)
        {
            from = rFrom.Value.Date; to = rTo.Value.Date;
            if (to < from) { DateTime x = from; from = to; to = x; }
        }

        void UpdateRange()
        {
            DateTime from, to;
            RangeDates(out from, out to);
            UsageStore u = app.Usage;
            long[] tot = u.Range(from, to);
            int days = (int)(to - from).TotalDays + 1;
            DateTime busiest = from; long busiestV = -1;
            for (DateTime d = from; d <= to; d = d.AddDays(1))
            {
                long[] v = u.Day(d);
                if (v[0] + v[1] > busiestV) { busiestV = v[0] + v[1]; busiest = d; }
            }
            string span = from == to ? from.ToString("ddd d MMM yyyy") : from.ToString("d MMM yyyy") + " – " + to.ToString("d MMM yyyy");
            rSummary.Text = span + "  (" + days + (days == 1 ? " day" : " days") + "):   ↓ " + Fmt.Size(tot[0]) + "   ↑ " + Fmt.Size(tot[1]) +
                "   = " + Fmt.Size(tot[0] + tot[1]) +
                (days > 1 ? "   ·  avg " + Fmt.Size((tot[0] + tot[1]) / (double)days) + "/day" +
                            (busiestV > 0 ? "   ·  busiest " + busiest.ToString("ddd d MMM") + " (" + Fmt.Size(busiestV) + ")" : "") : "");

            Dictionary<string, long[]> apps = app.Log.AppTotals(from, to);
            List<UsageAppRow> rows = new List<UsageAppRow>();
            foreach (KeyValuePair<string, long[]> kv in apps)
            {
                UsageAppRow ur = new UsageAppRow();
                ur.Name = kv.Key; ur.Down = kv.Value[0]; ur.Up = kv.Value[1];
                if (ur.Total > 0) rows.Add(ur);
            }
            rows.Sort(delegate(UsageAppRow a, UsageAppRow b) { return b.Total.CompareTo(a.Total); });
            usageTopApps.SetData(span, tot[0] + tot[1], rows);
        }

        void ExportRange()
        {
            DateTime from, to;
            RangeDates(out from, out to);
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV files|*.csv";
                d.InitialDirectory = Settings.LogDir;
                d.FileName = "usage-" + from.ToString("yyyy-MM-dd") + "-to-" + to.ToString("yyyy-MM-dd") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder("Date,Downloaded bytes,Uploaded bytes,Total bytes,Downloaded MB,Uploaded MB,Total MB\r\n");
                    long[] sum = new long[2];
                    for (DateTime day = from; day <= to; day = day.AddDays(1))
                    {
                        long[] v = app.Usage.Day(day);
                        sum[0] += v[0]; sum[1] += v[1];
                        sb.AppendFormat(inv, "{0:yyyy-MM-dd},{1},{2},{3},{4:0.00},{5:0.00},{6:0.00}\r\n", day, v[0], v[1], v[0] + v[1], v[0] / 1048576.0, v[1] / 1048576.0, (v[0] + v[1]) / 1048576.0);
                    }
                    sb.AppendFormat(inv, "TOTAL,{0},{1},{2},{3:0.00},{4:0.00},{5:0.00}\r\n", sum[0], sum[1], sum[0] + sum[1], sum[0] / 1048576.0, sum[1] / 1048576.0, (sum[0] + sum[1]) / 1048576.0);
                    Dictionary<string, long[]> apps = app.Log.AppTotals(from, to);
                    if (apps.Count > 0)
                    {
                        sb.Append("\r\nApp,Downloaded bytes,Uploaded bytes,Total bytes,Downloaded MB,Uploaded MB,Total MB\r\n");
                        List<KeyValuePair<string, long[]>> l = new List<KeyValuePair<string, long[]>>(apps);
                        l.Sort(delegate(KeyValuePair<string, long[]> a, KeyValuePair<string, long[]> b)
                        { return (b.Value[0] + b.Value[1]).CompareTo(a.Value[0] + a.Value[1]); });
                        foreach (KeyValuePair<string, long[]> kv in l)
                            sb.AppendFormat(inv, "{0},{1},{2},{3},{4:0.00},{5:0.00},{6:0.00}\r\n", kv.Key, kv.Value[0], kv.Value[1], kv.Value[0] + kv.Value[1],
                                kv.Value[0] / 1048576.0, kv.Value[1] / 1048576.0, (kv.Value[0] + kv.Value[1]) / 1048576.0);
                    }
                    File.WriteAllText(d.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
            }
        }

        void OpenLogFolder()
        {
            try
            {
                app.Log.Flush(); app.Usage.Save();
                Directory.CreateDirectory(Settings.LogDir);
                UsageLog.WriteReadme();
                Process.Start("explorer.exe", "\"" + Settings.LogDir + "\"");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
        }

        static void SetUsageCard(StatCard c, long[] v)
        {
            c.Set(Fmt.Size(v[0] + v[1]), "↓ " + Fmt.Size(v[0]) + "   ↑ " + Fmt.Size(v[1]));
        }

        void TickUsage()
        {
            Settings s = app.S;
            UsageStore u = app.Usage;
            DateTime today = DateTime.Today;
            DateTime ps = UsageStore.PeriodStart(s.MonthStartDay);
            uPeriod.Title(s.MonthStartDay == 1 ? "This month" : "Since " + ps.ToString("d MMM"));
            SetUsageCard(uToday, u.Day(today));
            SetUsageCard(uYesterday, u.Day(today.AddDays(-1)));
            long[] period = u.Range(ps, today);
            SetUsageCard(uPeriod, period);
            SetUsageCard(u30, u.Range(today.AddDays(-29), today));
            SetUsageCard(uAll, u.Range(DateTime.MinValue, today));

            if (s.MonthlyLimitGB > 0)
            {
                double limit = s.MonthlyLimitGB * 1024 * 1024 * 1024, used = period[0] + period[1];
                limitBar.Visible = true;
                limitBar.Value = (int)Math.Min(1000, used / limit * 1000);
                limitLabel.Text = "Monthly limit: " + Fmt.Size(used) + " of " + s.MonthlyLimitGB + " GB (" + (used / limit * 100).ToString("0.0") + "%)";
            }
            else
            {
                double pCap = u.GetProfileCapGB(app.CurrentNetProfile);
                long[] pu = u.ProfileRange(app.CurrentNetProfile, ps, today);
                double pUsed = pu[0] + pu[1];
                if (pCap > 0)
                {
                    double limit = pCap * 1024 * 1024 * 1024;
                    limitBar.Visible = true;
                    limitBar.Value = (int)Math.Min(1000, pUsed / limit * 1000);
                    limitLabel.Text = app.CurrentNetProfile + ": " + Fmt.Size(pUsed) + " / " + pCap + " GB (" + (pUsed / limit * 100).ToString("0.0") + "%)";
                }
                else
                {
                    limitBar.Visible = false;
                    limitLabel.Text = app.CurrentNetProfile + " this cycle: " + Fmt.Size(pUsed) + " (no monthly limit set)";
                }
            }

            usageChart.Invalidate();
            if ((DateTime.UtcNow - lastUsageRefresh).TotalSeconds >= 5)
            {
                lastUsageRefresh = DateTime.UtcNow;
                UpdateRange();
                usageList.BeginUpdate();
                usageList.Items.Clear();
                foreach (KeyValuePair<DateTime, long[]> kv in u.All())
                {
                    ListViewItem it = new ListViewItem(new string[] { kv.Key.ToString("ddd, d MMM yyyy"), Fmt.Size(kv.Value[0]),
                        Fmt.Size(kv.Value[1]), Fmt.Size(kv.Value[0] + kv.Value[1]) });
                    it.Tag = kv.Key;
                    usageList.Items.Add(it);
                }
                usageList.EndUpdate();
            }
        }

        void PromptProfileCap()
        {
            string prof = app.CurrentNetProfile;
            double cur = app.Usage.GetProfileCapGB(prof);
            using (Form f = new Form())
            {
                f.Text = "Network Profile Monthly Cap";
                f.Icon = GetAppIcon();
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.Size = new Size(360, 170);
                f.BackColor = Theme.Bg; f.ForeColor = Theme.Ink; f.Font = uiFont;
                Label l = new Label(); l.Text = "Monthly cap in GB for \"" + prof + "\" (0 = off):";
                l.AutoSize = false; l.SetBounds(16, 16, 320, 32);
                NumericUpDown n = new NumericUpDown(); n.Minimum = 0; n.Maximum = 10000; n.DecimalPlaces = 1;
                n.Value = (decimal)Math.Max(0, Math.Min(10000, cur)); n.SetBounds(16, 52, 120, 26);
                Button ok = new Button(); ok.Text = "Save"; ok.SetBounds(172, 92, 76, 28);
                Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(254, 92, 76, 28);
                ok.Click += delegate { app.Usage.SetProfileCapGB(prof, (double)n.Value); lastUsageRefresh = DateTime.MinValue; TickUsage(); f.Close(); };
                cancel.Click += delegate { f.Close(); };
                f.Controls.AddRange(new Control[] { l, n, ok, cancel });
                ApplyThemeTo(f);
                f.ShowDialog(this);
            }
        }

        // =================================================================== SYSTEM
        UtilCard cpuCard, gpuCard, memCard;
        SpecList specs, netSpecs, osSpecs;
        Panel specHost;
        TableLayoutPanel sysBottomRow;
        Button btnCheckIp, btnCopyIp, btnDnsBench;
        SystemInfo sysInfo;
        PublicIpInfo publicIp;
        string lastLocalIp = "";
        DateTime lastNetRefresh = DateTime.MinValue;
        bool sysInfoRequested, ipLookupBusy;

        TabPage BuildSystem()
        {
            TabPage p = Page("System");
            TableLayoutPanel cards = new TableLayoutPanel();
            cards.Dock = DockStyle.Top; cards.Height = 194; cards.ColumnCount = 3; cards.RowCount = 1;
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            SystemMonitor m = app.Sys;
            cpuCard = new UtilCard(); cpuCard.Title = "CPU"; cpuCard.Kind = "cpu"; cpuCard.Accent = Color.FromArgb(59, 130, 246); cpuCard.History = delegate(int ago) { return m.CpuAt(ago); };
            gpuCard = new UtilCard(); gpuCard.Title = "GPU"; gpuCard.Kind = "gpu"; gpuCard.Accent = Color.FromArgb(34, 160, 90);
            memCard = new UtilCard(); memCard.Title = "Memory"; memCard.Kind = "ram"; memCard.Accent = Color.FromArgb(150, 80, 230); memCard.History = delegate(int ago) { return m.MemAt(ago); };
            memCard.Brand = new BrandStyle("ram", "RAM", Color.FromArgb(150, 90, 235), Color.FromArgb(90, 40, 170), Color.White);
            foreach (UtilCard c in new UtilCard[] { cpuCard, gpuCard, memCard }) { c.Dock = DockStyle.Fill; cards.Controls.Add(c); }

            specHost = new Panel(); specHost.Dock = DockStyle.Fill; specHost.AutoScroll = true; specHost.Padding = new Padding(6, 2, 6, 6);
            specs = new SpecList(); specs.Heading = "Hardware"; specs.Loading = "Reading hardware specifications..."; specs.Dock = DockStyle.Top; specs.Height = 120;
            Panel gap = new Panel(); gap.Dock = DockStyle.Top; gap.Height = 8;

            sysBottomRow = new TableLayoutPanel();
            sysBottomRow.Dock = DockStyle.Top; sysBottomRow.Height = 180; sysBottomRow.ColumnCount = 2; sysBottomRow.RowCount = 1;
            sysBottomRow.Margin = Padding.Empty; sysBottomRow.Padding = Padding.Empty;
            sysBottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sysBottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sysBottomRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            netSpecs = new SpecList(); netSpecs.Heading = "Network & ISP"; netSpecs.Loading = "Reading network information..."; netSpecs.Dock = DockStyle.Fill; netSpecs.Margin = new Padding(0, 0, 4, 0);
            osSpecs = new SpecList(); osSpecs.Heading = "Operating System"; osSpecs.Loading = "Reading Windows details..."; osSpecs.Dock = DockStyle.Fill; osSpecs.Margin = new Padding(4, 0, 0, 0);
            sysBottomRow.Controls.Add(netSpecs, 0, 0);
            sysBottomRow.Controls.Add(osSpecs, 1, 0);

            FlowLayoutPanel bar = new FlowLayoutPanel(); bar.Dock = DockStyle.Top; bar.Height = 32; bar.FlowDirection = FlowDirection.RightToLeft;
            Button copy = new Button(); copy.Text = "Copy system info"; copy.AutoSize = true;
            copy.Click += delegate { if (sysInfo != null) try { Clipboard.SetText(BuildSystemClipboardText()); copy.Text = "Copied ✔"; } catch { } };
            btnCopyIp = new Button(); btnCopyIp.Text = "Copy IP"; btnCopyIp.AutoSize = true;
            btnCopyIp.Click += delegate
            {
                string ip = (publicIp != null && publicIp.Ok) ? publicIp.Ip : lastLocalIp;
                if (ip.Length > 0) try { Clipboard.SetText(ip); btnCopyIp.Text = "Copied ✔"; } catch { }
            };
            btnCheckIp = new Button(); btnCheckIp.Text = "Check Public IP & ISP"; btnCheckIp.AutoSize = true;
            btnCheckIp.Click += delegate { RequestPublicIp(); };
            btnDnsBench = new Button(); btnDnsBench.Text = "DNS benchmark"; btnDnsBench.AutoSize = true;
            btnDnsBench.Click += delegate { RunDnsBenchmarkDialog(); };
            bar.Controls.Add(copy); bar.Controls.Add(btnCopyIp); bar.Controls.Add(btnDnsBench); bar.Controls.Add(btnCheckIp);

            specHost.Controls.Add(sysBottomRow); specHost.Controls.Add(gap); specHost.Controls.Add(specs); specHost.Controls.Add(bar);
            p.Controls.Add(specHost); p.Controls.Add(cards);
            return p;
        }

        string BuildSystemClipboardText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (netSpecs != null && netSpecs.Rows.Count > 0)
            {
                sb.Append("Network & ISP (NetSpeed Boss)\r\n");
                foreach (SpecRow r in netSpecs.Rows) sb.Append(r.Label).Append(": ").Append(r.Value.Replace("   \u00B7   ", ", ")).Append("\r\n");
                sb.Append("\r\n");
            }
            if (sysInfo != null) sb.Append(sysInfo.ToText());
            return sb.ToString();
        }

        void RefreshNetworkCard(bool force)
        {
            if (!force && (DateTime.UtcNow - lastNetRefresh).TotalSeconds < 5) return;
            lastNetRefresh = DateTime.UtcNow;
            List<AdapterInfo> nets = app.Net.Adapters;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string localIp;
                List<SpecRow> rows = NetworkInfo.GatherLocalRows(nets, out localIp);
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) ApplyNetworkCard(rows, localIp); }); } catch { }
            });
        }

        void ApplyNetworkCard(List<SpecRow> localRows, string localIp)
        {
            lastLocalIp = localIp;
            List<SpecRow> rows = new List<SpecRow>(localRows);
            if (ipLookupBusy)
            {
                rows.Add(new SpecRow("Public IP", "Looking up public IP & ISP...", null));
            }
            else if (publicIp != null && publicIp.Ok)
            {
                rows.Add(new SpecRow("Public IP", publicIp.Ip + "   \u00B7   checked " + publicIp.CheckedAt.ToString("t"), null));
                if (publicIp.Isp.Length > 0) rows.Add(new SpecRow("ISP", publicIp.Isp, null));
                if (publicIp.Location.Length > 0) rows.Add(new SpecRow("Location", publicIp.Location, null));
            }
            else if (publicIp != null && publicIp.Error.Length > 0)
            {
                rows.Add(new SpecRow("Public IP", "Lookup failed (" + publicIp.Error + ")", null));
            }
            else
            {
                rows.Add(new SpecRow("Public IP & ISP", "Checked automatically when dashboard opens", null));
            }
            netSpecs.Rows = rows;
            SyncBottomRowHeight();
            netSpecs.Invalidate();
        }

        void SyncBottomRowHeight()
        {
            if (sysBottomRow == null || netSpecs == null || osSpecs == null) return;
            sysBottomRow.Height = Math.Max(netSpecs.ContentHeight, osSpecs.ContentHeight);
        }

        void RequestPublicIp()
        {
            if (ipLookupBusy) return;
            ipLookupBusy = true;
            btnCheckIp.Enabled = false;
            btnCheckIp.Text = "Checking...";
            btnCopyIp.Text = "Copy IP";
            RefreshNetworkCard(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                PublicIpInfo info = NetworkInfo.LookupPublicIp();
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            ipLookupBusy = false;
                            publicIp = info;
                            btnCheckIp.Enabled = true;
                            btnCheckIp.Text = info.Ok ? "Refresh IP & ISP" : "Check Public IP & ISP";
                            RefreshNetworkCard(true);
                        });
                }
                catch { }
            });
        }

        void RequestSystemInfo()
        {
            if (sysInfoRequested) return;
            sysInfoRequested = true;
            app.Sys.SampleGpu();                                     // enumerates GPUs (DXGI) on the UI thread
            List<GpuInfo> gpus = app.Sys.Gpus;
            List<AdapterInfo> nets = app.Net.Adapters;
            ThreadPool.QueueUserWorkItem(delegate
            {
                SystemInfo si;
                try { si = SystemInfo.Gather(gpus, nets); } catch { si = new SystemInfo(); }
                try { BeginInvoke((MethodInvoker)delegate { ApplySystemInfo(si); }); } catch { }
            });
        }

        void ApplySystemInfo(SystemInfo si)
        {
            sysInfo = si;
            app.Sys.BaseGhz = si.BaseGhz;
            cpuCard.Brand = si.CpuBrand; cpuCard.Series = si.CpuSeries;
            cpuCard.Accent = si.CpuBrand != null && si.CpuBrand.Key != "generic" ? si.CpuBrand.Back : cpuCard.Accent;
            if (si.MemSummary.Length > 0)
            {
                string[] parts = si.MemSummary.Split(' ');
                memCard.Brand = new BrandStyle("ram", parts.Length > 2 ? parts[2] : "RAM", Color.FromArgb(150, 90, 235), Color.FromArgb(90, 40, 170), Color.White);
                memCard.Series = parts[0] + " " + parts[1];
            }
            specs.Rows = new List<SpecRow>(si.HardwareRows);
            specs.Height = specs.ContentHeight;
            List<SpecRow> osRows = new List<SpecRow>(si.OsRows);
            osRows.Add(new SpecRow("Uptime", SystemInfo.Uptime(), null));
            osSpecs.Rows = osRows;
            SyncBottomRowHeight();
            TickSystem();
        }

        void TickSystem()
        {
            RequestSystemInfo();
            RefreshNetworkCard(false);
            SystemMonitor m = app.Sys;
            m.SampleGpu();

            cpuCard.Value = m.Cpu.ToString("0") + "%";
            cpuCard.Line1 = (sysInfo != null ? sysInfo.CpuName : "");
            cpuCard.Line2 = (m.CpuGhz > 0 ? m.CpuGhz.ToString("0.00") + " GHz now" : "") +
                            (sysInfo != null && sysInfo.Cores > 0 ? (m.CpuGhz > 0 ? "   ·   " : "") + sysInfo.Cores + " cores / " + sysInfo.Threads + " threads" : "");

            if (m.Gpus.Count > 0)
            {
                GpuInfo g = m.Gpus[0];
                gpuCard.Brand = g.Brand; gpuCard.Series = SystemInfo.Series(g.Name);
                if (g.Brand != null && g.Brand.Key != "generic") gpuCard.Accent = g.Brand.Key == "amd" ? Color.FromArgb(220, 40, 40) : g.Brand.Back;
                gpuCard.History = delegate(int ago) { return SystemMonitor.HistAt(g.Hist, g.Pos, ago); };
                gpuCard.Value = g.Util < 0 ? "n/a" : g.Util.ToString("0") + "%";
                gpuCard.Line1 = g.Name;
                string vram = g.Vram >= 512L * 1048576
                    ? "VRAM " + Fmt.Size(g.DedicatedUsed) + " / " + Fmt.Size(g.Vram)
                    : "Shared memory " + Fmt.Size(g.SharedUsed);
                for (int i = 1; i < m.Gpus.Count; i++)
                    vram += "   ·   GPU " + (i + 1) + ": " + (m.Gpus[i].Util < 0 ? "n/a" : m.Gpus[i].Util.ToString("0") + "%");
                gpuCard.Line2 = vram;
            }
            else { gpuCard.Value = "n/a"; gpuCard.Line1 = "No GPU usage information available"; gpuCard.Line2 = ""; }

            memCard.Value = m.Mem.ToString("0") + "%";
            memCard.Line1 = Fmt.Size(m.MemUsed) + " used of " + Fmt.Size(m.MemTotal);
            memCard.Line2 = (sysInfo != null && sysInfo.MemSummary.Length > 0 ? sysInfo.MemSummary + "   ·   " : "") +
                            "committed " + Fmt.Size(m.CommitUsed) + " / " + Fmt.Size(m.CommitTotal);

            cpuCard.Invalidate(); gpuCard.Invalidate(); memCard.Invalidate();
            if (osSpecs != null && osSpecs.Rows.Count > 0)
            {
                SpecRow last = osSpecs.Rows[osSpecs.Rows.Count - 1];
                if (last.Label == "Uptime") { last.Value = SystemInfo.Uptime(); osSpecs.Invalidate(); }
            }
        }

        // =================================================================== GAMING HUD
        TabPage BuildGamingHud()
        {
            TabPage p = Page("Gaming HUD");
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill; layout.ColumnCount = 2; layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Panel leftScroll = new Panel(); leftScroll.Dock = DockStyle.Fill; leftScroll.AutoScroll = true; leftScroll.Padding = new Padding(4, 4, 8, 4);
            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true; t.ColumnCount = 2; t.Location = new Point(4, 2);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Action<string> header = delegate(string h)
            {
                Label l = new Label(); l.Text = h; l.Font = new Font("Segoe UI Semibold", 10f); l.AutoSize = true; l.Margin = new Padding(0, 12, 0, 4);
                t.Controls.Add(l); t.SetColumnSpan(l, 2);
            };
            Action<string, Control> row = delegate(string label, Control c)
            {
                Label l = new Label(); l.Text = label; l.AutoSize = true; l.Margin = new Padding(0, 6, 8, 2);
                c.Margin = new Padding(0, 2, 0, 2);
                t.Controls.Add(l); t.Controls.Add(c);
            };
            Func<string, CheckBox> chk = delegate(string txt)
            {
                CheckBox c = new CheckBox(); c.Text = txt; c.AutoSize = true;
                c.CheckedChanged += delegate { SaveUi(); };
                return c;
            };
            Func<int, int, NumericUpDown> num = delegate(int min, int max)
            {
                NumericUpDown n = new NumericUpDown(); n.Minimum = min; n.Maximum = max; n.Width = 58;
                n.ValueChanged += delegate { SaveUi(); };
                return n;
            };

            header("Overlay & auto-detection");
            sGameAuto = chk("Auto-show for all Windows GPU games"); row("", sGameAuto);
            sGameShow = chk("Pin / preview Gaming HUD overlay now"); row("", sGameShow);
            sGameClickThrough = chk("Click-through (ignore mouse clicks)"); row("", sGameClickThrough);
            sGameStrip = chk("Compact horizontal top-strip layout"); row("", sGameStrip);
            sGameHotkeys = chk("Global hotkeys (Ctrl+Shift+H HUD, +L lock, +M mini meter)"); row("", sGameHotkeys);
            sGameLogSess = chk("Save finished game session summaries (game-sessions.csv)"); row("", sGameLogSess);

            FlowLayoutPanel gpos = new FlowLayoutPanel(); gpos.AutoSize = true; gpos.WrapContents = false;
            sGameCorner = new ComboBox(); sGameCorner.DropDownStyle = ComboBoxStyle.DropDownList; sGameCorner.Width = 126;
            sGameCorner.Items.AddRange(new object[] { "Top-Left", "Top-Right", "Bottom-Left", "Bottom-Right" });
            sGameCorner.SelectedIndexChanged += delegate { if (!loadingSettings) { app.S.GameHudX = int.MinValue; SaveUi(); } };
            Button resetHudPos = new Button(); resetHudPos.Text = "Reset position"; resetHudPos.AutoSize = true;
            resetHudPos.Click += delegate { app.S.GameHudX = int.MinValue; app.Changed(); };
            gpos.Controls.Add(sGameCorner); gpos.Controls.Add(resetHudPos);
            row("Screen corner", gpos);

            header("Transparency & visual theme");
            FlowLayoutPanel opRow = new FlowLayoutPanel(); opRow.AutoSize = true; opRow.WrapContents = false;
            sGameOpacity = new TrackBar(); sGameOpacity.Minimum = 0; sGameOpacity.Maximum = 99; sGameOpacity.TickFrequency = 10; sGameOpacity.Width = 180;
            sGameOpacity.ValueChanged += delegate
            {
                if (lblGameOpacity != null) lblGameOpacity.Text = sGameOpacity.Value + "%";
                SaveUi();
            };
            lblGameOpacity = new Label(); lblGameOpacity.AutoSize = true; lblGameOpacity.Margin = new Padding(6, 8, 0, 0);
            opRow.Controls.Add(sGameOpacity); opRow.Controls.Add(lblGameOpacity);
            row("Background opacity", opRow);

            FlowLayoutPanel preRow = new FlowLayoutPanel(); preRow.AutoSize = true; preRow.WrapContents = false;
            sGamePreset = new ComboBox(); sGamePreset.DropDownStyle = ComboBoxStyle.DropDownList; sGamePreset.Width = 160;
            sGamePreset.Items.AddRange(new object[] { "Metal Dark", "Obsidian Neon", "Crimson Tactical", "Emerald HUD", "Custom" });
            sGamePreset.SelectedIndexChanged += delegate
            {
                if (loadingSettings || sGamePreset.SelectedIndex < 0 || sGamePreset.SelectedIndex >= 4) return;
                ApplyHudPreset(sGamePreset.SelectedIndex);
            };
            Button resetColors = new Button(); resetColors.Text = "Reset colors"; resetColors.AutoSize = true;
            resetColors.Click += delegate { ApplyHudPreset(0); };
            preRow.Controls.Add(sGamePreset); preRow.Controls.Add(resetColors);
            row("Color preset", preRow);

            FlowLayoutPanel hudCols = new FlowLayoutPanel(); hudCols.AutoSize = true; hudCols.MaximumSize = new Size(288, 0); hudCols.WrapContents = true;
            bHudBg = HudColorButton("Background"); bHudBorder = HudColorButton("Border");
            bHudText = HudColorButton("Text"); bHudFps = HudColorButton("FPS");
            bHudGpu = HudColorButton("GPU"); bHudCpu = HudColorButton("CPU");
            bHudMem = HudColorButton("Memory"); bHudNet = HudColorButton("Network");
            hudCols.Controls.AddRange(new Control[] { bHudBg, bHudBorder, bHudText, bHudFps, bHudGpu, bHudCpu, bHudMem, bHudNet });
            row("Custom colors", hudCols);

            header("RAG status thresholds (Red / Amber / Green)");
            sGameRag = chk("Enable RAG colors (FPS, Temp, Load & Ping)"); row("", sGameRag);
            nFpsG = num(15, 240); nFpsA = num(10, 200);
            row("FPS (Green >= / Amber >=)", PairRow(nFpsG, "/", nFpsA));
            nTempG = num(40, 95); nTempA = num(50, 105);
            row("Temp °C (Green < / Amber <)", PairRow(nTempG, "/", nTempA));
            nPingG = num(10, 200); nPingA = num(20, 400);
            row("Ping ms (Green < / Amber <)", PairRow(nPingG, "/", nPingA));
            nLoadA = num(40, 95); nLoadR = num(50, 99);
            row("Load % (Amber >= / Red >=)", PairRow(nLoadA, "/", nLoadR));

            header("Visible metrics");
            FlowLayoutPanel vis = new FlowLayoutPanel(); vis.AutoSize = true; vis.MaximumSize = new Size(288, 0); vis.WrapContents = true;
            sHudFps = chk("FPS & frame time"); sHudGpu = chk("GPU temp / fan / W");
            sHudCpu = chk("CPU & cores"); sHudMem = chk("Memory & VRAM");
            sHudNet = chk("Net speed & ping"); sHudGraph = chk("Waveform graph");
            foreach (CheckBox c in new CheckBox[] { sHudFps, sHudGpu, sHudCpu, sHudMem, sHudNet, sHudGraph })
            {
                c.Margin = new Padding(0, 2, 12, 2);
                vis.Controls.Add(c);
            }
            row("Sections", vis);

            leftScroll.Controls.Add(t);

            Panel rightCol = new Panel(); rightCol.Dock = DockStyle.Fill; rightCol.Padding = new Padding(4);
            CardPanel prevCard = new CardPanel(); prevCard.Dock = DockStyle.Top; prevCard.Height = 312; prevCard.Padding = new Padding(10, 8, 10, 10);
            Label pl = new Label(); pl.Text = "Live HUD preview (transparency & RAG colors)"; pl.Font = new Font("Segoe UI Semibold", 9.5f); pl.Dock = DockStyle.Top; pl.Height = 24;
            hudPreview = new GameHudPreview(); hudPreview.App = app; hudPreview.Dock = DockStyle.Fill;
            prevCard.Controls.Add(hudPreview); prevCard.Controls.Add(pl);

            Panel diagGap = new Panel(); diagGap.Dock = DockStyle.Top; diagGap.Height = 8;
            gameDiagList = new SpecList(); gameDiagList.Heading = "Game & graphics detection"; gameDiagList.Loading = "Sampling GPU and foreground state...";
            gameDiagList.Dock = DockStyle.Fill;

            rightCol.Controls.Add(gameDiagList);
            rightCol.Controls.Add(diagGap);
            rightCol.Controls.Add(prevCard);

            layout.Controls.Add(leftScroll, 0, 0);
            layout.Controls.Add(rightCol, 1, 0);
            p.Controls.Add(layout);
            return p;
        }

        static FlowLayoutPanel PairRow(Control a, string sep, Control b)
        {
            FlowLayoutPanel fp = new FlowLayoutPanel(); fp.AutoSize = true; fp.WrapContents = false;
            Label l = new Label(); l.Text = sep; l.AutoSize = true; l.Margin = new Padding(6, 6, 6, 0);
            fp.Controls.Add(a); fp.Controls.Add(l); fp.Controls.Add(b);
            return fp;
        }

        Button HudColorButton(string text)
        {
            Button b = new Button(); b.Text = text; b.AutoSize = true; b.Margin = new Padding(0, 0, 6, 4);
            b.TextImageRelation = TextImageRelation.ImageBeforeText; b.ImageAlign = ContentAlignment.MiddleLeft;
            b.Click += delegate
            {
                using (ColorDialog d = new ColorDialog())
                {
                    d.FullOpen = true;
                    d.Color = GetColor(b);
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    if (b == bHudBg) app.S.GameHudBackColor = d.Color;
                    else if (b == bHudBorder) app.S.GameHudBorderColor = d.Color;
                    else if (b == bHudText) app.S.GameHudTextColor = d.Color;
                    else if (b == bHudFps) app.S.GameHudFpsColor = d.Color;
                    else if (b == bHudGpu) app.S.GameHudGpuColor = d.Color;
                    else if (b == bHudCpu) app.S.GameHudCpuColor = d.Color;
                    else if (b == bHudMem) app.S.GameHudMemColor = d.Color;
                    else if (b == bHudNet) app.S.GameHudNetColor = d.Color;
                    if (sGamePreset != null) { loadingSettings = true; sGamePreset.SelectedIndex = 4; loadingSettings = false; }
                    app.Changed();
                    if (hudPreview != null) hudPreview.Invalidate();
                }
            };
            return b;
        }

        void ApplyHudPreset(int idx)
        {
            Settings s = app.S;
            switch (idx)
            {
                case 1: // Obsidian Neon
                    s.GameHudBackColor = Color.FromArgb(10, 12, 18); s.GameHudBorderColor = Color.FromArgb(88, 101, 242);
                    s.GameHudTextColor = Color.FromArgb(242, 246, 255); s.GameHudFpsColor = Color.FromArgb(34, 211, 238);
                    s.GameHudGpuColor = Color.FromArgb(52, 211, 153); s.GameHudCpuColor = Color.FromArgb(129, 140, 248);
                    s.GameHudMemColor = Color.FromArgb(232, 121, 249); s.GameHudNetColor = Color.FromArgb(250, 204, 21);
                    break;
                case 2: // Crimson Tactical
                    s.GameHudBackColor = Color.FromArgb(18, 12, 14); s.GameHudBorderColor = Color.FromArgb(153, 27, 27);
                    s.GameHudTextColor = Color.FromArgb(250, 242, 242); s.GameHudFpsColor = Color.FromArgb(248, 113, 113);
                    s.GameHudGpuColor = Color.FromArgb(251, 146, 60); s.GameHudCpuColor = Color.FromArgb(251, 191, 36);
                    s.GameHudMemColor = Color.FromArgb(244, 114, 182); s.GameHudNetColor = Color.FromArgb(56, 189, 248);
                    break;
                case 3: // Emerald HUD
                    s.GameHudBackColor = Color.FromArgb(10, 18, 15); s.GameHudBorderColor = Color.FromArgb(16, 120, 82);
                    s.GameHudTextColor = Color.FromArgb(236, 253, 245); s.GameHudFpsColor = Color.FromArgb(52, 211, 153);
                    s.GameHudGpuColor = Color.FromArgb(110, 231, 183); s.GameHudCpuColor = Color.FromArgb(96, 165, 250);
                    s.GameHudMemColor = Color.FromArgb(167, 139, 250); s.GameHudNetColor = Color.FromArgb(251, 191, 36);
                    break;
                default: // Metal Dark
                    s.GameHudBackColor = Color.FromArgb(14, 16, 20); s.GameHudBorderColor = Color.FromArgb(62, 70, 84);
                    s.GameHudTextColor = Color.FromArgb(240, 244, 248); s.GameHudFpsColor = Color.FromArgb(56, 189, 248);
                    s.GameHudGpuColor = Color.FromArgb(74, 222, 128); s.GameHudCpuColor = Color.FromArgb(96, 165, 250);
                    s.GameHudMemColor = Color.FromArgb(192, 132, 252); s.GameHudNetColor = Color.FromArgb(251, 191, 36);
                    break;
            }
            app.Changed();
            LoadSettingsIntoUi();
            if (hudPreview != null) hudPreview.Invalidate();
        }

        void TickGamingHud()
        {
            if (hudPreview != null) hudPreview.Invalidate();
            if (gameDiagList == null) return;
            if (app.Sys.Gpus.Count == 0) app.Sys.SampleGpu();
            GameTelemetry t = app.Game;
            string gpuDisp = t.GpuName;
            if (string.IsNullOrEmpty(gpuDisp) && app.Sys.Gpus.Count > 0) gpuDisp = app.Sys.Gpus[0].Name;
            List<SpecRow> rows = new List<SpecRow>();
            rows.Add(new SpecRow("Overlay mode", t.GameActive ? "Active (" + t.GameName + ")" : (app.S.GameHudPinned ? "Pinned preview" : (app.S.GameHudAuto ? "Armed (auto-detect)" : "Disabled")), null));
            rows.Add(new SpecRow("Detection", string.IsNullOrEmpty(t.DetectReason) ? "Waiting for fullscreen / 3D GPU game" : t.DetectReason, null));
            rows.Add(new SpecRow("Graphics API", string.IsNullOrEmpty(t.GraphicsApi) ? "GPU Engine counters" : t.GraphicsApi, null));
            BrandStyle gBrand = app.Sys.Gpus.Count > 0 ? app.Sys.Gpus[0].Brand : null;
            rows.Add(new SpecRow("Active GPU", string.IsNullOrEmpty(gpuDisp) ? "Detecting..." : gpuDisp + (t.GpuUtil >= 0 ? "   ·   " + t.GpuUtil.ToString("0") + "%" : ""), gBrand));
            rows.Add(new SpecRow("FPS trace", app.IsAdmin ? (t.FpsEtwActive ? "ETW DXGI + D3D9 ready" + (t.Fps > 0.5 ? "  ·  " + t.Fps.ToString("0") + " FPS (1% " + t.Fps1PctLow.ToString("0") + ")" : "") : "Standby") : "Standard user (Restart as Admin for DXGI/D3D9 FPS)", null));
            rows.Add(new SpecRow("Online ping", t.PingMs >= 0 ? t.PingMs + " ms (" + t.PingLabel + (t.PingTarget.Length > 0 ? " · " + t.PingTarget : "") + ")" : "Idle", null));
            if (app.LagDoc.LastResult != null)
            {
                LagDiagnosis ld = app.LagDoc.LastResult;
                rows.Add(new SpecRow("Bufferbloat", "Grade " + ld.Grade + "  ·  " + ld.Verdict, null));
            }
            if (t.LastSession != null)
            {
                GameSessionSummary ls = t.LastSession;
                string dur = ls.DurationSec >= 60 ? (ls.DurationSec / 60) + "m " + (ls.DurationSec % 60) + "s" : ls.DurationSec + "s";
                rows.Add(new SpecRow("Last session", ls.GameName + " (" + dur + ")  ·  Avg " + ls.AvgFps.ToString("0") + " / 1% " + ls.Low1Fps.ToString("0") + " FPS", null));
            }
            gameDiagList.Rows = rows;
            gameDiagList.Invalidate();
        }

        // =================================================================== SETTINGS
        TabPage BuildSettings()
        {
            TabPage p = Page("Settings");
            p.AutoScroll = true;
            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true; t.ColumnCount = 2; t.Location = new Point(16, 12);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Action<string> header = delegate(string h)
            {
                Label l = new Label(); l.Text = h; l.Font = new Font("Segoe UI Semibold", 10.5f); l.AutoSize = true; l.Margin = new Padding(0, 14, 0, 4);
                t.Controls.Add(l); t.SetColumnSpan(l, 2);
            };
            Action<string, Control> row = delegate(string label, Control c)
            {
                Label l = new Label(); l.Text = label; l.AutoSize = true; l.Margin = new Padding(0, 7, 8, 3);
                c.Margin = new Padding(0, 3, 0, 3);
                t.Controls.Add(l); t.Controls.Add(c);
            };
            Func<string, CheckBox> check = delegate(string text)
            {
                CheckBox c = new CheckBox(); c.Text = text; c.AutoSize = true;
                c.CheckedChanged += delegate { SaveUi(); };
                return c;
            };

            header("Appearance");
            sThemeMode = new ComboBox(); sThemeMode.DropDownStyle = ComboBoxStyle.DropDownList; sThemeMode.Width = 220;
            sThemeMode.Items.AddRange(new object[] { "Follow Windows theme", "Light", "Dark" });
            sThemeMode.SelectedIndexChanged += delegate { SaveUi(); };
            row("Dashboard theme", sThemeMode);
            sProfilePreset = new ComboBox(); sProfilePreset.DropDownStyle = ComboBoxStyle.DropDownList; sProfilePreset.Width = 220;
            sProfilePreset.Items.AddRange(new object[] { "Standard", "Gaming", "Travel" });
            sProfilePreset.SelectedIndexChanged += delegate
            {
                if (loadingSettings || sProfilePreset.SelectedItem == null) return;
                app.ApplyProfilePreset((string)sProfilePreset.SelectedItem);
                LoadSettingsIntoUi();
            };
            row("Quick profile", sProfilePreset);

            header("Administrator & Startup");
            Label adminState = new Label(); adminState.AutoSize = true;
            adminState.Tag = "keepcolor";
            adminState.Font = new Font("Segoe UI Semibold", 9f);
            adminState.ForeColor = app.IsAdmin ? Color.FromArgb(16, 185, 129) : Color.FromArgb(217, 119, 6);
            adminState.Text = app.IsAdmin
                ? "✔ Running as Administrator — live per-app speeds and app usage logs are active."
                : "Running in Standard mode — restart as Administrator once to unlock per-app speeds.";
            row("Mode", adminState);
            if (!app.IsAdmin)
            {
                Button el = new Button(); el.Text = "Restart as Administrator"; el.AutoSize = true;
                el.Tag = "primary";
                el.FlatStyle = FlatStyle.Flat; el.FlatAppearance.BorderSize = 0;
                el.BackColor = Color.FromArgb(217, 119, 6); el.ForeColor = Color.White;
                el.Font = new Font("Segoe UI Semibold", 9f); el.Padding = new Padding(10, 3, 10, 3);
                el.Cursor = Cursors.Hand;
                el.Click += delegate { app.RestartElevated(); };
                row("", el);
            }
            sStartup = new CheckBox(); sStartup.Text = "Start with Windows (Standard mode)"; sStartup.AutoSize = true;
            sStartup.CheckedChanged += delegate { if (!loadingSettings) app.StartupEnabled = sStartup.Checked; };
            row("", sStartup);
            sElevated = new CheckBox(); sElevated.AutoSize = true;
            sElevated.Text = "Start as Administrator at sign-in (per-app speeds, no UAC prompt)";
            sElevated.Enabled = app.IsAdmin || app.ElevatedStartupEnabled;
            sElevated.CheckedChanged += delegate
            {
                if (loadingSettings) return;
                if (!app.SetElevatedStartup(sElevated.Checked))
                    MessageBox.Show(sElevated.Checked ? "Could not create the sign-in task. Restart NetSpeed Boss as administrator and try again."
                                                      : "Could not remove the sign-in task (administrator rights may be required).", TrayApp.AppName);
                LoadSettingsIntoUi();
            };
            row("", sElevated);

            header("Taskbar speed");
            sTaskbar = check("Show speed on the taskbar (drag freely to move anywhere)"); row("", sTaskbar);
            tAutoPlace = check("Auto-position in empty taskbar space (near Weather bar)"); row("", tAutoPlace);
            tFont = new ComboBox(); tFont.DropDownStyle = ComboBoxStyle.DropDownList; tFont.Width = 220; tFont.MaxDropDownItems = 20;
            foreach (FontFamily ff in FontFamily.Families) tFont.Items.Add(ff.Name);
            tFont.SelectedIndexChanged += delegate { SaveUi(); };
            row("Font", tFont);
            tSize = new NumericUpDown(); tSize.Minimum = 6; tSize.Maximum = 16; tSize.DecimalPlaces = 1; tSize.Increment = 0.5m; tSize.Width = 70;
            tSize.ValueChanged += delegate { SaveUi(); };
            row("Taskbar text size (pt)", tSize);
            tBold = check("Bold text"); row("", tBold);
            tAuto = check("Match Windows light / dark theme colors"); row("", tAuto);
            FlowLayoutPanel tcol = new FlowLayoutPanel(); tcol.AutoSize = true; tcol.WrapContents = false;
            bTbText = ColorButton("Text color");
            Label tHint = new Label(); tHint.Text = "Arrows use the Download / Upload colors below"; tHint.AutoSize = true;
            tHint.ForeColor = Theme.Muted; tHint.Margin = new Padding(6, 7, 0, 0);
            tcol.Controls.Add(bTbText); tcol.Controls.Add(tHint);
            row("Custom colors", tcol);

            header("Floating meter");
            sShowMeter = check("Show a floating speed window instead of the taskbar meter"); row("", sShowMeter);
            sTop = check("Always on top"); row("", sTop);
            sLock = check("Lock position"); row("", sLock);
            sClickThrough = check("Click-through (use the tray icon for the menu)"); row("", sClickThrough);
            sFull = check("Hide while a full-screen app or game runs"); row("", sFull);
            sOneLine = check("Single-line layout"); row("", sOneLine);
            sGraph = check("Background mini graph"); row("", sGraph);
            sOpacity = new TrackBar(); sOpacity.Minimum = 20; sOpacity.Maximum = 100; sOpacity.TickFrequency = 10; sOpacity.Width = 220;
            sOpacity.ValueChanged += delegate { SaveUi(); };
            row("Opacity", sOpacity);
            sFont = new NumericUpDown(); sFont.Minimum = 6; sFont.Maximum = 30; sFont.DecimalPlaces = 1; sFont.Increment = 0.5m; sFont.Width = 70;
            sFont.ValueChanged += delegate { SaveUi(); };
            row("Floating text size (pt)", sFont);
            FlowLayoutPanel colors = new FlowLayoutPanel(); colors.AutoSize = true; colors.WrapContents = false;
            bDown = ColorButton("Download"); bUp = ColorButton("Upload"); bBack = ColorButton("Background"); bText = ColorButton("Text");
            colors.Controls.AddRange(new Control[] { bDown, bUp, bBack, bText });
            row("Colors", colors);
            FlowLayoutPanel pos = new FlowLayoutPanel(); pos.AutoSize = true; pos.WrapContents = false;
            Button resetPos = new Button(); resetPos.Text = "Reset positions"; resetPos.AutoSize = true;
            resetPos.Click += delegate { app.S.X = int.MinValue; app.S.TaskbarAutoPlace = true; app.S.TaskbarOffset = 6; app.Changed(); };
            pos.Controls.Add(resetPos);
            row("Position", pos);

            header("Measurement");
            sUnit = new ComboBox(); sUnit.DropDownStyle = ComboBoxStyle.DropDownList; sUnit.Width = 220;
            sUnit.Items.AddRange(new object[] { "Bytes per second (KB/s, MB/s)", "Bits per second (Kbps, Mbps)" });
            sUnit.SelectedIndexChanged += delegate { SaveUi(); };
            row("Units", sUnit);
            sInterval = new ComboBox(); sInterval.DropDownStyle = ComboBoxStyle.DropDownList; sInterval.Width = 220;
            sInterval.Items.AddRange(new object[] { "0.5 seconds", "1 second", "2 seconds", "3 seconds", "5 seconds" });
            sInterval.SelectedIndexChanged += delegate { SaveUi(); };
            row("Update interval", sInterval);
            sAdapter = new ComboBox(); sAdapter.DropDownStyle = ComboBoxStyle.DropDownList; sAdapter.Width = 360;
            sAdapter.SelectedIndexChanged += delegate { SaveUi(); };
            row("Network adapter", sAdapter);
            sWanDrop = check("Alert when a multi-WAN internet link drops or recovers");
            row("", sWanDrop);

            header("Data usage");
            sLimit = new NumericUpDown(); sLimit.Minimum = 0; sLimit.Maximum = 100000; sLimit.DecimalPlaces = 1; sLimit.Width = 90;
            sLimit.ValueChanged += delegate { SaveUi(); };
            row("Monthly limit in GB (0 = off)", sLimit);
            sStartDay = new NumericUpDown(); sStartDay.Minimum = 1; sStartDay.Maximum = 28; sStartDay.Width = 70;
            sStartDay.ValueChanged += delegate { SaveUi(); };
            row("Billing cycle starts on day", sStartDay);
            sHogAlert = new NumericUpDown(); sHogAlert.Minimum = 0; sHogAlert.Maximum = 500; sHogAlert.DecimalPlaces = 1; sHogAlert.Width = 90;
            sHogAlert.ValueChanged += delegate { SaveUi(); };
            row("Bandwidth hog alert MB/s (0 = off)", sHogAlert);
            sHogGameOnly = check("Only alert about background downloads while gaming");
            row("", sHogGameOnly);

            header("Updates");
            bool isStore = Updater.IsStoreApp();
            sAutoUpdate = check("Automatically check for new versions and prompt to update");
            sAutoUpdate.Enabled = !isStore;
            row("", sAutoUpdate);
            FlowLayoutPanel updRow = new FlowLayoutPanel(); updRow.AutoSize = true; updRow.WrapContents = false;
            btnCheckUpdates = new Button(); btnCheckUpdates.Text = "Check for updates"; btnCheckUpdates.AutoSize = true;
            btnCheckUpdates.Enabled = !isStore;
            lblUpdateStatus = new Label(); lblUpdateStatus.AutoSize = true; lblUpdateStatus.ForeColor = Theme.Muted;
            lblUpdateStatus.Margin = new Padding(8, 6, 0, 0);
            lblUpdateStatus.Text = isStore ? "Managed by Microsoft Store" : "Current version: v" + Updater.CurrentVersionString();
            btnCheckUpdates.Click += delegate
            {
                btnCheckUpdates.Enabled = false;
                lblUpdateStatus.Text = "Checking GitHub releases...";
                app.CheckForUpdatesAsync(false, delegate(UpdateInfo info)
                {
                    if (IsDisposed) return;
                    btnCheckUpdates.Enabled = true;
                    if (info.Error.Length > 0) lblUpdateStatus.Text = "Check failed: " + info.Error;
                    else if (info.UpdateAvailable)
                    {
                        lblUpdateStatus.Text = "v" + info.Version + " available!";
                        app.PromptAndInstallUpdate();
                    }
                    else lblUpdateStatus.Text = "Up to date (v" + info.CurrentVersion + ")";
                });
            };
            updRow.Controls.Add(btnCheckUpdates); updRow.Controls.Add(lblUpdateStatus);
            row("", updRow);

            header("Data & folders");
            FlowLayoutPanel sys = new FlowLayoutPanel(); sys.AutoSize = true; sys.WrapContents = true;
            Button logsBtn = new Button(); logsBtn.Text = "Open usage logs (Documents)"; logsBtn.AutoSize = true;
            logsBtn.Click += delegate { OpenLogFolder(); };
            sys.Controls.Add(logsBtn);
            Button folder = new Button(); folder.Text = "Open settings folder"; folder.AutoSize = true;
            folder.Click += delegate { Directory.CreateDirectory(Settings.Dir); Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); };
            sys.Controls.Add(folder);
            Button diagCopy = new Button(); diagCopy.Text = "Copy diagnostics"; diagCopy.AutoSize = true;
            diagCopy.Click += delegate { try { Clipboard.SetText(app.BuildDiagnosticsReport()); diagCopy.Text = "Copied ✔"; } catch { } };
            sys.Controls.Add(diagCopy);
            Button diagSave = new Button(); diagSave.Text = "Save diagnostics..."; diagSave.AutoSize = true;
            diagSave.Click += delegate
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Filter = "Text files|*.txt";
                    d.FileName = "netspeedboss-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt";
                    if (d.ShowDialog(this) == DialogResult.OK)
                        try { File.WriteAllText(d.FileName, app.BuildDiagnosticsReport(), System.Text.Encoding.UTF8); } catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
                }
            };
            sys.Controls.Add(diagSave);
            row("", sys);
            Label about = new Label(); about.AutoSize = true; about.ForeColor = Theme.Muted; about.Margin = new Padding(0, 16, 0, 10);
            about.Text = TrayApp.AppName + " " + Application.ProductVersion + (app.IsAdmin ? "  ·  running as administrator" : "") +
                         "\nUsage logs: " + Settings.LogDir + "\nSettings: " + Settings.Dir;
            t.Controls.Add(about); t.SetColumnSpan(about, 2);

            p.Controls.Add(t);
            return p;
        }

        Button ColorButton(string text)
        {
            Button b = new Button(); b.Text = text; b.AutoSize = true; b.Margin = new Padding(0, 0, 6, 0);
            b.TextImageRelation = TextImageRelation.ImageBeforeText; b.ImageAlign = ContentAlignment.MiddleLeft;
            b.Click += delegate
            {
                using (ColorDialog d = new ColorDialog())
                {
                    d.FullOpen = true;
                    d.Color = GetColor(b);
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    if (b == bDown) app.S.DownColor = d.Color;
                    else if (b == bUp) app.S.UpColor = d.Color;
                    else if (b == bBack) app.S.BackColor = d.Color;
                    else if (b == bTbText) { app.S.TaskbarTextColor = d.Color; app.S.TaskbarAutoColors = false; }
                    else app.S.TextColor = d.Color;
                    app.Changed();
                }
            };
            return b;
        }

        Color GetColor(Button b)
        {
            if (b == bDown) return app.S.DownColor;
            if (b == bUp) return app.S.UpColor;
            if (b == bBack) return app.S.BackColor;
            if (b == bTbText) return app.S.TaskbarTextColor;
            if (b == bHudBg) return app.S.GameHudBackColor;
            if (b == bHudBorder) return app.S.GameHudBorderColor;
            if (b == bHudText) return app.S.GameHudTextColor;
            if (b == bHudFps) return app.S.GameHudFpsColor;
            if (b == bHudGpu) return app.S.GameHudGpuColor;
            if (b == bHudCpu) return app.S.GameHudCpuColor;
            if (b == bHudMem) return app.S.GameHudMemColor;
            if (b == bHudNet) return app.S.GameHudNetColor;
            return app.S.TextColor;
        }

        static Bitmap Swatch(Color c)
        {
            Bitmap bmp = new Bitmap(14, 14);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(c);
                g.DrawRectangle(Pens.Gray, 0, 0, 13, 13);
            }
            return bmp;
        }

        static readonly int[] Intervals = { 500, 1000, 2000, 3000, 5000 };

        void LoadSettingsIntoUi()
        {
            loadingSettings = true;
            Settings s = app.S;
            sTaskbar.Checked = s.ShowOnTaskbar;
            int fi = tFont.Items.IndexOf(s.TaskbarFontName); tFont.SelectedIndex = fi >= 0 ? fi : Math.Max(0, tFont.Items.IndexOf("Segoe UI"));
            tSize.Value = (decimal)Math.Max(6f, Math.Min(16f, s.TaskbarFontSize));
            tBold.Checked = s.TaskbarBold; tAuto.Checked = s.TaskbarAutoColors;
            if (tAutoPlace != null) tAutoPlace.Checked = s.TaskbarAutoPlace;
            sShowMeter.Checked = s.ShowMeter; sTop.Checked = s.AlwaysOnTop; sLock.Checked = s.LockPosition;
            sClickThrough.Checked = s.ClickThrough;
            sFull.Checked = s.HideOnFullscreen; sOneLine.Checked = s.Horizontal; sGraph.Checked = s.ShowGraph;
            sOpacity.Value = Math.Max(20, Math.Min(100, s.OpacityPct));
            sFont.Value = (decimal)s.FontSize;
            sUnit.SelectedIndex = s.Unit == SpeedUnit.Bits ? 1 : 0;
            int ii = Array.IndexOf(Intervals, s.IntervalMs); sInterval.SelectedIndex = ii < 0 ? 1 : ii;
            sAdapter.Items.Clear();
            sAdapter.Items.Add(new KeyValuePair<string, string>("auto", "Automatic (adapters with internet access)"));
            sAdapter.Items.Add(new KeyValuePair<string, string>("all", "All adapters"));
            int sel = s.Adapter == "all" ? 1 : 0;
            foreach (AdapterInfo a in app.Net.Adapters)
            {
                sAdapter.Items.Add(new KeyValuePair<string, string>(a.Id, a.Name + " (" + a.Description + ")"));
                if (a.Id == s.Adapter) sel = sAdapter.Items.Count - 1;
            }
            sAdapter.DisplayMember = "Value";
            sAdapter.SelectedIndex = sel;
            sLimit.Value = (decimal)Math.Max(0, Math.Min(100000, s.MonthlyLimitGB));
            sStartDay.Value = s.MonthStartDay;
            if (sHogAlert != null) sHogAlert.Value = (decimal)Math.Max(0, Math.Min(500, s.HogAlertMBps));
            if (sHogGameOnly != null) sHogGameOnly.Checked = s.HogAlertDuringGame;
            sStartup.Checked = app.StartupEnabled;
            sElevated.Checked = app.ElevatedStartupEnabled;
            sAutoUpdate.Checked = s.AutoCheckUpdates;
            if (sWanDrop != null) sWanDrop.Checked = s.AlertOnWanDrop;
            if (sThemeMode != null) sThemeMode.SelectedIndex = (int)s.ThemeMode;
            if (chkAlertNewApps != null) chkAlertNewApps.Checked = s.AlertNewNetworkApps;
            if (chkWatchLan != null) chkWatchLan.Checked = s.WatchNewLanDevices;
            if (sProfilePreset != null)
            {
                int pi = sProfilePreset.Items.IndexOf(s.ProfilePreset);
                sProfilePreset.SelectedIndex = pi >= 0 ? pi : 0;
            }
            if (sGameAuto != null)
            {
                sGameAuto.Checked = s.GameHudAuto;
                sGameShow.Checked = s.GameHudPinned;
                sGameClickThrough.Checked = s.GameHudClickThrough;
                sGameStrip.Checked = s.GameHudStrip;
                sGameHotkeys.Checked = s.GlobalHotkeysEnabled;
                sGameLogSess.Checked = s.GameHudLogSessions;
                sGameCorner.SelectedIndex = Math.Max(0, Math.Min(3, s.GameHudCorner));
                sGameOpacity.Value = Math.Max(0, Math.Min(99, s.GameHudOpacity));
                lblGameOpacity.Text = sGameOpacity.Value + "%";
                sGameRag.Checked = s.GameHudRag;
                nFpsG.Value = Math.Max(15, Math.Min(240, s.GameHudFpsGreen));
                nFpsA.Value = Math.Max(10, Math.Min(200, s.GameHudFpsAmber));
                nTempG.Value = Math.Max(40, Math.Min(95, s.GameHudTempGreen));
                nTempA.Value = Math.Max(50, Math.Min(105, s.GameHudTempAmber));
                nPingG.Value = Math.Max(10, Math.Min(200, s.GameHudPingGreen));
                nPingA.Value = Math.Max(20, Math.Min(400, s.GameHudPingAmber));
                nLoadA.Value = Math.Max(40, Math.Min(95, s.GameHudLoadAmber));
                nLoadR.Value = Math.Max(50, Math.Min(99, s.GameHudLoadRed));
                sHudFps.Checked = s.GameHudShowFps; sHudGpu.Checked = s.GameHudShowGpu;
                sHudCpu.Checked = s.GameHudShowCpu; sHudMem.Checked = s.GameHudShowMem;
                sHudNet.Checked = s.GameHudShowNet; sHudGraph.Checked = s.GameHudShowGraph;
                if (sGamePreset.SelectedIndex < 0) sGamePreset.SelectedIndex = 0;
            }
            foreach (Button b in new Button[] { bDown, bUp, bBack, bText, bTbText, bHudBg, bHudBorder, bHudText, bHudFps, bHudGpu, bHudCpu, bHudMem, bHudNet })
            {
                if (b == null) continue;
                Image old = b.Image;
                b.Image = Swatch(GetColor(b));
                if (old != null) old.Dispose();
            }
            loadingSettings = false;
        }

        void SaveUi()
        {
            if (loadingSettings) return;
            Settings s = app.S;
            bool meterWas = s.ShowMeter;
            bool clickWas = s.ClickThrough;
            s.AlwaysOnTop = sTop.Checked; s.LockPosition = sLock.Checked; s.HideOnFullscreen = sFull.Checked;
            s.ClickThrough = sClickThrough.Checked;
            s.Horizontal = sOneLine.Checked; s.ShowGraph = sGraph.Checked; s.OpacityPct = sOpacity.Value;
            s.FontSize = (float)sFont.Value;
            if (tFont.SelectedItem != null) s.TaskbarFontName = (string)tFont.SelectedItem;
            s.TaskbarFontSize = (float)tSize.Value;
            s.TaskbarBold = tBold.Checked; s.TaskbarAutoColors = tAuto.Checked;
            if (tAutoPlace != null) s.TaskbarAutoPlace = tAutoPlace.Checked;
            s.Unit = sUnit.SelectedIndex == 1 ? SpeedUnit.Bits : SpeedUnit.Bytes;
            if (sInterval.SelectedIndex >= 0) app.SetTimerInterval(Intervals[sInterval.SelectedIndex]);
            if (sAdapter.SelectedItem != null) s.Adapter = ((KeyValuePair<string, string>)sAdapter.SelectedItem).Key;
            s.MonthlyLimitGB = (double)sLimit.Value;
            s.MonthStartDay = (int)sStartDay.Value;
            if (sHogAlert != null) s.HogAlertMBps = (double)sHogAlert.Value;
            if (sHogGameOnly != null) s.HogAlertDuringGame = sHogGameOnly.Checked;
            s.AutoCheckUpdates = sAutoUpdate.Checked;
            if (sWanDrop != null) s.AlertOnWanDrop = sWanDrop.Checked;
            DashTheme prevTheme = s.ThemeMode;
            if (sThemeMode != null && sThemeMode.SelectedIndex >= 0) s.ThemeMode = (DashTheme)sThemeMode.SelectedIndex;
            if (prevTheme != s.ThemeMode) ApplyTheme();
            bool gameClickWas = s.GameHudClickThrough;
            bool gamePinWas = s.GameHudPinned;
            if (sGameAuto != null)
            {
                s.GameHudAuto = sGameAuto.Checked;
                s.GameHudClickThrough = sGameClickThrough.Checked;
                bool stripWas = s.GameHudStrip;
                s.GameHudStrip = sGameStrip.Checked;
                if (stripWas != s.GameHudStrip) s.GameHudX = int.MinValue;
                s.GlobalHotkeysEnabled = sGameHotkeys.Checked;
                s.GameHudLogSessions = sGameLogSess.Checked;
                if (sGameCorner.SelectedIndex >= 0) s.GameHudCorner = sGameCorner.SelectedIndex;
                s.GameHudOpacity = sGameOpacity.Value;
                s.GameHudRag = sGameRag.Checked;
                s.GameHudFpsGreen = (int)nFpsG.Value; s.GameHudFpsAmber = (int)nFpsA.Value;
                s.GameHudTempGreen = (int)nTempG.Value; s.GameHudTempAmber = (int)nTempA.Value;
                s.GameHudPingGreen = (int)nPingG.Value; s.GameHudPingAmber = (int)nPingA.Value;
                s.GameHudLoadAmber = (int)nLoadA.Value; s.GameHudLoadRed = (int)nLoadR.Value;
                s.GameHudShowFps = sHudFps.Checked; s.GameHudShowGpu = sHudGpu.Checked;
                s.GameHudShowCpu = sHudCpu.Checked; s.GameHudShowMem = sHudMem.Checked;
                s.GameHudShowNet = sHudNet.Checked; s.GameHudShowGraph = sHudGraph.Checked;
                if (hudPreview != null) hudPreview.Invalidate();
            }
            savingUi = true;
            try
            {
                // taskbar meter and floating meter are mutually exclusive
                bool wantMeter = sShowMeter.Checked, wantTb = sTaskbar.Checked;
                if (wantMeter && !meterWas) app.ToggleMeter();
                else if (wantTb && !s.ShowOnTaskbar) app.ToggleTaskbar();
                else
                {
                    if (!wantMeter && s.ShowMeter) app.ToggleMeter();
                    if (!wantTb && s.ShowOnTaskbar) app.ToggleTaskbar();
                }
                if (sGameShow != null && sGameShow.Checked != gamePinWas) app.ToggleGameHud();
                loadingSettings = true;
                sShowMeter.Checked = s.ShowMeter; sTaskbar.Checked = s.ShowOnTaskbar;
                if (sGameShow != null) sGameShow.Checked = s.GameHudPinned;
                loadingSettings = false;
                app.Changed();
                if (clickWas != s.ClickThrough) app.MeterClickThroughChanged();
                if (gameClickWas != s.GameHudClickThrough) app.GameHudClickThroughChanged();
            }
            finally { savingUi = false; }
            RefreshAccents();
        }

        bool savingUi;

        void RefreshAccents()
        {
            cDown.AccentColor = cPeakDown.AccentColor = app.S.DownColor;
            cUp.AccentColor = cPeakUp.AccentColor = app.S.UpColor;
            cDown.Invalidate(); cUp.Invalidate(); cPeakDown.Invalidate(); cPeakUp.Invalidate();
        }

        void PaintPingPill(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.DrawCard(g, lblPing.ClientRectangle, Theme.CardAlt, Theme.Border, null, 10);
            Color dot = Theme.Muted;
            string text = "Ping: ...";
            if (lastPingMs >= 0)
            {
                text = "Ping: " + lastPingMs + " ms" + (app.PingLossPct > 0 ? " · " + app.PingLossPct.ToString("0") + "% loss"
                    : " · ±" + app.PingJitterMs.ToString("0") + "ms");
                dot = (lastPingMs < 50 && app.PingLossPct < 5) ? Color.FromArgb(16, 185, 129)
                    : ((lastPingMs < 120 && app.PingLossPct < 15) ? Color.FromArgb(217, 119, 6) : Color.FromArgb(220, 38, 38));
            }
            else if (lastPingMs == -1)
            {
                text = "Ping: lost (" + app.PingLossPct.ToString("0") + "%)";
                dot = Color.FromArgb(220, 38, 38);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, 8, (lblPing.Height - 7) / 2, 7, 7);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, text, uiFont, new Rectangle(18, 0, lblPing.Width - 22, lblPing.Height),
                Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }

        void CheckPingAsync(bool force)
        {
            if (pingBusy || (!force && !Visible)) return;
            pingBusy = true;
            lastPingAt = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long ms = -1;
                try
                {
                    using (Ping p = new Ping())
                    {
                        PingReply r = p.Send("1.1.1.1", 1500);
                        if (r != null && r.Status == IPStatus.Success) ms = r.RoundtripTime;
                    }
                }
                catch { ms = -1; }
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate
                        {
                            lastPingMs = ms;
                            app.RecordPingSample(ms);
                            pingBusy = false;
                            if (lblPing != null)
                            {
                                tip.SetToolTip(lblPing, string.Format("1.1.1.1 RTT: {0}  ·  Jitter: ±{1:0.0} ms  ·  Loss (last 20): {2:0.0}%\nClick to re-test",
                                    ms >= 0 ? ms + " ms" : "timeout", app.PingJitterMs, app.PingLossPct));
                                lblPing.Invalidate();
                            }
                        });
                    else pingBusy = false;
                }
                catch { pingBusy = false; }
            });
        }

        void ShowSpeedHistoryDialog()
        {
            List<SpeedTestRecord> rows = SpeedTester.LoadHistory(100);
            using (Form f = new Form())
            {
                f.Text = "Speedtest History";
                f.Icon = GetAppIcon();
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(640, 420);
                f.BackColor = Theme.Bg; f.ForeColor = Theme.Ink; f.Font = uiFont;
                DbListView lv = new DbListView(); lv.Dock = DockStyle.Fill;
                Col(lv, "Date / Time", 150, HorizontalAlignment.Left);
                Col(lv, "Download", 105, HorizontalAlignment.Right);
                Col(lv, "Upload", 105, HorizontalAlignment.Right);
                Col(lv, "Ping", 75, HorizontalAlignment.Right);
                Col(lv, "Jitter", 75, HorizontalAlignment.Right);
                Col(lv, "WANs", 60, HorizontalAlignment.Center);
                foreach (SpeedTestRecord r in rows)
                {
                    lv.Items.Add(new ListViewItem(new string[] {
                        r.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                        Fmt.Speed(r.DownloadBps, app.S.Unit),
                        Fmt.Speed(r.UploadBps, app.S.Unit),
                        r.PingMs >= 0 ? r.PingMs.ToString("0") + " ms" : "-",
                        r.JitterMs >= 0 ? r.JitterMs.ToString("0.0") + " ms" : "-",
                        r.WanCount.ToString()
                    }));
                }
                FlowLayoutPanel bot = new FlowLayoutPanel(); bot.Dock = DockStyle.Bottom; bot.Height = 38; bot.FlowDirection = FlowDirection.RightToLeft;
                bot.Padding = new Padding(8, 5, 8, 5);
                Button close = new Button(); close.Text = "Close"; close.AutoSize = true;
                close.Click += delegate { f.Close(); };
                Button openCsv = new Button(); openCsv.Text = "Open CSV folder"; openCsv.AutoSize = true;
                openCsv.Click += delegate { OpenLogFolder(); };
                Label summary = new Label(); summary.Dock = DockStyle.Fill; summary.TextAlign = ContentAlignment.MiddleLeft; summary.Padding = new Padding(8, 0, 0, 0);
                summary.Text = rows.Count == 0 ? "No completed speedtests logged yet. Run Speedtest on the Live tab to save results." : rows.Count + " logged test(s)";
                bot.Controls.Add(close); bot.Controls.Add(openCsv); bot.Controls.Add(summary);
                f.Controls.Add(lv); f.Controls.Add(bot);
                ApplyThemeTo(f);
                f.ShowDialog(this);
            }
        }

        void RunDnsBenchmarkDialog()
        {
            btnDnsBench.Enabled = false;
            btnDnsBench.Text = "Testing DNS...";
            List<AdapterInfo> nets = app.Net.Adapters;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DnsBenchRow> rows = NetworkInfo.RunDnsBenchmark(nets);
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate
                        {
                            btnDnsBench.Enabled = true;
                            btnDnsBench.Text = "DNS benchmark";
                            using (Form f = new Form())
                            {
                                f.Text = "DNS Resolver Benchmark (UDP port 53)";
                                f.Icon = GetAppIcon();
                                f.StartPosition = FormStartPosition.CenterParent;
                                f.Size = new Size(440, 290);
                                f.BackColor = Theme.Bg; f.ForeColor = Theme.Ink; f.Font = uiFont;
                                DbListView lv = new DbListView(); lv.Dock = DockStyle.Fill;
                                Col(lv, "Provider", 150, HorizontalAlignment.Left);
                                Col(lv, "IP", 130, HorizontalAlignment.Left);
                                Col(lv, "Latency", 110, HorizontalAlignment.Right);
                                foreach (DnsBenchRow r in rows)
                                    lv.Items.Add(new ListViewItem(new string[] {
                                        r.Name, r.Server, r.LatencyMs >= 0 ? r.LatencyMs.ToString("0.0") + " ms" : "Timeout"
                                    }));
                                FlowLayoutPanel bot = new FlowLayoutPanel(); bot.Dock = DockStyle.Bottom; bot.Height = 38; bot.FlowDirection = FlowDirection.RightToLeft;
                                bot.Padding = new Padding(8, 5, 8, 5);
                                Button close = new Button(); close.Text = "Close"; close.AutoSize = true;
                                close.Click += delegate { f.Close(); };
                                bot.Controls.Add(close);
                                f.Controls.Add(lv); f.Controls.Add(bot);
                                ApplyThemeTo(f);
                                f.ShowDialog(this);
                            }
                        });
                }
                catch { btnDnsBench.Enabled = true; btnDnsBench.Text = "DNS benchmark"; }
            });
        }

        void ToggleSpeedTest()
        {
            if (speedTester.State.Running)
            {
                speedTester.Cancel();
                btnSpeedTest.Text = "Speedtest";
                return;
            }
            btnSpeedTest.Text = "Stop test";
            speedTester.Start(delegate(SpeedTestState st)
            {
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate { UpdateSpeedTestUi(st); });
                }
                catch { }
            });
        }

        void UpdateSpeedTestUi(SpeedTestState st)
        {
            if (IsDisposed) return;
            SpeedUnit u = app.S.Unit;
            switch (st.Phase)
            {
                case SpeedTestPhase.Ping:
                    lblSpeedResult.Text = "Testing ping...";
                    lblSpeedResult.ForeColor = Theme.Muted;
                    break;
                case SpeedTestPhase.Download:
                    lblSpeedResult.Text = "↓ " + Fmt.Speed(st.DownloadBps, u) + " (" + st.ProgressPct + "%)";
                    lblSpeedResult.ForeColor = Theme.Ink;
                    break;
                case SpeedTestPhase.Upload:
                    lblSpeedResult.Text = "↓ " + Fmt.Speed(st.DownloadBps, u) + "  ↑ " + Fmt.Speed(st.UploadBps, u) + " (" + st.ProgressPct + "%)";
                    lblSpeedResult.ForeColor = Theme.Ink;
                    break;
                case SpeedTestPhase.Done:
                    SpeedTester.AppendHistory(st, Math.Max(1, app.Net.ActiveWanCount));
                    btnSpeedTest.Text = "Speedtest";
                    lblSpeedResult.Text = "↓ " + Fmt.Speed(st.DownloadBps, u) + "  ↑ " + Fmt.Speed(st.UploadBps, u) +
                        (st.PingMs >= 0 ? "  · " + st.PingMs.ToString("0") + " ms" : "");
                    lblSpeedResult.ForeColor = Theme.IsDark ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 124, 16);
                    RefreshSpeedHistoryList();
                    break;
                case SpeedTestPhase.Cancelled:
                    btnSpeedTest.Text = "Speedtest";
                    lblSpeedResult.Text = "Speedtest cancelled";
                    lblSpeedResult.ForeColor = Theme.Muted;
                    break;
                case SpeedTestPhase.Error:
                    btnSpeedTest.Text = "Speedtest";
                    lblSpeedResult.Text = "Test failed: " + st.Error;
                    lblSpeedResult.ForeColor = Color.FromArgb(220, 38, 38);
                    break;
            }
            SyncNetworkSpeedUi(st);
        }

        public void SettingsChanged()
        {
            if (loadingSettings || savingUi) return;
            ApplyTheme();
            LoadSettingsIntoUi();
            RefreshAccents();
            lastAdapterRefresh = DateTime.MinValue;
            OnTick();
        }

        // =================================================================== tick
        public void OnTick()
        {
            if (WindowState == FormWindowState.Minimized) return;
            switch (tabs.SelectedIndex)
            {
                case 0: TickLive(); break;
                case 1: TickApps(); break;
                case 2: TickNetwork(); break;
                case 3: TickUsage(); break;
                case 4: TickSystem(); break;
                case 5: TickGamingHud(); break;
                case 6: TickDisplays(); break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeDisplays(); speedTester.Cancel(); lanScanner.Cancel(); app.LagDoc.Cancel(); icons.Dispose(); uiFont.Dispose(); navFont.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
