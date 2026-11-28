using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetSpeedBoss
{
    public class PublicIpInfo
    {
        public string Ip = "";
        public string Isp = "";
        public string Location = "";
        public string Error = "";
        public DateTime CheckedAt;
        public bool Ok { get { return Ip.Length > 0 && Error.Length == 0; } }
    }

    public class DnsBenchRow
    {
        public string Name = "";
        public string Server = "";
        public double LatencyMs = -1;
    }

    public class WifiNeighborAp
    {
        public string Ssid = "";
        public string Bssid = "";
        public string Band = "";
        public string Radio = "";
        public int Channel;
        public int SignalPct;
    }

    public class WifiChannelReport
    {
        public List<WifiNeighborAp> AccessPoints = new List<WifiNeighborAp>();
        public string Recommendation = "";
        public string Error = "";
    }

    public static class NetworkInfo
    {
        public static List<SpecRow> GatherLocalRows(List<AdapterInfo> adapters, out string bestLocalIp)
        {
            bestLocalIp = "";
            List<SpecRow> rows = new List<SpecRow>();
            AdapterInfo primary = PickPrimary(adapters);
            if (primary == null)
            {
                rows.Add(new SpecRow("Adapter", "No active network adapter found", null));
                return rows;
            }

            string ipv4 = "", ipv6 = "", gw = "", dns = "", ssid = "";
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Id != primary.Id) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    List<string> v6s = new List<string>();
                    foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && ipv4.Length == 0)
                            ipv4 = u.Address.ToString();
                        else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && v6s.Count < 1)
                            v6s.Add(u.Address.ToString());
                    }
                    ipv6 = string.Join(", ", v6s.ToArray());

                    List<string> gws = new List<string>();
                    foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                        if (g.Address != null && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))
                            gws.Add(g.Address.ToString());
                    gw = string.Join(", ", gws.ToArray());

                    List<string> dnss = new List<string>();
                    foreach (IPAddress d in props.DnsAddresses)
                        if (!d.IsIPv6SiteLocal && dnss.Count < 3) dnss.Add(d.ToString());
                    dns = string.Join(", ", dnss.ToArray());

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        ssid = ReadWifiSsid();
                    break;
                }
            }
            catch { }

            bestLocalIp = ipv4.Length > 0 ? ipv4 : ipv6;
            string link = Fmt.LinkSpeed(primary.LinkSpeed);
            string adapterVal = primary.Name + (ssid.Length > 0 ? " (" + ssid + ")" : "") +
                "   \u00B7   " + primary.Description + (link != "-" ? "   \u00B7   " + link : "");
            rows.Add(new SpecRow("Connection", adapterVal, Brands.Detect(primary.Description)));
            rows.Add(new SpecRow("Local IP", (ipv4.Length > 0 ? ipv4 : "-") + (ipv6.Length > 0 ? "   \u00B7   " + ipv6 : ""), null));
            rows.Add(new SpecRow("Gateway / DNS", (gw.Length > 0 ? gw : "-") + "   \u00B7   DNS " + (dns.Length > 0 ? dns : "-"), null));

            List<string> wans = new List<string>();
            foreach (AdapterInfo a in adapters)
            {
                if (!a.HasGateway) continue;
                string ip = !string.IsNullOrEmpty(a.IPv4) ? a.IPv4 : NetMonitor.GetIPv4(a.Id);
                wans.Add(a.Name + (ip.Length > 0 ? " (" + ip + ")" : ""));
            }
            if (wans.Count >= 2)
                rows.Add(new SpecRow("Load balancing", wans.Count + " active WANs:  " + string.Join("  +  ", wans.ToArray()), null));
            return rows;
        }

        static AdapterInfo PickPrimary(List<AdapterInfo> adapters)
        {
            if (adapters == null || adapters.Count == 0) return null;
            foreach (AdapterInfo a in adapters) if (a.Selected && a.HasGateway) return a;
            foreach (AdapterInfo a in adapters) if (a.HasGateway) return a;
            return adapters[0];
        }

        static string ReadWifiSsid()
        {
            Dictionary<string, string> map = ParseWlanInterface();
            string v;
            return map.TryGetValue("SSID", out v) ? v : "";
        }

        static Dictionary<string, string> ParseWlanInterface()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show interfaces");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string txt = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(1500);
                    string lastKey = "";
                    foreach (string raw in txt.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int col = raw.IndexOf(':');
                        if (col > 0 && raw.TrimStart().IndexOf(':') > 0)
                        {
                            string k = raw.Substring(0, col).Trim();
                            string v = raw.Substring(col + 1).Trim();
                            if (k.Length > 0 && k.IndexOf("on the system", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                map[k] = v;
                                lastKey = k;
                                continue;
                            }
                        }
                        if (lastKey.Length > 0 && raw.StartsWith("  "))
                        {
                            string extra = raw.Trim();
                            if (extra.Length > 0) map[lastKey] = map[lastKey] + " / " + extra;
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        public static List<SpecRow> GatherWifiRows()
        {
            List<SpecRow> rows = new List<SpecRow>();
            Dictionary<string, string> w = ParseWlanInterface();
            if (w.Count == 0)
            {
                rows.Add(new SpecRow("Wi-Fi", "No wireless adapter detected", null));
                return rows;
            }
            Func<string, string> get = delegate(string k) { string v; return w.TryGetValue(k, out v) ? v : ""; };
            string desc = get("Description"), name = get("Name"), state = get("State");
            string ssid = get("SSID"), bssid = get("AP BSSID");
            if (bssid.Length == 0) bssid = get("BSSID");
            string radio = get("Radio type"), band = get("Band"), ch = get("Channel");
            string rxR = get("Receive rate (Mbps)"), txR = get("Transmit rate (Mbps)");
            string sig = get("Signal"), auth = get("Authentication"), cipher = get("Cipher");
            string radioSt = get("Radio status");

            if (desc.Length > 0 || name.Length > 0)
                rows.Add(new SpecRow("Adapter", (name.Length > 0 ? name + " · " : "") + desc, Brands.Detect(desc)));
            rows.Add(new SpecRow("Status", state.Length > 0 ? state : "Unknown", null));

            if (string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase))
            {
                if (ssid.Length > 0) rows.Add(new SpecRow("SSID", ssid + (bssid.Length > 0 ? "  (" + bssid + ")" : ""), null));
                string phy = JoinNonEmpty(" · ", radio, band, ch.Length > 0 ? "Ch " + ch : "");
                if (phy.Length > 0) rows.Add(new SpecRow("Radio / Band", phy, null));
                if (rxR.Length > 0 || txR.Length > 0)
                    rows.Add(new SpecRow("Link / Signal", "↓ " + (rxR.Length > 0 ? rxR : "-") + "  ↑ " + (txR.Length > 0 ? txR : "-") + " Mbps" + (sig.Length > 0 ? "  ·  " + sig : ""), null));
                else if (sig.Length > 0)
                    rows.Add(new SpecRow("Signal", sig, null));
                string sec = JoinNonEmpty(" / ", auth, cipher);
                if (sec.Length > 0) rows.Add(new SpecRow("Security", sec, null));
            }
            else
            {
                if (radioSt.Length > 0) rows.Add(new SpecRow("Radio", radioSt, null));
                string mac = get("Physical address");
                if (mac.Length > 0) rows.Add(new SpecRow("MAC", mac.ToUpperInvariant(), null));
            }
            return rows;
        }

        public static WifiChannelReport AnalyzeWifiChannels()
        {
            WifiChannelReport rep = new WifiChannelReport();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show networks mode=bssid");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
                string txt;
                using (Process p = Process.Start(psi))
                {
                    txt = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                }
                string curSsid = "";
                WifiNeighborAp cur = null;
                foreach (string raw in txt.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase))
                    {
                        int c = line.IndexOf(':');
                        curSsid = c > 0 ? line.Substring(c + 1).Trim() : "(Hidden SSID)";
                        if (curSsid.Length == 0) curSsid = "(Hidden SSID)";
                    }
                    else if (line.StartsWith("BSSID ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cur != null) rep.AccessPoints.Add(cur);
                        cur = new WifiNeighborAp();
                        cur.Ssid = curSsid;
                        int c = line.IndexOf(':');
                        cur.Bssid = c > 0 ? line.Substring(c + 1).Trim().ToUpperInvariant() : "";
                    }
                    else if (cur != null)
                    {
                        int c = line.IndexOf(':');
                        if (c <= 0) continue;
                        string k = line.Substring(0, c).Trim();
                        string v = line.Substring(c + 1).Trim();
                        if (k.Equals("Signal", StringComparison.OrdinalIgnoreCase))
                        {
                            int pct; if (int.TryParse(v.Replace("%", "").Trim(), out pct)) cur.SignalPct = pct;
                        }
                        else if (k.Equals("Channel", StringComparison.OrdinalIgnoreCase))
                        {
                            int ch; if (int.TryParse(v, out ch)) cur.Channel = ch;
                        }
                        else if (k.Equals("Radio type", StringComparison.OrdinalIgnoreCase))
                        {
                            cur.Radio = v;
                        }
                        else if (k.Equals("Band", StringComparison.OrdinalIgnoreCase))
                        {
                            cur.Band = v;
                        }
                    }
                }
                if (cur != null) rep.AccessPoints.Add(cur);

                foreach (WifiNeighborAp ap in rep.AccessPoints)
                    if (ap.Band.Length == 0 && ap.Channel > 0)
                        ap.Band = ap.Channel <= 14 ? "2.4 GHz" : "5 GHz";

                if (rep.AccessPoints.Count == 0)
                {
                    rep.Recommendation = "No nearby Wi-Fi APs detected (ensure Wi-Fi radio is turned on).";
                    return rep;
                }

                // Best non-overlapping 2.4 GHz channel (1, 6, 11) weighted by overlapping neighbors
                int[] ch24 = { 1, 6, 11 };
                int best24 = 1, best24Score = int.MaxValue;
                foreach (int c24 in ch24)
                {
                    int score = 0;
                    foreach (WifiNeighborAp ap in rep.AccessPoints)
                        if (ap.Channel >= 1 && ap.Channel <= 14 && Math.Abs(ap.Channel - c24) <= 3)
                            score += Math.Max(10, ap.SignalPct) * (ap.Channel == c24 ? 2 : 1);
                    if (score < best24Score) { best24Score = score; best24 = c24; }
                }

                // Best common 5 GHz channel
                int[] ch5 = { 36, 40, 44, 48, 149, 153, 157, 161 };
                int best5 = 36, best5Score = int.MaxValue;
                foreach (int c5 in ch5)
                {
                    int score = 0;
                    foreach (WifiNeighborAp ap in rep.AccessPoints)
                        if (ap.Channel == c5) score += Math.Max(10, ap.SignalPct);
                    if (score < best5Score) { best5Score = score; best5 = c5; }
                }

                rep.Recommendation = string.Format("Detected {0} nearby BSSID(s) · Cleanest 2.4 GHz: Ch {1} · Cleanest 5 GHz: Ch {2}",
                    rep.AccessPoints.Count, best24, best5);
            }
            catch (Exception ex) { rep.Error = ex.Message; }
            return rep;
        }

        /// <summary>On-demand HTTPS lookup for public IP and ISP; never called automatically in the background.</summary>
        public static PublicIpInfo LookupPublicIp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            PublicIpInfo info = TryIpInfo();
            if (info.Ok) return info;

            PublicIpInfo fallback = TryIpWhoIs();
            if (fallback.Ok) return fallback;

            if (info.Error.Length == 0) info.Error = fallback.Error.Length > 0 ? fallback.Error : "Lookup failed";
            return info;
        }

        static PublicIpInfo TryIpInfo()
        {
            PublicIpInfo r = new PublicIpInfo();
            r.CheckedAt = DateTime.Now;
            string json, err;
            if (!HttpGet("https://ipinfo.io/json", out json, out err)) { r.Error = err; return r; }
            r.Ip = JsonStr(json, "ip");
            r.Isp = JsonStr(json, "org");
            string city = JsonStr(json, "city"), region = JsonStr(json, "region"), country = JsonStr(json, "country");
            r.Location = JoinNonEmpty(", ", city, region, country);
            if (r.Ip.Length == 0) r.Error = "No IP returned";
            return r;
        }

        static PublicIpInfo TryIpWhoIs()
        {
            PublicIpInfo r = new PublicIpInfo();
            r.CheckedAt = DateTime.Now;
            string json, err;
            if (!HttpGet("https://ipwho.is/", out json, out err)) { r.Error = err; return r; }
            r.Ip = JsonStr(json, "ip");
            string isp = JsonStr(json, "isp"), org = JsonStr(json, "org");
            r.Isp = isp.Length > 0 ? isp : org;
            string city = JsonStr(json, "city"), region = JsonStr(json, "region"), country = JsonStr(json, "country_code");
            r.Location = JoinNonEmpty(", ", city, region, country);
            if (r.Ip.Length == 0) r.Error = "No IP returned";
            return r;
        }

        public static bool HttpGet(string url, out string body, out string err)
        {
            body = ""; err = "";
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 6000;
                req.ReadWriteTimeout = 6000;
                req.UserAgent = "NetSpeedBoss";
                req.Accept = "application/json";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    body = sr.ReadToEnd();
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        public static string JsonStr(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return "";
            try { return Regex.Unescape(m.Groups[1].Value).Trim(); }
            catch { return m.Groups[1].Value.Trim(); }
        }

        static string JoinNonEmpty(string sep, params string[] parts)
        {
            List<string> non = new List<string>();
            foreach (string p in parts) if (!string.IsNullOrEmpty(p)) non.Add(p);
            return string.Join(sep, non.ToArray());
        }

        public static string CurrentProfileKey(List<AdapterInfo> adapters)
        {
            AdapterInfo primary = PickPrimary(adapters);
            if (primary == null) return "Offline";
            try
            {
                if (string.Equals(primary.Type, "Wireless80211", StringComparison.OrdinalIgnoreCase))
                {
                    string ssid = ReadWifiSsid();
                    if (ssid.Length > 0) return "WiFi:" + ssid;
                }
            }
            catch { }
            return primary.Name;
        }

        public static List<DnsBenchRow> RunDnsBenchmark(List<AdapterInfo> adapters)
        {
            List<KeyValuePair<string, string>> targets = new List<KeyValuePair<string, string>>();
            AdapterInfo primary = PickPrimary(adapters);
            if (primary != null)
            {
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.Id != primary.Id) continue;
                        foreach (IPAddress d in ni.GetIPProperties().DnsAddresses)
                        {
                            if (d.AddressFamily == AddressFamily.InterNetwork)
                            {
                                targets.Add(new KeyValuePair<string, string>("Current DNS", d.ToString()));
                                break;
                            }
                        }
                        break;
                    }
                }
                catch { }
            }
            targets.Add(new KeyValuePair<string, string>("Cloudflare", "1.1.1.1"));
            targets.Add(new KeyValuePair<string, string>("Google", "8.8.8.8"));
            targets.Add(new KeyValuePair<string, string>("Quad9", "9.9.9.9"));
            targets.Add(new KeyValuePair<string, string>("OpenDNS", "208.67.222.222"));

            List<DnsBenchRow> rows = new List<DnsBenchRow>();
            foreach (KeyValuePair<string, string> kv in targets)
            {
                DnsBenchRow row = new DnsBenchRow();
                row.Name = kv.Key;
                row.Server = kv.Value;
                row.LatencyMs = ProbeDns(kv.Value);
                rows.Add(row);
            }
            rows.Sort(delegate(DnsBenchRow a, DnsBenchRow b)
            {
                if (a.LatencyMs < 0 && b.LatencyMs < 0) return 0;
                if (a.LatencyMs < 0) return 1;
                if (b.LatencyMs < 0) return -1;
                return a.LatencyMs.CompareTo(b.LatencyMs);
            });
            return rows;
        }

        static double ProbeDns(string ip)
        {
            byte[] query = {
                0x13, 0x37, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                0x03, (byte)'c', (byte)'o', (byte)'m', 0x00,
                0x00, 0x01, 0x00, 0x01
            };
            double best = -1;
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    using (UdpClient udp = new UdpClient())
                    {
                        udp.Client.SendTimeout = 1000;
                        udp.Client.ReceiveTimeout = 1000;
                        IPEndPoint ep = new IPEndPoint(IPAddress.Parse(ip), 53);
                        Stopwatch sw = Stopwatch.StartNew();
                        udp.Send(query, query.Length, ep);
                        IPEndPoint remote = null;
                        byte[] resp = udp.Receive(ref remote);
                        sw.Stop();
                        if (resp != null && resp.Length >= 12)
                        {
                            double ms = sw.Elapsed.TotalMilliseconds;
                            if (best < 0 || ms < best) best = ms;
                        }
                    }
                }
                catch { }
            }
            return best;
        }
    }
}
