using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MirrorDock
{
    class ProcResult
    {
        public int Code;
        public string Out = "";
        public string Err = "";
        public string All { get { return (Out + "\n" + Err).Trim(); } }
    }

    class Device
    {
        public string Serial;
        public string State;   // device | unauthorized | offline
        public string Model;
        public bool IsWifi { get { return Serial.Contains(":") || Serial.Contains("._adb-tls-connect"); } }
        public bool Ready { get { return State == "device"; } }
    }

    // Everything that talks to adb.exe. Always runs hidden (no console window).
    static class Adb
    {
        public static string AdbPath = "adb.exe";

        public static ProcessStartInfo Hidden(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try { psi.WorkingDirectory = Path.GetDirectoryName(exe); } catch { }
            return psi;
        }

        public static Task<ProcResult> Run(string args, int timeoutMs = 15000)
        {
            return Task.Run(() =>
            {
                var r = new ProcResult();
                try
                {
                    using (var p = Process.Start(Hidden(AdbPath, args)))
                    {
                        var o = p.StandardOutput.ReadToEndAsync();
                        var e = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } r.Code = -1; r.Err = "Timed out"; return r; }
                        p.WaitForExit();
                        r.Code = p.ExitCode; r.Out = o.Result; r.Err = e.Result;
                    }
                }
                catch (Exception ex) { r.Code = -2; r.Err = ex.Message; }
                return r;
            });
        }

        public static async Task<List<Device>> ListDevices()
        {
            var list = new List<Device>();
            ProcResult r = await Run("devices -l", 8000);
            foreach (string raw in r.Out.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith("*")) continue;
                string[] parts = Regex.Split(line, @"\s+");
                if (parts.Length < 2) continue;
                var d = new Device { Serial = parts[0], State = parts[1], Model = "" };
                foreach (string p in parts)
                    if (p.StartsWith("model:")) d.Model = p.Substring(6).Replace('_', ' ');
                list.Add(d);
            }
            return list;
        }

        public static async Task<string> WifiIp(string serial)
        {
            ProcResult r = await Run("-s " + serial + " shell ip -f inet addr show wlan0");
            Match m = Regex.Match(r.Out, @"inet (\d+\.\d+\.\d+\.\d+)");
            if (m.Success) return m.Groups[1].Value;
            r = await Run("-s " + serial + " shell ip route");
            m = Regex.Match(r.Out, @"wlan\S*.*?src (\d+\.\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Switches a USB phone to Wi-Fi. Returns "ip:port" on success, or throws with a plain message.
        public static async Task<string> MakeWireless(string serial)
        {
            string ip = await WifiIp(serial);
            if (ip == null) throw new Exception("The phone isn't on Wi-Fi. Connect it to the same Wi-Fi as this PC.");
            ProcResult r = await Run("-s " + serial + " tcpip 5555");
            if (r.Code != 0) throw new Exception("The phone refused Wi-Fi mode: " + r.All);
            await Task.Delay(2000);
            string addr = ip + ":5555";
            for (int i = 0; i < 3; i++)
            {
                r = await Run("connect " + addr, 10000);
                if (r.Out.Contains("connected to")) return addr;
                await Task.Delay(1500);
            }
            throw new Exception("Couldn't reach " + addr + ". Check the PC and phone are on the same Wi-Fi.");
        }

        public static async Task<string> Connect(string addr)
        {
            if (!addr.Contains(":")) addr += ":5555";
            ProcResult r = await Run("connect " + addr, 12000);
            if (r.Out.Contains("connected to")) return addr;
            string msg = r.All;
            if (msg.Contains("10061") || msg.Contains("refused"))
                throw new Exception("Phone refused the connection. On Android 11+, use Pair first, or plug in USB once and click Make wireless.");
            if (msg.Contains("10060") || msg.Contains("timed out") || msg.Contains("Timed out"))
                throw new Exception("No answer from " + addr + ". Is the phone on the same Wi-Fi and is the IP right?");
            throw new Exception(msg.Length > 0 ? msg : "Couldn't connect to " + addr);
        }

        public static async Task<string> Pair(string addr, string code)
        {
            ProcResult r = await Run("pair " + addr + " " + code, 20000);
            if (r.Out.Contains("Successfully paired")) return r.Out.Trim();
            throw new Exception("Pairing failed. Check the IP:port and 6-digit code from Wireless debugging > Pair device with pairing code. " + r.All);
        }

        public static Task<ProcResult> Disconnect(string serial) { return Run("disconnect " + serial); }

        // Switches the phone's adb back to USB-only. This closes the network port that "Make wireless" opened
        // (and stops Android 11+ wireless debugging). Returns true when the port is confirmed closed.
        public static async Task<bool> TurnOffWifi(string serial)
        {
            string host = serial.Contains(":") ? serial.Substring(0, serial.LastIndexOf(':')) : null;
            int port = 5555;
            if (host != null) int.TryParse(serial.Substring(serial.LastIndexOf(':') + 1), out port);
            await Run("-s " + serial + " usb", 8000);
            if (host != null) await Run("disconnect " + serial, 4000);
            if (host == null) return true;
            await Task.Delay(1500);
            return !await Task.Run(() => PortOpen(host, port));
        }

        static bool PortOpen(string host, int port)
        {
            try
            {
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(2000) && c.Connected;
                    try { c.EndConnect(ar); } catch { }
                    return ok;
                }
            }
            catch { return false; }
        }

        public static Task<string> Screenshot(string serial, string file)
        {
            return Task.Run(() =>
            {
                using (var p = Process.Start(Hidden(AdbPath, "-s " + serial + " exec-out screencap -p")))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    using (var fs = File.Create(file)) p.StandardOutput.BaseStream.CopyTo(fs);
                    p.WaitForExit(15000);
                    if (new FileInfo(file).Length < 100) { File.Delete(file); throw new Exception("Screenshot failed: " + err.Result); }
                }
                return file;
            });
        }

        public static async Task<string> InstallApk(string serial, string apk)
        {
            ProcResult r = await Run("-s " + serial + " install -r \"" + apk + "\"", 180000);
            if (r.Out.Contains("Success")) return "Installed " + Path.GetFileName(apk);
            throw new Exception("Install failed: " + r.All);
        }
    }
}
