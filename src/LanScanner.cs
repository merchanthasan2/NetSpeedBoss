using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NetSpeedBoss
{
    public class LanHostResult
    {
        public string Ip = "";
        public uint IpOrder;
        public string Status = "";
        public long LatencyMs = -1;
        public string Hostname = "";
        public string Mac = "";
        public string OpenPorts = "";
    }

    /// <summary>
    /// Fast multi-threaded local subnet scanner combining ICMP Echo and Win32 SendARP
    /// (so firewalled devices still surface with MAC addresses), plus optional rDNS and TCP port checks.
    /// </summary>
    public class LanScanner
    {
        static readonly int[] CommonPorts = { 22, 53, 80, 443, 445, 3389, 8080 };
        volatile bool cancelled;
        volatile bool running;

        public bool Running { get { return running; } }

        public void Cancel() { cancelled = true; }

        public static string DefaultRange(List<AdapterInfo> adapters)
        {
            if (adapters != null)
            {
                foreach (AdapterInfo a in adapters)
                {
                    if (!a.Selected || !a.HasGateway) continue;
                    string ip = !string.IsNullOrEmpty(a.IPv4) ? a.IPv4 : NetMonitor.GetIPv4(a.Id);
                    string r = RangeFromIp(ip);
                    if (r.Length > 0) return r;
                }
                foreach (AdapterInfo a in adapters)
                {
                    if (!a.HasGateway) continue;
                    string ip = !string.IsNullOrEmpty(a.IPv4) ? a.IPv4 : NetMonitor.GetIPv4(a.Id);
                    string r = RangeFromIp(ip);
                    if (r.Length > 0) return r;
                }
            }
            return "192.168.1.1-254";
        }

        static string RangeFromIp(string ipv4)
        {
            if (string.IsNullOrEmpty(ipv4)) return "";
            string[] p = ipv4.Split('.');
            if (p.Length != 4) return "";
            return p[0] + "." + p[1] + "." + p[2] + ".1-254";
        }

        public static List<IPAddress> ParseRange(string input, out string error)
        {
            error = "";
            List<IPAddress> list = new List<IPAddress>();
            if (string.IsNullOrEmpty(input)) { error = "Enter an IP range (e.g. 192.168.1.1-254 or 192.168.1.0/24)."; return list; }
            string s = input.Trim();
            uint startIp = 0, endIp = 0;

            int slash = s.IndexOf('/');
            int dash = s.IndexOf('-');
            if (slash > 0)
            {
                IPAddress net;
                int prefix;
                if (!IPAddress.TryParse(s.Substring(0, slash).Trim(), out net) || net.AddressFamily != AddressFamily.InterNetwork ||
                    !int.TryParse(s.Substring(slash + 1).Trim(), out prefix) || prefix < 16 || prefix > 32)
                {
                    error = "Invalid CIDR notation. Use /16 to /32 (e.g. 192.168.1.0/24).";
                    return list;
                }
                uint ip = ToUInt(net);
                uint mask = prefix == 0 ? 0u : (~0u << (32 - prefix));
                startIp = ip & mask;
                endIp = startIp | (~mask);
                if (prefix <= 30) { startIp++; endIp--; }
            }
            else if (dash > 0)
            {
                string left = s.Substring(0, dash).Trim();
                string right = s.Substring(dash + 1).Trim();
                IPAddress a;
                if (!IPAddress.TryParse(left, out a) || a.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = "Invalid start IPv4 address.";
                    return list;
                }
                startIp = ToUInt(a);
                IPAddress b;
                int lastOctet;
                if (int.TryParse(right, out lastOctet) && lastOctet >= 0 && lastOctet <= 255)
                {
                    byte[] bytes = a.GetAddressBytes();
                    bytes[3] = (byte)lastOctet;
                    endIp = ToUInt(new IPAddress(bytes));
                }
                else if (right.IndexOf('.') >= 0 && IPAddress.TryParse(right, out b) && b.AddressFamily == AddressFamily.InterNetwork)
                {
                    endIp = ToUInt(b);
                }
                else
                {
                    error = "Invalid end IP or octet (e.g. 192.168.1.1-254).";
                    return list;
                }
                if (endIp < startIp) { uint tmp = startIp; startIp = endIp; endIp = tmp; }
            }
            else
            {
                IPAddress single;
                if (!IPAddress.TryParse(s, out single) || single.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = "Invalid IPv4 range. Examples: 192.168.1.1-254 or 192.168.1.0/24";
                    return list;
                }
                startIp = endIp = ToUInt(single);
            }

            ulong count = (ulong)endIp - startIp + 1;
            if (count > 1024)
            {
                error = "Range too large (" + count + " hosts). Maximum is 1024 addresses per scan.";
                return list;
            }
            for (uint cur = startIp; cur <= endIp; cur++)
            {
                list.Add(FromUInt(cur));
                if (cur == uint.MaxValue) break;
            }
            return list;
        }

        public static uint ToUInt(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        static IPAddress FromUInt(uint v)
        {
            return new IPAddress(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }

        public bool Start(string rangeText, bool resolveNames, bool scanPorts,
            Action<LanHostResult, int, int> onProgress, Action<bool, string> onDone)
        {
            if (running) return false;
            string err;
            List<IPAddress> targets = ParseRange(rangeText, out err);
            if (err.Length > 0 || targets.Count == 0)
            {
                if (onDone != null) onDone(false, err.Length > 0 ? err : "No IP addresses to scan.");
                return false;
            }

            cancelled = false;
            running = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int total = targets.Count;
                    int done = 0;
                    int idx = -1;
                    int workers = Math.Min(36, total);
                    ManualResetEvent finished = new ManualResetEvent(false);
                    int alive = workers;

                    for (int w = 0; w < workers; w++)
                    {
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try
                            {
                                while (!cancelled)
                                {
                                    int i = Interlocked.Increment(ref idx);
                                    if (i >= total) break;
                                    IPAddress ip = targets[i];
                                    LanHostResult res = ProbeHost(ip, resolveNames, scanPorts);
                                    int curDone = Interlocked.Increment(ref done);
                                    if (onProgress != null) onProgress(res, curDone, total);
                                }
                            }
                            finally
                            {
                                if (Interlocked.Decrement(ref alive) == 0) finished.Set();
                            }
                        });
                    }
                    finished.WaitOne();
                    running = false;
                    if (onDone != null) onDone(cancelled, "");
                }
                catch (Exception ex)
                {
                    running = false;
                    if (onDone != null) onDone(false, ex.Message);
                }
            });
            return true;
        }

        LanHostResult ProbeHost(IPAddress ip, bool resolveNames, bool scanPorts)
        {
            long rtt = -1;
            bool pingOk = false;
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply r = p.Send(ip, 650);
                    if (r != null && r.Status == IPStatus.Success)
                    {
                        pingOk = true;
                        rtt = r.RoundtripTime;
                    }
                }
            }
            catch { }

            string mac = TryGetMac(ip);
            if (!pingOk && mac.Length == 0) return null;

            LanHostResult h = new LanHostResult();
            h.Ip = ip.ToString();
            h.IpOrder = ToUInt(ip);
            h.LatencyMs = rtt;
            h.Mac = mac;
            h.Status = pingOk ? "Alive (ICMP)" : "Alive (ARP)";

            if (resolveNames && !cancelled)
            {
                try
                {
                    string hn = Dns.GetHostEntry(ip).HostName;
                    if (!string.IsNullOrEmpty(hn) && hn != h.Ip) h.Hostname = hn;
                }
                catch { }
            }

            if (scanPorts && !cancelled)
            {
                List<string> open = new List<string>();
                foreach (int port in CommonPorts)
                {
                    if (cancelled) break;
                    if (ProbePort(ip, port, 260)) open.Add(port.ToString(CultureInfo.InvariantCulture));
                }
                h.OpenPorts = string.Join(", ", open.ToArray());
            }
            return h;
        }

        static string TryGetMac(IPAddress ip)
        {
            try
            {
                byte[] addrBytes = ip.GetAddressBytes();
                uint dest = BitConverter.ToUInt32(addrBytes, 0);
                byte[] mac = new byte[6];
                int len = mac.Length;
                if (Native.SendARP(dest, 0, mac, ref len) == 0 && len == 6)
                    return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                        mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
            }
            catch { }
            return "";
        }

        static bool ProbePort(IPAddress ip, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient tc = new TcpClient())
                {
                    IAsyncResult ar = tc.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                    tc.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static void ExportCsv(string path, List<LanHostResult> rows)
        {
            StringBuilder sb = new StringBuilder("IP,Status,Latency (ms),Hostname,MAC Address,Open Ports\r\n");
            foreach (LanHostResult r in rows)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},\"{5}\"\r\n",
                    r.Ip, r.Status, r.LatencyMs >= 0 ? r.LatencyMs.ToString(CultureInfo.InvariantCulture) : "",
                    (r.Hostname ?? "").Replace(',', ' '), r.Mac ?? "", r.OpenPorts ?? "");
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        public static bool SendWakeOnLan(string macAddress, out string error)
        {
            error = "";
            try
            {
                string hex = (macAddress ?? "").Replace(":", "").Replace("-", "").Replace(".", "").Trim();
                if (hex.Length != 12) { error = "Invalid MAC address."; return false; }
                byte[] mac = new byte[6];
                for (int i = 0; i < 6; i++) mac[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

                byte[] packet = new byte[102];
                for (int i = 0; i < 6; i++) packet[i] = 0xFF;
                for (int i = 1; i <= 16; i++) Buffer.BlockCopy(mac, 0, packet, i * 6, 6);

                using (UdpClient client = new UdpClient())
                {
                    client.EnableBroadcast = true;
                    client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                    client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
