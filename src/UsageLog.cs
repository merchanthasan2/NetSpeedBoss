using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetSpeedBoss
{
    /// <summary>
    /// Human-readable, append-only usage logs in Documents\NetSpeed Boss\:
    ///   hourly-usage.csv  one row per hour: bytes down/up and peak speeds
    ///   app-usage.csv     one row per app per hour: bytes down/up (needs administrator mode)
    /// Rows for the same period may appear more than once (e.g. after a restart); readers simply add them up.
    /// </summary>
    public class UsageLog
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string HourlyPath { get { return Path.Combine(Settings.LogDir, "hourly-usage.csv"); } }
        public static string AppPath { get { return Path.Combine(Settings.LogDir, "app-usage.csv"); } }

        readonly object gate = new object();
        DateTime hour = DateTime.MinValue;
        long hRx, hTx;
        double hPeakRx, hPeakTx;
        readonly Dictionary<string, long[]> appHour = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        DateTime appHourStart = DateTime.MinValue;

        static DateTime HourOf(DateTime t) { return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0); }

        public void Add(long rx, long tx, double rxBps, double txBps)
        {
            lock (gate)
            {
                DateTime h = HourOf(DateTime.Now);
                if (h != hour) { FlushHourLocked(); FlushAppsLocked(); hour = h; }
                hRx += Math.Max(0, rx); hTx += Math.Max(0, tx);
                if (rxBps > hPeakRx) hPeakRx = rxBps;
                if (txBps > hPeakTx) hPeakTx = txBps;
            }
        }

        public void AddApps(List<ProcView> procs)
        {
            lock (gate)
            {
                DateTime h = HourOf(DateTime.Now);
                if (h != appHourStart) { FlushAppsLocked(); appHourStart = h; }
                foreach (ProcView v in procs)
                {
                    if (v.LastIn + v.LastOut <= 0 || v.Pid == 0) continue;
                    string name = (v.Name ?? ("PID " + v.Pid)).Replace(',', ' ');
                    long[] a;
                    if (!appHour.TryGetValue(name, out a)) { a = new long[2]; appHour[name] = a; }
                    a[0] += v.LastIn; a[1] += v.LastOut;
                }
            }
        }

        public void Flush() { lock (gate) { FlushHourLocked(); FlushAppsLocked(); } }

        void FlushHourLocked()
        {
            if (hour == DateTime.MinValue || (hRx == 0 && hTx == 0)) { hRx = hTx = 0; hPeakRx = hPeakTx = 0; return; }
            string row = string.Format(Inv, "{0:yyyy-MM-dd HH:mm},{1},{2},{3:0},{4:0},{5:0.00},{6:0.00}",
                hour, hRx, hTx, hPeakRx, hPeakTx, hRx / 1048576.0, hTx / 1048576.0);
            if (Append(HourlyPath, "Hour,Downloaded bytes,Uploaded bytes,Peak download (bytes/s),Peak upload (bytes/s),Downloaded MB,Uploaded MB", row))
            { hRx = hTx = 0; hPeakRx = hPeakTx = 0; }
        }

        void FlushAppsLocked()
        {
            if (appHour.Count == 0 || appHourStart == DateTime.MinValue) { appHour.Clear(); return; }
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, long[]> kv in appHour)
            {
                if (sb.Length > 0) sb.Append("\r\n");
                sb.AppendFormat(Inv, "{0:yyyy-MM-dd HH:mm},{1},{2},{3},{4:0.00},{5:0.00}",
                    appHourStart, kv.Key, kv.Value[0], kv.Value[1], kv.Value[0] / 1048576.0, kv.Value[1] / 1048576.0);
            }
            if (Append(AppPath, "Hour,App,Downloaded bytes,Uploaded bytes,Downloaded MB,Uploaded MB", sb.ToString()))
                appHour.Clear();
        }

        static bool Append(string path, string header, string rows)
        {
            try
            {
                Directory.CreateDirectory(Settings.LogDir);
                WriteReadme();
                bool isNew = !File.Exists(path);
                File.AppendAllText(path, (isNew ? header + "\r\n" : "") + rows + "\r\n", Encoding.UTF8);
                return true;
            }
            catch { return false; }   // e.g. file open and locked in Excel: keep the data and retry next time
        }

        public static void WriteReadme()
        {
            string p = Path.Combine(Settings.LogDir, "README.txt");
            if (File.Exists(p)) return;
            try
            {
                File.WriteAllText(p,
"NetSpeed Boss usage logs\r\n" +
"========================\r\n\r\n" +
"These files are written automatically by NetSpeed Boss and open in Excel or any spreadsheet app.\r\n\r\n" +
"daily-usage.csv   Total downloaded / uploaded per day.\r\n" +
"hourly-usage.csv  Downloaded / uploaded per hour, plus the peak speed reached in that hour.\r\n" +
"app-usage.csv     Downloaded / uploaded per app per hour (recorded while NetSpeed Boss runs as administrator).\r\n\r\n" +
"Times are local time. If the same hour appears more than once (for example after a restart), add the rows together.\r\n" +
"To see totals for any date range, open NetSpeed Boss > Usage > Date range.\r\n" +
"You can delete these files at any time; new ones will be started.\r\n");
            }
            catch { }
        }

        // ------------------------------------------------------------------ queries
        /// <summary>Per-app totals (bytes down, up) for the given dates, including data not yet written to disk.</summary>
        public Dictionary<string, long[]> AppTotals(DateTime from, DateTime to)
        {
            Dictionary<string, long[]> r = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            DateTime f = from.Date, t = to.Date;
            try
            {
                if (File.Exists(AppPath))
                {
                    using (FileStream fs = new FileStream(AppPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            string[] p = line.Split(',');
                            DateTime d;
                            long rx, tx;
                            if (p.Length < 4 || p[0].Length < 10) continue;
                            if (!DateTime.TryParseExact(p[0].Substring(0, 10), "yyyy-MM-dd", Inv, DateTimeStyles.None, out d)) continue;
                            if (d < f || d > t) continue;
                            if (!long.TryParse(p[2], out rx) || !long.TryParse(p[3], out tx)) continue;
                            Add(r, p[1], rx, tx);
                        }
                    }
                }
            }
            catch { }
            lock (gate)
                if (appHourStart.Date >= f && appHourStart.Date <= t)
                    foreach (KeyValuePair<string, long[]> kv in appHour) Add(r, kv.Key, kv.Value[0], kv.Value[1]);
            return r;
        }

        static void Add(Dictionary<string, long[]> r, string k, long rx, long tx)
        {
            long[] a;
            if (!r.TryGetValue(k, out a)) { a = new long[2]; r[k] = a; }
            a[0] += rx; a[1] += tx;
        }
    }
}
