using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("MirrorDock Setup")]
[assembly: AssemblyProduct("MirrorDock")]
[assembly: AssemblyDescription("Installs MirrorDock for the current Windows user")]
[assembly: AssemblyCompany("onlyilir")]
[assembly: AssemblyCopyright("Copyright (c) 2026 onlyilir. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MirrorDock
{
    // MirrorDock-Setup.exe: installs for the current user into %LOCALAPPDATA%\Programs\MirrorDock. No admin rights needed.
    // The app files are embedded as resources named "payload/<relative path>".
    class SetupForm : Form
    {
        readonly CheckBox desktop, launch;
        readonly Button install;
        readonly Label status;
        readonly ProgressBar bar;
        bool done;

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("/S") || args.Contains("--silent")) { Install(true, null); return; }
            Application.Run(new SetupForm());
        }

        SetupForm()
        {
            Text = "MirrorDock " + MainForm.Version + " Setup";
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Ui;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            ClientSize = new Size(480, 330);
            Controls.Add(new PictureBox { Image = Theme.Logo(128), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(24, 24, 64, 64) });
            var t = Theme.MakeLabel("MirrorDock", Theme.Title, Theme.Text); t.Location = new Point(100, 26); Controls.Add(t);
            var v = Theme.MakeLabel("Version " + MainForm.Version + "  |  Free and open source (MIT)", Theme.Small, Theme.Sub); v.Location = new Point(102, 60); Controls.Add(v);
            var d = Theme.MakeLabel("See and control your Android phone from this PC.\r\nInstalls just for you. No admin rights needed.", Theme.Ui, Theme.Sub);
            d.Location = new Point(24, 108); Controls.Add(d);
            var where = Theme.MakeLabel("Installs to: " + InstallInfo.InstallDir, Theme.Small, Theme.Sub);
            where.AutoSize = false; where.AutoEllipsis = true; where.Bounds = new Rectangle(24, 156, 432, 18); Controls.Add(where);
            desktop = Theme.MakeCheck("Add a desktop shortcut"); desktop.Location = new Point(24, 184); Controls.Add(desktop);
            launch = Theme.MakeCheck("Open MirrorDock when done"); launch.Location = new Point(24, 212); Controls.Add(launch);
            desktop.BackColor = Theme.Bg; launch.BackColor = Theme.Bg;
            desktop.Checked = true; launch.Checked = true;
            bar = new ProgressBar { Bounds = new Rectangle(24, 248, 432, 8), Style = ProgressBarStyle.Continuous, Visible = false };
            Controls.Add(bar);
            status = Theme.MakeLabel("", Theme.Small, Theme.Sub); status.Location = new Point(24, 262); Controls.Add(status);
            install = Theme.MakeButton("Install", true); install.Bounds = new Rectangle(276, 276, 180, 38);
            install.Click += (s, e) => OnInstall();
            Controls.Add(install);
            var cancel = Theme.MakeButton("Cancel", false); cancel.Bounds = new Rectangle(168, 276, 100, 38);
            cancel.Click += (s, e) => Close();
            Controls.Add(cancel);
            AcceptButton = install;
            ResumeLayout(false);
            HandleCreated += (s, e) => Native.DarkTitleBar(Handle);
            if (File.Exists(Path.Combine(InstallInfo.InstallDir, "MirrorDock.exe"))) install.Text = "Update";
        }

        async void OnInstall()
        {
            if (done)
            {
                if (launch.Checked) { try { Process.Start(Path.Combine(InstallInfo.InstallDir, "MirrorDock.exe")); } catch { } }
                Close();
                return;
            }
            install.Enabled = false; desktop.Enabled = false;
            bar.Visible = true;
            bool wantDesktop = desktop.Checked;
            try
            {
                await Task.Run(() => Install(wantDesktop, p => BeginInvoke((Action)(() => { bar.Value = p; status.Text = "Installing... " + p + "%"; }))));
                done = true;
                status.Text = "MirrorDock is installed. Find it in the Start menu any time.";
                status.ForeColor = Theme.Green;
                install.Text = launch.Checked ? "Open MirrorDock" : "Finish";
                install.Enabled = true;
            }
            catch (Exception ex)
            {
                status.Text = "Install failed: " + ex.Message;
                status.ForeColor = Theme.Red;
                install.Enabled = true; desktop.Enabled = true;
            }
        }

        static void Install(bool desktopLink, Action<int> progress)
        {
            string dir = InstallInfo.InstallDir;
            InstallInfo.StopRunning(dir);
            Directory.CreateDirectory(dir);
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames().Where(n => n.StartsWith("payload/")).ToArray();
            long total = 0;
            for (int i = 0; i < names.Length; i++)
            {
                string rel = names[i].Substring("payload/".Length).Replace('/', '\\');
                string target = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream s = asm.GetManifestResourceStream(names[i]))
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try { using (var f = File.Create(target)) s.CopyTo(f); break; }
                        catch (IOException) { if (attempt > 10) throw; System.Threading.Thread.Sleep(300); s.Position = 0; }
                    }
                    total += s.Length;
                }
                if (progress != null) progress((i + 1) * 100 / names.Length);
            }
            string exe = Path.Combine(dir, "MirrorDock.exe");
            InstallInfo.CreateShortcut(InstallInfo.StartMenuLink, exe, "Mirror and control your Android phone");
            if (desktopLink) InstallInfo.CreateShortcut(InstallInfo.DesktopLink, exe, "Mirror and control your Android phone");
            else if (File.Exists(InstallInfo.DesktopLink)) File.Delete(InstallInfo.DesktopLink);
            InstallInfo.Register(exe, MainForm.Version, total / 1024);
        }
    }
}
