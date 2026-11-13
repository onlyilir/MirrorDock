using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

namespace MirrorDock
{
    // Developer self-test: mirrors the first phone headless for a few seconds, records an MP4 and checks it.
    // Build + run: powershell -File tests\run-selftest.ps1
    static class SelfTest
    {
        [STAThread]
        static int Main(string[] args)
        {
            Adb.AdbPath = args.Length > 0 ? args[0] : "adb.exe";
            var devs = Adb.ListDevices().Result.Where(d => d.Ready).ToList();
            if (devs.Count == 0) { Console.WriteLine("FAIL no phone"); return 1; }
            Device dev = devs[0];
            var o = new Options { MaxSize = "1280", BitRate = "8", MaxFps = "60", Audio = true, StayAwake = false };
            var s = new MirrorSession { Serial = dev.Serial, Name = "SelfTest", Opts = o, Muted = true };
            s.Line += (ss, l) => Console.WriteLine("  log: " + l);
            bool ended = false;
            s.Ended += (ss, dropped) => { ended = true; Console.WriteLine("  ended, dropped=" + dropped + " error=" + ss.LastError); };
            s.Start(false).Wait();
            Console.WriteLine("H264=" + s.H264 + " audio=" + s.AudioOn);
            int frames = 0; Size size = Size.Empty;
            var t = new Thread(() => s.ReadVideo(b => { frames++; size = b.Size; return null; })) { IsBackground = true };
            t.Start();
            Thread.Sleep(2500);
            Console.WriteLine("frames decoded after 2.5 s: " + frames + " size " + size);
            int ok = frames > 0 ? 0 : 1;

            s.ToggleRecording();
            Thread.Sleep(6000);
            string mp4 = s.ToggleRecording();
            Console.WriteLine("recorded: " + mp4 + " " + (File.Exists(mp4) ? new FileInfo(mp4).Length + " bytes" : "MISSING"));
            if (!File.Exists(mp4)) ok = 1;
            else
            {
                string dur = ShellDuration(mp4);
                Console.WriteLine("Windows reads duration as: '" + dur + "'");
                if (string.IsNullOrEmpty(dur)) ok = 1;
            }
            s.Stop();
            for (int i = 0; i < 30 && !ended; i++) Thread.Sleep(100);
            Console.WriteLine(ok == 0 ? "PASS" : "FAIL");
            return ok;
        }

        static string ShellDuration(string path)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic folder = shell.NameSpace(Path.GetDirectoryName(path));
                dynamic item = folder.ParseName(Path.GetFileName(path));
                string length = folder.GetDetailsOf(item, 27);
                string frameW = folder.GetDetailsOf(item, 316);
                string frameH = folder.GetDetailsOf(item, 314);
                return length + (frameW != "" ? " (" + frameW + " x " + frameH + ")" : "");
            }
            catch (Exception e) { return "error: " + e.Message; }
        }
    }
}
