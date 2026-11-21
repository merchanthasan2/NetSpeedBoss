using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    public class Dashboard : Form
    {
        readonly TrayApp app;
        readonly TabControl tabs = new TabControl();
        readonly Font uiFont = new Font("Segoe UI", 9f);

        // live
        StatCard cDown, cUp, cPeakDown, cPeakUp, cSession;
        GraphPanel graph;
        ComboBox rangeBox;
        DbListView adapterList;
        DateTime lastAdapterRefresh = DateTime.MinValue;

        // apps
        DbListView procList, epList;
        Label appsStatus;
        Button btnElevate;
        CheckBox chkActiveOnly, chkResolve;
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
        CheckBox sTaskbar, sShowMeter, sTop, sLock, sFull, sOneLine, sGraph, sStartup, sClickThrough;
        ComboBox sUnit, sInterval, sAdapter;
        TrackBar sOpacity;
        NumericUpDown sFont, sLimit, sStartDay;
        Button bDown, bUp, bBack, bText, bTbText;
        ComboBox tFont;
        NumericUpDown tSize;
        CheckBox tBold, tAuto;

        public Dashboard(TrayApp app)
        {
            this.app = app;
            Text = TrayApp.AppName;
            Font = uiFont;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Math.Max(760, app.S.DashW), Math.Max(520, app.S.DashH));
            MinimumSize = new Size(720, 480);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            icons.ColorDepth = ColorDepth.Depth32Bit;
            icons.ImageSize = new Size(16, 16);

            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(14, 5);
            tabs.TabPages.Add(BuildLive());
            tabs.TabPages.Add(BuildApps());
            tabs.TabPages.Add(BuildUsage());
            tabs.TabPages.Add(BuildSettings());
            tabs.SelectedIndexChanged += delegate { lastUsageRefresh = DateTime.MinValue; OnTick(); };
            Controls.Add(tabs);

            FormClosing += delegate
            {
                if (WindowState == FormWindowState.Normal) { app.S.DashW = Width; app.S.DashH = Height; }
                app.S.Save();
            };
            LoadSettingsIntoUi();
            OnTick();
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
            graph = new GraphPanel(); graph.App = app; graph.Dock = DockStyle.Fill;
            Panel graphBar = new Panel(); graphBar.Dock = DockStyle.Top; graphBar.Height = 30; graphBar.BackColor = Theme.Card;
            Label gl = new Label(); gl.Text = "Speed history"; gl.Font = new Font("Segoe UI Semibold", 9.5f); gl.AutoSize = true; gl.Location = new Point(8, 6);
            rangeBox = new ComboBox(); rangeBox.DropDownStyle = ComboBoxStyle.DropDownList; rangeBox.Width = 110;
            rangeBox.Items.AddRange(new object[] { "1 minute", "2 minutes", "5 minutes", "10 minutes" });
            rangeBox.SelectedIndex = 1;
            rangeBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            rangeBox.SelectedIndexChanged += delegate { graph.Seconds = new int[] { 60, 120, 300, 600 }[rangeBox.SelectedIndex]; graph.Invalidate(); };
            graphBar.Controls.Add(gl); graphBar.Controls.Add(rangeBox);
            graphBar.Resize += delegate { rangeBox.Location = new Point(graphBar.Width - rangeBox.Width - 6, 3); };
            graphHost.Controls.Add(graph); graphHost.Controls.Add(graphBar);

            adapterList = new DbListView(); adapterList.Dock = DockStyle.Bottom; adapterList.Height = 130;
            Col(adapterList, "Adapter", 170, HorizontalAlignment.Left);
            Col(adapterList, "Description", 230, HorizontalAlignment.Left);
            Col(adapterList, "Type", 90, HorizontalAlignment.Left);
            Col(adapterList, "Link speed", 85, HorizontalAlignment.Right);
            Col(adapterList, "IPv4", 110, HorizontalAlignment.Left);
            Col(adapterList, "Received (since boot)", 135, HorizontalAlignment.Right);
            Col(adapterList, "Sent (since boot)", 115, HorizontalAlignment.Right);
            Col(adapterList, "Measured", 70, HorizontalAlignment.Center);

            Panel adapterHost = new Panel(); adapterHost.Dock = DockStyle.Bottom; adapterHost.Height = 160; adapterHost.Padding = new Padding(6);
            Label al = new Label(); al.Text = "Network adapters"; al.Font = new Font("Segoe UI Semibold", 9.5f); al.Dock = DockStyle.Top; al.Height = 22;
            adapterHost.Controls.Add(adapterList); adapterHost.Controls.Add(al);
            adapterList.Dock = DockStyle.Fill;

            p.Controls.Add(graphHost);
            p.Controls.Add(adapterHost);
            p.Controls.Add(cards);
            return p;
        }

        void TickLive()
        {
            Settings s = app.S;
            cDown.Set(Fmt.Speed(app.CurRx, s.Unit), "↓ receiving");
            cUp.Set(Fmt.Speed(app.CurTx, s.Unit), "↑ sending");
            cPeakDown.Set(Fmt.Speed(app.PeakRx, s.Unit), "since " + app.SessionStart.ToString("t"));
            cPeakUp.Set(Fmt.Speed(app.PeakTx, s.Unit), "since " + app.SessionStart.ToString("t"));
            TimeSpan up = DateTime.Now - app.SessionStart;
            cSession.Title("This session (" +
                (up.TotalHours >= 1 ? ((int)up.TotalHours) + "h " + up.Minutes + "m" : up.Minutes + "m " + up.Seconds + "s") + ")");
            cSession.Set(Fmt.Size(app.SessionRx + app.SessionTx),
                "↓ " + Fmt.Size(app.SessionRx) + "  ↑ " + Fmt.Size(app.SessionTx));
            graph.Invalidate();

            if ((DateTime.UtcNow - lastAdapterRefresh).TotalSeconds >= 3)
            {
                lastAdapterRefresh = DateTime.UtcNow;
                adapterList.BeginUpdate();
                adapterList.Items.Clear();
                foreach (AdapterInfo a in app.Net.Adapters)
                {
                    if (a.IPv4 == null) a.IPv4 = NetMonitor.GetIPv4(a.Id);
                    ListViewItem it = new ListViewItem(new string[] { a.Name, a.Description, a.Type, Fmt.LinkSpeed(a.LinkSpeed),
                        a.IPv4, Fmt.Size(a.BytesIn), Fmt.Size(a.BytesOut), a.Selected ? "✔" : "" });
                    if (!a.Selected) it.ForeColor = Theme.Muted;
                    adapterList.Items.Add(it);
                }
                adapterList.EndUpdate();
            }
        }

        // =================================================================== APPS
        TabPage BuildApps()
        {
            TabPage p = Page("Apps");
            Panel bar = new Panel(); bar.Dock = DockStyle.Top; bar.Height = 40; bar.BackColor = Theme.Card; bar.Padding = new Padding(8, 6, 8, 6);
            appsStatus = new Label(); appsStatus.AutoSize = false; appsStatus.Dock = DockStyle.Fill; appsStatus.TextAlign = ContentAlignment.MiddleLeft;
            FlowLayoutPanel right = new FlowLayoutPanel(); right.Dock = DockStyle.Right; right.AutoSize = true; right.WrapContents = false;
            right.FlowDirection = FlowDirection.LeftToRight;
            chkActiveOnly = new CheckBox(); chkActiveOnly.Text = "Active only"; chkActiveOnly.AutoSize = true; chkActiveOnly.Margin = new Padding(6, 5, 6, 0);
            chkActiveOnly.CheckedChanged += delegate { TickApps(); };
            chkResolve = new CheckBox(); chkResolve.Text = "Resolve host names"; chkResolve.Checked = true; chkResolve.AutoSize = true; chkResolve.Margin = new Padding(6, 5, 6, 0);
            Button reset = new Button(); reset.Text = "Reset totals"; reset.AutoSize = true; reset.Click += delegate { app.Procs.ResetTotals(); };
            btnElevate = new Button(); btnElevate.Text = "Restart as administrator"; btnElevate.AutoSize = true;
            btnElevate.Click += delegate { app.RestartElevated(); };
            btnElevate.Visible = !app.IsAdmin;
            right.Controls.Add(chkActiveOnly); right.Controls.Add(chkResolve); right.Controls.Add(reset); right.Controls.Add(btnElevate);
            bar.Controls.Add(appsStatus); bar.Controls.Add(right);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill; split.Orientation = Orientation.Horizontal; split.SplitterWidth = 6;
            split.BackColor = Theme.Bg;

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
            pm.Items.Add("Open file location", null, delegate
            {
                ProcView v = SelectedProc();
                if (v != null && v.Path != null) Process.Start("explorer.exe", "/select,\"" + v.Path + "\"");
            });
            pm.Items.Add("Copy name", null, delegate { ProcView v = SelectedProc(); if (v != null) Clipboard.SetText(v.Name); });
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
            em.Items.Add("Copy remote address", null, delegate { if (epList.SelectedItems.Count > 0) Clipboard.SetText((string)epList.SelectedItems[0].Tag); });
            epList.ContextMenuStrip = em;

            Label epTitle = new Label(); epTitle.Text = "Connections of the selected app"; epTitle.Dock = DockStyle.Top; epTitle.Height = 22;
            epTitle.Font = new Font("Segoe UI Semibold", 9.5f); epTitle.Padding = new Padding(2, 4, 0, 0);
            split.Panel1.Controls.Add(procList);
            split.Panel2.Controls.Add(epList); split.Panel2.Controls.Add(epTitle);
            p.Controls.Add(split); p.Controls.Add(bar);
            p.Layout += delegate { try { if (split.Height > 200 && split.SplitterDistance < 100) split.SplitterDistance = (int)(split.Height * 0.58); } catch { } };
            return p;
        }

        ProcView SelectedProc()
        {
            if (procList.SelectedItems.Count == 0) return null;
            int pid = (int)procList.SelectedItems[0].Tag;
            foreach (ProcView v in app.Procs.Snapshot()) if (v.Pid == pid) return v;
            return null;
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
            Settings s = app.S;
            ProcessMonitor pm = app.Procs;
            List<ProcView> list = pm.Snapshot();
            bool etw = pm.EtwActive;
            if (chkActiveOnly.Checked) list.RemoveAll(delegate(ProcView v) { return v.Rx + v.Tx <= 0 && v.Conns == 0; });
            list.Sort(CompareProc);

            double sumRx = 0, sumTx = 0;
            foreach (ProcView v in list) { sumRx += v.Rx; sumTx += v.Tx; }
            if (etw && pm.EventsSeen == 0 && app.SessionRx + app.SessionTx > 5 * 1024 * 1024)
                pm.Diagnose("Kernel trace started but no network events were received - per-app speeds unavailable.");
            string status = etw
                ? "● Live per-app tracking  ·  " + list.Count + " apps  ·  ↓ " + Fmt.Speed(sumRx, s.Unit) + "  ↑ " + Fmt.Speed(sumTx, s.Unit)
                : "⚠ " + pm.Status + "  Showing TCP connections only.";
            if (appsStatus.Text != status) appsStatus.Text = status;
            appsStatus.ForeColor = etw ? Color.FromArgb(16, 124, 16) : Color.FromArgb(157, 93, 0);

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
            limitLabel = new Label(); limitLabel.Dock = DockStyle.Left; limitLabel.Width = 330; limitLabel.TextAlign = ContentAlignment.MiddleLeft;
            limitBar = new ProgressBar(); limitBar.Dock = DockStyle.Fill; limitBar.Maximum = 1000;
            limitPanel.Controls.Add(limitBar); limitPanel.Controls.Add(limitLabel);

            Panel chartHost = new Panel(); chartHost.Dock = DockStyle.Fill; chartHost.Padding = new Padding(6);
            usageChart = new UsageChart(); usageChart.Usage = app.Usage; usageChart.S = app.S; usageChart.Dock = DockStyle.Fill;
            Label ct = new Label(); ct.Text = "Daily usage, last 30 days"; ct.Font = new Font("Segoe UI Semibold", 9.5f);
            ct.Dock = DockStyle.Top; ct.Height = 24; ct.BackColor = Theme.Card; ct.Padding = new Padding(6, 4, 0, 0);
            chartHost.Controls.Add(usageChart); chartHost.Controls.Add(ct);

            Panel listHost = new Panel(); listHost.Dock = DockStyle.Bottom; listHost.Height = 190; listHost.Padding = new Padding(6);
            usageList = new DbListView(); usageList.Dock = DockStyle.Fill;
            Col(usageList, "Date", 150, HorizontalAlignment.Left);
            Col(usageList, "Downloaded", 120, HorizontalAlignment.Right);
            Col(usageList, "Uploaded", 120, HorizontalAlignment.Right);
            Col(usageList, "Total", 120, HorizontalAlignment.Right);
            FlowLayoutPanel btns = new FlowLayoutPanel(); btns.Dock = DockStyle.Bottom; btns.Height = 34; btns.FlowDirection = FlowDirection.RightToLeft;
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
            listHost.Controls.Add(usageList); listHost.Controls.Add(btns);

            p.Controls.Add(chartHost); p.Controls.Add(listHost); p.Controls.Add(limitPanel); p.Controls.Add(cards);
            return p;
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
                limitLabel.Text = "Monthly limit: " + Fmt.Size(used) + " of " + s.MonthlyLimitGB + " GB used (" + (used / limit * 100).ToString("0.0") +
                                  "%), resets on day " + s.MonthStartDay;
            }
            else { limitBar.Visible = false; limitLabel.Text = "No monthly data limit set (configure in Settings)."; limitLabel.Width = 600; }

            usageChart.Invalidate();
            if ((DateTime.UtcNow - lastUsageRefresh).TotalSeconds >= 5)
            {
                lastUsageRefresh = DateTime.UtcNow;
                usageList.BeginUpdate();
                usageList.Items.Clear();
                foreach (KeyValuePair<DateTime, long[]> kv in u.All())
                    usageList.Items.Add(new ListViewItem(new string[] { kv.Key.ToString("ddd, d MMM yyyy"), Fmt.Size(kv.Value[0]),
                        Fmt.Size(kv.Value[1]), Fmt.Size(kv.Value[0] + kv.Value[1]) }));
                usageList.EndUpdate();
            }
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

            header("Taskbar speed");
            sTaskbar = check("Show speed on the taskbar (next to the clock - drag it to move)"); row("", sTaskbar);
            tFont = new ComboBox(); tFont.DropDownStyle = ComboBoxStyle.DropDownList; tFont.Width = 220; tFont.MaxDropDownItems = 20;
            foreach (FontFamily ff in FontFamily.Families) tFont.Items.Add(ff.Name);
            tFont.SelectedIndexChanged += delegate { SaveUi(); };
            row("Font", tFont);
            tSize = new NumericUpDown(); tSize.Minimum = 6; tSize.Maximum = 16; tSize.DecimalPlaces = 1; tSize.Increment = 0.5m; tSize.Width = 70;
            tSize.ValueChanged += delegate { SaveUi(); };
            row("Text size (pt)", tSize);
            tBold = check("Bold text"); row("", tBold);
            tAuto = check("Match Windows light / dark theme colors"); row("", tAuto);
            FlowLayoutPanel tcol = new FlowLayoutPanel(); tcol.AutoSize = true; tcol.WrapContents = false;
            bTbText = ColorButton("Text color");
            Label tHint = new Label(); tHint.Text = "Arrows use the Download / Upload colors below"; tHint.AutoSize = true;
            tHint.ForeColor = Theme.Muted; tHint.Margin = new Padding(6, 7, 0, 0);
            tcol.Controls.Add(bTbText); tcol.Controls.Add(tHint);
            row("Custom colors", tcol);

            header("Floating meter");
            sShowMeter = check("Show floating speed meter"); row("", sShowMeter);
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
            row("Text size (pt)", sFont);
            FlowLayoutPanel colors = new FlowLayoutPanel(); colors.AutoSize = true; colors.WrapContents = false;
            bDown = ColorButton("Download"); bUp = ColorButton("Upload"); bBack = ColorButton("Background"); bText = ColorButton("Text");
            colors.Controls.AddRange(new Control[] { bDown, bUp, bBack, bText });
            row("Colors", colors);
            FlowLayoutPanel pos = new FlowLayoutPanel(); pos.AutoSize = true; pos.WrapContents = false;
            Button resetPos = new Button(); resetPos.Text = "Reset positions"; resetPos.AutoSize = true;
            resetPos.Click += delegate { app.S.X = int.MinValue; app.S.TaskbarOffset = 6; app.Changed(); };
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

            header("Data usage");
            sLimit = new NumericUpDown(); sLimit.Minimum = 0; sLimit.Maximum = 100000; sLimit.DecimalPlaces = 1; sLimit.Width = 90;
            sLimit.ValueChanged += delegate { SaveUi(); };
            row("Monthly limit in GB (0 = off)", sLimit);
            sStartDay = new NumericUpDown(); sStartDay.Minimum = 1; sStartDay.Maximum = 28; sStartDay.Width = 70;
            sStartDay.ValueChanged += delegate { SaveUi(); };
            row("Billing cycle starts on day", sStartDay);

            header("System");
            sStartup = new CheckBox(); sStartup.Text = "Start with Windows"; sStartup.AutoSize = true;
            sStartup.CheckedChanged += delegate { if (!loadingSettings) app.StartupEnabled = sStartup.Checked; };
            row("", sStartup);
            FlowLayoutPanel sys = new FlowLayoutPanel(); sys.AutoSize = true; sys.WrapContents = false;
            Button folder = new Button(); folder.Text = "Open data folder"; folder.AutoSize = true;
            folder.Click += delegate { Directory.CreateDirectory(Settings.Dir); Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); };
            sys.Controls.Add(folder);
            if (!app.IsAdmin)
            {
                Button el = new Button(); el.Text = "Restart as administrator"; el.AutoSize = true; el.Click += delegate { app.RestartElevated(); };
                sys.Controls.Add(el);
            }
            row("", sys);
            Label about = new Label(); about.AutoSize = true; about.ForeColor = Theme.Muted; about.Margin = new Padding(0, 16, 0, 10);
            about.Text = TrayApp.AppName + " " + Application.ProductVersion + (app.IsAdmin ? "  ·  running as administrator" : "") +
                         "\nSettings and usage history are stored in " + Settings.Dir;
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
            sStartup.Checked = app.StartupEnabled;
            foreach (Button b in new Button[] { bDown, bUp, bBack, bText, bTbText })
            {
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
            s.Unit = sUnit.SelectedIndex == 1 ? SpeedUnit.Bits : SpeedUnit.Bytes;
            if (sInterval.SelectedIndex >= 0) app.SetTimerInterval(Intervals[sInterval.SelectedIndex]);
            if (sAdapter.SelectedItem != null) s.Adapter = ((KeyValuePair<string, string>)sAdapter.SelectedItem).Key;
            s.MonthlyLimitGB = (double)sLimit.Value;
            s.MonthStartDay = (int)sStartDay.Value;
            savingUi = true;
            try
            {
                if (sShowMeter.Checked != meterWas) app.ToggleMeter();
                if (sTaskbar.Checked != s.ShowOnTaskbar) app.ToggleTaskbar();
                app.Changed();
                if (clickWas != s.ClickThrough) app.MeterClickThroughChanged();
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

        public void SettingsChanged()
        {
            if (loadingSettings || savingUi) return;
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
                case 2: TickUsage(); break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { icons.Dispose(); uiFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
