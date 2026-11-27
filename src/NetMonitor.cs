using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetSpeedBoss
{
    public class AdapterInfo
    {
        public string Id, Name, Description, Type, IPv4, Gateway = "";
        public long LinkSpeed;
        public bool HasGateway, Selected;
        public long BytesIn, BytesOut;
        public double RxBps, TxBps;
    }

    /// <summary>Samples OS interface counters (64-bit) and turns them into speeds.</summary>
    public class NetMonitor
    {
        readonly Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
        readonly Dictionary<string, string> gatewayCache = new Dictionary<string, string>();
        readonly Stopwatch sw = Stopwatch.StartNew();
        double lastT = -1;
        DateTime gatewayCacheTime = DateTime.MinValue;

        public double RxBps, TxBps;      // bytes per second
        public long RxDelta, TxDelta;    // bytes since previous sample
        public int ActiveWanCount;
        public bool IsMultiWan { get { return ActiveWanCount >= 2; } }
        public List<AdapterInfo> Adapters = new List<AdapterInfo>();

        static bool IsCandidate(NetworkInterface ni)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;
            return ni.OperationalStatus == OperationalStatus.Up;
        }

        public void Sample(string selection)
        {
            double now = sw.Elapsed.TotalSeconds;
            bool refreshGw = (DateTime.UtcNow - gatewayCacheTime).TotalSeconds > 15;
            if (refreshGw) gatewayCacheTime = DateTime.UtcNow;

            long rx = 0, tx = 0;
            List<AdapterInfo> list = new List<AdapterInfo>();
            HashSet<string> seen = new HashSet<string>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { all = new NetworkInterface[0]; }

            foreach (NetworkInterface ni in all)
            {
                if (!IsCandidate(ni)) continue;
                long bin, bout;
                try
                {
                    IPInterfaceStatistics st = ni.GetIPStatistics();
                    bin = st.BytesReceived; bout = st.BytesSent;
                }
                catch { continue; }

                string gwIp;
                if (refreshGw || !gatewayCache.TryGetValue(ni.Id, out gwIp))
                {
                    gwIp = "";
                    try
                    {
                        foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                            if (g.Address != null && !g.Address.Equals(System.Net.IPAddress.Any) &&
                                !g.Address.Equals(System.Net.IPAddress.IPv6Any)) { gwIp = g.Address.ToString(); break; }
                    }
                    catch { }
                    gatewayCache[ni.Id] = gwIp;
                }
                bool gw = gwIp.Length > 0;

                AdapterInfo a = new AdapterInfo();
                a.Id = ni.Id; a.Name = ni.Name; a.Description = ni.Description;
                a.Type = ni.NetworkInterfaceType.ToString();
                try { a.LinkSpeed = ni.Speed; } catch { }
                a.HasGateway = gw; a.Gateway = gwIp; a.BytesIn = bin; a.BytesOut = bout;
                list.Add(a);
            }

            bool anyGw = false;
            foreach (AdapterInfo a in list) if (a.HasGateway) { anyGw = true; break; }

            double dt = lastT < 0 ? 0 : now - lastT;
            lastT = now;
            int wanCount = 0;

            foreach (AdapterInfo a in list)
            {
                if (selection == "all") a.Selected = true;
                else if (selection == "auto") a.Selected = anyGw ? a.HasGateway : true;
                else a.Selected = a.Id == selection;
                if (a.Selected && a.HasGateway) wanCount++;

                seen.Add(a.Id);
                long[] p;
                if (prev.TryGetValue(a.Id, out p))
                {
                    long dIn = a.BytesIn - p[0], dOut = a.BytesOut - p[1];
                    if (dIn < 0) dIn = 0;          // counter reset (adapter restarted)
                    if (dOut < 0) dOut = 0;
                    if (dt > 0.05) { a.RxBps = dIn / dt; a.TxBps = dOut / dt; }
                    if (a.Selected) { rx += dIn; tx += dOut; }
                }
                prev[a.Id] = new long[] { a.BytesIn, a.BytesOut };
            }
            List<string> gone = new List<string>();
            foreach (string k in prev.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (string k in gone) prev.Remove(k);

            ActiveWanCount = wanCount;
            RxDelta = rx; TxDelta = tx;
            if (dt > 0.05) { RxBps = rx / dt; TxBps = tx / dt; }
            else { RxBps = 0; TxBps = 0; }
            Adapters = list;
        }

        public static string GetIPv4(string adapterId)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Id != adapterId) continue;
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork) return u.Address.ToString();
                }
            }
            catch { }
            return "";
        }
    }

    public static class Fmt
    {
        public static string Speed(double bytesPerSec, SpeedUnit unit)
        {
            if (unit == SpeedUnit.Bits)
            {
                double b = bytesPerSec * 8;
                if (b < 1000) return Num(b) + " bps";
                if (b < 1000e3) return Num(b / 1e3) + " Kbps";
                if (b < 1000e6) return Num(b / 1e6) + " Mbps";
                return Num(b / 1e9) + " Gbps";
            }
            if (bytesPerSec < 1024) return Num(bytesPerSec) + " B/s";
            if (bytesPerSec < 1024 * 1024) return Num(bytesPerSec / 1024) + " KB/s";
            if (bytesPerSec < 1024.0 * 1024 * 1024) return Num(bytesPerSec / (1024 * 1024)) + " MB/s";
            return Num(bytesPerSec / (1024.0 * 1024 * 1024)) + " GB/s";
        }

        public static string Size(double bytes)
        {
            if (bytes < 1024) return bytes.ToString("0") + " B";
            if (bytes < 1024 * 1024) return Num(bytes / 1024) + " KB";
            if (bytes < 1024.0 * 1024 * 1024) return Num(bytes / (1024 * 1024)) + " MB";
            if (bytes < 1024.0 * 1024 * 1024 * 1024) return Num(bytes / (1024.0 * 1024 * 1024)) + " GB";
            return Num(bytes / (1024.0 * 1024 * 1024 * 1024)) + " TB";
        }

        public static string LinkSpeed(long bps)
        {
            if (bps <= 0) return "-";
            if (bps >= 1000000000) return (bps / 1e9).ToString("0.#") + " Gbps";
            if (bps >= 1000000) return (bps / 1e6).ToString("0.#") + " Mbps";
            return (bps / 1e3).ToString("0") + " Kbps";
        }

        static string Num(double v)
        {
            if (v < 10) return v.ToString("0.00");
            if (v < 100) return v.ToString("0.0");
            return v.ToString("0");
        }
    }
}
