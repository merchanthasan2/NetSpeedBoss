using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    /// <summary>
    /// Live gaming telemetry: foreground game detection, DXGI ETW FPS/frame-time,
    /// NVML GPU temp/fan/power/clocks/VRAM, per-core CPU utilization, game-process memory,
    /// and online game-server ping + transfer rates.
    /// </summary>
    public class GameTelemetry : IDisposable
    {
        // ---- published snapshot ----
        public bool GameActive;
        public int GamePid;
        public string GameName = "";
        public string GameTitle = "";
        public string GpuName = "";
        public string GraphicsApi = "";
        public string DetectReason = "";
        public double GameGpu3d;
        public int ScreenW = 1920, ScreenH = 1080, RefreshHz = 60;

        public double Fps, FpsMin, FpsMax, Fps1PctLow, FrameMs;
        public readonly double[] FpsHist = new double[60];
        public readonly double[] FrameMsHist = new double[60];
        public int HistPos;

        public GameSessionSummary LastSession;
        bool sessTracking;
        DateTime sessStartLocal = DateTime.MinValue;
        double sessFpsSum, sessLow1Min = double.MaxValue, sessPeakGpuTemp = -1, sessPeakCpuTemp = -1;
        int sessFpsSamples;
        long sessNetRxBytes, sessNetTxBytes;
        DateTime lastSessionSampleUtc = DateTime.MinValue;
        string sessGameName = "";

        public double GpuUtil = -1, GpuTempC = -1, GpuFanPct = -1, GpuPowerW = -1;
        public int GpuClockMhz = -1;
        public long VramUsedMB = -1, VramTotalMB = -1;

        public double CpuUtil, CpuGhz, CpuTempC = -1;
        public double[] CoreUtils = new double[0];

        public double MemUtil;
        public long MemUsedBytes, MemTotalBytes, GameMemBytes;

        public double GameRx, GameTx, TotalRx, TotalTx;
        public long PingMs = -1;
        public string PingTarget = "", PingLabel = "Net";

        readonly int selfPid = Process.GetCurrentProcess().Id;
        DateTime lastGameSeenUtc = DateTime.MinValue;
        DateTime lastCpuTempUtc = DateTime.MinValue;
        DateTime lastPingUtc = DateTime.MinValue;
        bool pingBusy;

        // ---- per-core CPU ----
        Dictionary<string, CounterSample> prevCoreSamples = new Dictionary<string, CounterSample>();
        string coreCounterName;
        bool coreCounterMissing;

        // ---- NVML (NVIDIA GPU temp / fan / power / clocks / VRAM) ----
        static class Nvml
        {
            [StructLayout(LayoutKind.Sequential)] public struct NvUtil { public uint gpu, mem; }
            [StructLayout(LayoutKind.Sequential)] public struct NvMem { public ulong total, free, used; }
            [DllImport("nvml.dll")] static extern int nvmlInit_v2();
            [DllImport("nvml.dll")] static extern int nvmlShutdown();
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint idx, out IntPtr dev);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, uint sensor, out uint temp);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint speed);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvUtil u);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out NvMem m);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
            [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr dev, uint type, out uint mhz);

            static bool tried, ok;
            static IntPtr dev;

            public static bool Read(ref double util, ref double tempC, ref double fanPct,
                ref long vramUsedMB, ref long vramTotalMB, ref int clockMhz, ref double powerW)
            {
                if (!tried)
                {
                    tried = true;
                    try { if (nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out dev) == 0 && dev != IntPtr.Zero) ok = true; }
                    catch { ok = false; }
                }
                if (!ok) return false;
                try
                {
                    uint t, f, pw, clk;
                    NvUtil u; NvMem m;
                    if (nvmlDeviceGetUtilizationRates(dev, out u) == 0) util = u.gpu;
                    if (nvmlDeviceGetTemperature(dev, 0, out t) == 0) tempC = t;
                    if (nvmlDeviceGetFanSpeed(dev, out f) == 0) fanPct = f;
                    if (nvmlDeviceGetMemoryInfo(dev, out m) == 0)
                    {
                        vramUsedMB = (long)(m.used / 1048576UL);
                        vramTotalMB = (long)(m.total / 1048576UL);
                    }
                    if (nvmlDeviceGetClockInfo(dev, 0, out clk) == 0) clockMhz = (int)clk;
                    if (nvmlDeviceGetPowerUsage(dev, out pw) == 0) powerW = pw / 1000.0;
                    return true;
                }
                catch { ok = false; return false; }
            }

            public static void Shutdown()
            {
                if (ok) { ok = false; try { nvmlShutdown(); } catch { } }
            }
        }

        // ---- AMD ADL (Radeon GPU temp / fan / clock / activity, zero external DLLs) ----
        static class AmdAdl
        {
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            delegate IntPtr AdlAllocDelegate(int size);
            [StructLayout(LayoutKind.Sequential)]
            struct ADLTemperature { public int Size; public int Temperature; }
            [StructLayout(LayoutKind.Sequential)]
            struct ADLFanSpeedValue { public int Size; public int SpeedType; public int FanSpeed; }
            [StructLayout(LayoutKind.Sequential)]
            struct ADLPMActivity
            {
                public int Size, EngineClock, MemoryClock, Vddc, ActivityPercent;
                public int CurrentPerformanceLevel, CurrentBusSpeed, CurrentBusLanes, MaximumBusLanes;
                public int Reserved;
            }

            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.StdCall)]
            static extern int ADL2_Main_Control_Create(AdlAllocDelegate cb, int enumConnectedAdapters, out IntPtr ctx);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.StdCall)]
            static extern int ADL2_Main_Control_Destroy(IntPtr ctx);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.StdCall)]
            static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int numAdapters);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.StdCall)]
            static extern int ADL2_Overdrive5_Temperature_Get(IntPtr ctx, int adapterIdx, int thermalCtrlIdx, ref ADLTemperature temp);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.StdCall)]
            static extern int ADL2_Overdrive5_FanSpeed_Get(IntPtr ctx, int adapterIdx, int thermalCtrlIdx, ref ADLFanSpeedValue fan);
            [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.StdCall)]
            static extern int ADL2_Overdrive5_CurrentActivity_Get(IntPtr ctx, int adapterIdx, ref ADLPMActivity act);

            static readonly AdlAllocDelegate AllocCb = delegate(int sz) { return Marshal.AllocHGlobal(sz); };
            static bool tried, ok;
            static IntPtr ctx = IntPtr.Zero;
            static int adapterIdx;

            public static bool Read(ref double util, ref double tempC, ref double fanPct, ref int clockMhz)
            {
                if (!tried)
                {
                    tried = true;
                    try
                    {
                        int n;
                        if (ADL2_Main_Control_Create(AllocCb, 1, out ctx) == 0 && ctx != IntPtr.Zero &&
                            ADL2_Adapter_NumberOfAdapters_Get(ctx, out n) == 0 && n > 0)
                        {
                            adapterIdx = 0;
                            ok = true;
                        }
                    }
                    catch { ok = false; }
                }
                if (!ok) return false;
                bool any = false;
                try
                {
                    ADLTemperature t = new ADLTemperature();
                    t.Size = Marshal.SizeOf(typeof(ADLTemperature));
                    if (ADL2_Overdrive5_Temperature_Get(ctx, adapterIdx, 0, ref t) == 0 && t.Temperature > 0)
                    {
                        tempC = t.Temperature / 1000.0;
                        any = true;
                    }
                    ADLFanSpeedValue fan = new ADLFanSpeedValue();
                    fan.Size = Marshal.SizeOf(typeof(ADLFanSpeedValue));
                    fan.SpeedType = 1; // ADL_DL_FANCTRL_SPEED_TYPE_PERCENT
                    if (ADL2_Overdrive5_FanSpeed_Get(ctx, adapterIdx, 0, ref fan) == 0 && fan.FanSpeed >= 0 && fan.FanSpeed <= 100)
                    {
                        fanPct = fan.FanSpeed;
                        any = true;
                    }
                    ADLPMActivity act = new ADLPMActivity();
                    act.Size = Marshal.SizeOf(typeof(ADLPMActivity));
                    if (ADL2_Overdrive5_CurrentActivity_Get(ctx, adapterIdx, ref act) == 0)
                    {
                        if (act.ActivityPercent >= 0 && act.ActivityPercent <= 100 && util < 0) util = act.ActivityPercent;
                        if (act.EngineClock > 0) clockMhz = act.EngineClock / 100; // 10 kHz units -> MHz
                        any = true;
                    }
                }
                catch { ok = false; }
                return any;
            }

            public static void Shutdown()
            {
                if (ctx != IntPtr.Zero) { try { ADL2_Main_Control_Destroy(ctx); } catch { } ctx = IntPtr.Zero; ok = false; }
            }
        }

        // ---- DXGI + D3D9 ETW FPS Monitor (admin only, zero external DLLs) ----
        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3d9Provider = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        const string FpsSessionName = "NetSpeedBoss-DxgiFps";
        readonly object fpsGate = new object();
        readonly Dictionary<int, List<long>> pidFrames = new Dictionary<int, List<long>>();
        readonly Dictionary<int, string> pidApi = new Dictionary<int, string>();
        readonly long qpcFreq = Math.Max(1L, Stopwatch.Frequency);
        IntPtr fpsProps = IntPtr.Zero, fpsLogfile = IntPtr.Zero, fpsNamePtr = IntPtr.Zero;
        ulong fpsSession, fpsTrace = ulong.MaxValue;
        Thread fpsThread;
        Native.EventRecordCallback fpsCb;
        public bool FpsEtwActive { get; private set; }

        static IntPtr AllocProps(string sessionName)
        {
            int size = 120 + 1024 * 2;
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, p, size);
            Marshal.WriteInt32(p, 0, size);
            Marshal.WriteInt32(p, 40, 1);              // QPC
            Marshal.WriteInt32(p, 44, 0x00020000);     // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);             // BufferSize KB
            Marshal.WriteInt32(p, 64, 0x100);          // REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);              // FlushTimer = 1s
            Marshal.WriteInt32(p, 116, 120);
            return p;
        }

        public void EnsureFpsEtw(bool wantActive)
        {
            if (!wantActive) { if (FpsEtwActive) StopFpsEtw(); return; }
            if (FpsEtwActive || IntPtr.Size != 8) return;
            try
            {
                fpsProps = AllocProps(FpsSessionName);
                int err = Native.StartTrace(out fpsSession, FpsSessionName, fpsProps);
                if (err == 183)
                {
                    Native.ControlTrace(0, FpsSessionName, fpsProps, 1);
                    Marshal.FreeHGlobal(fpsProps);
                    fpsProps = AllocProps(FpsSessionName);
                    err = Native.StartTrace(out fpsSession, FpsSessionName, fpsProps);
                }
                if (err != 0) { Marshal.FreeHGlobal(fpsProps); fpsProps = IntPtr.Zero; return; }

                Guid g = DxgiProvider;
                err = Native.EnableTraceEx2(fpsSession, ref g, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
                if (err != 0) { StopFpsEtw(); return; }
                Guid g9 = D3d9Provider;
                Native.EnableTraceEx2(fpsSession, ref g9, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);

                fpsLogfile = Marshal.AllocHGlobal(448);
                Marshal.Copy(new byte[448], 0, fpsLogfile, 448);
                fpsNamePtr = Marshal.StringToHGlobalUni(FpsSessionName);
                Marshal.WriteIntPtr(fpsLogfile, 8, fpsNamePtr);
                Marshal.WriteInt32(fpsLogfile, 28, 0x100 | 0x10000000);
                fpsCb = OnDxgiEvent;
                Marshal.WriteIntPtr(fpsLogfile, 424, Marshal.GetFunctionPointerForDelegate(fpsCb));
                fpsTrace = Native.OpenTrace(fpsLogfile);
                if (fpsTrace == ulong.MaxValue) { StopFpsEtw(); return; }

                ulong th = fpsTrace;
                fpsThread = new Thread(delegate() { try { Native.ProcessTrace(new ulong[] { th }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } });
                fpsThread.IsBackground = true;
                fpsThread.Name = "DXGI FPS ETW";
                fpsThread.Start();
                FpsEtwActive = true;
            }
            catch { StopFpsEtw(); }
        }

        public void StopFpsEtw()
        {
            FpsEtwActive = false;
            try
            {
                if (fpsProps != IntPtr.Zero) Native.ControlTrace(fpsSession, FpsSessionName, fpsProps, 1);
                if (fpsTrace != ulong.MaxValue) { Native.CloseTrace(fpsTrace); fpsTrace = ulong.MaxValue; }
                if (fpsThread != null) { fpsThread.Join(1000); fpsThread = null; }
            }
            catch { }
            if (fpsProps != IntPtr.Zero) { Marshal.FreeHGlobal(fpsProps); fpsProps = IntPtr.Zero; }
            if (fpsLogfile != IntPtr.Zero) { Marshal.FreeHGlobal(fpsLogfile); fpsLogfile = IntPtr.Zero; }
            if (fpsNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(fpsNamePtr); fpsNamePtr = IntPtr.Zero; }
        }

        void OnDxgiEvent(IntPtr rec)
        {
            try
            {
                uint prov0 = (uint)Marshal.ReadInt32(rec, 24);
                int id = (ushort)Marshal.ReadInt16(rec, 40);
                byte opcode = Marshal.ReadByte(rec, 45);
                int task = (ushort)Marshal.ReadInt16(rec, 46);
                string api = null;
                if (prov0 == 0xCA11C036u)
                {
                    if (opcode == 1 && (task == 9 || task == 80 || id == 42)) api = "DXGI (DX10/11/12)";
                }
                else if (prov0 == 0x783ACA0Au)
                {
                    if (opcode == 1 && task == 1) api = "Direct3D 9";
                }
                if (api == null) return;
                int pid = Marshal.ReadInt32(rec, 12);
                long qpc = Marshal.ReadInt64(rec, 16);
                if (pid <= 4) return;
                lock (fpsGate)
                {
                    List<long> list;
                    if (!pidFrames.TryGetValue(pid, out list)) { list = new List<long>(128); pidFrames[pid] = list; }
                    list.Add(qpc);
                    pidApi[pid] = api;
                    if (list.Count > 360) list.RemoveRange(0, list.Count - 240);
                }
            }
            catch { }
        }

        void ComputeFpsForPid(int pid, out double fps, out double minFps, out double maxFps, out double low1Fps, out double frameMs)
        {
            fps = 0; minFps = 0; maxFps = 0; low1Fps = 0; frameMs = 0;
            long nowQpc = Stopwatch.GetTimestamp();
            long windowQpc = (long)(1.25 * qpcFreq);
            List<long> recent = null;
            lock (fpsGate)
            {
                List<long> list;
                if (pid > 0 && pidFrames.TryGetValue(pid, out list) && list.Count >= 2)
                {
                    list.RemoveAll(delegate(long t) { return nowQpc - t > windowQpc * 2; });
                    if (list.Count >= 2) recent = new List<long>(list);
                }
                else
                {
                    // prune stale entries
                    List<int> dead = new List<int>();
                    foreach (KeyValuePair<int, List<long>> kv in pidFrames)
                        if (kv.Value.Count == 0 || nowQpc - kv.Value[kv.Value.Count - 1] > qpcFreq * 5) dead.Add(kv.Key);
                    foreach (int d in dead) pidFrames.Remove(d);
                }
            }
            if (recent == null || recent.Count < 2) return;
            long cutoff = nowQpc - windowQpc;
            int start = 0;
            while (start < recent.Count && recent[start] < cutoff) start++;
            int count = recent.Count - start;
            if (count < 2) return;
            double spanSec = (recent[recent.Count - 1] - recent[start]) / (double)qpcFreq;
            if (spanSec <= 0.05) return;

            fps = (count - 1) / spanSec;
            frameMs = 1000.0 / Math.Max(0.1, fps);
            double lo = double.MaxValue, hi = 0;
            List<double> deltas = new List<double>(count);
            for (int i = start + 1; i < recent.Count; i++)
            {
                double dt = (recent[i] - recent[i - 1]) / (double)qpcFreq;
                if (dt > 0.0005 && dt < 0.5)
                {
                    deltas.Add(dt);
                    double inst = 1.0 / dt;
                    if (inst < lo) lo = inst;
                    if (inst > hi) hi = inst;
                }
            }
            minFps = lo < double.MaxValue ? lo : fps;
            maxFps = hi > 0 ? hi : fps;
            if (deltas.Count > 0)
            {
                deltas.Sort();
                int idx99 = Math.Max(0, Math.Min(deltas.Count - 1, (int)Math.Floor(deltas.Count * 0.99)));
                low1Fps = 1.0 / deltas[idx99];
            }
            else low1Fps = minFps;
        }

        // ---- Non-game exclusion list & game path heuristics ----
        static readonly HashSet<string> NonGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc",
            "dwm", "taskmgr", "applicationframehost", "shellexperiencehost", "searchhost", "startmenuexperiencehost",
            "code", "devenv", "rider64", "idea64", "orca", "powershell", "pwsh", "cmd", "windowsterminal", "conhost",
            "discord", "slack", "teams", "ms-teams", "spotify", "obs64", "netspeedboss", "steamwebhelper"
        };

        static bool IsGameFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            return p.Contains("\\steamapps\\common\\") || p.Contains("\\epic games\\") || p.Contains("\\riot games\\") ||
                   p.Contains("\\xboxgames\\") || p.Contains("\\gog galaxy\\games\\") || p.Contains("\\battle.net\\") ||
                   p.Contains("\\ea games\\") || p.Contains("\\ubisoft game launcher\\games\\") || p.Contains("\\rockstar games\\");
        }

        static bool IsPrivateOrLoopback(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 10 || b[0] == 127) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 169 && b[1] == 254) return true;
            }
            else if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            return false;
        }

        static string ExtractRemoteIp(string remote)
        {
            if (string.IsNullOrEmpty(remote)) return null;
            int colon = remote.LastIndexOf(':');
            if (colon <= 0) return null;
            string host = remote.Substring(0, colon).Trim('[', ']');
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip) || IsPrivateOrLoopback(ip)) return null;
            return ip.ToString();
        }

        /// <summary>Updates all telemetry once per tick; call every second while HUD auto-detect or HUD is visible.</summary>
        public void Sample(TrayApp app, bool hudForcedOn)
        {
            // 1. Foreground process & window check
            IntPtr fg = Native.GetForegroundWindow();
            uint pidU = 0;
            if (fg != IntPtr.Zero) Native.GetWindowThreadProcessId(fg, out pidU);
            int fgPid = (int)pidU;
            bool fgFullscreen = false;
            Screen sc = Screen.PrimaryScreen;
            if (fg != IntPtr.Zero)
            {
                Native.RECT r;
                if (Native.GetWindowRect(fg, out r))
                {
                    System.Drawing.Rectangle wr = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    sc = Screen.FromRectangle(wr);
                    fgFullscreen = wr.Width >= sc.Bounds.Width - 2 && wr.Height >= sc.Bounds.Height - 2;
                }
            }
            ScreenW = sc.Bounds.Width; ScreenH = sc.Bounds.Height;
            RefreshHz = Native.GetScreenRefreshHz(sc);

            int d3dState = 0;
            try { Native.SHQueryUserNotificationState(out d3dState); } catch { }
            bool d3dExclusive = d3dState == 3; // QUNS_RUNNING_D3D_FULL_SCREEN

            string fgName = "", fgPath = "";
            ProcView fgProc = null;
            List<ProcView> snap = (app.Procs.EtwActive || hudForcedOn || fgFullscreen || d3dExclusive)
                ? app.Procs.Snapshot() : null;
            if (snap != null)
                foreach (ProcView v in snap) if (v.Pid == fgPid) { fgProc = v; break; }

            if (fgProc != null) { fgName = fgProc.Name ?? ""; fgPath = fgProc.Path ?? ""; }
            else if (fgPid > 4 && fgPid != selfPid)
            {
                try { using (Process p = Process.GetProcessById(fgPid)) fgName = p.ProcessName; } catch { }
                fgPath = Native.GetProcessPath(fgPid) ?? "";
            }
            string bareName = fgName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? fgName.Substring(0, fgName.Length - 4) : fgName;

            // 2. GPU sensors (per-PID LUID match across NVIDIA/AMD/Intel/Qualcomm + NVML when NVIDIA)
            app.Sys.SampleGpu();
            double fgGpu3d = 0; string fgLuid = null;
            lock (app.Sys.PidGpu3d)
            {
                app.Sys.PidGpu3d.TryGetValue(fgPid, out fgGpu3d);
                app.Sys.PidGpuLuid.TryGetValue(fgPid, out fgLuid);
            }
            GameGpu3d = fgGpu3d;

            GpuInfo activeGpu = app.Sys.Gpus.Count > 0 ? app.Sys.Gpus[0] : null;
            if (!string.IsNullOrEmpty(fgLuid))
                foreach (GpuInfo gi in app.Sys.Gpus) if (gi.Luid == fgLuid) { activeGpu = gi; break; }
            if (activeGpu != null) GpuName = activeGpu.Name;

            double nvUtil = -1, nvTemp = -1, nvFan = -1, nvPower = -1;
            long nvUsed = -1, nvTot = -1;
            int nvClk = -1;
            bool isNv = activeGpu != null && activeGpu.VendorId == 0x10DE;
            bool isAmd = activeGpu != null && activeGpu.VendorId == 0x1002;
            if (isNv && Nvml.Read(ref nvUtil, ref nvTemp, ref nvFan, ref nvUsed, ref nvTot, ref nvClk, ref nvPower))
            {
                GpuUtil = nvUtil; GpuTempC = nvTemp; GpuFanPct = nvFan;
                VramUsedMB = nvUsed; VramTotalMB = nvTot; GpuClockMhz = nvClk; GpuPowerW = nvPower;
            }
            else if (activeGpu != null)
            {
                GpuUtil = activeGpu.Util;
                GpuTempC = -1; GpuFanPct = -1; GpuPowerW = -1; GpuClockMhz = -1;
                VramUsedMB = activeGpu.DedicatedUsed / 1048576L;
                VramTotalMB = activeGpu.Vram / 1048576L;
                if (isAmd)
                {
                    double aUtil = GpuUtil, aTemp = -1, aFan = -1; int aClk = -1;
                    if (AmdAdl.Read(ref aUtil, ref aTemp, ref aFan, ref aClk))
                    {
                        GpuUtil = aUtil; GpuTempC = aTemp; GpuFanPct = aFan; GpuClockMhz = aClk;
                    }
                }
            }

            // 3. DXGI + D3D9 ETW FPS (when Admin + HUD auto or forced)
            EnsureFpsEtw(app.IsAdmin && (app.S.GameHudAuto || hudForcedOn));
            double fps, minF, maxF, low1F, fms;
            ComputeFpsForPid(fgPid, out fps, out minF, out maxF, out low1F, out fms);
            Fps = fps; FpsMin = minF; FpsMax = maxF; Fps1PctLow = low1F; FrameMs = fms;
            FpsHist[HistPos] = fps;
            FrameMsHist[HistPos] = fms;
            HistPos = (HistPos + 1) % FpsHist.Length;

            string api = null;
            lock (fpsGate) pidApi.TryGetValue(fgPid, out api);
            if (string.IsNullOrEmpty(api) && fgGpu3d >= 5) api = "GPU 3D Engine (Vulkan/OpenGL/DX)";
            GraphicsApi = api ?? (FpsEtwActive ? "Idle" : "GPU Engine counters");

            // 4. Universal Windows game detection decision with 2.5s sticky grace
            bool excluded = fgPid <= 4 || fgPid == selfPid || string.IsNullOrEmpty(bareName) || NonGames.Contains(bareName);
            string reason = "";
            if (!excluded)
            {
                if (fps >= 12) reason = (api ?? "DX") + " (" + fps.ToString("0") + " FPS)";
                else if (d3dExclusive) reason = "D3D Exclusive Fullscreen";
                else if (fgGpu3d >= 15) reason = "Active GPU 3D (" + fgGpu3d.ToString("0") + "%)";
                else if (IsGameFolder(fgPath) && (fgFullscreen || GpuUtil >= 10)) reason = "Game library + GPU";
                else if (fgFullscreen && GpuUtil >= 25) reason = "Fullscreen + GPU load";
            }
            bool looksLikeGame = reason.Length > 0;
            if (looksLikeGame)
            {
                GamePid = fgPid;
                GameName = bareName;
                GameTitle = fgName;
                DetectReason = reason;
                lastGameSeenUtc = DateTime.UtcNow;
                GameActive = true;
            }
            else if (GameActive && (DateTime.UtcNow - lastGameSeenUtc).TotalSeconds > 2.5)
            {
                FinishSession(app.S.GameHudLogSessions);
                GameActive = false;
                DetectReason = "No active game";
                if (!hudForcedOn) return;
            }
            else if (!GameActive)
            {
                DetectReason = excluded ? "Desktop / app (" + bareName + ")" : "Idle";
            }

            if (!GameActive && !hudForcedOn) return;

            // 5. CPU + Multi-core + optional CPU temp
            CpuUtil = app.Sys.Cpu;
            CpuGhz = app.Sys.CpuGhz;
            SamplePerCoreCpu();
            if ((DateTime.UtcNow - lastCpuTempUtc).TotalSeconds >= 3)
            {
                lastCpuTempUtc = DateTime.UtcNow;
                if (app.IsAdmin) SampleCpuTemp();
            }

            // 6. Memory + Game Process RAM
            MemUtil = app.Sys.Mem;
            MemUsedBytes = app.Sys.MemUsed;
            MemTotalBytes = app.Sys.MemTotal;
            int targetPid = GameActive ? GamePid : fgPid;
            if (targetPid > 4)
            {
                try { using (Process tp = Process.GetProcessById(targetPid)) GameMemBytes = tp.WorkingSet64; }
                catch { GameMemBytes = 0; }
            }
            else GameMemBytes = 0;

            // 7. Network speed & game server ping
            TotalRx = app.CurRx; TotalTx = app.CurTx;
            ProcView activeProc = null;
            if (snap != null && targetPid > 4)
                foreach (ProcView v in snap) if (v.Pid == targetPid) { activeProc = v; break; }
            if (activeProc != null && app.Procs.EtwActive)
            {
                GameRx = activeProc.Rx; GameTx = activeProc.Tx;
            }
            else
            {
                GameRx = TotalRx; GameTx = TotalTx;
            }

            string srvIp = null;
            if (activeProc != null && activeProc.Eps.Count > 0)
            {
                double bestRate = -1;
                foreach (EpView ep in activeProc.Eps)
                {
                    string ip = ExtractRemoteIp(ep.Remote);
                    if (ip == null) continue;
                    double r = ep.Rx + ep.Tx + (ep.State == "Established" ? 1.0 : 0.0);
                    if (r > bestRate) { bestRate = r; srvIp = ip; }
                }
            }
            if ((DateTime.UtcNow - lastPingUtc).TotalSeconds >= 2 && !pingBusy)
                PingAsync(srvIp);

            // 8. Track active game session telemetry
            if (GameActive) UpdateSession(app.S.GameHudLogSessions);
        }

        void UpdateSession(bool saveCsv)
        {
            if (!sessTracking || !string.Equals(sessGameName, GameName, StringComparison.OrdinalIgnoreCase))
            {
                if (sessTracking) FinishSession(saveCsv);
                sessTracking = true;
                sessGameName = GameName;
                sessStartLocal = DateTime.Now;
                sessFpsSum = 0; sessFpsSamples = 0; sessLow1Min = double.MaxValue;
                sessPeakGpuTemp = -1; sessPeakCpuTemp = -1;
                sessNetRxBytes = 0; sessNetTxBytes = 0;
                lastSessionSampleUtc = DateTime.UtcNow;
            }
            DateTime nowU = DateTime.UtcNow;
            double dt = Math.Max(0, Math.Min(5.0, (nowU - lastSessionSampleUtc).TotalSeconds));
            lastSessionSampleUtc = nowU;
            sessNetRxBytes += (long)(GameRx * dt);
            sessNetTxBytes += (long)(GameTx * dt);
            if (Fps > 1) { sessFpsSum += Fps; sessFpsSamples++; }
            if (Fps1PctLow > 1 && Fps1PctLow < sessLow1Min) sessLow1Min = Fps1PctLow;
            if (GpuTempC > sessPeakGpuTemp) sessPeakGpuTemp = GpuTempC;
            if (CpuTempC > sessPeakCpuTemp) sessPeakCpuTemp = CpuTempC;
        }

        void FinishSession(bool saveCsv)
        {
            if (!sessTracking) return;
            sessTracking = false;
            DateTime end = DateTime.Now;
            double sec = (end - sessStartLocal).TotalSeconds;
            if (sec < 30 || string.IsNullOrEmpty(sessGameName)) return;
            GameSessionSummary sum = new GameSessionSummary();
            sum.GameName = sessGameName;
            sum.StartedAt = sessStartLocal;
            sum.EndedAt = end;
            sum.DurationSec = (int)sec;
            sum.AvgFps = sessFpsSamples > 0 ? sessFpsSum / sessFpsSamples : 0;
            sum.Low1Fps = sessLow1Min < double.MaxValue ? sessLow1Min : 0;
            sum.PeakGpuTemp = sessPeakGpuTemp;
            sum.PeakCpuTemp = sessPeakCpuTemp;
            sum.BytesDown = sessNetRxBytes;
            sum.BytesUp = sessNetTxBytes;
            LastSession = sum;
            if (saveCsv) sum.AppendToCsv();
        }

        static readonly Regex CoreRx = new Regex(@"^(\d+),(\d+)$", RegexOptions.Compiled);

        void SamplePerCoreCpu()
        {
            if (coreCounterMissing) return;
            try
            {
                InstanceDataCollectionCollection all = new PerformanceCounterCategory("Processor Information").ReadCategory();
                if (coreCounterName == null)
                {
                    coreCounterName = all.Contains("% Processor Utility") ? "% Processor Utility"
                        : (all.Contains("% Processor Time") ? "% Processor Time" : "");
                }
                if (string.IsNullOrEmpty(coreCounterName) || !all.Contains(coreCounterName)) { coreCounterMissing = true; return; }
                InstanceDataCollection col = all[coreCounterName];
                List<KeyValuePair<int, double>> vals = new List<KeyValuePair<int, double>>();
                Dictionary<string, CounterSample> now = new Dictionary<string, CounterSample>();
                foreach (InstanceData d in col.Values)
                {
                    Match m = CoreRx.Match(d.InstanceName);
                    if (!m.Success) continue;
                    now[d.InstanceName] = d.Sample;
                    CounterSample prev;
                    if (!prevCoreSamples.TryGetValue(d.InstanceName, out prev)) continue;
                    double v = Math.Max(0, Math.Min(100, CounterSample.Calculate(prev, d.Sample)));
                    int idx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    vals.Add(new KeyValuePair<int, double>(idx, v));
                }
                prevCoreSamples = now;
                if (vals.Count == 0) return;
                vals.Sort(delegate(KeyValuePair<int, double> a, KeyValuePair<int, double> b) { return a.Key.CompareTo(b.Key); });
                double[] arr = new double[vals.Count];
                for (int i = 0; i < vals.Count; i++) arr[i] = vals[i].Value;
                CoreUtils = arr;
            }
            catch { coreCounterMissing = true; }
        }

        void SampleCpuTemp()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    double maxC = -1;
                    foreach (ManagementObject o in s.Get())
                    {
                        object v = o["CurrentTemperature"];
                        if (v == null) continue;
                        double c = Convert.ToDouble(v, CultureInfo.InvariantCulture) / 10.0 - 273.15;
                        if (c > 0 && c < 130 && c > maxC) maxC = c;
                    }
                    if (maxC > 0) CpuTempC = maxC;
                }
            }
            catch { }
        }

        void PingAsync(string gameServerIp)
        {
            pingBusy = true;
            lastPingUtc = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(delegate
            {
                long ms = -1;
                string tgt = gameServerIp;
                string lbl = "Game Srv";
                try
                {
                    using (Ping p = new Ping())
                    {
                        if (!string.IsNullOrEmpty(gameServerIp))
                        {
                            PingReply r = p.Send(gameServerIp, 900);
                            if (r != null && r.Status == IPStatus.Success) ms = r.RoundtripTime;
                        }
                        if (ms < 0)
                        {
                            tgt = "1.1.1.1";
                            lbl = string.IsNullOrEmpty(gameServerIp) ? "Internet" : "Net (Srv blocked ICMP)";
                            PingReply r2 = p.Send("1.1.1.1", 1000);
                            if (r2 != null && r2.Status == IPStatus.Success) ms = r2.RoundtripTime;
                        }
                    }
                }
                catch { ms = -1; }
                PingMs = ms;
                PingTarget = tgt ?? "";
                PingLabel = lbl;
                pingBusy = false;
            });
        }

        public void Dispose()
        {
            FinishSession(true);
            StopFpsEtw();
            Nvml.Shutdown();
            AmdAdl.Shutdown();
        }
    }

    public class GameSessionSummary
    {
        public string GameName = "";
        public DateTime StartedAt, EndedAt;
        public int DurationSec;
        public double AvgFps, Low1Fps, PeakGpuTemp, PeakCpuTemp;
        public long BytesDown, BytesUp;

        public void AppendToCsv()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Settings.LogDir);
                string path = System.IO.Path.Combine(Settings.LogDir, "game-sessions.csv");
                bool isNew = !System.IO.File.Exists(path);
                string hdr = "Started,Ended,Game,Duration(s),Avg FPS,1% Low FPS,Peak GPU Temp(C),Peak CPU Temp(C),Downloaded(MB),Uploaded(MB)\r\n";
                string row = string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss},{2},{3},{4:0.0},{5:0.0},{6:0},{7:0},{8:0.00},{9:0.00}\r\n",
                    StartedAt, EndedAt, GameName.Replace(',', ' '), DurationSec, AvgFps, Low1Fps,
                    PeakGpuTemp > 0 ? PeakGpuTemp : 0, PeakCpuTemp > 0 ? PeakCpuTemp : 0,
                    BytesDown / 1048576.0, BytesUp / 1048576.0);
                System.IO.File.AppendAllText(path, (isNew ? hdr : "") + row, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
