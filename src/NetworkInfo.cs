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
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show interfaces");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string txt = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(1500);
                    Match m = Regex.Match(txt, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            catch { }
            return "";
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
    }
}
