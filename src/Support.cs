using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MirrorDock
{
    // Project links. Fill in BuyMeACoffee with your username to show the coffee button.
    static class Links
    {
        public const string Repo = "onlyilir/MirrorDock";
        public const string BuyMeACoffee = "only.ilir";
        public static string RepoUrl { get { return "https://github.com/" + Repo; } }
        public static string IssuesUrl { get { return RepoUrl + "/issues/new"; } }
        public static string CoffeeUrl { get { return "https://buymeacoffee.com/" + BuyMeACoffee; } }
    }

    // Log file in %APPDATA%\MirrorDock, kept under ~1 MB.
    static class AppLog
    {
        static readonly object gate = new object();
        public static string FilePath { get { return Path.Combine(AppSettings.Dir, "mirrordock.log"); } }

        public static void Write(string line)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(AppSettings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1000000)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n");
                }
                catch { }
            }
        }

        public static string Tail(int lines)
        {
            lock (gate)
            {
                try { return File.Exists(FilePath) ? string.Join("\r\n", File.ReadAllLines(FilePath).Reverse().Take(lines).Reverse().ToArray()) : ""; }
                catch { return ""; }
            }
        }

        // A text block people can paste into a GitHub issue. Serial numbers are shortened for privacy.
        public static async Task<string> Diagnostics(IEnumerable<Device> devices)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MirrorDock " + MainForm.Version + " diagnostics");
            sb.AppendLine("Windows: " + WindowsVersion() + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit"));
            sb.AppendLine(".NET: " + Environment.Version);
            ProcResult v = await Adb.Run("version", 5000);
            sb.AppendLine("adb: " + (v.Out.Split('\n').FirstOrDefault() ?? "").Trim() + " (" + Adb.AdbPath + ")");
            foreach (Device d in devices)
            {
                string release = "", sdk = "", maker = "";
                if (d.Ready)
                {
                    release = (await Adb.Run("-s " + d.Serial + " shell getprop ro.build.version.release", 5000)).Out.Trim();
                    sdk = (await Adb.Run("-s " + d.Serial + " shell getprop ro.build.version.sdk", 5000)).Out.Trim();
                    maker = (await Adb.Run("-s " + d.Serial + " shell getprop ro.product.manufacturer", 5000)).Out.Trim();
                }
                string serial = d.IsWifi ? "wifi" : (d.Serial.Length > 4 ? "..." + d.Serial.Substring(d.Serial.Length - 4) : d.Serial);
                sb.AppendLine("Phone: " + maker + " " + d.Model + " | Android " + release + " (API " + sdk + ") | " + d.State + " | " + serial);
            }
            if (!devices.Any()) sb.AppendLine("Phone: none connected");
            sb.AppendLine("--- last log lines");
            string tail = Tail(80);
            tail = Regex.Replace(tail, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "x.x.x.x");   // hide local IPs
            foreach (Device d in devices) if (!d.IsWifi && d.Serial.Length > 4) tail = tail.Replace(d.Serial, "..." + d.Serial.Substring(d.Serial.Length - 4));
            sb.AppendLine(tail);
            return sb.ToString();
        }

        public static string WindowsVersion()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = (string)k.GetValue("ProductName", "Windows");
                    string build = (string)k.GetValue("CurrentBuild", "");
                    string disp = (string)k.GetValue("DisplayVersion", "");
                    int b;
                    if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return name + " " + disp + " (build " + build + ")";
                }
            }
            catch { return Environment.OSVersion.ToString(); }
        }
    }

    // Checks GitHub once a day for a newer release. Sends nothing but a plain request to the GitHub API.
    static class Updates
    {
        public class Info { public string Version, Url; }

        public static Task<Info> Check()
        {
            return Task.Run(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Links.Repo + "/releases/latest");
                    req.UserAgent = "MirrorDock-update-check";
                    req.Timeout = 8000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var r = new StreamReader(resp.GetResponseStream()))
                    {
                        string json = r.ReadToEnd();
                        Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                        Match url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+/releases/tag/[^\"]+)\"");
                        if (!tag.Success) return null;
                        if (!Newer(tag.Groups[1].Value, MainForm.Version)) return null;
                        return new Info { Version = tag.Groups[1].Value, Url = url.Success ? url.Groups[1].Value : Links.RepoUrl + "/releases/latest" };
                    }
                }
                catch { return null; }
            });
        }

        public static bool Newer(string a, string b)
        {
            Version va, vb;
            return System.Version.TryParse(Clean(a), out va) && System.Version.TryParse(Clean(b), out vb) && va > vb;
        }

        static string Clean(string s)
        {
            Match m = Regex.Match(s, @"\d+(\.\d+){1,3}");
            return m.Success ? m.Value : "0.0";
        }
    }
}
