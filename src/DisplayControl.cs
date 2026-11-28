using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace NetSpeedBoss
{
    public class MonitorItem : IDisposable
    {
        public string DeviceName = "";
        public string FriendlyName = "";
        public int Width, Height, CurrentHz;
        public List<int> AvailableHz = new List<int>();
        public bool IsPrimary, IsLaptopWmi;
        public bool HasBrightness, HasContrast, HasInputVcp;
        public uint BrightMin, BrightCur, BrightMax = 100;
        public uint ContrastMin, ContrastCur, ContrastMax = 100;
        public uint CurrentInput;
        public IntPtr PhysicalHandle = IntPtr.Zero;

        public void Dispose()
        {
            if (PhysicalHandle != IntPtr.Zero)
            {
                try { Native.DestroyPhysicalMonitors(1, new Native.PHYSICAL_MONITOR[] { new Native.PHYSICAL_MONITOR { hPhysicalMonitor = PhysicalHandle } }); } catch { }
                PhysicalHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Native multi-monitor hardware controller via Win32 DDC/CI (dxva2.dll) for external
    /// displays, plus WMI WmiMonitorBrightnessMethods fallback for laptop panels, and refresh-rate control.
    /// </summary>
    public static class DisplayControl
    {
        public static List<MonitorItem> Enumerate()
        {
            List<MonitorItem> items = new List<MonitorItem>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref Native.RECT rc, IntPtr data)
            {
                try
                {
                    Native.MONITORINFOEX mi = new Native.MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                    if (!Native.GetMonitorInfo(hMon, ref mi)) return true;

                    MonitorItem m = new MonitorItem();
                    m.DeviceName = mi.szDevice ?? "";
                    m.IsPrimary = (mi.dwFlags & 1) != 0;
                    m.Width = rc.Right - rc.Left;
                    m.Height = rc.Bottom - rc.Top;
                    PopulateDisplayModes(m);

                    uint cnt;
                    if (Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out cnt) && cnt > 0)
                    {
                        Native.PHYSICAL_MONITOR[] pms = new Native.PHYSICAL_MONITOR[cnt];
                        if (Native.GetPhysicalMonitorsFromHMONITOR(hMon, cnt, pms))
                        {
                            // Keep first physical handle, destroy extra handles if any
                            m.PhysicalHandle = pms[0].hPhysicalMonitor;
                            if (!string.IsNullOrEmpty(pms[0].szPhysicalMonitorDescription))
                                m.FriendlyName = pms[0].szPhysicalMonitorDescription.Trim();
                            for (int i = 1; i < pms.Length; i++)
                                try { Native.DestroyPhysicalMonitors(1, new Native.PHYSICAL_MONITOR[] { pms[i] }); } catch { }

                            uint minB, curB, maxB;
                            if (Native.GetMonitorBrightness(m.PhysicalHandle, out minB, out curB, out maxB) && maxB > minB)
                            {
                                m.HasBrightness = true; m.BrightMin = minB; m.BrightCur = curB; m.BrightMax = maxB;
                            }
                            uint minC, curC, maxC;
                            if (Native.GetMonitorContrast(m.PhysicalHandle, out minC, out curC, out maxC) && maxC > minC)
                            {
                                m.HasContrast = true; m.ContrastMin = minC; m.ContrastCur = curC; m.ContrastMax = maxC;
                            }
                            uint vt, curIn, maxIn;
                            if (Native.GetVCPFeatureAndVCPFeatureReply(m.PhysicalHandle, 0x60, out vt, out curIn, out maxIn))
                            {
                                m.HasInputVcp = true; m.CurrentInput = curIn;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(m.FriendlyName))
                        m.FriendlyName = "Display " + (items.Count + 1) + (m.IsPrimary ? " (Primary)" : "");

                    if (!m.HasBrightness) TryAttachWmiLaptopBrightness(m);
                    items.Add(m);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return items;
        }

        static void PopulateDisplayModes(MonitorItem m)
        {
            try
            {
                Native.DEVMODE cur = new Native.DEVMODE();
                cur.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                if (Native.EnumDisplaySettings(m.DeviceName, -1, ref cur))
                {
                    m.Width = cur.dmPelsWidth;
                    m.Height = cur.dmPelsHeight;
                    m.CurrentHz = cur.dmDisplayFrequency;
                }
                HashSet<int> rates = new HashSet<int>();
                Native.DEVMODE dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                for (int mode = 0; Native.EnumDisplaySettings(m.DeviceName, mode, ref dm); mode++)
                {
                    if (dm.dmPelsWidth == m.Width && dm.dmPelsHeight == m.Height && dm.dmDisplayFrequency > 1)
                        rates.Add(dm.dmDisplayFrequency);
                }
                m.AvailableHz.AddRange(rates);
                m.AvailableHz.Sort();
            }
            catch { }
        }

        static void TryAttachWmiLaptopBrightness(MonitorItem m)
        {
            if (!m.IsPrimary) return;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        m.HasBrightness = true; m.IsLaptopWmi = true;
                        m.BrightMin = 0; m.BrightMax = 100;
                        m.BrightCur = Convert.ToUInt32(o["CurrentBrightness"]);
                        break;
                    }
                }
            }
            catch { }
        }

        public static bool SetBrightness(MonitorItem m, int pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            if (m.IsLaptopWmi)
            {
                try
                {
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)pct });
                            m.BrightCur = (uint)pct;
                            return true;
                        }
                    }
                }
                catch { }
                return false;
            }
            if (m.PhysicalHandle == IntPtr.Zero || !m.HasBrightness) return false;
            uint raw = m.BrightMin + (uint)Math.Round((m.BrightMax - m.BrightMin) * (pct / 100.0));
            if (Native.SetMonitorBrightness(m.PhysicalHandle, raw)) { m.BrightCur = raw; return true; }
            return false;
        }

        public static bool SetContrast(MonitorItem m, int pct)
        {
            if (m.PhysicalHandle == IntPtr.Zero || !m.HasContrast) return false;
            pct = Math.Max(0, Math.Min(100, pct));
            uint raw = m.ContrastMin + (uint)Math.Round((m.ContrastMax - m.ContrastMin) * (pct / 100.0));
            if (Native.SetMonitorContrast(m.PhysicalHandle, raw)) { m.ContrastCur = raw; return true; }
            return false;
        }

        public static bool SetInputSource(MonitorItem m, uint vcpVal)
        {
            if (m.PhysicalHandle == IntPtr.Zero || !m.HasInputVcp) return false;
            if (Native.SetVCPFeature(m.PhysicalHandle, 0x60, vcpVal)) { m.CurrentInput = vcpVal; return true; }
            return false;
        }

        public static bool SetRefreshHz(MonitorItem m, int hz)
        {
            try
            {
                Native.DEVMODE dm = new Native.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
                if (!Native.EnumDisplaySettings(m.DeviceName, -1, ref dm)) return false;
                dm.dmDisplayFrequency = hz;
                dm.dmFields = (int)Native.DM_DISPLAYFREQUENCY;
                int r = Native.ChangeDisplaySettingsEx(m.DeviceName, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (r == 0) { m.CurrentHz = hz; return true; }
            }
            catch { }
            return false;
        }

        public static string InputLabel(uint code)
        {
            switch (code)
            {
                case 1: return "VGA";
                case 3: return "DVI";
                case 15: return "DisplayPort 1";
                case 16: return "DisplayPort 2";
                case 17: return "HDMI 1";
                case 18: return "HDMI 2";
                case 27: return "USB-C / DP";
                default: return "Input " + code;
            }
        }
    }
}
