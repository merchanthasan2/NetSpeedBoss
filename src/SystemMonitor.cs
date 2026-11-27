using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NetSpeedBoss
{
    // =====================================================================================
    // Brand styling: colored badges / chip graphics drawn from the brand's color and name.
    // (Plain wordmark text in the brand color: no logo artwork is embedded.)
    // =====================================================================================
    public class BrandStyle
    {
        public string Key, Word;
        public Color Back, Back2, Fore;
        public BrandStyle(string key, string word, Color back, Color back2, Color fore) { Key = key; Word = word; Back = back; Back2 = back2; Fore = fore; }
    }

    public static class Brands
    {
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        static readonly List<KeyValuePair<string, BrandStyle>> map = new List<KeyValuePair<string, BrandStyle>>();
        static void Add(string match, BrandStyle s) { map.Add(new KeyValuePair<string, BrandStyle>(match, s)); }

        static Brands()
        {
            // order matters: more specific first
            Add("snapdragon", new BrandStyle("snapdragon", "Snapdragon", C(232, 40, 52), C(160, 16, 30), Color.White));
            Add("qualcomm", new BrandStyle("snapdragon", "Snapdragon", C(232, 40, 52), C(160, 16, 30), Color.White));
            Add("adreno", new BrandStyle("snapdragon", "Snapdragon", C(232, 40, 52), C(160, 16, 30), Color.White));
            Add("nvidia", new BrandStyle("nvidia", "NVIDIA", C(118, 185, 0), C(70, 120, 0), Color.White));
            Add("geforce", new BrandStyle("nvidia", "NVIDIA", C(118, 185, 0), C(70, 120, 0), Color.White));
            Add("radeon", new BrandStyle("amd", "AMD", C(40, 40, 44), C(12, 12, 14), Color.White));
            Add("ryzen", new BrandStyle("amd", "AMD", C(40, 40, 44), C(12, 12, 14), Color.White));
            Add("amd", new BrandStyle("amd", "AMD", C(40, 40, 44), C(12, 12, 14), Color.White));
            Add("authenticamd", new BrandStyle("amd", "AMD", C(40, 40, 44), C(12, 12, 14), Color.White));
            Add("advanced micro", new BrandStyle("amd", "AMD", C(40, 40, 44), C(12, 12, 14), Color.White));
            Add("intel", new BrandStyle("intel", "intel", C(0, 113, 197), C(0, 64, 140), Color.White));
            Add("genuineintel", new BrandStyle("intel", "intel", C(0, 113, 197), C(0, 64, 140), Color.White));
            Add("apple", new BrandStyle("apple", "Apple", C(60, 60, 64), C(20, 20, 22), Color.White));
            Add("mediatek", new BrandStyle("mediatek", "MediaTek", C(245, 160, 30), C(200, 110, 0), Color.White));
            Add("microsoft", new BrandStyle("microsoft", "Microsoft", C(90, 90, 96), C(50, 50, 54), Color.White));
            Add("samsung", new BrandStyle("samsung", "SAMSUNG", C(20, 40, 160), C(10, 22, 100), Color.White));
            Add("kingston", new BrandStyle("kingston", "Kingston", C(214, 30, 36), C(150, 10, 16), Color.White));
            Add("crucial", new BrandStyle("crucial", "Crucial", C(0, 92, 170), C(0, 52, 110), Color.White));
            Add("micron", new BrandStyle("micron", "Micron", C(0, 92, 170), C(0, 52, 110), Color.White));
            Add("corsair", new BrandStyle("corsair", "CORSAIR", C(30, 30, 30), C(5, 5, 5), C(255, 215, 0)));
            Add("g.skill", new BrandStyle("gskill", "G.SKILL", C(190, 20, 30), C(110, 0, 10), Color.White));
            Add("gskill", new BrandStyle("gskill", "G.SKILL", C(190, 20, 30), C(110, 0, 10), Color.White));
            Add("hynix", new BrandStyle("skhynix", "SK hynix", C(230, 0, 40), C(150, 0, 20), Color.White));
            Add("western digital", new BrandStyle("wd", "WD", C(0, 108, 190), C(0, 60, 120), Color.White));
            Add("wdc", new BrandStyle("wd", "WD", C(0, 108, 190), C(0, 60, 120), Color.White));
            Add("wd ", new BrandStyle("wd", "WD", C(0, 108, 190), C(0, 60, 120), Color.White));
            Add("seagate", new BrandStyle("seagate", "Seagate", C(110, 190, 73), C(60, 130, 30), Color.White));
            Add("toshiba", new BrandStyle("toshiba", "TOSHIBA", C(220, 30, 40), C(150, 10, 20), Color.White));
            Add("sandisk", new BrandStyle("sandisk", "SanDisk", C(220, 30, 40), C(150, 10, 20), Color.White));
            Add("asustek", new BrandStyle("asus", "ASUS", C(0, 83, 155), C(0, 44, 90), Color.White));
            Add("asus", new BrandStyle("asus", "ASUS", C(0, 83, 155), C(0, 44, 90), Color.White));
            Add("micro-star", new BrandStyle("msi", "MSI", C(220, 0, 20), C(140, 0, 10), Color.White));
            Add("msi", new BrandStyle("msi", "MSI", C(220, 0, 20), C(140, 0, 10), Color.White));
            Add("gigabyte", new BrandStyle("gigabyte", "GIGABYTE", C(240, 110, 30), C(170, 60, 0), Color.White));
            Add("asrock", new BrandStyle("asrock", "ASRock", C(20, 110, 180), C(10, 60, 110), Color.White));
            Add("dell", new BrandStyle("dell", "DELL", C(0, 125, 184), C(0, 76, 120), Color.White));
            Add("alienware", new BrandStyle("dell", "Alienware", C(30, 30, 40), C(10, 10, 16), C(0, 220, 255)));
            Add("hewlett", new BrandStyle("hp", "HP", C(0, 150, 214), C(0, 90, 140), Color.White));
            Add("hp ", new BrandStyle("hp", "HP", C(0, 150, 214), C(0, 90, 140), Color.White));
            Add("lenovo", new BrandStyle("lenovo", "Lenovo", C(226, 35, 26), C(150, 15, 10), Color.White));
            Add("acer", new BrandStyle("acer", "acer", C(131, 184, 26), C(80, 120, 10), Color.White));
            Add("realtek", new BrandStyle("realtek", "Realtek", C(44, 91, 168), C(20, 50, 110), Color.White));
            Add("killer", new BrandStyle("killer", "Killer", C(180, 20, 30), C(100, 0, 10), Color.White));
            Add("broadcom", new BrandStyle("broadcom", "Broadcom", C(204, 9, 47), C(130, 0, 20), Color.White));
            Add("american megatrends", new BrandStyle("ami", "AMI", C(52, 65, 85), C(28, 36, 48), Color.White));
            Add("ami", new BrandStyle("ami", "AMI", C(52, 65, 85), C(28, 36, 48), Color.White));
            Add("windows", new BrandStyle("windows", "Windows", C(0, 120, 212), C(0, 70, 150), Color.White));
        }

        public static BrandStyle Detect(params string[] texts)
        {
            foreach (string t in texts)
            {
                if (string.IsNullOrEmpty(t)) continue;
                string s = " " + t.ToLowerInvariant() + " ";
                foreach (KeyValuePair<string, BrandStyle> kv in map)
                    if (s.Contains(kv.Key.EndsWith(" ") ? " " + kv.Key : kv.Key)) return kv.Value;
            }
            return null;
        }

        public static BrandStyle FromVendorId(uint id)
        {
            switch (id)
            {
                case 0x10DE: return Detect("nvidia");
                case 0x1002: case 0x1022: return Detect("amd");
                case 0x8086: return Detect("intel");
                case 0x5143: case 0x4D4F4351: return Detect("snapdragon");
                case 0x106B: return Detect("apple");
                case 0x1414: return Detect("microsoft");
            }
            return null;
        }

        /// <summary>Generic fallback badge using the first word of the name.</summary>
        public static BrandStyle Generic(string name)
        {
            string w = string.IsNullOrEmpty(name) ? "?" : name.Trim().Split(' ')[0];
            if (w.Length > 12) w = w.Substring(0, 12);
            return new BrandStyle("generic", w, Color.FromArgb(110, 118, 130), Color.FromArgb(70, 76, 86), Color.White);
        }
    }

    // =====================================================================================
    // Live CPU / GPU / memory sampling
    // =====================================================================================
    public class GpuInfo
    {
        public string Name, Luid;
        public uint VendorId;
        public long Vram, SharedMax;
        public double Util = -1;              // %; -1 = unavailable
        public long DedicatedUsed, SharedUsed;
        public readonly double[] Hist = new double[120];
        public int Pos;
        public BrandStyle Brand;
    }

    public class SystemMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

        public double Cpu, Mem, CpuGhz;
        public long MemTotal, MemUsed, CommitUsed, CommitTotal;
        public readonly double[] CpuHist = new double[120], MemHist = new double[120];
        int pos;
        long pIdle = -1, pKernel, pUser;
        PerformanceCounter perfCounter;
        bool perfTried;
        public double BaseGhz;

        public List<GpuInfo> Gpus = new List<GpuInfo>();
        public readonly Dictionary<int, double> PidGpu3d = new Dictionary<int, double>();
        public readonly Dictionary<int, string> PidGpuLuid = new Dictionary<int, string>();
        bool gpusEnumerated;
        Dictionary<string, CounterSample> prevEngine = new Dictionary<string, CounterSample>();
        bool gpuCountersMissing;

        public static double HistAt(double[] arr, int p, int ago)
        {
            int n = arr.Length;
            return arr[((p - 1 - ago) % n + n) % n];
        }
        public double CpuAt(int ago) { return HistAt(CpuHist, pos, ago); }
        public double MemAt(int ago) { return HistAt(MemHist, pos, ago); }

        /// <summary>Cheap: CPU + memory. Call every tick.</summary>
        public void Sample()
        {
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                if (pIdle >= 0)
                {
                    long di = idle - pIdle, dt = (kernel - pKernel) + (user - pUser);
                    if (dt > 0) Cpu = Math.Max(0, Math.Min(100, 100.0 * (dt - di) / dt));
                }
                pIdle = idle; pKernel = kernel; pUser = user;
            }
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref m))
            {
                MemTotal = (long)m.ullTotalPhys; MemUsed = (long)(m.ullTotalPhys - m.ullAvailPhys);
                Mem = MemTotal > 0 ? 100.0 * MemUsed / MemTotal : 0;
                CommitTotal = (long)m.ullTotalPageFile; CommitUsed = (long)(m.ullTotalPageFile - m.ullAvailPageFile);
            }
            if (!perfTried)
            {
                perfTried = true;
                try { perfCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true); perfCounter.NextValue(); }
                catch { perfCounter = null; }
            }
            if (perfCounter != null && BaseGhz > 0)
                try { CpuGhz = perfCounter.NextValue() / 100.0 * BaseGhz; } catch { perfCounter = null; }

            CpuHist[pos] = Cpu; MemHist[pos] = Mem;
            pos = (pos + 1) % CpuHist.Length;
        }

        /// <summary>Heavier: GPU engines + GPU memory. Call while the System tab is visible.</summary>
        public void SampleGpu()
        {
            if (!gpusEnumerated) { gpusEnumerated = true; Gpus = Dxgi.Enumerate(); }
            if (Gpus.Count == 0 || gpuCountersMissing) return;
            try
            {
                Dictionary<string, Dictionary<string, double>> perLuidType = new Dictionary<string, Dictionary<string, double>>();
                Dictionary<int, double> pid3d = new Dictionary<int, double>();
                Dictionary<int, string> pidLuid = new Dictionary<int, string>();
                Dictionary<string, CounterSample> now = new Dictionary<string, CounterSample>();
                InstanceDataCollectionCollection all = new PerformanceCounterCategory("GPU Engine").ReadCategory();
                InstanceDataCollection util = all["utilization percentage"];
                if (util != null)
                    foreach (InstanceData d in util.Values)
                    {
                        string inst = d.InstanceName;
                        now[inst] = d.Sample;
                        CounterSample prev;
                        if (!prevEngine.TryGetValue(inst, out prev)) continue;
                        double v = CounterSample.Calculate(prev, d.Sample);
                        if (double.IsNaN(v) || v <= 0) continue;
                        string luid = ParseLuid(inst), type = ParseEngType(inst);
                        if (luid == null) continue;
                        Dictionary<string, double> t;
                        if (!perLuidType.TryGetValue(luid, out t)) { t = new Dictionary<string, double>(); perLuidType[luid] = t; }
                        double cur;
                        t.TryGetValue(type, out cur);
                        t[type] = cur + v;

                        int pid = ParsePid(inst);
                        if (pid > 4 && (type.IndexOf("3D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        type.IndexOf("Compute", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            double pv;
                            pid3d.TryGetValue(pid, out pv);
                            if (v > pv) { pid3d[pid] = Math.Min(100, v); pidLuid[pid] = luid; }
                        }
                    }
                bool first = prevEngine.Count == 0;
                prevEngine = now;

                Dictionary<string, long[]> mem = new Dictionary<string, long[]>();
                try
                {
                    InstanceDataCollectionCollection gm = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
                    InstanceDataCollection ded = gm["dedicated usage"], sh = gm["shared usage"];
                    if (ded != null) foreach (InstanceData d in ded.Values) { string l = ParseLuid(d.InstanceName); if (l != null) Acc(mem, l, 0, d.RawValue); }
                    if (sh != null) foreach (InstanceData d in sh.Values) { string l = ParseLuid(d.InstanceName); if (l != null) Acc(mem, l, 1, d.RawValue); }
                }
                catch { }

                if (first) return;
                lock (PidGpu3d)
                {
                    PidGpu3d.Clear(); PidGpuLuid.Clear();
                    foreach (KeyValuePair<int, double> kv in pid3d) PidGpu3d[kv.Key] = kv.Value;
                    foreach (KeyValuePair<int, string> kv in pidLuid) PidGpuLuid[kv.Key] = kv.Value;
                }
                foreach (GpuInfo g in Gpus)
                {
                    Dictionary<string, double> t;
                    double u = 0;
                    if (perLuidType.TryGetValue(g.Luid, out t)) foreach (double v in t.Values) u = Math.Max(u, v);   // Task Manager: busiest engine type
                    g.Util = Math.Min(100, u);
                    long[] mm;
                    if (mem.TryGetValue(g.Luid, out mm)) { g.DedicatedUsed = mm[0]; g.SharedUsed = mm[1]; }
                    g.Hist[g.Pos] = g.Util; g.Pos = (g.Pos + 1) % g.Hist.Length;
                }
            }
            catch (InvalidOperationException) { gpuCountersMissing = true; }   // category not present (old driver)
            catch { }
        }

        static void Acc(Dictionary<string, long[]> d, string k, int i, long v)
        {
            long[] a;
            if (!d.TryGetValue(k, out a)) { a = new long[2]; d[k] = a; }
            a[i] += v;
        }

        static readonly Regex LuidRx = new Regex(@"luid_0x([0-9a-fA-F]{8})_0x([0-9a-fA-F]{8})", RegexOptions.Compiled);
        static readonly Regex TypeRx = new Regex(@"engtype_(.*)$", RegexOptions.Compiled);
        static readonly Regex PidRx = new Regex(@"^pid_(\d+)_", RegexOptions.Compiled);
        static int ParsePid(string inst)
        {
            Match m = PidRx.Match(inst);
            int pid;
            return m.Success && int.TryParse(m.Groups[1].Value, out pid) ? pid : 0;
        }
        static string ParseLuid(string inst)
        {
            Match m = LuidRx.Match(inst);
            return m.Success ? (m.Groups[1].Value + "_" + m.Groups[2].Value).ToUpperInvariant() : null;
        }
        static string ParseEngType(string inst)
        {
            Match m = TypeRx.Match(inst);
            return m.Success ? m.Groups[1].Value : "other";
        }
    }

    // =====================================================================================
    // DXGI adapter enumeration: real GPU names, 64-bit VRAM sizes and LUIDs
    // =====================================================================================
    static class Dxgi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh;
            public uint Flags;
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] bool IsCurrent();
        }

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        public static List<GpuInfo> Enumerate()
        {
            List<GpuInfo> list = new List<GpuInfo>();
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                IDXGIFactory1 f;
                if (CreateDXGIFactory1(ref iid, out f) != 0 || f == null) return list;
                HashSet<string> seen = new HashSet<string>();
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 a;
                    if (f.EnumAdapters1(i, out a) != 0 || a == null) break;
                    DXGI_ADAPTER_DESC1 d;
                    if (a.GetDesc1(out d) == 0 && (d.Flags & 2) == 0 && d.VendorId != 0x1414)   // skip software / Basic Render
                    {
                        string luid = (((uint)d.LuidHigh).ToString("X8") + "_" + d.LuidLow.ToString("X8"));
                        if (seen.Add(luid))
                        {
                            GpuInfo g = new GpuInfo();
                            g.Name = (d.Description ?? "").Trim(); g.VendorId = d.VendorId; g.Luid = luid;
                            g.Vram = (long)d.DedicatedVideoMemory.ToUInt64(); g.SharedMax = (long)d.SharedSystemMemory.ToUInt64();
                            g.Brand = Brands.FromVendorId(d.VendorId) ?? Brands.Detect(g.Name) ?? Brands.Generic(g.Name);
                            list.Add(g);
                        }
                    }
                    Marshal.ReleaseComObject(a);
                }
                Marshal.ReleaseComObject(f);
            }
            catch { }
            // discrete GPUs (most VRAM) first
            list.Sort(delegate(GpuInfo x, GpuInfo y) { return y.Vram.CompareTo(x.Vram); });
            return list;
        }
    }

    // =====================================================================================
    // Static "About this system" information (WMI + registry), gathered once on a worker thread
    // =====================================================================================
    public class SpecRow
    {
        public string Label, Value; public BrandStyle Brand;
        public SpecRow(string l, string v, BrandStyle b) { Label = l; Value = v; Brand = b; }
    }

    public class SystemInfo
    {
        public string CpuName = "", CpuVendor = "", CpuSeries = "";
        public int Cores, Threads;
        public double BaseGhz;
        public BrandStyle CpuBrand;
        public List<SpecRow> Rows = new List<SpecRow>();
        public List<SpecRow> HardwareRows = new List<SpecRow>();
        public List<SpecRow> OsRows = new List<SpecRow>();
        public string MemSummary = "";

        static List<ManagementObject> Query(string q) { return Query(null, q); }
        static List<ManagementObject> Query(string scope, string q)
        {
            List<ManagementObject> l = new List<ManagementObject>();
            try
            {
                ManagementObjectSearcher s = scope == null ? new ManagementObjectSearcher(q) : new ManagementObjectSearcher(scope, q);
                foreach (ManagementObject o in s.Get()) l.Add(o);
            }
            catch { }
            return l;
        }
        static string S(ManagementObject o, string p) { try { object v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; } }
        static long L(ManagementObject o, string p) { try { object v = o[p]; return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }

        public static string CleanCpu(string n)
        {
            n = Regex.Replace(n ?? "", @"\((R|TM|tm|r)\)|®|™", "");
            n = Regex.Replace(n, @"\s+CPU\s*@.*$", "");
            n = Regex.Replace(n, @"\s+(\d+-Core|with Radeon.*) Processor\s*$", " $1");
            n = Regex.Replace(n, @"\s+Processor\s*$", "");
            return Regex.Replace(n, @"\s{2,}", " ").Trim();
        }

        public static string Series(string name)
        {
            string n = name ?? "";
            Match m = Regex.Match(n, @"(Core\s+Ultra\s+\d|Core\s+i\d|Core\s+\d|Xeon|Pentium|Celeron|Atom|Ryzen\s+(Threadripper|AI\s+\d+|\d+)|Athlon|EPYC|Snapdragon\s+X\s*\w*|Snapdragon\s*\d*\w*|GeForce\s+(RTX|GTX|MX)|RTX|Quadro|Radeon\s*(RX|Pro)?|Arc|Iris\s*Xe|UHD|Adreno|Apple\s+M\d)", RegexOptions.IgnoreCase);
            return m.Success ? Regex.Replace(m.Value, @"\s+", " ") : "";
        }

        public static string CleanVendor(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            string s = Regex.Replace(v, @"\((R|TM)\)|®|™", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"ASUSTeK COMPUTER INC\.?|ASUSTeK Computer Inc\.?", "ASUS", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"Micro-Star International Co\.?,? ?Ltd\.?", "MSI", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"Gigabyte Technology Co\.?,? ?Ltd\.?", "Gigabyte", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"Hewlett-Packard|HP Inc\.", "HP", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"Dell Inc\.", "Dell", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"LENOVO", "Lenovo");
            s = Regex.Replace(s, @"\s+(Corporation|Corp\.?|Inc\.?|Co\.?,? ?Ltd\.?)\s*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        static string Gb(long b) { return (b / 1073741824.0).ToString(b >= 10L * 1073741824 ? "0" : "0.#") + " GB"; }

        public static SystemInfo Gather(List<GpuInfo> gpus, List<AdapterInfo> nets)
        {
            SystemInfo si = new SystemInfo();

            // device
            string devMfr = "", devModel = "";
            foreach (ManagementObject o in Query("SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem"))
            { devMfr = S(o, "Manufacturer"); devModel = S(o, "Model"); }
            string board = "", boardMfr = "";
            foreach (ManagementObject o in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard")) { boardMfr = S(o, "Manufacturer"); board = S(o, "Product"); }
            bool oemJunk = devModel.Length == 0 || Regex.IsMatch(devModel + devMfr, "System Product Name|To Be Filled|Default string|System manufacturer", RegexOptions.IgnoreCase);
            string device = oemJunk ? "Custom-built PC" : (CleanVendor(devMfr) + " " + devModel).Trim();
            SpecRow devRow = new SpecRow("Device", device, Brands.Detect(oemJunk ? boardMfr : devMfr));
            SpecRow nameRow = new SpecRow("Computer name", Environment.MachineName, null);
            si.Rows.Add(devRow);
            si.Rows.Add(nameRow);
            si.HardwareRows.Add(devRow);
            si.OsRows.Add(nameRow);

            // cpu
            foreach (ManagementObject o in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            {
                si.CpuName = CleanCpu(S(o, "Name")); si.CpuVendor = S(o, "Manufacturer");
                si.Cores += (int)L(o, "NumberOfCores"); si.Threads += (int)L(o, "NumberOfLogicalProcessors");
                if (si.BaseGhz == 0) si.BaseGhz = L(o, "MaxClockSpeed") / 1000.0;
            }
            if (si.CpuName.Length == 0)
                try { si.CpuName = CleanCpu((string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "")); } catch { }
            si.CpuBrand = Brands.Detect(si.CpuName, si.CpuVendor) ?? Brands.Generic(si.CpuName);
            si.CpuSeries = Series(si.CpuName);
            SpecRow cpuRow = new SpecRow("Processor", si.CpuName + (si.Cores > 0 ? "   \u00B7   " + si.Cores + " cores, " + si.Threads + " threads" : "") +
                (si.BaseGhz > 0 ? "   \u00B7   " + si.BaseGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz base" : ""), si.CpuBrand);
            si.Rows.Add(cpuRow);
            si.HardwareRows.Add(cpuRow);

            // gpu(s)
            if (gpus.Count == 0)
                foreach (ManagementObject o in Query("SELECT Name FROM Win32_VideoController"))
                {
                    SpecRow gr = new SpecRow("Graphics", S(o, "Name"), Brands.Detect(S(o, "Name")) ?? Brands.Generic(S(o, "Name")));
                    si.Rows.Add(gr); si.HardwareRows.Add(gr);
                }
            for (int i = 0; i < gpus.Count; i++)
            {
                GpuInfo g = gpus[i];
                string kind = g.Vram >= 512L * 1048576 ? Gb(g.Vram) + " dedicated memory" : "integrated, shares system memory";
                SpecRow gr = new SpecRow(gpus.Count > 1 ? "Graphics " + (i + 1) : "Graphics", g.Name + "   \u00B7   " + kind, g.Brand);
                si.Rows.Add(gr); si.HardwareRows.Add(gr);
            }

            // memory
            long total = 0; int sticks = 0; long speed = 0, rated = 0; int type = 0; string memMfr = "";
            foreach (ManagementObject o in Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                total += L(o, "Capacity"); sticks++;
                rated = Math.Max(rated, L(o, "Speed"));
                long sp = L(o, "ConfiguredClockSpeed"); if (sp == 0) sp = L(o, "Speed"); speed = Math.Max(speed, sp);
                if (type == 0) type = (int)L(o, "SMBIOSMemoryType");
                string mm = S(o, "Manufacturer");
                if (memMfr.Length == 0 && mm.Length > 0 && !Regex.IsMatch(mm, "^(0+|Unknown|Undefined)$", RegexOptions.IgnoreCase)) memMfr = mm;
            }
            string memType = type == 26 ? "DDR4" : type == 34 ? "DDR5" : type == 24 ? "DDR3" : type == 30 ? "LPDDR4" : type == 35 ? "LPDDR5" : "";
            if (total > 0)
            {
                si.MemSummary = (Gb(total) + " " + memType).Trim();
                string v = si.MemSummary + (speed > 0 ? "   \u00B7   " + speed + " MT/s" + (rated > speed ? " (rated " + rated + ")" : "") : "") +
                           (sticks > 1 ? "   \u00B7   " + sticks + " \u00D7 " + Gb(total / sticks) : "") +
                           (memMfr.Length > 0 && Brands.Detect(memMfr) == null ? "   \u00B7   " + memMfr : "");
                SpecRow mr = new SpecRow("Memory", v, Brands.Detect(memMfr));
                si.Rows.Add(mr); si.HardwareRows.Add(mr);
            }

            // storage
            List<ManagementObject> disks = Query(@"\\.\root\Microsoft\Windows\Storage", "SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk");
            if (disks.Count > 0)
                foreach (ManagementObject o in disks)
                {
                    long media = L(o, "MediaType"), bus = L(o, "BusType");
                    string kind = (bus == 17 ? "NVMe " : bus == 7 ? "USB " : "") + (media == 4 ? "SSD" : media == 3 ? "HDD" : bus == 17 ? "SSD" : "drive");
                    string name = S(o, "FriendlyName");
                    BrandStyle db = Brands.Detect(name);
                    if (db == null && Regex.IsMatch(name, @"^CT\d", RegexOptions.IgnoreCase)) db = Brands.Detect("crucial");   // Crucial model numbers
                    if (db == null && Regex.IsMatch(name, @"^(MZ|MZV)", RegexOptions.IgnoreCase)) db = Brands.Detect("samsung");
                    SpecRow sr = new SpecRow("Storage", name + "   \u00B7   " + Gb(L(o, "Size")) + "   \u00B7   " + kind.Trim(), db);
                    si.Rows.Add(sr); si.HardwareRows.Add(sr);
                }
            else
                foreach (ManagementObject o in Query("SELECT Model, Size FROM Win32_DiskDrive"))
                {
                    SpecRow sr = new SpecRow("Storage", S(o, "Model") + "   \u00B7   " + Gb(L(o, "Size")), Brands.Detect(S(o, "Model")));
                    si.Rows.Add(sr); si.HardwareRows.Add(sr);
                }

            // motherboard + bios
            if (board.Length > 0)
            {
                SpecRow br = new SpecRow("Motherboard", (CleanVendor(boardMfr) + " " + board).Trim(), Brands.Detect(boardMfr));
                si.Rows.Add(br); si.HardwareRows.Add(br);
            }
            foreach (ManagementObject o in Query("SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS"))
            {
                string d = S(o, "ReleaseDate"), date = "", biosMfr = S(o, "Manufacturer");
                if (d.Length >= 8) { DateTime dt; if (DateTime.TryParseExact(d.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) date = "   \u00B7   " + dt.ToString("d MMM yyyy"); }
                SpecRow bsr = new SpecRow("BIOS", (biosMfr + " " + S(o, "SMBIOSBIOSVersion")).Trim() + date, Brands.Detect(biosMfr));
                si.Rows.Add(bsr); si.HardwareRows.Add(bsr);
            }

            // network
            foreach (AdapterInfo a in nets)
                if (a.HasGateway)
                {
                    SpecRow nr = new SpecRow("Network", CleanVendor(a.Description) + "   \u00B7   " + Fmt.LinkSpeed(a.LinkSpeed), Brands.Detect(a.Description));
                    si.Rows.Add(nr); si.HardwareRows.Add(nr);
                }

            // os
            string caption = "", arch = "", build = "", installed = "";
            foreach (ManagementObject o in Query("SELECT Caption, OSArchitecture, BuildNumber, InstallDate FROM Win32_OperatingSystem"))
            {
                caption = S(o, "Caption").Replace("Microsoft ", ""); arch = S(o, "OSArchitecture"); build = S(o, "BuildNumber");
                try { installed = ManagementDateTimeConverter.ToDateTime(S(o, "InstallDate")).ToString("d MMM yyyy"); } catch { }
            }
            string disp = "", ubr = "";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (k != null) { disp = Convert.ToString(k.GetValue("DisplayVersion", "")); ubr = Convert.ToString(k.GetValue("UBR", "")); }
            }
            catch { }
            SpecRow winRow = new SpecRow("Windows", (caption + " " + disp).Trim() + "   \u00B7   " + build + (ubr.Length > 0 ? "." + ubr : "") + (arch.Length > 0 ? " (" + arch + ")" : ""),
                Brands.Detect("windows"));
            si.Rows.Add(winRow); si.OsRows.Add(winRow);
            if (installed.Length > 0)
            {
                SpecRow ir = new SpecRow("Installed", installed, null);
                si.Rows.Add(ir); si.OsRows.Add(ir);
            }
            return si;
        }

        public static string Uptime()
        {
            TimeSpan t = TimeSpan.FromMilliseconds(SystemMonitor.GetTickCount64());
            return (t.Days > 0 ? t.Days + " d " : "") + t.Hours + " h " + t.Minutes + " min";
        }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder("About this system (NetSpeed Boss)\r\n");
            foreach (SpecRow r in Rows) sb.Append(r.Label).Append(": ").Append(r.Value.Replace("   \u00B7   ", ", ")).Append("\r\n");
            sb.Append("Uptime: ").Append(Uptime()).Append("\r\n");
            return sb.ToString();
        }
    }
}
