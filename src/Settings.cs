using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace NetSpeedBoss
{
    public enum SpeedUnit { Bytes, Bits }

    public class Settings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpeedBoss");
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        // ----- general -----
        public SpeedUnit Unit = SpeedUnit.Bytes;
        public int IntervalMs = 1000;
        public string Adapter = "auto";          // "auto" | "all" | adapter Id
        // ----- taskbar meter -----
        public bool ShowOnTaskbar = true;
        public int TaskbarOffset = 6;            // px between the meter and the notification area
        public float TaskbarFontSize = 9f;
        public string TaskbarFontName = "Segoe UI";
        public bool TaskbarBold = true;
        public bool TaskbarAutoColors = true;    // follow the Windows light/dark theme
        public Color TaskbarTextColor = Color.FromArgb(26, 26, 26);
        // ----- floating meter -----
        public bool ShowMeter = false;
        public bool AlwaysOnTop = true;
        public bool LockPosition = false;
        public bool HideOnFullscreen = true;
        public bool Horizontal = false;
        public bool ShowGraph = true;
        public bool ClickThrough = false;
        public int OpacityPct = 92;
        public float FontSize = 9f;
        public int X = int.MinValue, Y = int.MinValue;
        public Color DownColor = Color.FromArgb(70, 215, 120);
        public Color UpColor = Color.FromArgb(255, 165, 60);
        public Color BackColor = Color.FromArgb(22, 24, 29);
        public Color TextColor = Color.FromArgb(235, 238, 242);
        // ----- usage -----
        public double MonthlyLimitGB = 0;      // 0 = off
        public int MonthStartDay = 1;
        public string LimitWarnedPeriod = "";
        // ----- dashboard -----
        public int DashW = 900, DashH = 600;
        public bool WelcomeShown = false;

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    FieldInfo f = typeof(Settings).GetField(key, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    try { f.SetValue(s, Parse(f.FieldType, val)); } catch { }
                }
            }
            catch { }
            if (s.IntervalMs < 250) s.IntervalMs = 250;
            if (s.OpacityPct < 20) s.OpacityPct = 20;
            if (s.FontSize < 6 || s.FontSize > 30) s.FontSize = 9f;
            if (s.MonthStartDay < 1 || s.MonthStartDay > 28) s.MonthStartDay = 1;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    sb.Append(f.Name).Append('=').Append(Format(f.GetValue(this))).AppendLine();
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        static object Parse(Type t, string v)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (t == typeof(string)) return v;
            if (t == typeof(int)) return int.Parse(v, ci);
            if (t == typeof(double)) return double.Parse(v, ci);
            if (t == typeof(float)) return float.Parse(v, ci);
            if (t == typeof(bool)) return bool.Parse(v);
            if (t == typeof(Color)) return Color.FromArgb(int.Parse(v, NumberStyles.HexNumber, ci));
            if (t.IsEnum) return Enum.Parse(t, v, true);
            throw new NotSupportedException();
        }

        static string Format(object o)
        {
            if (o is Color) return ((Color)o).ToArgb().ToString("X8");
            if (o is IFormattable) return ((IFormattable)o).ToString(null, CultureInfo.InvariantCulture);
            return o == null ? "" : o.ToString();
        }
    }
}
