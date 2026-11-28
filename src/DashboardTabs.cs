using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    public partial class Dashboard
    {
        // =================================================================== NETWORK TAB
        readonly LanScanner lanScanner = new LanScanner();
        readonly List<LanHostResult> lanResults = new List<LanHostResult>();
        TextBox txtScanRange;
        CheckBox chkScanDns, chkScanPorts, chkWatchLan;
        Button btnScan, btnScanExport, btnNetSpeedTest, btnLagDoctor, btnHistMode, btnOutageMode;
        Label lblScanStatus, lblNetWan, lblNetSpeedStat, lblLagGrade, lblLagVerdict;
        ProgressBar netSpeedBar, lanProgressBar;
        DbListView lanList, netHistList;
        SpecList wifiSpecs;
        DateTime lastWifiRefresh = DateTime.MinValue;
        bool netRangeInit, wifiBusy, showOutagesMode;

        TabPage BuildNetwork()
        {
            TabPage p = Page("Network");

            // Top row: Speedtest & Bufferbloat Doctor (left) + Wireless & ISP Uptime (middle) + Speedtest/Outage History (right)
            TableLayoutPanel topRow = new TableLayoutPanel();
            topRow.Dock = DockStyle.Top; topRow.Height = 224; topRow.ColumnCount = 3; topRow.RowCount = 1;
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            topRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Card 0: Multi-Stream Speedtest & Bufferbloat Lag Doctor
            Panel speedHost = new Panel(); speedHost.Dock = DockStyle.Fill; speedHost.Padding = new Padding(6, 4, 4, 6);
            CardPanel speedCard = new CardPanel(); speedCard.Dock = DockStyle.Fill; speedCard.Padding = new Padding(12, 10, 12, 10);
            Label stTitle = new Label(); stTitle.Text = "Speed Test & Bufferbloat Doctor"; stTitle.Font = new Font("Segoe UI Semibold", 9.5f);
            stTitle.Dock = DockStyle.Top; stTitle.Height = 22;
            lblNetWan = new Label(); lblNetWan.Text = "Detecting WAN connections..."; lblNetWan.Dock = DockStyle.Top; lblNetWan.Height = 20; lblNetWan.Tag = "muted";
            netSpeedBar = new ProgressBar(); netSpeedBar.Dock = DockStyle.Top; netSpeedBar.Height = 14; netSpeedBar.Maximum = 100;

            lblNetSpeedStat = new Label(); lblNetSpeedStat.Text = "Ready — multi-stream Speedtest + Bufferbloat Check";
            lblNetSpeedStat.Dock = DockStyle.Top; lblNetSpeedStat.Height = 32; lblNetSpeedStat.TextAlign = ContentAlignment.MiddleLeft;
            lblNetSpeedStat.Font = new Font("Segoe UI Semibold", 9f);

            Panel lagRow = new Panel(); lagRow.Dock = DockStyle.Fill; lagRow.Tag = "card";
            lblLagGrade = new Label(); lblLagGrade.Text = "-"; lblLagGrade.Dock = DockStyle.Left; lblLagGrade.Width = 42;
            lblLagGrade.TextAlign = ContentAlignment.MiddleCenter; lblLagGrade.Font = new Font("Segoe UI Semibold", 11f);
            lblLagGrade.Tag = "keepcolor"; lblLagGrade.ForeColor = Theme.Muted;
            lblLagVerdict = new Label(); lblLagVerdict.Dock = DockStyle.Fill; lblLagVerdict.TextAlign = ContentAlignment.MiddleLeft;
            lblLagVerdict.Padding = new Padding(6, 0, 0, 0); lblLagVerdict.Tag = "muted";
            lblLagVerdict.Text = "Click 'Bufferbloat' to test idle vs load latency + hops";
            lagRow.Controls.Add(lblLagVerdict); lagRow.Controls.Add(lblLagGrade);

            FlowLayoutPanel stBtns = new FlowLayoutPanel(); stBtns.Dock = DockStyle.Bottom; stBtns.Height = 32;
            btnNetSpeedTest = new Button(); btnNetSpeedTest.Text = "Speedtest"; btnNetSpeedTest.Size = new Size(86, 26);
            btnNetSpeedTest.Tag = "primary"; btnNetSpeedTest.FlatStyle = FlatStyle.Flat; btnNetSpeedTest.FlatAppearance.BorderSize = 0;
            btnNetSpeedTest.BackColor = Theme.Accent; btnNetSpeedTest.ForeColor = Color.White;
            btnNetSpeedTest.Font = new Font("Segoe UI Semibold", 8.5f); btnNetSpeedTest.Cursor = Cursors.Hand;
            btnNetSpeedTest.Click += delegate { ToggleSpeedTest(); };

            btnLagDoctor = new Button(); btnLagDoctor.Text = "Bufferbloat"; btnLagDoctor.Size = new Size(88, 26);
            btnLagDoctor.Click += delegate { ToggleLagDoctor(); };

            Button netDnsBtn = new Button(); netDnsBtn.Text = "DNS Check"; netDnsBtn.Size = new Size(82, 26);
            netDnsBtn.Click += delegate { if (btnDnsBench != null) btnDnsBench.PerformClick(); };
            stBtns.Controls.Add(btnNetSpeedTest); stBtns.Controls.Add(btnLagDoctor); stBtns.Controls.Add(netDnsBtn);

            speedCard.Controls.Add(lagRow); speedCard.Controls.Add(stBtns); speedCard.Controls.Add(lblNetSpeedStat);
            speedCard.Controls.Add(netSpeedBar); speedCard.Controls.Add(lblNetWan); speedCard.Controls.Add(stTitle);
            speedHost.Controls.Add(speedCard);

            // Card 1: Wireless (Wi-Fi) + ISP Uptime & Plan SLA
            Panel wifiHost = new Panel(); wifiHost.Dock = DockStyle.Fill; wifiHost.Padding = new Padding(4, 4, 4, 6);
            CardPanel wifiWrap = new CardPanel(); wifiWrap.Dock = DockStyle.Fill; wifiWrap.Padding = new Padding(8, 6, 8, 6);
            Panel wifiBar = new Panel(); wifiBar.Dock = DockStyle.Bottom; wifiBar.Height = 26; wifiBar.Tag = "card";
            Button setPlanBtn = new Button(); setPlanBtn.Text = "Plan"; setPlanBtn.Size = new Size(56, 22); setPlanBtn.Dock = DockStyle.Right;
            setPlanBtn.Click += delegate { PromptIspPlan(); };
            Button wifiChBtn = new Button(); wifiChBtn.Text = "Wi-Fi Channels"; wifiChBtn.Size = new Size(100, 22); wifiChBtn.Dock = DockStyle.Left;
            wifiChBtn.Click += delegate { ShowWifiChannelsDialog(); };
            Button expSlaBtn = new Button(); expSlaBtn.Text = "ISP Report"; expSlaBtn.Size = new Size(76, 22); expSlaBtn.Dock = DockStyle.Fill;
            expSlaBtn.Click += delegate { ExportIspReport(); };
            wifiBar.Controls.Add(expSlaBtn); wifiBar.Controls.Add(wifiChBtn); wifiBar.Controls.Add(setPlanBtn);
            wifiSpecs = new SpecList(); wifiSpecs.Heading = "Wireless & ISP Uptime"; wifiSpecs.Loading = "Reading Wi-Fi & uptime status...";
            wifiSpecs.Dock = DockStyle.Fill;
            wifiWrap.Controls.Add(wifiSpecs); wifiWrap.Controls.Add(wifiBar);
            wifiHost.Controls.Add(wifiWrap);

            // Card 2: Speedtest History & ISP Outages Toggle
            Panel histHost = new Panel(); histHost.Dock = DockStyle.Fill; histHost.Padding = new Padding(4, 4, 6, 6);
            CardPanel histCard = new CardPanel(); histCard.Dock = DockStyle.Fill; histCard.Padding = new Padding(10, 8, 10, 8);
            FlowLayoutPanel histHdr = new FlowLayoutPanel(); histHdr.Dock = DockStyle.Top; histHdr.Height = 28; histHdr.Tag = "card"; histHdr.WrapContents = false;
            btnHistMode = new Button(); btnHistMode.Text = "Speed History"; btnHistMode.Size = new Size(98, 22);
            btnHistMode.Click += delegate { showOutagesMode = false; RefreshNetworkRightCard(); };
            btnOutageMode = new Button(); btnOutageMode.Text = "Outages"; btnOutageMode.Size = new Size(72, 22);
            btnOutageMode.Click += delegate { showOutagesMode = true; RefreshNetworkRightCard(); };
            Button histCsv = new Button(); histCsv.Text = "Logs"; histCsv.Size = new Size(50, 22);
            histCsv.Click += delegate { OpenLogFolder(); };
            histHdr.Controls.AddRange(new Control[] { btnHistMode, btnOutageMode, histCsv });

            netHistList = new DbListView(); netHistList.Dock = DockStyle.Fill;
            ConfigureHistoryColumns();
            histCard.Controls.Add(netHistList); histCard.Controls.Add(histHdr);
            histHost.Controls.Add(histCard);

            topRow.Controls.Add(speedHost, 0, 0);
            topRow.Controls.Add(wifiHost, 1, 0);
            topRow.Controls.Add(histHost, 2, 0);

            // Bottom Fill: Angry-IP-Scanner-style LAN Subnet Scanner & Device Watchdog
            Panel scanWrap = new Panel(); scanWrap.Dock = DockStyle.Fill; scanWrap.Padding = new Padding(6, 2, 6, 6);
            CardPanel scanCard = new CardPanel(); scanCard.Dock = DockStyle.Fill; scanCard.Padding = new Padding(10, 8, 10, 8);

            FlowLayoutPanel scanBar = new FlowLayoutPanel(); scanBar.Dock = DockStyle.Top; scanBar.Height = 36; scanBar.WrapContents = false;
            scanBar.Tag = "card";
            Label sl = new Label(); sl.Text = "LAN IP Scanner"; sl.Font = new Font("Segoe UI Semibold", 9.5f); sl.AutoSize = true; sl.Margin = new Padding(0, 7, 8, 0);
            Label rl = new Label(); rl.Text = "Range:"; rl.AutoSize = true; rl.Margin = new Padding(0, 7, 4, 0);
            txtScanRange = new TextBox(); txtScanRange.Width = 142; txtScanRange.BorderStyle = BorderStyle.FixedSingle; txtScanRange.Margin = new Padding(0, 4, 6, 0);
            chkScanDns = new CheckBox(); chkScanDns.Text = "Names"; chkScanDns.Checked = true; chkScanDns.AutoSize = true; chkScanDns.Margin = new Padding(2, 6, 6, 0);
            chkScanPorts = new CheckBox(); chkScanPorts.Text = "Ports"; chkScanPorts.Checked = false; chkScanPorts.AutoSize = true; chkScanPorts.Margin = new Padding(0, 6, 6, 0);
            chkWatchLan = new CheckBox(); chkWatchLan.Text = "Watch new devices"; chkWatchLan.AutoSize = true; chkWatchLan.Margin = new Padding(0, 6, 8, 0);
            chkWatchLan.CheckedChanged += delegate { if (!loadingSettings) { app.S.WatchNewLanDevices = chkWatchLan.Checked; app.S.Save(); } };
            btnScan = new Button(); btnScan.Text = "Scan LAN"; btnScan.Size = new Size(84, 26); btnScan.Margin = new Padding(0, 3, 6, 0);
            btnScan.Tag = "primary"; btnScan.FlatStyle = FlatStyle.Flat; btnScan.FlatAppearance.BorderSize = 0;
            btnScan.BackColor = Theme.Accent; btnScan.ForeColor = Color.White; btnScan.Font = new Font("Segoe UI Semibold", 8.5f); btnScan.Cursor = Cursors.Hand;
            btnScan.Click += delegate { ToggleLanScan(); };
            btnScanExport = new Button(); btnScanExport.Text = "Export CSV"; btnScanExport.Size = new Size(82, 26); btnScanExport.Margin = new Padding(0, 3, 0, 0);
            btnScanExport.Click += delegate { ExportLanCsv(); };
            scanBar.Controls.AddRange(new Control[] { sl, rl, txtScanRange, chkScanDns, chkScanPorts, chkWatchLan, btnScan, btnScanExport });

            Panel statBar = new Panel(); statBar.Dock = DockStyle.Top; statBar.Height = 24; statBar.Tag = "card";
            lanProgressBar = new ProgressBar(); lanProgressBar.Dock = DockStyle.Left; lanProgressBar.Width = 160; lanProgressBar.Maximum = 100;
            lblScanStatus = new Label(); lblScanStatus.Dock = DockStyle.Fill; lblScanStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblScanStatus.Padding = new Padding(10, 0, 0, 0); lblScanStatus.Tag = "muted";
            lblScanStatus.Text = "Ready — scans ICMP + ARP MACs; trust known devices to enable Wi-Fi/LAN intruder watch";
            statBar.Controls.Add(lblScanStatus); statBar.Controls.Add(lanProgressBar);

            lanList = new DbListView(); lanList.Dock = DockStyle.Fill; lanList.MultiSelect = false;
            Col(lanList, "IP Address", 114, HorizontalAlignment.Left);
            Col(lanList, "Status / Trust", 132, HorizontalAlignment.Left);
            Col(lanList, "Ping", 58, HorizontalAlignment.Right);
            Col(lanList, "Hostname", 190, HorizontalAlignment.Left);
            Col(lanList, "MAC Address", 132, HorizontalAlignment.Left);
            Col(lanList, "Open Ports", 160, HorizontalAlignment.Left);

            ContextMenuStrip hm = new ContextMenuStrip(); hm.Renderer = ModernMenuRenderer.Instance;
            hm.Items.Add("Trust / Label device...", null, delegate { TrustSelectedLanDevice(true); });
            hm.Items.Add("Remove trusted status", null, delegate { TrustSelectedLanDevice(false); });
            hm.Items.Add("Wake device (Wake-on-LAN)", null, delegate { WakeSelectedLanDevice(); });
            hm.Items.Add(new ToolStripSeparator());
            hm.Items.Add("Copy IP address", null, delegate { string ip = SelectedLanIp(); if (ip != null) Clipboard.SetText(ip); });
            hm.Items.Add("Copy MAC address", null, delegate { string mac = SelectedLanMac(); if (!string.IsNullOrEmpty(mac)) Clipboard.SetText(mac); });
            hm.Items.Add("Open http://<IP> in browser", null, delegate { string ip = SelectedLanIp(); if (ip != null) try { Process.Start("http://" + ip); } catch { } });
            hm.Items.Add("Open \\\\<IP> shared folder", null, delegate { string ip = SelectedLanIp(); if (ip != null) try { Process.Start("explorer.exe", "\\\\" + ip); } catch { } });
            hm.Items.Add("Continuous ping in terminal", null, delegate { string ip = SelectedLanIp(); if (ip != null) try { Process.Start("cmd.exe", "/k ping -t " + ip); } catch { } });
            lanList.ContextMenuStrip = hm;

            scanCard.Controls.Add(lanList); scanCard.Controls.Add(statBar); scanCard.Controls.Add(scanBar);
            scanWrap.Controls.Add(scanCard);

            p.Controls.Add(scanWrap); p.Controls.Add(topRow);
            RefreshNetworkRightCard();
            return p;
        }

        void ConfigureHistoryColumns()
        {
            if (netHistList == null) return;
            netHistList.Columns.Clear();
            if (!showOutagesMode)
            {
                Col(netHistList, "Time", 76, HorizontalAlignment.Left);
                Col(netHistList, "Down", 72, HorizontalAlignment.Right);
                Col(netHistList, "Up", 72, HorizontalAlignment.Right);
                Col(netHistList, "Ping/SLA", 62, HorizontalAlignment.Right);
            }
            else
            {
                Col(netHistList, "Started", 104, HorizontalAlignment.Left);
                Col(netHistList, "Duration", 64, HorizontalAlignment.Right);
                Col(netHistList, "Reason", 130, HorizontalAlignment.Left);
            }
        }

        string SelectedLanIp()
        {
            if (lanList == null || lanList.SelectedItems.Count == 0) return null;
            return lanList.SelectedItems[0].Text;
        }

        string SelectedLanMac()
        {
            if (lanList == null || lanList.SelectedItems.Count == 0 || lanList.SelectedItems[0].SubItems.Count <= 4) return null;
            return lanList.SelectedItems[0].SubItems[4].Text;
        }

        void TrustSelectedLanDevice(bool trust)
        {
            string mac = SelectedLanMac();
            if (string.IsNullOrEmpty(mac)) { MessageBox.Show("Select a host with a resolved MAC address.", TrayApp.AppName); return; }
            if (!trust)
            {
                GuardStore.UntrustDevice(mac);
            }
            else
            {
                string defName = lanList.SelectedItems[0].SubItems[3].Text;
                if (string.IsNullOrEmpty(defName)) defName = SelectedLanIp() ?? "Trusted Device";
                using (Form f = new Form())
                {
                    f.Text = "Trust LAN Device"; f.Icon = GetAppIcon();
                    f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                    f.MaximizeBox = false; f.MinimizeBox = false; f.Size = new Size(340, 156);
                    f.BackColor = Theme.Bg; f.ForeColor = Theme.Ink; f.Font = uiFont;
                    Label l = new Label(); l.Text = "Friendly label for MAC " + mac + ":"; l.SetBounds(14, 14, 300, 22);
                    TextBox tb = new TextBox(); tb.Text = defName; tb.SetBounds(14, 40, 296, 24);
                    Button ok = new Button(); ok.Text = "Trust"; ok.SetBounds(154, 78, 76, 26);
                    Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(236, 78, 76, 26);
                    ok.Click += delegate { GuardStore.TrustDevice(mac, tb.Text); f.Close(); };
                    cancel.Click += delegate { f.Close(); };
                    f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                    ApplyThemeTo(f);
                    f.ShowDialog(this);
                }
            }
            foreach (ListViewItem it in lanList.Items)
            {
                if (it.SubItems.Count > 4 && it.SubItems[4].Text == mac)
                {
                    string lbl = GuardStore.GetTrustedLabel(mac);
                    it.SubItems[1].Text = lbl != null ? "✔ " + lbl : (GuardStore.HasAnyTrustedDevices() ? "New Device!" : "Alive");
                }
            }
        }

        void RefreshSpeedHistoryList() { RefreshNetworkRightCard(); }

        void RefreshNetworkRightCard()
        {
            if (netHistList == null) return;
            ConfigureHistoryColumns();
            netHistList.BeginUpdate();
            netHistList.Items.Clear();
            if (!showOutagesMode)
            {
                List<SpeedTestRecord> rows = SpeedTester.LoadHistory(25);
                foreach (SpeedTestRecord r in rows)
                {
                    string right = r.PingMs >= 0 ? r.PingMs.ToString("0") + "ms" : "-";
                    if (app.S.PlanDownMbps > 0 && r.DownloadBps > 0)
                        right = Math.Min(999, (r.DownloadBps * 8 / 1e6) / app.S.PlanDownMbps * 100.0).ToString("0") + "% SLA";
                    netHistList.Items.Add(new ListViewItem(new string[] {
                        r.Timestamp.ToString("MM-dd HH:mm"),
                        Fmt.Speed(r.DownloadBps, app.S.Unit),
                        Fmt.Speed(r.UploadBps, app.S.Unit),
                        right
                    }));
                }
            }
            else
            {
                List<OutageRecord> rows = OutageTracker.LoadRecent(25);
                foreach (OutageRecord r in rows)
                {
                    string dur = r.DurationSec >= 60 ? (r.DurationSec / 60) + "m " + (r.DurationSec % 60) + "s" : r.DurationSec + "s";
                    netHistList.Items.Add(new ListViewItem(new string[] {
                        r.StartedAt.ToString("MM-dd HH:mm:ss"),
                        dur,
                        r.Reason
                    }));
                }
            }
            netHistList.EndUpdate();
        }

        void ToggleLagDoctor()
        {
            if (app.LagDoc.Running)
            {
                app.LagDoc.Cancel();
                btnLagDoctor.Text = "Bufferbloat";
                return;
            }
            btnLagDoctor.Text = "Stop";
            lblLagGrade.Text = "...";
            lblLagGrade.ForeColor = Theme.Muted;
            app.LagDoc.Start(app,
                delegate(string msg, int pct)
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((MethodInvoker)delegate
                            {
                                netSpeedBar.Value = Math.Max(0, Math.Min(100, pct));
                                lblLagVerdict.Text = msg;
                            });
                    }
                    catch { }
                },
                delegate(LagDiagnosis res, string err)
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((MethodInvoker)delegate
                            {
                                btnLagDoctor.Text = "Bufferbloat";
                                netSpeedBar.Value = res != null ? 100 : 0;
                                if (res == null)
                                {
                                    lblLagGrade.Text = "!";
                                    lblLagGrade.ForeColor = Color.FromArgb(220, 38, 38);
                                    lblLagVerdict.Text = string.IsNullOrEmpty(err) ? "Test stopped" : err;
                                    return;
                                }
                                lblLagGrade.Text = res.Grade;
                                lblLagGrade.ForeColor = (res.Grade == "A+" || res.Grade == "A") ? Color.FromArgb(16, 185, 129)
                                    : ((res.Grade == "B" || res.Grade == "C") ? Color.FromArgb(217, 119, 6) : Color.FromArgb(220, 38, 38));
                                lblLagVerdict.Text = res.Verdict;
                            });
                    }
                    catch { }
                });
        }

        void PromptIspPlan()
        {
            using (Form f = new Form())
            {
                f.Text = "Advertised ISP Speed Plan"; f.Icon = GetAppIcon();
                f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false; f.Size = new Size(340, 180);
                f.BackColor = Theme.Bg; f.ForeColor = Theme.Ink; f.Font = uiFont;
                Label l1 = new Label(); l1.Text = "Paid Download (Mbps, 0 = off):"; l1.SetBounds(16, 16, 200, 22);
                NumericUpDown nDn = new NumericUpDown(); nDn.Maximum = 100000; nDn.Value = (decimal)app.S.PlanDownMbps; nDn.SetBounds(220, 14, 90, 24);
                Label l2 = new Label(); l2.Text = "Paid Upload (Mbps, 0 = off):"; l2.SetBounds(16, 52, 200, 22);
                NumericUpDown nUp = new NumericUpDown(); nUp.Maximum = 100000; nUp.Value = (decimal)app.S.PlanUpMbps; nUp.SetBounds(220, 50, 90, 24);
                Button ok = new Button(); ok.Text = "Save"; ok.SetBounds(150, 98, 76, 26);
                Button cancel = new Button(); cancel.Text = "Cancel"; cancel.SetBounds(234, 98, 76, 26);
                ok.Click += delegate
                {
                    app.S.PlanDownMbps = (double)nDn.Value; app.S.PlanUpMbps = (double)nUp.Value;
                    app.S.Save(); lastWifiRefresh = DateTime.MinValue; RefreshWifiCardAsync(); RefreshNetworkRightCard(); f.Close();
                };
                cancel.Click += delegate { f.Close(); };
                f.Controls.AddRange(new Control[] { l1, nDn, l2, nUp, ok, cancel });
                ApplyThemeTo(f);
                f.ShowDialog(this);
            }
        }

        void ExportIspReport()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Text report|*.txt";
                d.FileName = "isp-sla-outage-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("NetSpeed Boss — ISP Accountability & SLA Report");
                    sb.AppendLine("===============================================");
                    sb.AppendLine("Generated:     " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Session Uptime:{0:0.00}% ({1} detected drop(s))", app.Outages.TodayUptimePct, app.Outages.TodayDropCount));
                    if (app.S.PlanDownMbps > 0)
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Advertised Plan: {0:0} Mbps Down / {1:0} Mbps Up", app.S.PlanDownMbps, app.S.PlanUpMbps));
                    if (app.LagDoc.LastResult != null)
                        sb.AppendLine("Bufferbloat:   Grade " + app.LagDoc.LastResult.Grade + " — " + app.LagDoc.LastResult.Verdict);
                    sb.AppendLine();
                    sb.AppendLine("Recent Speedtest Runs:");
                    foreach (SpeedTestRecord r in SpeedTester.LoadHistory(20))
                    {
                        double dnM = r.DownloadBps * 8 / 1e6, upM = r.UploadBps * 8 / 1e6;
                        string sla = app.S.PlanDownMbps > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0:0}% of plan)", dnM / app.S.PlanDownMbps * 100.0) : "";
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0:yyyy-MM-dd HH:mm}  Down {1:0.0} Mbps{2}  Up {3:0.0} Mbps  Ping {4:0} ms  WANs {5}",
                            r.Timestamp, dnM, sla, upM, r.PingMs, r.WanCount));
                    }
                    sb.AppendLine();
                    sb.AppendLine("Logged Outages:");
                    List<OutageRecord> outs = OutageTracker.LoadRecent(30);
                    if (outs.Count == 0) sb.AppendLine("  None logged.");
                    foreach (OutageRecord o in outs)
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0:yyyy-MM-dd HH:mm:ss} -> {1:HH:mm:ss} ({2}s) : {3}", o.StartedAt, o.EndedAt, o.DurationSec, o.Reason));
                    File.WriteAllText(d.FileName, sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
            }
        }

        void SyncNetworkSpeedUi(SpeedTestState st)
        {
            if (btnNetSpeedTest == null || IsDisposed) return;
            SpeedUnit u = app.S.Unit;
            netSpeedBar.Value = Math.Max(0, Math.Min(100, st.ProgressPct));
            switch (st.Phase)
            {
                case SpeedTestPhase.Ping:
                    btnNetSpeedTest.Text = "Stop";
                    lblNetSpeedStat.Text = "Measuring ping & jitter...";
                    break;
                case SpeedTestPhase.Download:
                    btnNetSpeedTest.Text = "Stop";
                    lblNetSpeedStat.Text = "Download: " + Fmt.Speed(st.DownloadBps, u) + " (" + st.ProgressPct + "%)";
                    break;
                case SpeedTestPhase.Upload:
                    btnNetSpeedTest.Text = "Stop";
                    lblNetSpeedStat.Text = "Down: " + Fmt.Speed(st.DownloadBps, u) + " | Up: " + Fmt.Speed(st.UploadBps, u);
                    break;
                case SpeedTestPhase.Done:
                    btnNetSpeedTest.Text = "Speedtest";
                    lblNetSpeedStat.Text = "↓ " + Fmt.Speed(st.DownloadBps, u) + "   ↑ " + Fmt.Speed(st.UploadBps, u) +
                        (st.PingMs >= 0 ? "   ·   " + st.PingMs.ToString("0") + " ms" : "");
                    break;
                case SpeedTestPhase.Cancelled:
                    btnNetSpeedTest.Text = "Speedtest";
                    lblNetSpeedStat.Text = "Speedtest cancelled";
                    break;
                case SpeedTestPhase.Error:
                    btnNetSpeedTest.Text = "Speedtest";
                    lblNetSpeedStat.Text = "Failed: " + st.Error;
                    break;
            }
        }

        void TickNetwork()
        {
            if (!netRangeInit && txtScanRange != null)
            {
                netRangeInit = true;
                txtScanRange.Text = LanScanner.DefaultRange(app.Net.Adapters);
            }
            if (lblNetWan != null)
            {
                lblNetWan.Text = app.Net.IsMultiWan
                    ? "● " + app.Net.ActiveWanCount + " active WANs (Load balanced aggregate)"
                    : (app.Net.ActiveWanCount == 1 ? "Single active WAN (" + app.CurrentNetProfile + ")" : "No gateway detected");
            }
            if (btnNetSpeedTest != null) btnNetSpeedTest.BackColor = Theme.Accent;
            if (btnScan != null) btnScan.BackColor = Theme.Accent;
            RefreshWifiCardAsync();
        }

        void RefreshWifiCardAsync()
        {
            if (wifiSpecs == null || wifiBusy || (DateTime.UtcNow - lastWifiRefresh).TotalSeconds < 4) return;
            wifiBusy = true;
            lastWifiRefresh = DateTime.UtcNow;
            double upPct = app.Outages.TodayUptimePct;
            int drops = app.Outages.TodayDropCount;
            double planDown = app.S.PlanDownMbps;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<SpecRow> rows = NetworkInfo.GatherWifiRows();
                rows.Add(new SpecRow("ISP Uptime", upPct.ToString("0.00") + "% today   ·   " + drops + " drop(s)", null));
                if (planDown > 0)
                {
                    List<SpeedTestRecord> hist = SpeedTester.LoadHistory(1);
                    if (hist.Count > 0)
                    {
                        double lastM = hist[0].DownloadBps * 8 / 1e6;
                        rows.Add(new SpecRow("Plan SLA", string.Format("{0:0.0} / {1:0} Mbps ({2:0}%)", lastM, planDown, lastM / planDown * 100.0), null));
                    }
                    else rows.Add(new SpecRow("Plan SLA", planDown.ToString("0") + " Mbps plan (run Speedtest)", null));
                }
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((MethodInvoker)delegate
                        {
                            wifiBusy = false;
                            if (!IsDisposed && wifiSpecs != null) { wifiSpecs.Rows = rows; wifiSpecs.Invalidate(); }
                        });
                    else wifiBusy = false;
                }
                catch { wifiBusy = false; }
            });
        }

        void ToggleLanScan()
        {
            if (lanScanner.Running)
            {
                lanScanner.Cancel();
                btnScan.Text = "Scan LAN";
                lblScanStatus.Text = "Stopping scan...";
                return;
            }
            lanResults.Clear();
            lanList.Items.Clear();
            lanProgressBar.Value = 0;
            btnScan.Text = "Stop";
            lblScanStatus.Text = "Scanning " + txtScanRange.Text + "...";
            bool started = lanScanner.Start(txtScanRange.Text, chkScanDns.Checked, chkScanPorts.Checked,
                delegate(LanHostResult host, int done, int total)
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (IsDisposed) return;
                                lanProgressBar.Value = Math.Max(0, Math.Min(100, done * 100 / Math.Max(1, total)));
                                if (host != null)
                                {
                                    lanResults.Add(host);
                                    lanResults.Sort(delegate(LanHostResult a, LanHostResult b) { return a.IpOrder.CompareTo(b.IpOrder); });
                                    int pos = lanResults.IndexOf(host);
                                    string lbl = GuardStore.GetTrustedLabel(host.Mac);
                                    string trustSt = lbl != null ? "✔ " + lbl
                                        : (GuardStore.HasAnyTrustedDevices() && !string.IsNullOrEmpty(host.Mac) ? "New Device!" : host.Status);
                                    ListViewItem it = new ListViewItem(new string[] {
                                        host.Ip, trustSt,
                                        host.LatencyMs >= 0 ? host.LatencyMs + " ms" : "<1 ms",
                                        host.Hostname, host.Mac, host.OpenPorts
                                    });
                                    lanList.Items.Insert(Math.Max(0, Math.Min(pos, lanList.Items.Count)), it);
                                }
                                lblScanStatus.Text = string.Format("Scanned {0}/{1}  ·  {2} alive host(s)", done, total, lanResults.Count);
                            });
                    }
                    catch { }
                },
                delegate(bool wasCancelled, string err)
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (IsDisposed) return;
                                btnScan.Text = "Scan LAN";
                                if (!string.IsNullOrEmpty(err))
                                {
                                    lblScanStatus.Text = err;
                                    MessageBox.Show(err, TrayApp.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }
                                else
                                {
                                    lanProgressBar.Value = wasCancelled ? lanProgressBar.Value : 100;
                                    lblScanStatus.Text = (wasCancelled ? "Scan stopped  ·  " : "Scan complete  ·  ") + lanResults.Count + " alive host(s)";
                                }
                            });
                    }
                    catch { }
                });
            if (!started) btnScan.Text = "Scan LAN";
        }

        void ExportLanCsv()
        {
            if (lanResults.Count == 0) { MessageBox.Show("Run a LAN scan first.", TrayApp.AppName); return; }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV files|*.csv";
                d.FileName = "lan-scan-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv";
                if (d.ShowDialog(this) == DialogResult.OK)
                    try { LanScanner.ExportCsv(d.FileName, lanResults); } catch (Exception ex) { MessageBox.Show(ex.Message, TrayApp.AppName); }
            }
        }

        void WakeSelectedLanDevice()
        {
            string mac = SelectedLanMac();
            if (string.IsNullOrEmpty(mac)) { MessageBox.Show("Select a host with a resolved MAC address.", TrayApp.AppName); return; }
            string err;
            if (LanScanner.SendWakeOnLan(mac, out err))
                MessageBox.Show("Sent Wake-on-LAN magic packet to " + mac + " (UDP port 9 & 7).", TrayApp.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show("Wake-on-LAN failed: " + err, TrayApp.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void ShowWifiChannelsDialog()
        {
            WifiChannelReport rep = NetworkInfo.AnalyzeWifiChannels();
            using (Form f = new Form())
            {
                f.Text = "Wi-Fi Channel & Neighbor Congestion Analyzer"; f.Icon = GetAppIcon();
                f.StartPosition = FormStartPosition.CenterParent; f.Size = new Size(680, 460);
                f.BackColor = Theme.Bg; f.ForeColor = Theme.Ink; f.Font = uiFont;

                Label rec = new Label(); rec.Dock = DockStyle.Top; rec.Height = 36; rec.Padding = new Padding(12, 8, 12, 4);
                rec.Font = new Font("Segoe UI Semibold", 9.5f); rec.Tag = "keepcolor";
                rec.ForeColor = Theme.IsDark ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 124, 16);
                rec.Text = rep.Error.Length > 0 ? rep.Error : rep.Recommendation;

                DbListView lv = new DbListView(); lv.Dock = DockStyle.Fill;
                Col(lv, "SSID", 180, HorizontalAlignment.Left);
                Col(lv, "Channel", 70, HorizontalAlignment.Right);
                Col(lv, "Band", 80, HorizontalAlignment.Center);
                Col(lv, "Signal", 72, HorizontalAlignment.Right);
                Col(lv, "Radio", 110, HorizontalAlignment.Left);
                Col(lv, "BSSID", 136, HorizontalAlignment.Left);
                rep.AccessPoints.Sort(delegate(WifiNeighborAp a, WifiNeighborAp b) { return b.SignalPct.CompareTo(a.SignalPct); });
                foreach (WifiNeighborAp ap in rep.AccessPoints)
                    lv.Items.Add(new ListViewItem(new string[] {
                        ap.Ssid, ap.Channel > 0 ? ap.Channel.ToString() : "-", ap.Band, ap.SignalPct + "%", ap.Radio, ap.Bssid
                    }));

                FlowLayoutPanel bot = new FlowLayoutPanel(); bot.Dock = DockStyle.Bottom; bot.Height = 38; bot.FlowDirection = FlowDirection.RightToLeft;
                bot.Padding = new Padding(8, 5, 8, 5);
                Button close = new Button(); close.Text = "Close"; close.AutoSize = true; close.Click += delegate { f.Close(); };
                bot.Controls.Add(close);
                f.Controls.Add(lv); f.Controls.Add(bot); f.Controls.Add(rec);
                ApplyThemeTo(f);
                f.ShowDialog(this);
            }
        }

        // =================================================================== DISPLAYS TAB
        readonly List<MonitorItem> monitors = new List<MonitorItem>();
        FlowLayoutPanel displaysListHost;
        TrackBar masterBrightness;
        Label lblMasterBrightness;
        bool displaysLoaded;

        TabPage BuildDisplays()
        {
            TabPage p = Page("Displays");

            CardPanel top = new CardPanel(); top.Radius = 8; top.Dock = DockStyle.Top; top.Height = 52; top.Padding = new Padding(12, 8, 12, 8);
            FlowLayoutPanel row = new FlowLayoutPanel(); row.Dock = DockStyle.Fill; row.WrapContents = false; row.Tag = "card";
            Label tl = new Label(); tl.Text = "All Monitors Brightness:"; tl.Font = new Font("Segoe UI Semibold", 9.5f); tl.AutoSize = true; tl.Margin = new Padding(0, 8, 8, 0);
            masterBrightness = new TrackBar(); masterBrightness.Minimum = 0; masterBrightness.Maximum = 100; masterBrightness.Value = 70;
            masterBrightness.TickFrequency = 10; masterBrightness.Width = 200; masterBrightness.Margin = new Padding(0, 2, 6, 0);
            lblMasterBrightness = new Label(); lblMasterBrightness.Text = "70%"; lblMasterBrightness.AutoSize = true; lblMasterBrightness.Margin = new Padding(0, 8, 12, 0);
            masterBrightness.Scroll += delegate
            {
                lblMasterBrightness.Text = masterBrightness.Value + "%";
                foreach (MonitorItem m in monitors) DisplayControl.SetBrightness(m, masterBrightness.Value);
                RefreshMonitorsUiOnly();
            };

            Action<string, int> addPreset = delegate(string name, int val)
            {
                Button b = new Button(); b.Text = name; b.AutoSize = true; b.Margin = new Padding(0, 4, 6, 0);
                b.Click += delegate
                {
                    masterBrightness.Value = val; lblMasterBrightness.Text = val + "%";
                    foreach (MonitorItem m in monitors) DisplayControl.SetBrightness(m, val);
                    RefreshMonitorsUiOnly();
                };
                row.Controls.Add(b);
            };
            row.Controls.Add(tl); row.Controls.Add(masterBrightness); row.Controls.Add(lblMasterBrightness);
            addPreset("Night (25%)", 25);
            addPreset("Desk (65%)", 65);
            addPreset("Daylight (95%)", 95);

            Button refBtn = new Button(); refBtn.Text = "Rescan Monitors"; refBtn.AutoSize = true; refBtn.Margin = new Padding(8, 4, 0, 0);
            refBtn.Click += delegate { displaysLoaded = false; RefreshMonitors(); };
            row.Controls.Add(refBtn);
            top.Controls.Add(row);

            Panel wrap = new Panel(); wrap.Dock = DockStyle.Fill; wrap.AutoScroll = true; wrap.Padding = new Padding(0, 8, 0, 4);
            displaysListHost = new FlowLayoutPanel(); displaysListHost.Dock = DockStyle.Top; displaysListHost.AutoSize = true;
            displaysListHost.FlowDirection = FlowDirection.TopDown; displaysListHost.WrapContents = false;
            wrap.Controls.Add(displaysListHost);

            p.Controls.Add(wrap); p.Controls.Add(top);
            return p;
        }

        void TickDisplays()
        {
            if (!displaysLoaded) RefreshMonitors();
        }

        void RefreshMonitors()
        {
            displaysLoaded = true;
            DisposeDisplays();
            monitors.AddRange(DisplayControl.Enumerate());
            RefreshMonitorsUiOnly();
        }

        void RefreshMonitorsUiOnly()
        {
            if (displaysListHost == null) return;
            displaysListHost.SuspendLayout();
            displaysListHost.Controls.Clear();
            foreach (MonitorItem item in monitors)
            {
                MonitorItem m = item;
                CardPanel card = new CardPanel(); card.Width = Math.Max(780, displaysListHost.Parent.ClientSize.Width - 28);
                card.Height = 148; card.Margin = new Padding(4, 0, 4, 10); card.Padding = new Padding(14, 10, 14, 10);

                Label title = new Label();
                title.Text = m.FriendlyName + (m.IsPrimary ? "  (Primary)" : "") + "   ·   " + m.Width + "×" + m.Height + " @ " + m.CurrentHz + " Hz";
                title.Font = new Font("Segoe UI Semibold", 10f); title.Dock = DockStyle.Top; title.Height = 24;

                TableLayoutPanel body = new TableLayoutPanel(); body.Dock = DockStyle.Fill; body.ColumnCount = 4; body.RowCount = 2;
                body.Tag = "card";
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

                // Brightness
                Label lb = new Label(); lb.Text = "Brightness:"; lb.AutoSize = true; lb.Margin = new Padding(0, 10, 0, 0);
                FlowLayoutPanel bp = new FlowLayoutPanel(); bp.Dock = DockStyle.Fill; bp.WrapContents = false; bp.Tag = "card";
                if (m.HasBrightness)
                {
                    int bPct = m.BrightMax > m.BrightMin ? (int)Math.Round(100.0 * (m.BrightCur - m.BrightMin) / (m.BrightMax - m.BrightMin)) : 70;
                    bPct = Math.Max(0, Math.Min(100, bPct));
                    TrackBar tb = new TrackBar(); tb.Minimum = 0; tb.Maximum = 100; tb.Value = bPct; tb.Width = 170; tb.TickFrequency = 20;
                    Label bv = new Label(); bv.Text = bPct + "%"; bv.AutoSize = true; bv.Margin = new Padding(4, 10, 0, 0);
                    tb.Scroll += delegate { bv.Text = tb.Value + "%"; DisplayControl.SetBrightness(m, tb.Value); };
                    bp.Controls.Add(tb); bp.Controls.Add(bv);
                }
                else { Label na = new Label(); na.Text = "DDC/CI unsupported"; na.Tag = "muted"; na.AutoSize = true; na.Margin = new Padding(0, 10, 0, 0); bp.Controls.Add(na); }

                // Contrast
                Label lc = new Label(); lc.Text = "Contrast:"; lc.AutoSize = true; lc.Margin = new Padding(8, 10, 0, 0);
                FlowLayoutPanel cp = new FlowLayoutPanel(); cp.Dock = DockStyle.Fill; cp.WrapContents = false; cp.Tag = "card";
                if (m.HasContrast)
                {
                    int cPct = m.ContrastMax > m.ContrastMin ? (int)Math.Round(100.0 * (m.ContrastCur - m.ContrastMin) / (m.ContrastMax - m.ContrastMin)) : 50;
                    cPct = Math.Max(0, Math.Min(100, cPct));
                    TrackBar tc = new TrackBar(); tc.Minimum = 0; tc.Maximum = 100; tc.Value = cPct; tc.Width = 170; tc.TickFrequency = 20;
                    Label cv = new Label(); cv.Text = cPct + "%"; cv.AutoSize = true; cv.Margin = new Padding(4, 10, 0, 0);
                    tc.Scroll += delegate { cv.Text = tc.Value + "%"; DisplayControl.SetContrast(m, tc.Value); };
                    cp.Controls.Add(tc); cp.Controls.Add(cv);
                }
                else { Label na = new Label(); na.Text = m.IsLaptopWmi ? "Internal panel" : "DDC/CI unsupported"; na.Tag = "muted"; na.AutoSize = true; na.Margin = new Padding(0, 10, 0, 0); cp.Controls.Add(na); }

                // Refresh Rate
                Label lhz = new Label(); lhz.Text = "Refresh Hz:"; lhz.AutoSize = true; lhz.Margin = new Padding(0, 10, 0, 0);
                FlowLayoutPanel hp = new FlowLayoutPanel(); hp.Dock = DockStyle.Fill; hp.WrapContents = false; hp.Tag = "card";
                ComboBox cbHz = new ComboBox(); cbHz.DropDownStyle = ComboBoxStyle.DropDownList; cbHz.Width = 105; cbHz.Margin = new Padding(0, 6, 6, 0);
                foreach (int hz in m.AvailableHz) cbHz.Items.Add(hz + " Hz");
                int curIdx = m.AvailableHz.IndexOf(m.CurrentHz);
                if (curIdx >= 0) cbHz.SelectedIndex = curIdx;
                Button bApplyHz = new Button(); bApplyHz.Text = "Apply"; bApplyHz.AutoSize = true; bApplyHz.Margin = new Padding(0, 5, 0, 0);
                bApplyHz.Click += delegate
                {
                    if (cbHz.SelectedIndex >= 0)
                    {
                        int hz = m.AvailableHz[cbHz.SelectedIndex];
                        if (DisplayControl.SetRefreshHz(m, hz)) RefreshMonitors();
                        else MessageBox.Show("Display driver rejected " + hz + " Hz for this mode.", TrayApp.AppName);
                    }
                };
                hp.Controls.Add(cbHz); hp.Controls.Add(bApplyHz);

                // Input Source (VCP 0x60)
                Label lin = new Label(); lin.Text = "Input Source:"; lin.AutoSize = true; lin.Margin = new Padding(8, 10, 0, 0);
                FlowLayoutPanel ip = new FlowLayoutPanel(); ip.Dock = DockStyle.Fill; ip.WrapContents = false; ip.Tag = "card";
                if (m.HasInputVcp)
                {
                    uint[] codes = { 17, 18, 15, 16, 27 };
                    ComboBox cbIn = new ComboBox(); cbIn.DropDownStyle = ComboBoxStyle.DropDownList; cbIn.Width = 128; cbIn.Margin = new Padding(0, 6, 6, 0);
                    foreach (uint c in codes) cbIn.Items.Add(DisplayControl.InputLabel(c));
                    int ci = Array.IndexOf(codes, m.CurrentInput);
                    if (ci >= 0) cbIn.SelectedIndex = ci;
                    Button bSw = new Button(); bSw.Text = "Switch"; bSw.AutoSize = true; bSw.Margin = new Padding(0, 5, 0, 0);
                    bSw.Click += delegate
                    {
                        if (cbIn.SelectedIndex >= 0 && !DisplayControl.SetInputSource(m, codes[cbIn.SelectedIndex]))
                            MessageBox.Show("Monitor did not switch input via DDC/CI.", TrayApp.AppName);
                    };
                    ip.Controls.Add(cbIn); ip.Controls.Add(bSw);
                }
                else { Label na = new Label(); na.Text = "Current: " + (m.IsLaptopWmi ? "Embedded eDP" : "Direct GPU"); na.Tag = "muted"; na.AutoSize = true; na.Margin = new Padding(0, 10, 0, 0); ip.Controls.Add(na); }

                body.Controls.Add(lb, 0, 0); body.Controls.Add(bp, 1, 0); body.Controls.Add(lc, 2, 0); body.Controls.Add(cp, 3, 0);
                body.Controls.Add(lhz, 0, 1); body.Controls.Add(hp, 1, 1); body.Controls.Add(lin, 2, 1); body.Controls.Add(ip, 3, 1);
                card.Controls.Add(body); card.Controls.Add(title);
                displaysListHost.Controls.Add(card);
            }
            ApplyThemeTo(displaysListHost);
            displaysListHost.ResumeLayout();
        }

        void DisposeDisplays()
        {
            foreach (MonitorItem m in monitors) m.Dispose();
            monitors.Clear();
        }

        // =================================================================== ABOUT TAB
        TabPage BuildAbout()
        {
            TabPage p = Page("About");
            p.AutoScroll = true;

            Panel container = new Panel(); container.Dock = DockStyle.Top; container.Height = 684; container.Padding = new Padding(8, 4, 8, 8);

            CardPanel hero = new CardPanel(); hero.Dock = DockStyle.Top; hero.Height = 178; hero.Padding = new Padding(18, 14, 18, 14);
            Panel iconBox = new Panel(); iconBox.Dock = DockStyle.Left; iconBox.Width = 104; iconBox.Tag = "card";
            iconBox.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                Icon ic = GetAppIcon();
                if (ic != null)
                {
                    using (Icon big = new Icon(ic, new Size(128, 128)))
                    using (Bitmap bmp = big.ToBitmap())
                        e.Graphics.DrawImage(bmp, 6, 18, 84, 84);
                }
            };

            Panel heroText = new Panel(); heroText.Dock = DockStyle.Fill; heroText.Tag = "card"; heroText.Padding = new Padding(10, 2, 0, 2);
            Label hTitle = new Label(); hTitle.Text = "NetSpeed Boss  v" + Updater.CurrentVersionString();
            hTitle.Font = new Font("Segoe UI Semibold", 15f); hTitle.Dock = DockStyle.Top; hTitle.Height = 32;
            Label hSub = new Label(); hSub.Text = "Published by Quantum Step   ·   MIT Open Source License   ·   Windows 10 / 11";
            hSub.Tag = "muted"; hSub.Dock = DockStyle.Top; hSub.Height = 22;
            Label hDesc = new Label();
            hDesc.Text = "Real-time taskbar & floating network monitor, multi-WAN aggregation & speedtest, Bufferbloat doctor, LAN IP/ARP watchdog, Gaming HUD, Display brightness & per-app controls.";
            hDesc.Dock = DockStyle.Fill; hDesc.Padding = new Padding(0, 4, 0, 4);
            FlowLayoutPanel heroBtns = new FlowLayoutPanel(); heroBtns.Dock = DockStyle.Bottom; heroBtns.Height = 36; heroBtns.WrapContents = false; heroBtns.Tag = "card";
            heroBtns.Padding = new Padding(0, 4, 0, 0);
            Button bUpd = new Button(); bUpd.Text = Updater.IsStoreApp() ? "Managed via Microsoft Store" : "Check for updates";
            bUpd.Enabled = !Updater.IsStoreApp(); bUpd.AutoSize = true;
            bUpd.Click += delegate { app.CheckForUpdatesAsync(true, null); };
            Button bWeb = new Button(); bWeb.Text = "Open Website"; bWeb.AutoSize = true;
            bWeb.Click += delegate { OpenUrl("https://merchanthasan2.github.io/NetSpeedBoss/"); };
            Button bGh = new Button(); bGh.Text = "GitHub Source"; bGh.AutoSize = true;
            bGh.Click += delegate { OpenUrl("https://github.com/merchanthasan2/NetSpeedBoss"); };
            heroBtns.Controls.AddRange(new Control[] { bUpd, bWeb, bGh });
            heroText.Controls.Add(hDesc); heroText.Controls.Add(heroBtns); heroText.Controls.Add(hSub); heroText.Controls.Add(hTitle);
            hero.Controls.Add(heroText); hero.Controls.Add(iconBox);

            Panel gap = new Panel(); gap.Dock = DockStyle.Top; gap.Height = 12;

            Label dlHdr = new Label(); dlHdr.Text = "Download, Install & Updates Across Platforms";
            dlHdr.Font = new Font("Segoe UI Semibold", 10.5f); dlHdr.Dock = DockStyle.Top; dlHdr.Height = 28; dlHdr.Padding = new Padding(4, 2, 0, 0);

            TableLayoutPanel grid = new TableLayoutPanel(); grid.Dock = DockStyle.Top; grid.Height = 446;
            grid.ColumnCount = 2; grid.RowCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));

            grid.Controls.Add(StoreCard("Microsoft Store", "Official NetSpeed Boss MSIX package by Quantum Step.\nAutomatic Microsoft Store updates.",
                Color.FromArgb(0, 120, 212), "msstore", "Open Store",
                delegate { OpenStorePage(); }, null), 0, 0);

            grid.Controls.Add(StoreCard("GitHub Releases", "Direct Setup installer (.exe), Portable (.zip), MSIX Bundle & release notes.",
                Color.FromArgb(36, 41, 47), "github", "Open Releases",
                delegate { OpenUrl("https://github.com/merchanthasan2/NetSpeedBoss/releases"); }, null), 1, 0);

            grid.Controls.Add(StoreCard("WinGet (Windows Package Manager)", "Install or upgrade from Windows Terminal:",
                Color.FromArgb(14, 165, 233), "winget", "View Package",
                delegate { OpenUrl("https://github.com/microsoft/winget-pkgs/pulls?q=Net.NetSpeedBoss"); },
                "winget install Net.NetSpeedBoss"), 0, 1);

            grid.Controls.Add(StoreCard("Scoop", "Portable bucket for PowerShell users:",
                Color.FromArgb(16, 185, 129), "scoop", "Open Bucket",
                delegate { OpenUrl("https://github.com/merchanthasan2/scoop-bucket"); },
                "scoop bucket add netspeedboss https://github.com/merchanthasan2/scoop-bucket && scoop install netspeedboss"), 1, 1);

            grid.Controls.Add(StoreCard("Chocolatey", "Community Chocolatey installer package:",
                Color.FromArgb(139, 92, 246), "choco", "Open Package",
                delegate { OpenUrl("https://community.chocolatey.org/packages/netspeedboss"); },
                "choco install netspeedboss"), 0, 2);

            grid.Controls.Add(StoreCard("Official Documentation & Website", "Feature guides, Gaming HUD setup, dual-WAN overview & privacy policy.",
                Color.FromArgb(245, 158, 11), "web", "Visit Website",
                delegate { OpenUrl("https://merchanthasan2.github.io/NetSpeedBoss/"); }, null), 1, 2);

            container.Controls.Add(grid); container.Controls.Add(dlHdr); container.Controls.Add(gap); container.Controls.Add(hero);
            p.Controls.Add(container);
            return p;
        }

        Control StoreCard(string title, string desc, Color badgeColor, string kind, string openLabel, Action onOpen, string cliCmd)
        {
            Panel wrap = new Panel(); wrap.Dock = DockStyle.Fill; wrap.Padding = new Padding(5);
            CardPanel card = new CardPanel(); card.Dock = DockStyle.Fill; card.Padding = new Padding(12);

            Panel badge = new Panel(); badge.Dock = DockStyle.Left; badge.Width = 48; badge.Tag = "card";
            badge.Paint += delegate(object s, PaintEventArgs e) { DrawStoreBadge(e.Graphics, new Rectangle(2, 6, 40, 40), badgeColor, kind); };

            Panel body = new Panel(); body.Dock = DockStyle.Fill; body.Padding = new Padding(8, 0, 0, 0); body.Tag = "card";
            Label tl = new Label(); tl.Text = title; tl.Font = new Font("Segoe UI Semibold", 9.5f); tl.Dock = DockStyle.Top; tl.Height = 20;
            Label dl = new Label(); dl.Text = desc; dl.Tag = "muted"; dl.Dock = DockStyle.Top; dl.Height = string.IsNullOrEmpty(cliCmd) ? 38 : 20;

            FlowLayoutPanel btns = new FlowLayoutPanel(); btns.Dock = DockStyle.Bottom; btns.Height = 30; btns.Tag = "card";
            Button bOpen = new Button(); bOpen.Text = openLabel; bOpen.AutoSize = true;
            bOpen.Tag = "primary"; bOpen.FlatStyle = FlatStyle.Flat; bOpen.FlatAppearance.BorderSize = 0;
            bOpen.BackColor = badgeColor; bOpen.ForeColor = Color.White; bOpen.Font = new Font("Segoe UI Semibold", 8.2f);
            bOpen.Cursor = Cursors.Hand; bOpen.Click += delegate { onOpen(); };
            btns.Controls.Add(bOpen);

            if (!string.IsNullOrEmpty(cliCmd))
            {
                Label cmdLbl = new Label(); cmdLbl.Text = cliCmd; cmdLbl.Font = new Font("Consolas", 8f);
                cmdLbl.Dock = DockStyle.Top; cmdLbl.Height = 22; cmdLbl.AutoEllipsis = true;
                body.Controls.Add(cmdLbl);
                Button bCopy = new Button(); bCopy.Text = "Copy command"; bCopy.AutoSize = true;
                bCopy.Click += delegate { try { Clipboard.SetText(cliCmd); bCopy.Text = "Copied ✔"; } catch { } };
                btns.Controls.Add(bCopy);
            }

            body.Controls.Add(dl); body.Controls.Add(tl); body.Controls.Add(btns);
            card.Controls.Add(body); card.Controls.Add(badge);
            wrap.Controls.Add(card);
            return wrap;
        }

        static void DrawStoreBadge(Graphics g, Rectangle r, Color bg, string kind)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = BrandArt.Round(r, 9))
            using (SolidBrush br = new SolidBrush(bg))
                g.FillPath(br, gp);
            using (SolidBrush wb = new SolidBrush(Color.White))
            using (Pen wp = new Pen(Color.White, 1.8f))
            {
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
                if (kind == "msstore")
                {
                    g.DrawArc(wp, cx - 5, r.Y + 6, 10, 8, 180, 180);
                    g.FillRectangle(wb, cx - 7, cy - 3, 6, 6); g.FillRectangle(wb, cx + 1, cy - 3, 6, 6);
                    g.FillRectangle(wb, cx - 7, cy + 5, 6, 6); g.FillRectangle(wb, cx + 1, cy + 5, 6, 6);
                }
                else if (kind == "github")
                {
                    g.DrawEllipse(wp, cx - 9, cy - 9, 18, 18);
                    g.FillEllipse(wb, cx - 4, cy - 2, 3, 3); g.FillEllipse(wb, cx + 1, cy - 2, 3, 3);
                    g.DrawArc(wp, cx - 5, cy + 1, 10, 6, 10, 160);
                }
                else if (kind == "winget")
                {
                    g.DrawLine(wp, cx - 7, cy - 4, cx - 2, cy); g.DrawLine(wp, cx - 7, cy + 4, cx - 2, cy);
                    g.DrawLine(wp, cx, cy + 5, cx + 8, cy + 5);
                }
                else if (kind == "scoop")
                {
                    g.DrawRectangle(wp, cx - 7, cy - 5, 14, 12); g.DrawLine(wp, cx - 7, cy - 1, cx + 7, cy - 1);
                }
                else if (kind == "choco")
                {
                    g.FillRectangle(wb, cx - 7, cy - 6, 14, 12);
                    using (Pen dp = new Pen(bg, 1.5f)) { g.DrawLine(dp, cx, cy - 6, cx, cy + 6); g.DrawLine(dp, cx - 7, cy, cx + 7, cy); }
                }
                else
                {
                    g.DrawEllipse(wp, cx - 8, cy - 8, 16, 16); g.DrawEllipse(wp, cx - 4, cy - 8, 8, 16);
                    g.DrawLine(wp, cx - 8, cy, cx + 8, cy);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        static void OpenUrl(string url) { try { Process.Start(url); } catch { } }
        static void OpenStorePage()
        {
            try { Process.Start("ms-windows-store://pdp/?productid=9NP6V92XWLSH"); }
            catch { OpenUrl("https://apps.microsoft.com/detail/9NP6V92XWLSH"); }
        }
    }
}
