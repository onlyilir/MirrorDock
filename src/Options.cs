using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MirrorDock
{
    // One set of mirroring choices. Saved as a profile (key=value text file).
    class Options
    {
        // Video
        public string MaxSize = "1280";     // long side in pixels, "0" = phone's full resolution
        public string Quality = "75";       // JPEG quality 1-100 (compatibility mode only)
        public string MaxFps = "60";
        public string BitRate = "8";        // H.264 Mbps
        public string Codec = "h264";       // h264 | jpeg (compatibility mode)
        public bool Audio = true;
        public bool Gamepad = true;         // forward PC game controllers to the phone
        public bool Notifications = true;   // show phone notifications on the PC
        // Window & session
        public bool OnTop = false;
        public bool Fullscreen = false;
        public bool Borderless = false;
        public bool Toolbar = true;
        public bool AutoReconnect = true;
        // Phone
        public bool ScreenOff = false;
        public bool StayAwake = true;
        public bool ShowTouches = false;
        public bool PowerOffOnClose = false;
        public bool ViewOnly = false;

        public Options Clone() { return (Options)MemberwiseClone(); }

        public string ToText()
        {
            var sb = new StringBuilder();
            foreach (FieldInfo f in typeof(Options).GetFields())
                sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(this))).Append("\r\n");
            return sb.ToString();
        }

        public static Options FromText(string text)
        {
            var o = new Options();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                FieldInfo f = typeof(Options).GetField(line.Substring(0, eq));
                if (f == null) continue;
                string v = line.Substring(eq + 1);
                try
                {
                    if (f.FieldType == typeof(bool)) f.SetValue(o, v.Equals("true", StringComparison.OrdinalIgnoreCase));
                    else f.SetValue(o, v);
                }
                catch { }
            }
            return o;
        }

        // Built-in profiles. The user can save their own next to these.
        public static Dictionary<string, Options> BuiltIns()
        {
            var d = new Dictionary<string, Options>();
            d["Balanced"] = new Options();

            var g = new Options();
            g.MaxSize = "1600"; g.BitRate = "16"; g.MaxFps = "60";
            d["Gaming"] = g;

            var m = new Options();
            m.MaxSize = "1920"; m.BitRate = "12"; m.MaxFps = "60"; m.ScreenOff = true; m.Toolbar = false;
            d["Movie"] = m;

            var s = new Options();
            s.MaxSize = "0"; s.BitRate = "16"; s.MaxFps = "30";
            d["Sharp (reading)"] = s;

            var w = new Options();
            w.MaxSize = "1024"; w.BitRate = "3"; w.MaxFps = "30";
            d["Weak Wi-Fi"] = w;

            var p = new Options();
            p.ViewOnly = true; p.StayAwake = false; p.Toolbar = false; p.Audio = false;
            d["Present (view only)"] = p;
            return d;
        }
    }

    // App-wide settings (scrcpy folder, recent IPs, phone nicknames...).
    class AppSettings
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MirrorDock");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");
        public static readonly string ProfileDir = Path.Combine(Dir, "profiles");

        readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string raw in File.ReadAllLines(FilePath))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq > 0) s.values[raw.Substring(0, eq)] = raw.Substring(eq + 1);
                    }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, values.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
            catch { }
        }

        public string Get(string key, string def)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : def;
        }
        public void Set(string key, string value)
        {
            if (value == null) values.Remove(key); else values[key] = value.Replace("\r", "").Replace("\n", " ");
            Save();
        }

        public string AdbDir { get { return Get("AdbDir", ""); } set { Set("AdbDir", value); } }
        public string LastProfile { get { return Get("LastProfile", "Balanced"); } set { Set("LastProfile", value); } }

        public List<string> RecentIps
        {
            get { return Get("RecentIps", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(); }
        }
        public void AddRecentIp(string ip)
        {
            var list = RecentIps;
            list.RemoveAll(x => x.Equals(ip, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, ip);
            Set("RecentIps", string.Join(",", list.Take(8).ToArray()));
        }

        public string PhoneProfile(string serial) { return Get("profile." + serial, ""); }
        public void SetPhoneProfile(string serial, string profile) { Set("profile." + serial, string.IsNullOrEmpty(profile) ? null : profile); }

        public System.Drawing.Rectangle Window(string serial)
        {
            string[] p = Get("win." + serial, "").Split(',');
            int x, y, w, h;
            if (p.Length == 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) && int.TryParse(p[3], out h))
                return new System.Drawing.Rectangle(x, y, w, h);
            return System.Drawing.Rectangle.Empty;
        }
        public void SetWindow(string serial, System.Drawing.Rectangle r) { Set("win." + serial, r.X + "," + r.Y + "," + r.Width + "," + r.Height); }

        public string Nickname(string serial) { return Get("nick." + serial, ""); }
        public void SetNickname(string serial, string name)
        {
            Set("nick." + serial, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        }

        // ---- profiles ----
        public Options CurrentOptions()
        {
            try
            {
                string p = Path.Combine(Dir, "current.ini");
                if (File.Exists(p)) return Options.FromText(File.ReadAllText(p));
            }
            catch { }
            return null;
        }
        public void SaveCurrent(Options o)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, "current.ini"), o.ToText()); } catch { }
        }

        public static Dictionary<string, Options> UserProfiles()
        {
            var d = new Dictionary<string, Options>();
            try
            {
                if (Directory.Exists(ProfileDir))
                    foreach (string f in Directory.GetFiles(ProfileDir, "*.ini"))
                        d[Path.GetFileNameWithoutExtension(f)] = Options.FromText(File.ReadAllText(f));
            }
            catch { }
            return d;
        }
        public static void SaveProfile(string name, Options o)
        {
            Directory.CreateDirectory(ProfileDir);
            File.WriteAllText(Path.Combine(ProfileDir, SafeName(name) + ".ini"), o.ToText());
        }
        public static void DeleteProfile(string name)
        {
            string p = Path.Combine(ProfileDir, SafeName(name) + ".ini");
            if (File.Exists(p)) File.Delete(p);
        }
        public static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
