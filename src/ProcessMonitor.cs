using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetSpeedBoss
{
    public class EpView
    {
        public string Proto, Remote, State;
        public double Rx, Tx;
        public long TotIn, TotOut;
    }

    public class ProcView
    {
        public int Pid, Conns;
        public string Name, Path;
        public double Rx, Tx;
        public long TotIn, TotOut;
        public long LastIn, LastOut;      // bytes moved during the most recent sample
        public List<EpView> Eps = new List<EpView>();
    }

    public class ListeningPortInfo
    {
        public string Proto = "TCP";
        public string LocalAddress = "";
        public int Port;
        public int Pid;
        public string ProcessName = "";
        public string Path = "";
    }

    /// <summary>
    /// Per-application network activity.
    ///  - Elevated: real-time ETW session on Microsoft-Windows-Kernel-Network (TCP + UDP, IPv4 + IPv6)
    ///    giving exact bytes per process and per remote endpoint.
    ///  - Not elevated: TCP connection table only (which apps are connected, and to where).
    /// </summary>
    public class ProcessMonitor : IDisposable
    {
        class Ep
        {
            public string Proto, Remote, State = "";
            public long PendIn, PendOut, TotIn, TotOut;
            public double Rx, Tx;
            public DateTime LastTraffic, LastInTable;
        }
        class Proc
        {
            public int Pid, Conns;
            public string Name, Path;
            public long PendIn, PendOut, TotIn, TotOut, LastIn, LastOut;
            public double Rx, Tx;
            public Dictionary<string, Ep> Eps = new Dictionary<string, Ep>();
        }

        static readonly Guid KernelNetworkProvider = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");
        const string SessionName = "NetSpeedBoss-KernelNetwork";

        readonly object gate = new object();
        readonly Dictionary<int, Proc> procs = new Dictionary<int, Proc>();
        readonly Stopwatch sw = Stopwatch.StartNew();
        double lastT = -1;
        volatile HashSet<string> localIps = new HashSet<string>();
        DateTime localIpsTime = DateTime.MinValue;

        IntPtr props = IntPtr.Zero, logfile = IntPtr.Zero, namePtr = IntPtr.Zero;
        ulong session, trace = ulong.MaxValue;
        Thread thread;
        Native.EventRecordCallback callback;   // keep delegate alive
        public bool EtwActive { get; private set; }
        public string Status { get; private set; }
        public long EventsSeen;

        public ProcessMonitor() { Status = "Not started"; }

        // ======================= ETW =======================
        static IntPtr AllocProps()
        {
            int size = 120 + 1024 * 2;
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, p, size);
            Marshal.WriteInt32(p, 0, size);            // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);              // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, 0x00020000);     // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);             // BufferSize (KB)
            Marshal.WriteInt32(p, 64, 0x100);          // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);              // FlushTimer = 1s
            Marshal.WriteInt32(p, 116, 120);           // LoggerNameOffset
            return p;
        }

        public bool StartEtw()
        {
            if (EtwActive) return true;
            if (IntPtr.Size != 8) { Status = "Per-app tracking requires the 64-bit build"; return false; }
            try
            {
                props = AllocProps();
                int err = Native.StartTrace(out session, SessionName, props);
                if (err == 183) // ERROR_ALREADY_EXISTS - stale session from a previous run
                {
                    Native.ControlTrace(0, SessionName, props, 1);
                    Marshal.FreeHGlobal(props);
                    props = AllocProps();
                    err = Native.StartTrace(out session, SessionName, props);
                }
                if (err != 0)
                {
                    Status = err == 5 ? "Run as administrator to see live per-app speeds (UDP + TCP)."
                                      : "Could not start kernel network trace (error " + err + ").";
                    Marshal.FreeHGlobal(props); props = IntPtr.Zero;
                    return false;
                }
                Guid g = KernelNetworkProvider;
                err = Native.EnableTraceEx2(session, ref g, 1 /*ENABLE*/, 5 /*VERBOSE*/, ulong.MaxValue, 0, 0, IntPtr.Zero);
                if (err != 0) { Status = "EnableTraceEx2 failed (" + err + ")"; StopEtw(); return false; }

                logfile = Marshal.AllocHGlobal(448);
                Marshal.Copy(new byte[448], 0, logfile, 448);
                namePtr = Marshal.StringToHGlobalUni(SessionName);
                Marshal.WriteIntPtr(logfile, 8, namePtr);                       // LoggerName
                Marshal.WriteInt32(logfile, 28, 0x100 | 0x10000000);            // REAL_TIME | EVENT_RECORD
                callback = OnEvent;
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback)); // EventRecordCallback
                trace = Native.OpenTrace(logfile);
                if (trace == ulong.MaxValue) { Status = "OpenTrace failed (" + Marshal.GetLastWin32Error() + ")"; StopEtw(); return false; }

                ulong th = trace;
                thread = new Thread(delegate() { try { Native.ProcessTrace(new ulong[] { th }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } });
                thread.IsBackground = true;
                thread.Name = "ETW consumer";
                thread.Start();
                EtwActive = true;
                Status = "Live per-app tracking active (kernel network trace).";
                return true;
            }
            catch (Exception ex)
            {
                Status = "Per-app tracking error: " + ex.Message;
                StopEtw();
                return false;
            }
        }

        public void StopEtw()
        {
            EtwActive = false;
            try
            {
                if (props != IntPtr.Zero) Native.ControlTrace(session, SessionName, props, 1 /*STOP*/);
                if (trace != ulong.MaxValue) { Native.CloseTrace(trace); trace = ulong.MaxValue; }
                if (thread != null) { thread.Join(2000); thread = null; }
            }
            catch { }
            if (props != IntPtr.Zero) { Marshal.FreeHGlobal(props); props = IntPtr.Zero; }
            if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
            if (namePtr != IntPtr.Zero) { Marshal.FreeHGlobal(namePtr); namePtr = IntPtr.Zero; }
        }

        public void Dispose() { StopEtw(); }

        bool diagnosed;
        /// <summary>Called when ETW is running but delivering nothing; falls back to table-only mode.</summary>
        public void Diagnose(string message)
        {
            if (diagnosed) return;
            diagnosed = true;
            StopEtw();
            Status = message;
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "error.log"),
                    DateTime.Now.ToString("s") + "  ETW: " + message + Environment.NewLine);
            }
            catch { }
        }

        static int Swap16(int v) { return ((v & 0xFF) << 8) | ((v >> 8) & 0xFF); }

        static IPAddress Norm(IPAddress a)
        {
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && a.IsIPv4MappedToIPv6) return a.MapToIPv4();
            return a;
        }

        public static string EndpointString(IPAddress a, int port)
        {
            a = Norm(a);
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return "[" + a + "]:" + port;
            return a + ":" + port;
        }

        void OnEvent(IntPtr rec)
        {
            try
            {
                int id = (ushort)Marshal.ReadInt16(rec, 40);
                bool send, v6, udp;
                switch (id)
                {
                    case 10: send = true; v6 = false; udp = false; break;
                    case 11: send = false; v6 = false; udp = false; break;
                    case 26: send = true; v6 = true; udp = false; break;
                    case 27: send = false; v6 = true; udp = false; break;
                    case 42: send = true; v6 = false; udp = true; break;
                    case 43: send = false; v6 = false; udp = true; break;
                    case 58: send = true; v6 = true; udp = true; break;
                    case 59: send = false; v6 = true; udp = true; break;
                    default: return;
                }
                int len = (ushort)Marshal.ReadInt16(rec, 86);
                IntPtr data = Marshal.ReadIntPtr(rec, 96);
                if (data == IntPtr.Zero || len < (v6 ? 44 : 20)) return;

                int pid = Marshal.ReadInt32(data, 0);
                long size = (uint)Marshal.ReadInt32(data, 4);
                IPAddress da, sa; int dport, sport;
                if (!v6)
                {
                    da = new IPAddress((long)(uint)Marshal.ReadInt32(data, 8));
                    sa = new IPAddress((long)(uint)Marshal.ReadInt32(data, 12));
                    dport = Swap16(Marshal.ReadInt16(data, 16) & 0xFFFF);
                    sport = Swap16(Marshal.ReadInt16(data, 18) & 0xFFFF);
                }
                else
                {
                    byte[] b = new byte[16];
                    Marshal.Copy(new IntPtr(data.ToInt64() + 8), b, 0, 16); da = new IPAddress(b);
                    b = new byte[16];
                    Marshal.Copy(new IntPtr(data.ToInt64() + 24), b, 0, 16); sa = new IPAddress(b);
                    dport = Swap16(Marshal.ReadInt16(data, 40) & 0xFFFF);
                    sport = Swap16(Marshal.ReadInt16(data, 42) & 0xFFFF);
                }
                da = Norm(da); sa = Norm(sa);
                if (IPAddress.IsLoopback(da) || IPAddress.IsLoopback(sa)) return;   // adapters never see loopback

                // daddr is normally the remote side; correct it if it is one of our own addresses
                IPAddress remote = da; int rport = dport;
                HashSet<string> local = localIps;
                if (local.Contains(da.ToString()) && !local.Contains(sa.ToString())) { remote = sa; rport = sport; }

                string proto = udp ? "UDP" : "TCP";
                string key = proto + " " + EndpointString(remote, rport);

                lock (gate)
                {
                    Proc p = GetProc(pid);
                    Ep e;
                    if (!p.Eps.TryGetValue(key, out e))
                    {
                        e = new Ep(); e.Proto = proto; e.Remote = EndpointString(remote, rport);
                        p.Eps[key] = e;
                    }
                    if (send) { p.PendOut += size; e.PendOut += size; }
                    else { p.PendIn += size; e.PendIn += size; }
                    EventsSeen++;
                }
            }
            catch { }
        }

        Proc GetProc(int pid)
        {
            Proc p;
            if (!procs.TryGetValue(pid, out p)) { p = new Proc(); p.Pid = pid; procs[pid] = p; }
            return p;
        }

        // ======================= sampling =======================
        static readonly string[] TcpStates = { "", "Closed", "Listen", "SYN sent", "SYN received", "Established",
            "FIN wait 1", "FIN wait 2", "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB" };

        struct TcpRow { public int Pid; public string Remote; public int State; }

        static List<TcpRow> ReadTcp(int af)
        {
            List<TcpRow> rows = new List<TcpRow>();
            int size = 0;
            Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int r = Native.GetExtendedTcpTable(buf, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
                    if (r == 122) continue; // grew, retry
                    if (r != 0) return rows;
                    int n = Marshal.ReadInt32(buf);
                    int rowSize = af == Native.AF_INET ? 24 : 56;
                    for (int i = 0; i < n; i++)
                    {
                        long off = buf.ToInt64() + 4 + (long)i * rowSize;
                        IntPtr row = new IntPtr(off);
                        int state, pid, rport;
                        IPAddress remote;
                        if (af == Native.AF_INET)
                        {
                            state = Marshal.ReadInt32(row, 0);
                            remote = new IPAddress((long)(uint)Marshal.ReadInt32(row, 12));
                            rport = Swap16(Marshal.ReadInt32(row, 16) & 0xFFFF);
                            pid = Marshal.ReadInt32(row, 20);
                        }
                        else
                        {
                            byte[] b = new byte[16];
                            Marshal.Copy(new IntPtr(off + 24), b, 0, 16);
                            remote = new IPAddress(b);
                            rport = Swap16(Marshal.ReadInt32(row, 44) & 0xFFFF);
                            state = Marshal.ReadInt32(row, 48);
                            pid = Marshal.ReadInt32(row, 52);
                        }
                        if (state == 2 || pid == 0) continue;                 // listeners / orphaned TIME_WAIT
                        remote = Norm(remote);
                        if (IPAddress.IsLoopback(remote) || remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any)) continue;
                        TcpRow t; t.Pid = pid; t.State = state; t.Remote = EndpointString(remote, rport);
                        rows.Add(t);
                    }
                    return rows;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return rows;
        }

        void RefreshLocalIps()
        {
            if ((DateTime.UtcNow - localIpsTime).TotalSeconds < 20) return;
            localIpsTime = DateTime.UtcNow;
            HashSet<string> s = new HashSet<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                        s.Add(Norm(u.Address).ToString().Split('%')[0]);
            }
            catch { }
            localIps = s;
        }

        /// <summary>Call once per UI tick. Converts accumulated bytes to rates and merges the TCP table.</summary>
        public void Sample()
        {
            RefreshLocalIps();
            double now = sw.Elapsed.TotalSeconds;
            double dt = lastT < 0 ? 1 : Math.Max(0.05, now - lastT);
            lastT = now;
            DateTime utc = DateTime.UtcNow;

            List<TcpRow> rows = ReadTcp(Native.AF_INET);
            rows.AddRange(ReadTcp(Native.AF_INET6));

            List<Proc> unnamed = new List<Proc>();
            lock (gate)
            {
                foreach (Proc p in procs.Values)
                {
                    p.Rx = p.PendIn / dt; p.Tx = p.PendOut / dt;
                    p.TotIn += p.PendIn; p.TotOut += p.PendOut;
                    p.LastIn = p.PendIn; p.LastOut = p.PendOut;
                    p.PendIn = p.PendOut = 0; p.Conns = 0;
                    foreach (Ep e in p.Eps.Values)
                    {
                        e.Rx = e.PendIn / dt; e.Tx = e.PendOut / dt;
                        if (e.PendIn + e.PendOut > 0) e.LastTraffic = utc;
                        e.TotIn += e.PendIn; e.TotOut += e.PendOut;
                        e.PendIn = e.PendOut = 0;
                    }
                }
                foreach (TcpRow r in rows)
                {
                    Proc p = GetProc(r.Pid);
                    p.Conns++;
                    string key = "TCP " + r.Remote;
                    Ep e;
                    if (!p.Eps.TryGetValue(key, out e)) { e = new Ep(); e.Proto = "TCP"; e.Remote = r.Remote; p.Eps[key] = e; }
                    e.State = r.State >= 0 && r.State < TcpStates.Length ? TcpStates[r.State] : r.State.ToString();
                    e.LastInTable = utc;
                }
                List<int> deadProcs = new List<int>();
                foreach (Proc p in procs.Values)
                {
                    List<string> dead = new List<string>();
                    foreach (KeyValuePair<string, Ep> kv in p.Eps)
                    {
                        Ep e = kv.Value;
                        bool inTable = (utc - e.LastInTable).TotalSeconds < 1.5 * Math.Max(1, dt);
                        if (!inTable) e.State = e.Proto == "UDP" ? "" : "Closed";
                        if (!inTable && (utc - e.LastTraffic).TotalSeconds > 20) dead.Add(kv.Key);
                    }
                    foreach (string k in dead) p.Eps.Remove(k);
                    if (p.Conns == 0 && p.Eps.Count == 0 && p.TotIn + p.TotOut == 0) deadProcs.Add(p.Pid);
                    if (p.Name == null) unnamed.Add(p);
                }
                foreach (int pid in deadProcs) procs.Remove(pid);
            }

            foreach (Proc p in unnamed)
            {
                string name = null, path = null;
                if (p.Pid == 0) name = "System Idle";
                else if (p.Pid == 4) name = "System";
                else
                {
                    try { name = Process.GetProcessById(p.Pid).ProcessName; } catch { name = "PID " + p.Pid; }
                    path = Native.GetProcessPath(p.Pid);
                }
                lock (gate) { p.Name = name; p.Path = path; }
            }
        }

        public List<ProcView> Snapshot()
        {
            List<ProcView> list = new List<ProcView>();
            lock (gate)
            {
                foreach (Proc p in procs.Values)
                {
                    ProcView v = new ProcView();
                    v.Pid = p.Pid; v.Name = p.Name ?? ("PID " + p.Pid); v.Path = p.Path;
                    v.Rx = p.Rx; v.Tx = p.Tx; v.TotIn = p.TotIn; v.TotOut = p.TotOut; v.Conns = p.Conns;
                    v.LastIn = p.LastIn; v.LastOut = p.LastOut;
                    foreach (Ep e in p.Eps.Values)
                    {
                        EpView ev = new EpView();
                        ev.Proto = e.Proto; ev.Remote = e.Remote; ev.State = e.State;
                        ev.Rx = e.Rx; ev.Tx = e.Tx; ev.TotIn = e.TotIn; ev.TotOut = e.TotOut;
                        v.Eps.Add(ev);
                    }
                    list.Add(v);
                }
            }
            return list;
        }

        public void ResetTotals()
        {
            lock (gate)
                foreach (Proc p in procs.Values)
                {
                    p.TotIn = p.TotOut = 0;
                    foreach (Ep e in p.Eps.Values) e.TotIn = e.TotOut = 0;
                }
        }

        public List<ListeningPortInfo> ReadListeners()
        {
            List<ListeningPortInfo> list = new List<ListeningPortInfo>();
            HashSet<string> seen = new HashSet<string>();
            ReadTcpListeners(Native.AF_INET, list, seen);
            ReadTcpListeners(Native.AF_INET6, list, seen);
            ReadUdpListeners(Native.AF_INET, list, seen);
            ReadUdpListeners(Native.AF_INET6, list, seen);
            Dictionary<int, string[]> pidCache = new Dictionary<int, string[]>();
            foreach (ListeningPortInfo item in list)
            {
                string[] meta;
                if (!pidCache.TryGetValue(item.Pid, out meta))
                {
                    string nm = "", pth = "";
                    try { using (Process pr = Process.GetProcessById(item.Pid)) nm = pr.ProcessName + ".exe"; } catch { nm = "PID " + item.Pid; }
                    pth = Native.GetProcessPath(item.Pid) ?? "";
                    meta = new string[] { nm, pth };
                    pidCache[item.Pid] = meta;
                }
                item.ProcessName = meta[0];
                item.Path = meta[1];
            }
            list.Sort(delegate(ListeningPortInfo a, ListeningPortInfo b)
            {
                int r = a.Port.CompareTo(b.Port);
                return r != 0 ? r : string.Compare(a.Proto, b.Proto, StringComparison.Ordinal);
            });
            return list;
        }

        static void ReadTcpListeners(int af, List<ListeningPortInfo> list, HashSet<string> seen)
        {
            int size = 0;
            Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int r = Native.GetExtendedTcpTable(buf, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
                    if (r == 122) continue;
                    if (r != 0) return;
                    int n = Marshal.ReadInt32(buf);
                    int rowSize = af == Native.AF_INET ? 24 : 56;
                    for (int i = 0; i < n; i++)
                    {
                        long off = buf.ToInt64() + 4 + (long)i * rowSize;
                        IntPtr row = new IntPtr(off);
                        int state, pid, lport; IPAddress local;
                        if (af == Native.AF_INET)
                        {
                            state = Marshal.ReadInt32(row, 0);
                            local = new IPAddress((long)(uint)Marshal.ReadInt32(row, 4));
                            lport = Swap16(Marshal.ReadInt32(row, 8) & 0xFFFF);
                            pid = Marshal.ReadInt32(row, 20);
                        }
                        else
                        {
                            byte[] b = new byte[16]; Marshal.Copy(new IntPtr(off), b, 0, 16); local = new IPAddress(b);
                            lport = Swap16(Marshal.ReadInt32(row, 20) & 0xFFFF);
                            state = Marshal.ReadInt32(row, 48);
                            pid = Marshal.ReadInt32(row, 52);
                        }
                        if (state != 2 || pid <= 0) continue;
                        AddListener("TCP", local, lport, pid, list, seen);
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        static void ReadUdpListeners(int af, List<ListeningPortInfo> list, HashSet<string> seen)
        {
            int size = 0;
            Native.GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, Native.UDP_TABLE_OWNER_PID, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int r = Native.GetExtendedUdpTable(buf, ref size, false, af, Native.UDP_TABLE_OWNER_PID, 0);
                    if (r == 122) continue;
                    if (r != 0) return;
                    int n = Marshal.ReadInt32(buf);
                    int rowSize = af == Native.AF_INET ? 12 : 28;
                    for (int i = 0; i < n; i++)
                    {
                        long off = buf.ToInt64() + 4 + (long)i * rowSize;
                        IntPtr row = new IntPtr(off);
                        int pid, lport; IPAddress local;
                        if (af == Native.AF_INET)
                        {
                            local = new IPAddress((long)(uint)Marshal.ReadInt32(row, 0));
                            lport = Swap16(Marshal.ReadInt32(row, 4) & 0xFFFF);
                            pid = Marshal.ReadInt32(row, 8);
                        }
                        else
                        {
                            byte[] b = new byte[16]; Marshal.Copy(new IntPtr(off), b, 0, 16); local = new IPAddress(b);
                            lport = Swap16(Marshal.ReadInt32(row, 20) & 0xFFFF);
                            pid = Marshal.ReadInt32(row, 24);
                        }
                        if (pid <= 0) continue;
                        AddListener("UDP", local, lport, pid, list, seen);
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        static void AddListener(string proto, IPAddress local, int port, int pid, List<ListeningPortInfo> list, HashSet<string> seen)
        {
            local = Norm(local);
            string addr = local.ToString();
            string key = proto + "|" + addr + "|" + port + "|" + pid;
            if (!seen.Add(key)) return;
            ListeningPortInfo info = new ListeningPortInfo();
            info.Proto = proto; info.LocalAddress = addr; info.Port = port; info.Pid = pid;
            list.Add(info);
        }
    }
}
