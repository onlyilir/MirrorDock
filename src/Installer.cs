using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MirrorDock
{
    // Shared install/uninstall helpers. The installer (MirrorDock-Setup.exe) and "MirrorDock.exe --uninstall" use these.
    static class InstallInfo
    {
        public const string AppName = "MirrorDock";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MirrorDock";
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MirrorDock"); }
        }
        public static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MirrorDock.lnk"); }
        }
        public static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MirrorDock.lnk"); }
        }

        public static void CreateShortcut(string link, string target, string description)
        {
            var sl = (IShellLinkW)new ShellLink();
            sl.SetPath(target);
            sl.SetWorkingDirectory(Path.GetDirectoryName(target));
            sl.SetDescription(description);
            sl.SetIconLocation(target, 0);
            ((IPersistFile)sl).Save(link, true);
            Marshal.ReleaseComObject(sl);
        }

        public static void Register(string exe, string version, long sizeKb)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "MirrorDock");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "onlyilir");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", Path.GetDirectoryName(exe));
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", Links.RepoUrl);
                k.SetValue("HelpLink", Links.RepoUrl + "/issues");
                k.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        // Stops MirrorDock and its adb so files can be replaced or removed.
        public static void StopRunning(string dir)
        {
            foreach (Process p in Process.GetProcessesByName("MirrorDock"))
            {
                try { if (p.Id != Process.GetCurrentProcess().Id && p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
            foreach (Process p in Process.GetProcessesByName("adb"))
            {
                try { if (p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
        }

        // ------------------------------------------------------------ uninstall (run from the installed MirrorDock.exe)
        public static void Uninstall(bool quiet)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            bool removeSettings = false;
            if (!quiet)
            {
                DialogResult r = MessageBox.Show(
                    "Remove MirrorDock from this PC?\r\n\r\nYour screenshots and recordings stay in Pictures and Videos.\r\n\r\n" +
                    "Also delete MirrorDock's settings and profiles? (Yes = delete them, No = keep them)",
                    "Uninstall MirrorDock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r == DialogResult.Cancel) return;
                removeSettings = r == DialogResult.Yes;
            }
            StopRunning(dir);
            foreach (string link in new[] { StartMenuLink, DesktopLink }) { try { if (File.Exists(link)) File.Delete(link); } catch { } }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    if (k != null && k.GetValue("MirrorDock") != null) k.DeleteValue("MirrorDock");
            }
            catch { }
            if (removeSettings) { try { Directory.Delete(AppSettings.Dir, true); } catch { } }
            // This exe is running from the folder, so delete the folder just after we exit.
            if (dir.TrimEnd('\\').EndsWith("MirrorDock", StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                Process.Start(psi);
            }
            if (!quiet) MessageBox.Show("MirrorDock was removed.", "Uninstall MirrorDock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath(); void GetIDList(); void SetIDList(); void GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
            void GetWorkingDirectory();
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
            void GetArguments(); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string s);
            void GetHotkey(); void SetHotkey(short k); void GetShowCmd(); void SetShowCmd(int c); void GetIconLocation();
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string s, int i);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string s, int r); void Resolve(IntPtr h, int f);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        interface IPersistFile
        {
            void GetClassID(out Guid g); [PreserveSig] int IsDirty(); void Load([MarshalAs(UnmanagedType.LPWStr)] string f, int m);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string f, bool remember);
        }
    }
}
