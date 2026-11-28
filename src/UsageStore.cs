using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetSpeedBoss
{
    /// <summary>Daily download/upload totals, persisted to Documents\NetSpeed Boss\daily-usage.csv</summary>
    public class UsageStore
    {
        readonly Dictionary<DateTime, long[]> days = new Dictionary<DateTime, long[]>();
        readonly Dictionary<string, long[]> profileDays = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, double> profileCapsGB = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        public static string FilePath { get { return Path.Combine(Settings.LogDir, "daily-usage.csv"); } }
        public static string ProfileFilePath { get { return Path.Combine(Settings.LogDir, "profile-usage.csv"); } }
        static string ProfileCapsPath { get { return Path.Combine(Settings.Dir, "profile-caps.ini"); } }
        static string LegacyPath { get { return Path.Combine(Settings.Dir, "usage.csv"); } }
        bool dirty, profileDirty;

        public void Load()
        {
            try
            {
                string src = FilePath;
                if (!File.Exists(src))
                {
                    if (!File.Exists(LegacyPath)) return;
                    src = LegacyPath;     // first run of a version with Documents logs: migrate
                    dirty = true;
                }
                string[] lines;
                using (FileStream fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                    lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
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
            LoadProfiles();
        }

        void LoadProfiles()
        {
            try
            {
                if (File.Exists(ProfileFilePath))
                {
                    string[] lines;
                    using (FileStream fs = new FileStream(ProfileFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs))
                        lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string[] p = line.Split(',');
                        if (p.Length < 4) continue;
                        DateTime d; long rx, tx;
                        if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                        if (!long.TryParse(p[2], out rx) || !long.TryParse(p[3], out tx)) continue;
                        string k = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + p[1];
                        profileDays[k] = new long[] { rx, tx };
                    }
                }
                if (File.Exists(ProfileCapsPath))
                {
                    foreach (string line in File.ReadAllLines(ProfileCapsPath))
                    {
                        int eq = line.LastIndexOf('=');
                        double gb;
                        if (eq > 0 && double.TryParse(line.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out gb) && gb > 0)
                            profileCapsGB[line.Substring(0, eq).Trim()] = gb;
                    }
                }
            }
            catch { }
        }

        static void WriteCsvAtomic(string path, string content)
        {
            Directory.CreateDirectory(Settings.LogDir);
            UsageLog.WriteReadme();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);   // fails cleanly if locked open in Excel
            File.Move(tmp, path);
        }

        public void Save()
        {
            lock (gate)
            {
                if (dirty)
                {
                    try
                    {
                        List<DateTime> keys = new List<DateTime>(days.Keys);
                        keys.Sort();
                        StringBuilder sb = new StringBuilder("Date,Downloaded bytes,Uploaded bytes,Total bytes,Downloaded MB,Uploaded MB,Total MB\r\n");
                        foreach (DateTime k in keys)
                        {
                            long rx = days[k][0], tx = days[k][1];
                            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd},{1},{2},{3},{4:0.00},{5:0.00},{6:0.00}\r\n",
                                k, rx, tx, rx + tx, rx / 1048576.0, tx / 1048576.0, (rx + tx) / 1048576.0);
                        }
                        WriteCsvAtomic(FilePath, sb.ToString());
                        dirty = false;
                    }
                    catch { }
                }
                if (profileDirty)
                {
                    try
                    {
                        List<string> pkeys = new List<string>(profileDays.Keys);
                        pkeys.Sort(StringComparer.Ordinal);
                        StringBuilder sb = new StringBuilder("Date,Profile,Downloaded bytes,Uploaded bytes,Total MB\r\n");
                        foreach (string pk in pkeys)
                        {
                            int sep = pk.IndexOf('|');
                            if (sep <= 0) continue;
                            long[] v = profileDays[pk];
                            sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:0.00}\r\n",
                                pk.Substring(0, sep), pk.Substring(sep + 1), v[0], v[1], (v[0] + v[1]) / 1048576.0);
                        }
                        WriteCsvAtomic(ProfileFilePath, sb.ToString());
                        profileDirty = false;
                    }
                    catch { }
                }
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

        public void AddProfileUsage(string profile, long rx, long tx)
        {
            if ((rx <= 0 && tx <= 0) || string.IsNullOrEmpty(profile)) return;
            string clean = profile.Replace(',', ' ').Replace('|', ' ').Trim();
            if (clean.Length == 0) return;
            lock (gate)
            {
                string key = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + clean;
                long[] v;
                if (!profileDays.TryGetValue(key, out v)) { v = new long[2]; profileDays[key] = v; }
                v[0] += rx; v[1] += tx;
                profileDirty = true;
            }
        }

        public long[] ProfileRange(string profile, DateTime fromInclusive, DateTime toInclusive)
        {
            long[] r = new long[2];
            if (string.IsNullOrEmpty(profile)) return r;
            string clean = profile.Replace(',', ' ').Replace('|', ' ').Trim();
            lock (gate)
            {
                foreach (KeyValuePair<string, long[]> kv in profileDays)
                {
                    int sep = kv.Key.IndexOf('|');
                    if (sep <= 0) continue;
                    if (!string.Equals(kv.Key.Substring(sep + 1), clean, StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime d;
                    if (DateTime.TryParseExact(kv.Key.Substring(0, sep), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                        && d >= fromInclusive.Date && d <= toInclusive.Date)
                    {
                        r[0] += kv.Value[0]; r[1] += kv.Value[1];
                    }
                }
            }
            return r;
        }

        public double GetProfileCapGB(string profile)
        {
            if (string.IsNullOrEmpty(profile)) return 0;
            lock (gate) { double v; return profileCapsGB.TryGetValue(profile, out v) ? v : 0; }
        }

        public void SetProfileCapGB(string profile, double gb)
        {
            if (string.IsNullOrEmpty(profile)) return;
            lock (gate)
            {
                if (gb > 0) profileCapsGB[profile] = gb;
                else profileCapsGB.Remove(profile);
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    StringBuilder sb = new StringBuilder();
                    foreach (KeyValuePair<string, double> kv in profileCapsGB)
                        sb.Append(kv.Key).Append('=').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).AppendLine();
                    File.WriteAllText(ProfileCapsPath, sb.ToString());
                }
                catch { }
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
