using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace NetSpeedBoss
{
    public class UpdateInfo
    {
        public string Version = "";
        public string CurrentVersion = "";
        public string SetupUrl = "";
        public string PortableUrl = "";
        public string ReleaseUrl = "";
        public string Error = "";
        public bool UpdateAvailable;
    }

    public static class Updater
    {
        const string LatestApi = "https://api.github.com/repos/merchanthasan2/NetSpeedBoss/releases/latest";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetCurrentPackageFullName(ref int length, StringBuilder fullName);

        public static bool IsStoreApp()
        {
            try
            {
                int len = 0;
                int rc = GetCurrentPackageFullName(ref len, null);
                if (rc == 0 || rc == 122) return true;   // ERROR_SUCCESS or ERROR_INSUFFICIENT_BUFFER
            }
            catch { }
            return Application.ExecutablePath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsInstalledBuild()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                return File.Exists(Path.Combine(dir, "unins000.exe"));
            }
            catch { return false; }
        }

        public static string CurrentVersionString()
        {
            Assembly asm = typeof(Updater).Assembly;
            object[] attrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            string raw = attrs.Length > 0 ? ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion : asm.GetName().Version.ToString();
            Version v = ParseVersion(raw);
            if (v == null) return raw;
            return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
        }

        public static Version ParseVersion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            Match m = Regex.Match(raw.Trim(), @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            int maj = int.Parse(m.Groups[1].Value);
            int min = int.Parse(m.Groups[2].Value);
            int bld = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return new Version(maj, min, bld);
        }

        public static UpdateInfo CheckLatest()
        {
            UpdateInfo info = new UpdateInfo();
            info.CurrentVersion = CurrentVersionString();
            string json, err;
            if (!NetworkInfo.HttpGet(LatestApi, out json, out err))
            {
                info.Error = err;
                return info;
            }

            string tag = NetworkInfo.JsonStr(json, "tag_name");
            Version latest = ParseVersion(tag);
            Version current = ParseVersion(info.CurrentVersion);
            if (latest == null || current == null)
            {
                info.Error = "Could not read release version";
                return info;
            }

            info.Version = latest.Major + "." + latest.Minor + "." + Math.Max(0, latest.Build);
            info.ReleaseUrl = NetworkInfo.JsonStr(json, "html_url");
            if (info.ReleaseUrl.Length == 0)
                info.ReleaseUrl = "https://github.com/merchanthasan2/NetSpeedBoss/releases/tag/v" + info.Version;

            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string u = m.Groups[1].Value;
                if (u.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (info.SetupUrl.Length == 0 || u.IndexOf(info.Version, StringComparison.OrdinalIgnoreCase) >= 0)
                        info.SetupUrl = u;
                }
                else if (u.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) info.PortableUrl = u;
            }

            info.UpdateAvailable = latest > current && (info.SetupUrl.Length > 0 || info.PortableUrl.Length > 0);
            return info;
        }

        public static void DownloadAndInstall(UpdateInfo info, Action<int> onProgress, Action exitApp)
        {
            if (info == null || !info.UpdateAvailable) throw new InvalidOperationException("No update available.");
            bool useInstaller = IsInstalledBuild() && info.SetupUrl.Length > 0;
            string url = useInstaller ? info.SetupUrl : (info.PortableUrl.Length > 0 ? info.PortableUrl : info.SetupUrl);
            if (!useInstaller && info.PortableUrl.Length == 0) useInstaller = true;

            string work = Path.Combine(Path.GetTempPath(), "NetSpeedBoss-update");
            Directory.CreateDirectory(work);
            string file = Path.Combine(work, useInstaller ? "NetSpeedBoss-Setup.exe" : "NetSpeedBoss-portable.zip");
            if (File.Exists(file)) File.Delete(file);

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            req.UserAgent = "NetSpeedBoss";

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (FileStream dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = resp.ContentLength;
                byte[] buf = new byte[32768];
                long got = 0;
                int lastPct = -1, n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    if (total > 0 && onProgress != null)
                    {
                        int pct = (int)Math.Min(100, got * 100 / total);
                        if (pct != lastPct) { lastPct = pct; onProgress(pct); }
                    }
                }
            }

            if (new FileInfo(file).Length < 50000) throw new InvalidDataException("Downloaded update file is incomplete.");

            string ps1 = Path.Combine(work, "apply-update.ps1");
            int pid = Process.GetCurrentProcess().Id;
            string exe = Application.ExecutablePath;
            string script = useInstaller
                ? BuildInstallerScript(pid, file, exe, work)
                : BuildPortableScript(pid, file, exe, work);
            File.WriteAllText(ps1, script, Encoding.UTF8);

            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps1 + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);

            if (exitApp != null) exitApp();
        }

        static string PsEsc(string s) { return s.Replace("'", "''"); }

        static string BuildInstallerScript(int pid, string setupPath, string exePath, string workDir)
        {
            return
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                "Wait-Process -Id " + pid + " -Timeout 15\r\n" +
                "Start-Sleep -Milliseconds 300\r\n" +
                "$p = Start-Process -FilePath '" + PsEsc(setupPath) + "' -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' -Wait -PassThru\r\n" +
                "if (Test-Path '" + PsEsc(exePath) + "') { Start-Process -FilePath '" + PsEsc(exePath) + "' -ArgumentList '--dashboard' }\r\n" +
                "Remove-Item -Path '" + PsEsc(workDir) + "' -Recurse -Force\r\n";
        }

        static string BuildPortableScript(int pid, string zipPath, string exePath, string workDir)
        {
            string extDir = Path.Combine(workDir, "extracted");
            return
                "$ErrorActionPreference = 'Stop'\r\n" +
                "Wait-Process -Id " + pid + " -Timeout 15 -ErrorAction SilentlyContinue\r\n" +
                "Start-Sleep -Milliseconds 350\r\n" +
                "Add-Type -AssemblyName System.IO.Compression.FileSystem\r\n" +
                "if (Test-Path '" + PsEsc(extDir) + "') { Remove-Item -Path '" + PsEsc(extDir) + "' -Recurse -Force }\r\n" +
                "[System.IO.Compression.ZipFile]::ExtractToDirectory('" + PsEsc(zipPath) + "', '" + PsEsc(extDir) + "')\r\n" +
                "$newExe = Join-Path '" + PsEsc(extDir) + "' 'NetSpeedBoss.exe'\r\n" +
                "for ($i = 0; $i -lt 20; $i++) {\r\n" +
                "  try { Copy-Item -Path $newExe -Destination '" + PsEsc(exePath) + "' -Force; break }\r\n" +
                "  catch { Start-Sleep -Milliseconds 250 }\r\n" +
                "}\r\n" +
                "Start-Process -FilePath '" + PsEsc(exePath) + "' -ArgumentList '--dashboard'\r\n" +
                "Remove-Item -Path '" + PsEsc(workDir) + "' -Recurse -Force -ErrorAction SilentlyContinue\r\n";
        }
    }
}
