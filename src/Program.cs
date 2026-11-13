using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("MirrorDock")]
[assembly: System.Reflection.AssemblyProduct("MirrorDock")]
[assembly: System.Reflection.AssemblyDescription("MirrorDock - mirror and control your Android phone")]
[assembly: System.Reflection.AssemblyCompany("onlyilir")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 onlyilir. MIT License.")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace MirrorDock
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--make-icon") { MakeIcon(args[1]); return; }
            if (args.Contains("--uninstall")) { Application.EnableVisualStyles(); InstallInfo.Uninstall(args.Contains("--quiet")); return; }
            bool created;
            using (var mutex = new Mutex(true, "MirrorDock.SingleInstance", out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MirrorDock.Show"))
            {
                if (!created)
                {
                    if (!args.Contains("--restarted")) { showEvent.Set(); return; } // already running: bring it forward
                    // Restarting (e.g. after a theme change): wait for the old copy to exit.
                    bool got;
                    try { got = mutex.WaitOne(8000); } catch (AbandonedMutexException) { got = true; }
                    if (!got) return;
                }

                Theme.Apply(AppSettings.Load().Get("Theme", "system"));
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm(args.Contains("--tray"));
                int mi = Array.IndexOf(args, "--mirror");   // MirrorDock.exe --mirror [serial]: start mirroring right away
                if (mi >= 0) form.AutoMirror = mi + 1 < args.Length ? args[mi + 1] : "*";

                var waiter = new Thread(() =>
                {
                    while (showEvent.WaitOne())
                        try { form.BeginInvoke((Action)form.ShowMain); } catch { }
                });
                waiter.IsBackground = true;
                waiter.Start();
                Application.Run(form);
            }
        }

        // Build helper: writes the logo as a multi-size .ico (PNG entries) for the exe icon.
        static void MakeIcon(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            var pngs = sizes.Select(s =>
            {
                using (var bmp = Theme.Logo(s))
                using (var ms = new System.IO.MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }
            }).ToArray();
            using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }
}
