using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace NetSpeedBoss
{
    public class LagDiagnosis
    {
        public string Grade = "-";
        public double IdlePingMs = -1;
        public double DownLoadPingMs = -1;
        public double UpLoadPingMs = -1;
        public double DownBps, UpBps;
        public long Hop1RouterMs = -1;
        public long Hop2IspMs = -1;
        public string Hop1Ip = "", Hop2Ip = "";
        public string TopBgApp = "";
        public double TopBgMbps;
        public string Verdict = "";
        public DateTime CheckedAt;
    }

    /// <summary>
    /// Bufferbloat & "Why am I lagging?" diagnostic engine:
    /// compares idle latency vs loaded latency during download/upload bursts,
    /// checks Hop 1 (router) & Hop 2 (ISP) RTT, and checks background ETW traffic.
    /// </summary>
    public class LagDoctor
    {
        const string PingUrl = "https://speed.cloudflare.com/__down?bytes=0";
        const string DownUrl = "https://speed.cloudflare.com/__down?bytes=15000000";
        const string UpUrl = "https://speed.cloudflare.com/__up";

        volatile bool cancelled, running;
        readonly List<HttpWebRequest> reqs = new List<HttpWebRequest>();
        readonly object gate = new object();

        public bool Running { get { return running; } }
        public LagDiagnosis LastResult;

        public void Cancel()
        {
            cancelled = true;
            lock (gate)
            {
                foreach (HttpWebRequest r in reqs) try { r.Abort(); } catch { }
                reqs.Clear();
            }
        }

        public bool Start(TrayApp app, Action<string, int> onProgress, Action<LagDiagnosis, string> onDone)
        {
            if (running) return false;
            cancelled = false;
            running = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    if (ServicePointManager.DefaultConnectionLimit < 12) ServicePointManager.DefaultConnectionLimit = 12;

                    LagDiagnosis res = new LagDiagnosis();
                    res.CheckedAt = DateTime.Now;

                    if (onProgress != null) onProgress("Checking router & ISP hops...", 10);
                    ProbeHops(out res.Hop1Ip, out res.Hop1RouterMs, out res.Hop2Ip, out res.Hop2IspMs);
                    if (cancelled) { Done(null, "Cancelled", onDone); return; }

                    if (onProgress != null) onProgress("Measuring idle latency...", 25);
                    res.IdlePingMs = MeasureMedianPing(5, 900);
                    if (cancelled) { Done(null, "Cancelled", onDone); return; }

                    if (onProgress != null) onProgress("Testing download under load (Bufferbloat)...", 45);
                    res.DownBps = MeasureLoadedPing(true, 4, 3800, out res.DownLoadPingMs);
                    if (cancelled) { Done(null, "Cancelled", onDone); return; }

                    if (onProgress != null) onProgress("Testing upload under load (Bufferbloat)...", 75);
                    res.UpBps = MeasureLoadedPing(false, 3, 3500, out res.UpLoadPingMs);
                    if (cancelled) { Done(null, "Cancelled", onDone); return; }

                    if (app != null && app.Procs.EtwActive)
                    {
                        double best = 0; string bName = "";
                        foreach (TrayApp.AppSeries a in app.AppNow)
                        {
                            if (a.Name.IndexOf("netspeedboss", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            double r = a.Rx + a.Tx;
                            if (r > best) { best = r; bName = a.Name; }
                        }
                        if (best > 131072) { res.TopBgApp = bName; res.TopBgMbps = best * 8 / 1e6; }
                    }

                    ComputeGradeAndVerdict(res);
                    LastResult = res;
                    Done(res, "", onDone);
                }
                catch (Exception ex)
                {
                    Done(null, cancelled ? "Cancelled" : ex.Message, onDone);
                }
            });
            return true;
        }

        void Done(LagDiagnosis res, string err, Action<LagDiagnosis, string> onDone)
        {
            running = false;
            if (onDone != null) onDone(res, err);
        }

        static double MeasureMedianPing(int count, int timeoutMs)
        {
            List<double> samples = new List<double>();
            using (Ping p = new Ping())
            {
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        PingReply r = p.Send("1.1.1.1", timeoutMs);
                        sw.Stop();
                        if (r != null && r.Status == IPStatus.Success) samples.Add(r.RoundtripTime);
                    }
                    catch { }
                    Thread.Sleep(60);
                }
            }
            if (samples.Count == 0) return -1;
            samples.Sort();
            return samples[samples.Count / 2];
        }

        static void ProbeHops(out string hop1Ip, out long hop1Ms, out string hop2Ip, out long hop2Ms)
        {
            hop1Ip = ""; hop1Ms = -1; hop2Ip = ""; hop2Ms = -1;
            try
            {
                using (Ping p = new Ping())
                {
                    byte[] buf = new byte[16];
                    PingOptions o1 = new PingOptions(1, true);
                    PingReply r1 = p.Send("1.1.1.1", 800, buf, o1);
                    if (r1 != null && r1.Address != null && (r1.Status == IPStatus.TtlExpired || r1.Status == IPStatus.Success))
                    {
                        hop1Ip = r1.Address.ToString();
                        PingReply p1 = p.Send(hop1Ip, 600);
                        if (p1 != null && p1.Status == IPStatus.Success) hop1Ms = p1.RoundtripTime;
                    }
                    PingOptions o2 = new PingOptions(2, true);
                    PingReply r2 = p.Send("1.1.1.1", 900, buf, o2);
                    if (r2 != null && r2.Address != null && (r2.Status == IPStatus.TtlExpired || r2.Status == IPStatus.Success))
                    {
                        hop2Ip = r2.Address.ToString();
                        PingReply p2 = p.Send(hop2Ip, 700);
                        if (p2 != null && p2.Status == IPStatus.Success) hop2Ms = p2.RoundtripTime;
                    }
                }
            }
            catch { }
        }

        double MeasureLoadedPing(bool download, int streams, int durationMs, out double loadedPingMs)
        {
            long bytes = 0;
            Stopwatch sw = Stopwatch.StartNew();
            ManualResetEvent doneEvt = new ManualResetEvent(false);
            int alive = streams;
            byte[] upPayload = download ? null : new byte[512 * 1024];

            for (int i = 0; i < streams; i++)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    byte[] buf = download ? new byte[65536] : null;
                    try
                    {
                        while (!cancelled && sw.ElapsedMilliseconds < durationMs)
                        {
                            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(download ? DownUrl : UpUrl);
                            req.Timeout = 4000; req.ReadWriteTimeout = 4000; req.UserAgent = "NetSpeedBoss";
                            lock (gate) reqs.Add(req);
                            try
                            {
                                if (download)
                                {
                                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                                    using (Stream s = resp.GetResponseStream())
                                    {
                                        int n;
                                        while (!cancelled && sw.ElapsedMilliseconds < durationMs && (n = s.Read(buf, 0, buf.Length)) > 0)
                                            Interlocked.Add(ref bytes, n);
                                    }
                                }
                                else
                                {
                                    req.Method = "POST";
                                    req.ContentType = "application/octet-stream";
                                    req.ContentLength = upPayload.Length;
                                    using (Stream rs = req.GetRequestStream())
                                    {
                                        int off = 0;
                                        while (!cancelled && off < upPayload.Length && sw.ElapsedMilliseconds < durationMs)
                                        {
                                            int ch = Math.Min(65536, upPayload.Length - off);
                                            rs.Write(upPayload, off, ch);
                                            off += ch;
                                            Interlocked.Add(ref bytes, ch);
                                        }
                                    }
                                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) { }
                                }
                            }
                            catch { if (cancelled) break; Thread.Sleep(40); }
                            finally { lock (gate) reqs.Remove(req); }
                        }
                    }
                    finally { if (Interlocked.Decrement(ref alive) == 0) doneEvt.Set(); }
                });
            }

            List<double> pings = new List<double>();
            using (Ping p = new Ping())
            {
                while (!doneEvt.WaitOne(280))
                {
                    if (cancelled) break;
                    if (sw.ElapsedMilliseconds >= 600)
                    {
                        try
                        {
                            PingReply r = p.Send("1.1.1.1", 800);
                            if (r != null && r.Status == IPStatus.Success) pings.Add(r.RoundtripTime);
                        }
                        catch { }
                    }
                }
            }
            if (pings.Count > 0) { pings.Sort(); loadedPingMs = pings[pings.Count / 2]; }
            else loadedPingMs = -1;
            double sec = Math.Max(0.2, Math.Min(sw.ElapsedMilliseconds, durationMs) / 1000.0);
            return Interlocked.Read(ref bytes) / sec;
        }

        static void ComputeGradeAndVerdict(LagDiagnosis res)
        {
            double idle = res.IdlePingMs >= 0 ? res.IdlePingMs : 0;
            double downBloat = res.DownLoadPingMs >= 0 && res.IdlePingMs >= 0 ? Math.Max(0, res.DownLoadPingMs - idle) : 0;
            double upBloat = res.UpLoadPingMs >= 0 && res.IdlePingMs >= 0 ? Math.Max(0, res.UpLoadPingMs - idle) : 0;
            double worst = Math.Max(downBloat, upBloat);

            if (res.IdlePingMs < 0) res.Grade = "?";
            else if (worst < 5) res.Grade = "A+";
            else if (worst < 15) res.Grade = "A";
            else if (worst < 30) res.Grade = "B";
            else if (worst < 60) res.Grade = "C";
            else if (worst < 200) res.Grade = "D";
            else res.Grade = "F";

            List<string> parts = new List<string>();
            if (res.Hop1RouterMs >= 0) parts.Add("Router " + res.Hop1RouterMs + "ms");
            if (res.Hop2IspMs >= 0) parts.Add("ISP hop " + res.Hop2IspMs + "ms");
            if (res.IdlePingMs >= 0) parts.Add("Idle " + res.IdlePingMs.ToString("0") + "ms");
            parts.Add("Load +" + worst.ToString("0") + "ms (Down +" + downBloat.ToString("0") + " / Up +" + upBloat.ToString("0") + ")");
            if (!string.IsNullOrEmpty(res.TopBgApp))
                parts.Add("Bg traffic: " + res.TopBgApp + " " + res.TopBgMbps.ToString("0.0") + " Mbps");
            res.Verdict = string.Join(" · ", parts.ToArray());
        }
    }

    public class OutageRecord
    {
        public DateTime StartedAt, EndedAt;
        public int DurationSec;
        public string Reason = "";
    }

    /// <summary>Lightweight ISP outage & uptime monitor logged to Documents\NetSpeed Boss\outages.csv.</summary>
    public class OutageTracker
    {
        public static string OutagePath { get { return Path.Combine(Settings.LogDir, "outages.csv"); } }
        readonly object gate = new object();
        DateTime outageStart = DateTime.MinValue;
        string outageReason = "";
        int totalProbes, failedProbes;
        bool busy;
        DateTime lastProbeUtc = DateTime.MinValue;

        public double TodayUptimePct
        {
            get
            {
                lock (gate)
                {
                    if (totalProbes <= 0) return 100.0;
                    return Math.Max(0, Math.Min(100.0, 100.0 * (totalProbes - failedProbes) / totalProbes));
                }
            }
        }
        public int TodayDropCount { get; private set; }
        public bool CurrentlyDown { get { lock (gate) return outageStart != DateTime.MinValue; } }

        public void TickProbe(int activeWanCount, bool enabled)
        {
            if (!enabled || busy || (DateTime.UtcNow - lastProbeUtc).TotalSeconds < 10) return;
            busy = true;
            lastProbeUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    bool up = false;
                    string reason = "";
                    if (activeWanCount <= 0)
                    {
                        reason = "No active gateway / WAN";
                    }
                    else
                    {
                        try
                        {
                            using (Ping p = new Ping())
                            {
                                PingReply r1 = p.Send("1.1.1.1", 1200);
                                if (r1 != null && r1.Status == IPStatus.Success) up = true;
                                else
                                {
                                    PingReply r2 = p.Send("8.8.8.8", 1200);
                                    if (r2 != null && r2.Status == IPStatus.Success) up = true;
                                    else reason = "Internet unreachable (1.1.1.1 & 8.8.8.8 timeout)";
                                }
                            }
                        }
                        catch { reason = "Ping exception"; }
                    }

                    lock (gate)
                    {
                        totalProbes++;
                        if (!up)
                        {
                            failedProbes++;
                            if (outageStart == DateTime.MinValue)
                            {
                                outageStart = DateTime.Now;
                                outageReason = reason;
                                TodayDropCount++;
                            }
                        }
                        else if (outageStart != DateTime.MinValue)
                        {
                            DateTime end = DateTime.Now;
                            int dur = Math.Max(1, (int)(end - outageStart).TotalSeconds);
                            AppendCsv(outageStart, end, dur, outageReason);
                            outageStart = DateTime.MinValue;
                            outageReason = "";
                        }
                    }
                }
                finally { busy = false; }
            });
        }

        public void FlushOpenOutage()
        {
            lock (gate)
            {
                if (outageStart != DateTime.MinValue)
                {
                    DateTime end = DateTime.Now;
                    int dur = Math.Max(1, (int)(end - outageStart).TotalSeconds);
                    AppendCsv(outageStart, end, dur, outageReason + " (app exit)");
                    outageStart = DateTime.MinValue;
                }
            }
        }

        static void AppendCsv(DateTime start, DateTime end, int durSec, string reason)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogDir);
                bool isNew = !File.Exists(OutagePath);
                string hdr = "Started,Ended,Duration(s),Reason\r\n";
                string row = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss},{2},{3}\r\n",
                    start, end, durSec, (reason ?? "").Replace(',', ' '));
                File.AppendAllText(OutagePath, (isNew ? hdr : "") + row, Encoding.UTF8);
            }
            catch { }
        }

        public static List<OutageRecord> LoadRecent(int maxRows)
        {
            List<OutageRecord> list = new List<OutageRecord>();
            try
            {
                if (!File.Exists(OutagePath)) return list;
                string[] lines;
                using (FileStream fs = new FileStream(OutagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                    lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                CultureInfo inv = CultureInfo.InvariantCulture;
                for (int i = lines.Length - 1; i >= 0 && list.Count < maxRows; i--)
                {
                    string[] p = lines[i].Split(',');
                    if (p.Length < 4) continue;
                    DateTime st, en; int dur;
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", inv, DateTimeStyles.None, out st)) continue;
                    DateTime.TryParseExact(p[1], "yyyy-MM-dd HH:mm:ss", inv, DateTimeStyles.None, out en);
                    int.TryParse(p[2], out dur);
                    OutageRecord r = new OutageRecord();
                    r.StartedAt = st; r.EndedAt = en; r.DurationSec = dur; r.Reason = p[3];
                    list.Add(r);
                }
            }
            catch { }
            return list;
        }
    }

    /// <summary>Trusted LAN MAC registry & first-time network app memory stored under %AppData%\NetSpeedBoss.</summary>
    public static class GuardStore
    {
        static string DevicesPath { get { return Path.Combine(Settings.Dir, "known-devices.ini"); } }
        static string AppsPath { get { return Path.Combine(Settings.Dir, "known-apps.txt"); } }
        static readonly object gate = new object();
        static Dictionary<string, string> trustedMacs;
        static HashSet<string> knownApps;

        static void EnsureLoaded()
        {
            if (trustedMacs != null) return;
            trustedMacs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(DevicesPath))
                {
                    foreach (string line in File.ReadAllLines(DevicesPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) trustedMacs[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }

            knownApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(AppsPath))
                    foreach (string line in File.ReadAllLines(AppsPath))
                        if (!string.IsNullOrEmpty(line.Trim())) knownApps.Add(line.Trim());
            }
            catch { }
        }

        public static string GetTrustedLabel(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            lock (gate) { EnsureLoaded(); string lbl; return trustedMacs.TryGetValue(mac, out lbl) ? lbl : null; }
        }

        public static void TrustDevice(string mac, string label)
        {
            if (string.IsNullOrEmpty(mac)) return;
            lock (gate)
            {
                EnsureLoaded();
                trustedMacs[mac.ToUpperInvariant()] = string.IsNullOrEmpty(label) ? "Trusted" : label.Replace('=', ' ').Trim();
                SaveDevices();
            }
        }

        public static void UntrustDevice(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return;
            lock (gate) { EnsureLoaded(); trustedMacs.Remove(mac); SaveDevices(); }
        }

        public static bool HasAnyTrustedDevices()
        {
            lock (gate) { EnsureLoaded(); return trustedMacs.Count > 0; }
        }

        static void SaveDevices()
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in trustedMacs) sb.Append(kv.Key).Append('=').Append(kv.Value).AppendLine();
                File.WriteAllText(DevicesPath, sb.ToString());
            }
            catch { }
        }

        /// <summary>Returns true the first time a process name accesses the network (once baselines exist).</summary>
        public static bool RecordAppAndCheckNew(string appName, out bool baselineSeeded)
        {
            baselineSeeded = false;
            if (string.IsNullOrEmpty(appName)) return false;
            lock (gate)
            {
                EnsureLoaded();
                if (knownApps.Count == 0) baselineSeeded = true;
                if (knownApps.Contains(appName)) return false;
                knownApps.Add(appName);
                try { Directory.CreateDirectory(Settings.Dir); File.AppendAllText(AppsPath, appName + Environment.NewLine); } catch { }
                return !baselineSeeded;
            }
        }
    }
}
