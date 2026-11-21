using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetSpeedBoss
{
    /// <summary>Daily download/upload totals, persisted to %APPDATA%\NetSpeedBoss\usage.csv</summary>
    public class UsageStore
    {
        readonly Dictionary<DateTime, long[]> days = new Dictionary<DateTime, long[]>();
        readonly object gate = new object();
        static string FilePath { get { return Path.Combine(Settings.Dir, "usage.csv"); } }
        bool dirty;

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split(',');
                    if (p.Length < 3) continue;
                    DateTime d;
                    long rx, tx;
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                    if (!long.TryParse(p[1], out rx) || !long.TryParse(p[2], out tx)) continue;
                    days[d.Date] = new long[] { rx, tx };
                }
            }
            catch { }
        }

        public void Save()
        {
            lock (gate)
            {
                if (!dirty) return;
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    List<DateTime> keys = new List<DateTime>(days.Keys);
                    keys.Sort();
                    StringBuilder sb = new StringBuilder();
                    foreach (DateTime k in keys)
                        sb.Append(k.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                          .Append(days[k][0]).Append(',').Append(days[k][1]).AppendLine();
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, sb.ToString());
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    File.Move(tmp, FilePath);
                    dirty = false;
                }
                catch { }
            }
        }

        public void Add(long rx, long tx)
        {
            if (rx <= 0 && tx <= 0) return;
            lock (gate)
            {
                DateTime d = DateTime.Today;
                long[] v;
                if (!days.TryGetValue(d, out v)) { v = new long[2]; days[d] = v; }
                v[0] += rx; v[1] += tx;
                dirty = true;
            }
        }

        public long[] Day(DateTime d)
        {
            lock (gate)
            {
                long[] v;
                if (days.TryGetValue(d.Date, out v)) return new long[] { v[0], v[1] };
                return new long[2];
            }
        }

        public long[] Range(DateTime fromInclusive, DateTime toInclusive)
        {
            long[] r = new long[2];
            lock (gate)
            {
                foreach (KeyValuePair<DateTime, long[]> kv in days)
                    if (kv.Key >= fromInclusive.Date && kv.Key <= toInclusive.Date) { r[0] += kv.Value[0]; r[1] += kv.Value[1]; }
            }
            return r;
        }

        /// <summary>Start of the current billing period given the day-of-month it resets on.</summary>
        public static DateTime PeriodStart(int startDay)
        {
            DateTime t = DateTime.Today;
            DateTime s = new DateTime(t.Year, t.Month, Math.Min(startDay, 28));
            if (s > t) s = s.AddMonths(-1);
            return s;
        }

        public List<KeyValuePair<DateTime, long[]>> All()
        {
            List<KeyValuePair<DateTime, long[]>> l = new List<KeyValuePair<DateTime, long[]>>();
            lock (gate)
                foreach (KeyValuePair<DateTime, long[]> kv in days)
                    l.Add(new KeyValuePair<DateTime, long[]>(kv.Key, new long[] { kv.Value[0], kv.Value[1] }));
            l.Sort(delegate(KeyValuePair<DateTime, long[]> a, KeyValuePair<DateTime, long[]> b) { return b.Key.CompareTo(a.Key); });
            return l;
        }

        public void Reset()
        {
            lock (gate) { days.Clear(); dirty = true; }
            Save();
        }

        public void ExportCsv(string path)
        {
            StringBuilder sb = new StringBuilder("Date,Downloaded (bytes),Uploaded (bytes),Total (bytes)\r\n");
            List<KeyValuePair<DateTime, long[]>> all = All();
            all.Reverse();
            foreach (KeyValuePair<DateTime, long[]> kv in all)
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd},{1},{2},{3}\r\n", kv.Key, kv.Value[0], kv.Value[1], kv.Value[0] + kv.Value[1]);
            File.WriteAllText(path, sb.ToString());
        }
    }
}
