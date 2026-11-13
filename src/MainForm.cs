using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MirrorDock
{
    class MainForm : Form
    {
        public const string Version = "1.0.2";

        readonly AppSettings settings = AppSettings.Load();
        readonly Dictionary<string, MirrorSession> sessions = new Dictionary<string, MirrorSession>();
        readonly HashSet<string> reconnecting = new HashSet<string>();
        readonly HashSet<string> forceJpeg = new HashSet<string>();
        List<Device> devices = new List<Device>();
        bool refreshing, loadingUi, quitting, firstRefreshDone;
        readonly bool startHidden;
        string clipboardFromPhone;
        public string AutoMirror;

        // Controls
        ListBox deviceList;
        TextBox ipBox;
        ComboBox profileBox, cSize, cBitrate, cFps;
        CheckBox kAudio, kCompat, kOnTop, kFull, kBorderless, kToolbar, kReconnect, kUpdates;
        CheckBox kScreenOff, kAwake, kTouches, kPowerOff, kViewOnly, kStartup;
        Button startBtn, logBtn;
        Label statusLabel, emptyHint;
        LinkLabel updateLink;
        TextBox logBox;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        readonly Timer refreshTimer = new Timer();
        readonly ToolTip tips = new ToolTip();
        int baseHeight;

        static readonly Choice[] Sizes = {
            new Choice("Phone's full resolution", "0"), new Choice("1920 - sharpest", "1920"),
            new Choice("1600", "1600"), new Choice("1280 - balanced", "1280"),
            new Choice("1024 - smooth", "1024"), new Choice("800 - fastest", "800") };
        static readonly Choice[] Bitrates = {
            new Choice("Low (weak Wi-Fi)", "3"), new Choice("Normal", "8"), new Choice("High", "12"),
            new Choice("Very high", "16"), new Choice("Maximum", "24") };
        static readonly Choice[] Fpss = {
            new Choice("30 fps", "30"), new Choice("60 fps", "60"), new Choice("90 fps", "90"), new Choice("120 fps", "120") };

        public MainForm(bool hidden)
        {
            startHidden = hidden;
            Text = "MirrorDock";
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            BuildUi();
            ResumeLayout(false);
            baseHeight = ClientSize.Height;
            BuildTray();
            LocateAdb();
            LoadOptions(settings.CurrentOptions() ?? FindProfile(settings.LastProfile) ?? new Options());
            AppLog.Write("MirrorDock " + Version + " started on " + AppLog.WindowsVersion());

            refreshTimer.Interval = 3000;
            refreshTimer.Tick += (s, e) => RefreshDevices();
            Shown += (s, e) => { RefreshDevices(); refreshTimer.Start(); CheckForUpdates(); };
            HandleCreated += (s, e) => { Native.DarkTitleBar(Handle); Native.AddClipboardFormatListener(Handle); };
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; RefreshDevices(); refreshTimer.Start(); CheckForUpdates(); }
            base.SetVisibleCore(value);
        }

        // ------------------------------------------------------------ layout
        void BuildUi()
        {
            ClientSize = new Size(900, 640);

            var logo = new PictureBox { Image = Theme.Logo(64), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(18, 14, 34, 34) };
            Controls.Add(logo);
            var title = Theme.MakeLabel("MirrorDock", Theme.Title, Theme.Text); title.Location = new Point(58, 10); Controls.Add(title);
            var sub = Theme.MakeLabel("Your phone on your PC  |  v" + Version, Theme.Small, Theme.Sub); sub.Location = new Point(61, 37); Controls.Add(sub);

            updateLink = new LinkLabel { AutoSize = true, Location = new Point(250, 22), Font = Theme.UiBold, Visible = false,
                LinkColor = Theme.Green, ActiveLinkColor = Theme.Green, BackColor = Color.Transparent };
            updateLink.LinkBehavior = LinkBehavior.HoverUnderline;
            Controls.Add(updateLink);

            int hx = 884;
            if (Links.BuyMeACoffee.Length > 0)
            {
                var coffee = Theme.MakeButton("☕  Buy me a coffee", false);
                coffee.BackColor = Color.FromArgb(255, 221, 0); coffee.ForeColor = Color.FromArgb(20, 20, 20);
                coffee.FlatAppearance.BorderSize = 0; coffee.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 230, 70);
                coffee.Font = Theme.UiBold;
                coffee.Bounds = new Rectangle(hx - 160, 16, 160, 32);
                tips.SetToolTip(coffee, "MirrorDock is free. If it helps you, a coffee keeps it going!");
                coffee.Click += (s, e) => OpenUrl(Links.CoffeeUrl);
                Controls.Add(coffee);
                hx -= 168;
            }
            var help = Theme.MakeButton("Help", false); help.Bounds = new Rectangle(hx - 80, 16, 80, 32);
            help.Click += (s, e) => HelpMenu().Show(help, new Point(0, help.Height));
            Controls.Add(help);

            // ---- phones column
            var phones = new CardPanel("Phones") { Bounds = new Rectangle(16, 64, 290, 486) };
            Controls.Add(phones);
            deviceList = new ListBox();
            deviceList.BorderStyle = BorderStyle.None;
            deviceList.BackColor = Theme.Card;
            deviceList.ForeColor = Theme.Text;
            deviceList.DrawMode = DrawMode.OwnerDrawFixed;
            deviceList.ItemHeight = 52;
            deviceList.IntegralHeight = false;
            deviceList.Bounds = new Rectangle(8, 40, 274, 250);
            deviceList.DrawItem += DrawDevice;
            deviceList.SelectedIndexChanged += (s, e) => UpdateStartButton();
            deviceList.DoubleClick += (s, e) => RenameDevice();
            deviceList.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = deviceList.IndexFromPoint(e.Location);
                if (i >= 0) { deviceList.SelectedIndex = i; DeviceMenu().Show(deviceList, e.Location); }
            };
            phones.Controls.Add(deviceList);
            emptyHint = Theme.MakeLabel("No phones yet.\r\n\r\nClick Help > Setup guide for a\r\nstep-by-step walkthrough.", Theme.Ui, Theme.Sub);
            emptyHint.BackColor = Theme.Card;
            emptyHint.Location = new Point(20, 56);
            phones.Controls.Add(emptyHint);
            emptyHint.BringToFront();

            var refresh = Theme.MakeButton("Refresh", false); refresh.Bounds = new Rectangle(12, 300, 86, 32);
            refresh.Click += (s, e) => RefreshDevices();
            var wifi = Theme.MakeButton("Make wireless", false); wifi.Bounds = new Rectangle(104, 300, 174, 32);
            wifi.Click += (s, e) => MakeWireless();
            tips.SetToolTip(wifi, "Switch the selected USB phone to Wi-Fi. You can unplug the cable afterwards.");
            phones.Controls.Add(refresh); phones.Controls.Add(wifi);

            var ipLbl = Theme.MakeLabel("Connect over Wi-Fi (IP address)", Theme.Small, Theme.Sub); ipLbl.BackColor = Theme.Card;
            ipLbl.Location = new Point(12, 342); phones.Controls.Add(ipLbl);
            ipBox = new TextBox();
            ipBox.BackColor = Theme.Field; ipBox.ForeColor = Theme.Text; ipBox.BorderStyle = BorderStyle.FixedSingle;
            ipBox.Font = Theme.Big; ipBox.Bounds = new Rectangle(12, 362, 176, 28);
            ipBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            ipBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
            ipBox.AutoCompleteCustomSource.AddRange(settings.RecentIps.ToArray());
            if (settings.RecentIps.Count > 0) ipBox.Text = settings.RecentIps[0];
            tips.SetToolTip(ipBox, "Phone's IP, e.g. 192.168.1.20 (recent ones auto-complete)");
            ipBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ConnectIp(); } };
            phones.Controls.Add(ipBox);
            var connect = Theme.MakeButton("Connect", false); connect.Bounds = new Rectangle(194, 361, 84, 30);
            connect.Click += (s, e) => ConnectIp();
            phones.Controls.Add(connect);

            var pair = Theme.MakeButton("Pair with code...", false); pair.Bounds = new Rectangle(12, 400, 130, 32);
            pair.Click += (s, e) => PairDevice();
            tips.SetToolTip(pair, "Android 11+: Settings > Developer options > Wireless debugging > Pair device with pairing code");
            var disc = Theme.MakeButton("Turn off Wi-Fi", false); disc.Bounds = new Rectangle(148, 400, 130, 32);
            disc.Click += (s, e) => DisconnectSelected();
            tips.SetToolTip(disc, "Stops Wi-Fi debugging on the selected phone and closes its network port. Plug in USB to use Wi-Fi again.");
            phones.Controls.Add(pair); phones.Controls.Add(disc);
            var hint = Theme.MakeLabel("Double-click to rename. Right-click for more.", Theme.Small, Theme.Sub);
            hint.BackColor = Theme.Card; hint.Location = new Point(12, 448); phones.Controls.Add(hint);

            // ---- profile bar
            var pl = Theme.MakeLabel("Profile", Theme.Header, Theme.Text); pl.Location = new Point(322, 72); Controls.Add(pl);
            profileBox = Theme.MakeCombo(false); profileBox.Bounds = new Rectangle(386, 70, 220, 28);
            profileBox.SelectedIndexChanged += (s, e) =>
            {
                if (loadingUi || profileBox.SelectedItem == null) return;
                string name = profileBox.SelectedItem.ToString();
                Options o = FindProfile(name);
                if (o != null) { LoadOptions(o); settings.LastProfile = name; Status("Loaded profile \"" + name + "\".", false); }
            };
            Controls.Add(profileBox);
            var saveP = Theme.MakeButton("Save as...", false); saveP.Bounds = new Rectangle(614, 68, 100, 32);
            saveP.Click += (s, e) => SaveProfile();
            var delP = Theme.MakeButton("Delete", false); delP.Bounds = new Rectangle(720, 68, 80, 32);
            delP.Click += (s, e) => DeleteProfile();
            Controls.Add(saveP); Controls.Add(delP);
            FillProfiles(settings.LastProfile);

            // ---- picture & sound card
            var video = new CardPanel("Picture & sound") { Bounds = new Rectangle(322, 112, 276, 214) };
            Controls.Add(video);
            cSize = Row(video, "Resolution", Sizes, 42);
            cBitrate = Row(video, "Quality", Bitrates, 76);
            cFps = Row(video, "Frame rate", Fpss, 110);
            kAudio = Check(video, "Play phone sound on PC (Android 11+)", 150);
            kCompat = Check(video, "Compatibility mode (older PCs)", 178);
            tips.SetToolTip(kCompat, "Sends pictures instead of video. Use it if the mirror stays black. No recording in this mode.");

            // ---- controls help card
            var helpCard = new CardPanel("Mouse & keyboard") { Bounds = new Rectangle(608, 112, 276, 214) };
            Controls.Add(helpCard);
            string[,] rows = {
                { "Click / drag", "Tap / swipe" }, { "Ctrl + drag", "Pinch to zoom" }, { "Right-click, Esc", "Back" },
                { "Middle-click", "Home" }, { "Wheel (+Shift)", "Scroll (sideways)" }, { "Type, Ctrl+V", "Type / paste" },
                { "Drop a file", "Install APK / send file" }, { "F11 / F12", "Fullscreen / Record" } };
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                var a = Theme.MakeLabel(rows[i, 0], Theme.Small, Theme.Text); a.BackColor = Theme.Card; a.Location = new Point(14, 40 + i * 21); helpCard.Controls.Add(a);
                var b = Theme.MakeLabel(rows[i, 1], Theme.Small, Theme.Sub); b.BackColor = Theme.Card; b.Location = new Point(120, 40 + i * 21); helpCard.Controls.Add(b);
            }

            // ---- window card
            var win = new CardPanel("Window & app") { Bounds = new Rectangle(322, 336, 276, 214) };
            Controls.Add(win);
            kToolbar = Check(win, "Show button bar beside mirror", 40);
            kOnTop = Check(win, "Always on top", 68);
            kFull = Check(win, "Start fullscreen", 96);
            kBorderless = Check(win, "No window border", 124);
            kReconnect = Check(win, "Reconnect if the phone drops", 152);
            kUpdates = Check(win, "Check GitHub for updates", 180);
            kUpdates.Checked = settings.Get("UpdateCheck", "1") == "1";
            kUpdates.CheckedChanged += (s, e) => settings.Set("UpdateCheck", kUpdates.Checked ? "1" : "0");

            // ---- phone card
            var ph = new CardPanel("Phone") { Bounds = new Rectangle(608, 336, 276, 214) };
            Controls.Add(ph);
            kScreenOff = Check(ph, "Turn phone screen off (saves battery)", 40);
            kAwake = Check(ph, "Keep phone awake while mirroring", 68);
            kTouches = Check(ph, "Show finger touches", 96);
            kPowerOff = Check(ph, "Lock phone when I close the mirror", 124);
            kViewOnly = Check(ph, "View only (PC can't control phone)", 152);

            foreach (var c in new[] { cSize, cBitrate, cFps })
                c.SelectedIndexChanged += (s, e) => OptionsChanged();
            foreach (var k in new[] { kAudio, kCompat, kOnTop, kFull, kBorderless, kToolbar, kReconnect, kScreenOff, kAwake, kTouches, kPowerOff, kViewOnly })
                k.CheckedChanged += (s, e) => OptionsChanged();

            // ---- action bar
            startBtn = Theme.MakeButton("Start mirroring", true);
            startBtn.Bounds = new Rectangle(16, 562, 290, 46);
            startBtn.Click += (s, e) => ToggleSelected();
            Controls.Add(startBtn);

            var shot = Theme.MakeButton("Screenshot", false); shot.Bounds = new Rectangle(322, 568, 106, 34);
            shot.Click += (s, e) => { Device d = Selected(); if (d != null) TakeScreenshot(d.Serial, NameOf(d)); else Status("Pick a phone first.", true); };
            var recs = Theme.MakeButton("Recordings", false); recs.Bounds = new Rectangle(434, 568, 106, 34);
            recs.Click += (s, e) => OpenFolder(MirrorSession.VideosDir);
            var shots = Theme.MakeButton("Screenshots", false); shots.Bounds = new Rectangle(546, 568, 106, 34);
            shots.Click += (s, e) => OpenFolder(MirrorSession.ShotsDir);
            logBtn = Theme.MakeButton("Show log", false); logBtn.Bounds = new Rectangle(658, 568, 106, 34);
            logBtn.Click += (s, e) => ToggleLog();
            var quit = Theme.MakeButton("Quit", false); quit.Bounds = new Rectangle(770, 568, 114, 34);
            tips.SetToolTip(quit, "Stop all mirrors and close MirrorDock (closing the window keeps it in the tray).");
            quit.Click += (s, e) => QuitApp();
            Controls.Add(shot); Controls.Add(recs); Controls.Add(shots); Controls.Add(logBtn); Controls.Add(quit);

            // ---- footer: status + start with Windows
            statusLabel = Theme.MakeLabel("Ready.", Theme.Small, Theme.Sub);
            statusLabel.AutoSize = false; statusLabel.AutoEllipsis = true;
            statusLabel.Bounds = new Rectangle(18, 616, 660, 18);
            Controls.Add(statusLabel);
            kStartup = Theme.MakeCheck("Start with Windows (in tray)");
            kStartup.Font = Theme.Small; kStartup.ForeColor = Theme.Sub;
            kStartup.Location = new Point(694, 613);
            kStartup.Checked = StartupEnabled();
            kStartup.CheckedChanged += (s, e) => SetStartup(kStartup.Checked);
            Controls.Add(kStartup);

            logBox = new TextBox();
            logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.FromArgb(14, 15, 19); logBox.ForeColor = Theme.Sub;
            logBox.BorderStyle = BorderStyle.None; logBox.Font = new Font("Consolas", 8.5f);
            logBox.Bounds = new Rectangle(16, 644, 868, 180);
            logBox.Visible = false;
            Controls.Add(logBox);
        }

        ComboBox Row(Panel card, string label, Choice[] items, int y)
        {
            var l = Theme.MakeLabel(label, Theme.Ui, Theme.Sub); l.BackColor = Theme.Card;
            l.Location = new Point(14, y + 4); card.Controls.Add(l);
            var c = Theme.MakeCombo(false); c.Bounds = new Rectangle(96, y, 166, 28);
            Theme.Fill(c, items, items[0].Value);
            card.Controls.Add(c);
            return c;
        }

        CheckBox Check(Panel card, string text, int y)
        {
            var k = Theme.MakeCheck(text); k.BackColor = Theme.Card;
            k.Location = new Point(14, y); card.Controls.Add(k);
            return k;
        }

        ContextMenuStrip HelpMenu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkRenderer() };
            m.Items.Add("Setup guide (connect a phone)", null, (s, e) => ShowSetupGuide());
            m.Items.Add("Copy diagnostics (for bug reports)", null, (s, e) => CopyDiagnostics());
            m.Items.Add("Report a problem on GitHub", null, (s, e) => OpenUrl(Links.IssuesUrl));
            m.Items.Add("Open log file", null, (s, e) => { try { Process.Start("notepad.exe", "\"" + AppLog.FilePath + "\""); } catch { } });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Change adb folder...", null, (s, e) => PickAdbFolder());
            m.Items.Add("Check for updates now", null, (s, e) => CheckForUpdates(true));
            m.Items.Add("MirrorDock on GitHub", null, (s, e) => OpenUrl(Links.RepoUrl));
            if (Links.BuyMeACoffee.Length > 0) m.Items.Add("☕ Buy me a coffee", null, (s, e) => OpenUrl(Links.CoffeeUrl));
            m.Items.Add(new ToolStripMenuItem("MirrorDock " + Version + "  |  adb: " + Adb.AdbPath) { Enabled = false });
            return m;
        }

        static void OpenUrl(string url) { try { Process.Start(url); } catch { } }

        // ------------------------------------------------------------ options
        static readonly Dictionary<string, string> JpegQualityFor = new Dictionary<string, string> { { "3", "55" }, { "8", "75" }, { "12", "82" }, { "16", "88" }, { "24", "95" } };

        Options ReadOptions()
        {
            var o = new Options();
            o.MaxSize = Theme.Value(cSize); o.BitRate = Theme.Value(cBitrate); o.MaxFps = Theme.Value(cFps);
            string q; o.Quality = JpegQualityFor.TryGetValue(o.BitRate, out q) ? q : "75";
            o.Audio = kAudio.Checked; o.Codec = kCompat.Checked ? "jpeg" : "h264";
            o.OnTop = kOnTop.Checked; o.Fullscreen = kFull.Checked; o.Borderless = kBorderless.Checked;
            o.Toolbar = kToolbar.Checked; o.AutoReconnect = kReconnect.Checked;
            o.ScreenOff = kScreenOff.Checked; o.StayAwake = kAwake.Checked; o.ShowTouches = kTouches.Checked;
            o.PowerOffOnClose = kPowerOff.Checked; o.ViewOnly = kViewOnly.Checked;
            return o;
        }

        void LoadOptions(Options o)
        {
            loadingUi = true;
            Theme.Select(cSize, o.MaxSize); Theme.Select(cBitrate, o.BitRate); Theme.Select(cFps, o.MaxFps);
            kAudio.Checked = o.Audio; kCompat.Checked = o.Codec == "jpeg";
            kOnTop.Checked = o.OnTop; kFull.Checked = o.Fullscreen; kBorderless.Checked = o.Borderless;
            kToolbar.Checked = o.Toolbar; kReconnect.Checked = o.AutoReconnect;
            kScreenOff.Checked = o.ScreenOff; kAwake.Checked = o.StayAwake; kTouches.Checked = o.ShowTouches;
            kPowerOff.Checked = o.PowerOffOnClose; kViewOnly.Checked = o.ViewOnly;
            loadingUi = false;
            UpdateEnabled();
            settings.SaveCurrent(ReadOptions());
        }

        void OptionsChanged()
        {
            if (loadingUi) return;
            UpdateEnabled();
            settings.SaveCurrent(ReadOptions());
        }

        void UpdateEnabled()
        {
            bool control = !kViewOnly.Checked;
            kScreenOff.Enabled = control; kAwake.Enabled = control; kPowerOff.Enabled = control; kTouches.Enabled = control;
        }

        Options FindProfile(string name)
        {
            var user = AppSettings.UserProfiles();
            if (user.ContainsKey(name)) return user[name];
            var b = Options.BuiltIns();
            return b.ContainsKey(name) ? b[name] : null;
        }

        List<string> ProfileNames()
        {
            var names = Options.BuiltIns().Keys.ToList();
            foreach (string n in AppSettings.UserProfiles().Keys) if (!names.Contains(n)) names.Add(n);
            return names;
        }

        void FillProfiles(string select)
        {
            loadingUi = true;
            profileBox.Items.Clear();
            foreach (string n in ProfileNames()) profileBox.Items.Add(new Choice(n, n));
            Theme.Select(profileBox, select);
            loadingUi = false;
        }

        void SaveProfile()
        {
            string cur = profileBox.SelectedItem == null ? "" : profileBox.SelectedItem.ToString();
            string[] r = Prompt.Ask(this, "Save profile", new[] { "Profile name" }, new[] { cur });
            if (r == null || r[0].Length == 0) return;
            string name = AppSettings.SafeName(r[0]);
            AppSettings.SaveProfile(name, ReadOptions());
            settings.LastProfile = name;
            FillProfiles(name);
            Status("Saved profile \"" + name + "\".", false);
        }

        void DeleteProfile()
        {
            if (profileBox.SelectedItem == null) return;
            string name = profileBox.SelectedItem.ToString();
            if (!AppSettings.UserProfiles().ContainsKey(name))
            {
                Status("\"" + name + "\" is built in and can't be deleted.", true);
                return;
            }
            if (MessageBox.Show(this, "Delete the profile \"" + name + "\"?", "MirrorDock", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            AppSettings.DeleteProfile(name);
            bool builtIn = Options.BuiltIns().ContainsKey(name);
            FillProfiles(builtIn ? name : "Balanced");
            Status(builtIn ? "Reset \"" + name + "\" to its default." : "Deleted \"" + name + "\".", false);
        }

        // ------------------------------------------------------------ adb location
        void LocateAdb()
        {
            var candidates = new List<string>();
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (settings.AdbDir.Length > 0) candidates.Add(settings.AdbDir);
            candidates.Add(Path.Combine(exeDir, "adb"));
            candidates.Add(exeDir);
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android\\Sdk\\platform-tools"));
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            foreach (string dir in candidates)
            {
                try
                {
                    if (dir.Length > 0 && File.Exists(Path.Combine(dir, "adb.exe"))) { Adb.AdbPath = Path.Combine(dir, "adb.exe"); return; }
                }
                catch { }
            }
            Status("adb.exe is missing. Reinstall MirrorDock, or use Help > Change adb folder.", true);
        }

        void PickAdbFolder()
        {
            using (var f = new FolderBrowserDialog())
            {
                f.Description = "Pick the folder with adb.exe (Android platform-tools)";
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(f.SelectedPath, "adb.exe"))) { Status("That folder doesn't have adb.exe.", true); return; }
                settings.AdbDir = f.SelectedPath;
                Adb.AdbPath = Path.Combine(f.SelectedPath, "adb.exe");
                Status("Using adb from " + f.SelectedPath, false);
                RefreshDevices();
            }
        }

        // ------------------------------------------------------------ devices
        Device Selected()
        {
            int i = deviceList.SelectedIndex;
            return i >= 0 && i < devices.Count ? devices[i] : null;
        }

        string NameOf(Device d)
        {
            string n = settings.Nickname(d.Serial);
            if (n.Length > 0) return n;
            return d.Model.Length > 0 ? d.Model : d.Serial;
        }

        bool IsLive(string serial)
        {
            MirrorSession s;
            return sessions.TryGetValue(serial, out s) && s.Running;
        }

        async void RefreshDevices()
        {
            if (refreshing || !File.Exists(Adb.AdbPath)) return;
            refreshing = true;
            try
            {
                List<Device> list = await Adb.ListDevices();
                string sel = Selected() == null ? null : Selected().Serial;
                bool changed = list.Count != devices.Count;
                for (int i = 0; !changed && i < list.Count; i++)
                    changed = list[i].Serial != devices[i].Serial || list[i].State != devices[i].State || list[i].Model != devices[i].Model;
                devices = list;
                if (changed)
                {
                    deviceList.BeginUpdate();
                    deviceList.Items.Clear();
                    foreach (Device d in devices) deviceList.Items.Add(d.Serial);
                    int idx = devices.FindIndex(d => d.Serial == sel);
                    if (idx < 0 && devices.Count > 0) idx = 0;
                    if (idx >= 0) deviceList.SelectedIndex = idx;
                    deviceList.EndUpdate();
                    foreach (Device d in devices)
                        if (d.State == "unauthorized")
                            Status(NameOf(d) + ": unlock the phone and tap Allow on the USB debugging popup.", true);
                }
                emptyHint.Visible = devices.Count == 0;
                deviceList.Invalidate();
                UpdateStartButton();
                if (AutoMirror != null)
                {
                    Device auto = devices.FirstOrDefault(d => d.Ready && (AutoMirror == "*" || d.Serial == AutoMirror));
                    if (auto != null) { AutoMirror = null; StartMirror(auto.Serial, NameOf(auto), OptionsFor(auto.Serial)); }
                }
                if (!firstRefreshDone)
                {
                    firstRefreshDone = true;
                    if (AutoMirror != null) return;
                    if (devices.Count == 0 && settings.Get("SetupShown", "") != "1" && Visible) { settings.Set("SetupShown", "1"); ShowSetupGuide(); }
                }
            }
            finally { refreshing = false; }
        }

        void DrawDevice(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= devices.Count) return;
            Device d = devices[e.Index];
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float sc = g.DpiX / 96f;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle r = e.Bounds;
            using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, r);
            if (sel)
                using (var path = Theme.Rounded(new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), 8 * sc))
                using (var br = new SolidBrush(Color.FromArgb(42, 50, 66))) g.FillPath(br, path);

            MirrorSession s;
            bool live = sessions.TryGetValue(d.Serial, out s) && s.Running;
            bool recon = reconnecting.Contains(d.Serial);
            Color dot = live ? (s.Recording ? Theme.Red : Theme.Green) : recon ? Theme.Amber : d.Ready ? Theme.Sub : Theme.Amber;
            using (var br = new SolidBrush(dot)) g.FillEllipse(br, r.X + 12 * sc, r.Y + r.Height / 2f - 5 * sc, 10 * sc, 10 * sc);

            string link = d.IsWifi ? "Wi-Fi" : "USB";
            string prof = settings.PhoneProfile(d.Serial);
            string line2 = link + "  |  " + (prof.Length > 0 ? "Profile: " + prof : d.Serial);
            if (live) line2 = (s.Recording ? "Recording" : "Mirroring") + "  |  " + link + (s.H264 ? "" : "  |  compat");
            else if (recon) line2 = "Reconnecting...";
            else if (d.State == "unauthorized") line2 = "Tap Allow on the phone";
            else if (d.State == "offline") line2 = "Offline - replug or reconnect";
            TextRenderer.DrawText(g, NameOf(d), Theme.UiBold, new Rectangle((int)(r.X + 32 * sc), r.Y + (int)(7 * sc), r.Width - (int)(40 * sc), (int)(20 * sc)),
                Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, line2, Theme.Small, new Rectangle((int)(r.X + 32 * sc), r.Y + (int)(27 * sc), r.Width - (int)(40 * sc), (int)(18 * sc)),
                live ? (s.Recording ? Theme.Red : Theme.Green) : Theme.Sub, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        ContextMenuStrip DeviceMenu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkRenderer() };
            Device d = Selected();
            if (d == null) return m;
            m.Items.Add(IsLive(d.Serial) ? "Stop mirroring" : "Start mirroring", null, (s, e) => ToggleSelected());
            m.Items.Add("Rename...", null, (s, e) => RenameDevice());
            var prof = new ToolStripMenuItem("Always use profile");
            string cur = settings.PhoneProfile(d.Serial);
            prof.DropDownItems.Add(new ToolStripMenuItem("(none - use what's selected)", null, (s, e) => { settings.SetPhoneProfile(d.Serial, null); deviceList.Invalidate(); }) { Checked = cur.Length == 0 });
            foreach (string n in ProfileNames())
            {
                string name = n;
                prof.DropDownItems.Add(new ToolStripMenuItem(name, null, (s, e) => { settings.SetPhoneProfile(d.Serial, name); deviceList.Invalidate(); Status(NameOf(d) + " will always use \"" + name + "\".", false); }) { Checked = cur == name });
            }
            m.Items.Add(prof);
            m.Items.Add("Screenshot", null, (s, e) => TakeScreenshot(d.Serial, NameOf(d)));
            m.Items.Add("Install APK...", null, (s, e) => InstallApk(d.Serial, this));
            if (!d.IsWifi) m.Items.Add("Make wireless", null, (s, e) => MakeWireless());
            else m.Items.Add("Turn off Wi-Fi debugging (close port)", null, (s, e) => DisconnectSelected());
            m.Items.Add("Forget window position", null, (s, e) => settings.Set("win." + d.Serial, null));
            m.Items.Add("Copy serial", null, (s, e) => Clipboard.SetText(d.Serial));
            return m;
        }

        void RenameDevice()
        {
            Device d = Selected();
            if (d == null) return;
            string[] r = Prompt.Ask(this, "Rename phone", new[] { "Name for " + (d.Model.Length > 0 ? d.Model : d.Serial) + " (leave empty to reset)" }, new[] { settings.Nickname(d.Serial) });
            if (r == null) return;
            settings.SetNickname(d.Serial, r[0]);
            deviceList.Invalidate();
        }

        async void MakeWireless()
        {
            Device d = Selected();
            if (d == null || !d.Ready) { Status("Pick a connected USB phone first.", true); return; }
            if (d.IsWifi) { Status(NameOf(d) + " is already on Wi-Fi.", false); return; }
            Status("Switching " + NameOf(d) + " to Wi-Fi...", false);
            try
            {
                string addr = await Adb.MakeWireless(d.Serial);
                string nick = settings.Nickname(d.Serial);
                settings.SetNickname(addr, nick.Length > 0 ? nick : (d.Model.Length > 0 ? d.Model : null));
                string prof = settings.PhoneProfile(d.Serial);
                if (prof.Length > 0) settings.SetPhoneProfile(addr, prof);
                RememberIp(addr);
                Status("Wireless on " + addr + ". You can unplug the cable now.", false);
                RefreshDevices();
            }
            catch (Exception ex) { Status(ex.Message, true); }
        }

        void RememberIp(string addr)
        {
            settings.AddRecentIp(addr);
            if (!ipBox.AutoCompleteCustomSource.Contains(addr)) ipBox.AutoCompleteCustomSource.Add(addr);
        }

        async void ConnectIp()
        {
            string addr = ipBox.Text.Trim();
            if (addr.Length == 0) { Status("Type the phone's IP address, like 192.168.1.20", true); return; }
            Status("Connecting to " + addr + "...", false);
            try
            {
                addr = await Adb.Connect(addr);
                RememberIp(addr);
                Status("Connected to " + addr + ".", false);
                RefreshDevices();
            }
            catch (Exception ex) { Status(ex.Message, true); }
        }

        async void PairDevice()
        {
            string[] r = Prompt.Ask(this, "Pair over Wi-Fi (Android 11+)",
                new[] { "Pairing IP:port (from \"Pair device with pairing code\")", "6-digit pairing code", "Connect IP:port (on the main Wireless debugging screen)" },
                null);
            if (r == null) return;
            try
            {
                Status("Pairing...", false);
                await Adb.Pair(r[0], r[1]);
                if (r[2].Length > 0)
                {
                    string addr = await Adb.Connect(r[2]);
                    RememberIp(addr);
                    Status("Paired and connected to " + addr + ".", false);
                }
                else Status("Paired. Now connect with the IP:port on the Wireless debugging screen.", false);
                RefreshDevices();
            }
            catch (Exception ex) { Status(ex.Message, true); }
        }

        async void DisconnectSelected()
        {
            Device d = Selected();
            if (d == null) return;
            if (!d.IsWifi)
            {
                // A USB phone may still have Wi-Fi debugging open from an earlier "Make wireless": close it too.
                Status("Turning off Wi-Fi debugging on " + NameOf(d) + "...", false);
                await Adb.TurnOffWifi(d.Serial);
                Status("Wi-Fi debugging is off on " + NameOf(d) + ". USB still works.", false);
                RefreshDevices();
                return;
            }
            MirrorSession s;
            if (sessions.TryGetValue(d.Serial, out s)) { reconnecting.Remove(d.Serial); s.Stop(); await Task.Delay(400); }
            Status("Turning off Wi-Fi debugging on " + NameOf(d) + "...", false);
            bool closed = await Adb.TurnOffWifi(d.Serial);
            Status(closed
                ? "Wi-Fi debugging is off on " + NameOf(d) + " and its port is closed. To use Wi-Fi again, plug in USB and click Make wireless."
                : "Disconnected " + NameOf(d) + ". To be sure the port is closed, turn off Developer options > Wireless debugging on the phone (or restart it).", !closed);
            RefreshDevices();
        }

        // Before quitting: offer to close Wi-Fi debugging on phones that are still reachable over the network.
        async Task<bool> OfferWifiShutdown()
        {
            List<Device> wifi = devices.Where(d => d.IsWifi && d.Ready).ToList();
            if (wifi.Count == 0) return true;
            Show();
            DialogResult r = MessageBox.Show(this,
                string.Join(", ", wifi.Select(NameOf).ToArray()) + " still accept" + (wifi.Count == 1 ? "s" : "") +
                " debugging connections over your Wi-Fi.\r\n\r\nTurn Wi-Fi debugging off now? (Recommended. You can turn it back on later with USB + Make wireless.)",
                "MirrorDock", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) foreach (Device d in wifi) await Adb.TurnOffWifi(d.Serial);
            return true;
        }

        // ------------------------------------------------------------ mirroring
        void UpdateStartButton()
        {
            Device d = Selected();
            bool live = d != null && IsLive(d.Serial);
            startBtn.Text = live ? "Stop mirroring" : d == null ? "Start mirroring" : "Start mirroring " + Shorten(NameOf(d), 14);
            startBtn.Enabled = d != null && (live || d.Ready);
            if (live) Theme.SetPrimaryColor(startBtn, Theme.Red, Color.FromArgb(240, 100, 105));
            else Theme.SetPrimaryColor(startBtn, Theme.Accent, Theme.AccentHover);
        }

        static string Shorten(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        void ToggleSelected()
        {
            Device d = Selected();
            if (d == null) return;
            MirrorSession s;
            if (sessions.TryGetValue(d.Serial, out s) && s.Running) { reconnecting.Remove(d.Serial); s.Stop(); return; }
            StartMirror(d.Serial, NameOf(d), OptionsFor(d.Serial));
        }

        Options OptionsFor(string serial)
        {
            string p = settings.PhoneProfile(serial);
            Options o = p.Length > 0 ? FindProfile(p) : null;
            return o ?? ReadOptions();
        }

        public async void StartMirror(string serial, string name, Options o)
        {
            if (!File.Exists(Adb.AdbPath)) { Status("adb.exe is missing. Use Help > Change adb folder.", true); return; }
            if (IsLive(serial)) return;
            var s = new MirrorSession { Serial = serial, Name = name, Opts = o };
            s.Line += (ss, line) => BeginInvokeSafe(() => Log(ss.Name, line));
            s.Ended += (ss, dropped) => BeginInvokeSafe(() => OnEnded(ss, dropped));
            s.PhoneClipboard += (ss, text) => BeginInvokeSafe(() => SetPcClipboard(text));
            s.RecordingChanged += ss => BeginInvokeSafe(() => deviceList.Invalidate());
            sessions[serial] = s;
            deviceList.Invalidate();
            UpdateStartButton();
            Status("Starting " + name + "...", false);

            s.Win = new MirrorWindow(this, s);
            s.Win.Show();
            try { await s.Start(forceJpeg.Contains(serial)); }
            catch (Exception ex)
            {
                s.LastError = ex.Message;
                s.Stop();
                return;
            }
            if (s.Stopping) return;
            s.Win.StartReading();
            if (o.ScreenOff && !o.ViewOnly) { await Task.Delay(1200); s.ScreenPower(false); }
            if (o.Toolbar)
            {
                s.Bar = new ToolbarForm(this, s);
                s.Bar.StartFollowing();
            }
            Status(name + " is mirroring" + (s.H264 ? "" : " (compatibility mode)") + ".", false);
            deviceList.Invalidate();
            UpdateStartButton();
        }

        void OnEnded(MirrorSession s, bool dropped)
        {
            if (s.Bar != null) { s.Bar.Close(); s.Bar = null; }
            if (s.Win != null && !s.Win.IsDisposed) s.Win.Close();
            deviceList.Invalidate();
            UpdateStartButton();

            if (s.NeedsJpeg && !forceJpeg.Contains(s.Serial))
            {   // Windows can't decode H.264 here (e.g. Windows N without the Media Feature Pack): retry with pictures
                forceJpeg.Add(s.Serial);
                Status(s.LastError, true);
                StartMirror(s.Serial, s.Name, s.Opts);
                return;
            }
            if (dropped && s.Opts.AutoReconnect && (DateTime.Now - s.Started).TotalSeconds > 8)
            {
                Reconnect(s);
                return;
            }
            if (s.LastError != null && (dropped || (DateTime.Now - s.Started).TotalSeconds < 8))
            {
                Status(s.Name + ": " + s.LastError, true);
                Balloon("Mirroring stopped", s.Name + ": " + s.LastError, null);
            }
            else Status(s.Name + " stopped.", false);
        }

        async void Reconnect(MirrorSession s)
        {
            reconnecting.Add(s.Serial);
            deviceList.Invalidate();
            Status(s.Name + " dropped. Reconnecting...", true);
            bool wifi = s.Serial.Contains(":");
            for (int i = 0; i < 30 && reconnecting.Contains(s.Serial) && !quitting; i++)
            {
                if (wifi) await Adb.Run("connect " + s.Serial, 6000);
                List<Device> list = await Adb.ListDevices();
                if (list.Any(d => d.Serial == s.Serial && d.Ready))
                {
                    reconnecting.Remove(s.Serial);
                    await Task.Delay(800);
                    StartMirror(s.Serial, s.Name, s.Opts);
                    return;
                }
                await Task.Delay(2000);
            }
            if (reconnecting.Remove(s.Serial))
            {
                Status(s.Name + " didn't come back. Reconnect it and press Start.", true);
                Balloon("Phone lost", s.Name + " didn't come back after 1 minute.", null);
            }
            deviceList.Invalidate();
        }

        public void ToggleRecording(MirrorSession s)
        {
            try
            {
                string saved = s.ToggleRecording();
                if (saved != null)
                {
                    Status("Recording saved: " + Path.GetFileName(saved), false);
                    if (s.Win != null) s.Win.ShowOverlay("Recording saved");
                    Balloon("Recording saved", Path.GetFileName(saved), saved);
                }
                else
                {
                    Status("Recording " + s.Name + "... press F12 or the record button to stop.", false);
                    if (s.Win != null) s.Win.ShowOverlay("Recording started");
                }
            }
            catch (Exception ex) { Status(ex.Message, true); if (s.Win != null) s.Win.ShowOverlay(ex.Message); }
            deviceList.Invalidate();
        }

        public Rectangle SavedWindow(string serial) { return settings.Window(serial); }
        public void SaveWindow(string serial, Rectangle r) { settings.SetWindow(serial, r); }

        // Files dropped on a mirror: APKs install, everything else goes to the phone's Download folder.
        public async void SendFiles(MirrorSession s, string[] files)
        {
            foreach (string f in files)
            {
                if (Directory.Exists(f)) { Status("Folders can't be dropped yet. Drop the files inside it.", true); continue; }
                string name = Path.GetFileName(f);
                try
                {
                    if (f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        s.Win.ShowOverlay("Installing " + name + "...");
                        Status("Installing " + name + "...", false);
                        string msg = await Adb.InstallApk(s.Serial, f);
                        Status(msg, false); s.Win.ShowOverlay(msg);
                    }
                    else
                    {
                        s.Win.ShowOverlay("Sending " + name + "...");
                        Status("Sending " + name + " to Download...", false);
                        ProcResult r = await Adb.Run("-s " + s.Serial + " push \"" + f + "\" \"/sdcard/Download/" + name.Replace("\"", "") + "\"", 600000);
                        if (r.Code != 0) throw new Exception("Couldn't send " + name + ": " + r.All);
                        await Adb.Run("-s " + s.Serial + " shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d \"file:///sdcard/Download/" + Uri.EscapeDataString(name) + "\"");
                        Status(name + " is in the phone's Download folder.", false);
                        s.Win.ShowOverlay("Saved to Download: " + name);
                    }
                }
                catch (Exception ex) { Status(ex.Message, true); if (!s.Win.IsDisposed) s.Win.ShowOverlay("Failed: " + name); }
            }
        }

        public async void TakeScreenshot(string serial, string name)
        {
            try
            {
                Directory.CreateDirectory(MirrorSession.ShotsDir);
                string file = Path.Combine(MirrorSession.ShotsDir, AppSettings.SafeName(name).Replace(' ', '_') + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
                Status("Taking screenshot...", false);
                await Adb.Screenshot(serial, file);
                Status("Screenshot saved: " + Path.GetFileName(file), false);
                Balloon("Screenshot saved", Path.GetFileName(file), file);
            }
            catch (Exception ex) { Status(ex.Message, true); }
        }

        public async void InstallApk(string serial, IWin32Window owner)
        {
            using (var f = new OpenFileDialog { Filter = "Android apps (*.apk)|*.apk", Title = "Install an app on the phone" })
            {
                if (f.ShowDialog(owner) != DialogResult.OK) return;
                Status("Installing " + Path.GetFileName(f.FileName) + "...", false);
                try { Status(await Adb.InstallApk(serial, f.FileName), false); }
                catch (Exception ex) { Status(ex.Message, true); }
            }
        }

        // ------------------------------------------------------------ clipboard sync
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) OnPcClipboardChanged(); // WM_CLIPBOARDUPDATE
            base.WndProc(ref m);
        }

        void OnPcClipboardChanged()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string t = Clipboard.GetText();
                if (t == clipboardFromPhone) return;
                foreach (var s in sessions.Values.Where(x => x.Running).ToList()) s.SetPhoneClipboard(t, false);
            }
            catch { }
        }

        void SetPcClipboard(string text)
        {
            try
            {
                clipboardFromPhone = text;
                if (string.IsNullOrEmpty(text)) return;
                Clipboard.SetText(text);
            }
            catch { }
        }

        // ------------------------------------------------------------ help: setup guide, diagnostics, updates
        void ShowSetupGuide()
        {
            if (!Visible) ShowMain();
            using (var g = new SetupGuide()) g.ShowDialog(this);
            RefreshDevices();
        }

        async void CopyDiagnostics()
        {
            Status("Collecting diagnostics...", false);
            string text = await AppLog.Diagnostics(devices);
            try { Clipboard.SetText(text); } catch { }
            Status("Diagnostics copied. Paste them into a GitHub issue (Help > Report a problem).", false);
        }

        async void CheckForUpdates(bool manual = false)
        {
            if (!manual)
            {
                if (settings.Get("UpdateCheck", "1") != "1") return;
                DateTime last;
                if (DateTime.TryParse(settings.Get("LastUpdateCheck", ""), out last) && (DateTime.Now - last).TotalHours < 20)
                {
                    ShowUpdate(settings.Get("UpdateVersion", ""), settings.Get("UpdateUrl", ""));
                    return;
                }
            }
            Updates.Info info = await Updates.Check();
            settings.Set("LastUpdateCheck", DateTime.Now.ToString("o"));
            settings.Set("UpdateVersion", info == null ? null : info.Version);
            settings.Set("UpdateUrl", info == null ? null : info.Url);
            if (info != null) { ShowUpdate(info.Version, info.Url); Balloon("MirrorDock " + info.Version + " is out", "Click to download the update.", null); }
            else if (manual) Status("You have the latest version (" + Version + ").", false);
        }

        void ShowUpdate(string version, string url)
        {
            if (string.IsNullOrEmpty(version) || !Updates.Newer(version, Version)) { updateLink.Visible = false; return; }
            updateLink.Text = "⬆ Update available: v" + version;
            updateLink.Visible = true;
            updateLink.Click -= UpdateClick;
            updateLink.Tag = url;
            updateLink.Click += UpdateClick;
        }

        void UpdateClick(object sender, EventArgs e) { OpenUrl(updateLink.Tag as string ?? Links.RepoUrl + "/releases/latest"); }

        // ------------------------------------------------------------ status, log, tray
        public void Status(string text, bool warn)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = warn ? Theme.Amber : Theme.Sub;
            Log("MirrorDock", text);
        }

        void Log(string who, string line)
        {
            AppLog.Write("[" + who + "] " + line);
            if (logBox.TextLength > 200000) logBox.Text = logBox.Text.Substring(100000);
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  [" + who + "]  " + line + "\r\n");
        }

        void ToggleLog()
        {
            logBox.Visible = !logBox.Visible;
            logBtn.Text = logBox.Visible ? "Hide log" : "Show log";
            int extra = (int)(196 * DeviceDpiScale());
            ClientSize = new Size(ClientSize.Width, logBox.Visible ? baseHeight + extra : baseHeight);
        }

        float DeviceDpiScale()
        {
            using (Graphics g = CreateGraphics()) return g.DpiX / 96f;
        }

        void BeginInvokeSafe(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip { Renderer = new DarkRenderer() };
            tray = new NotifyIcon { Icon = Icon, Text = "MirrorDock", Visible = true, ContextMenuStrip = trayMenu };
            tray.DoubleClick += (s, e) => ShowMain();
            trayMenu.Opening += (s, e) => RebuildTrayMenu();
            tray.BalloonTipClicked += (s, e) =>
            {
                var path = tray.Tag as string;
                if (path == null) { if (updateLink.Visible) UpdateClick(null, null); else ShowMain(); return; }
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
            };
            RebuildTrayMenu();
        }

        void RebuildTrayMenu()
        {
            if (trayMenu == null) return;
            trayMenu.Items.Clear();
            var open = new ToolStripMenuItem("Open MirrorDock", null, (s, e) => ShowMain());
            open.Font = new Font(open.Font, FontStyle.Bold);
            trayMenu.Items.Add(open);
            trayMenu.Items.Add(new ToolStripSeparator());
            if (devices.Count == 0) trayMenu.Items.Add(new ToolStripMenuItem("No phones connected") { Enabled = false });
            foreach (Device d in devices.Where(x => x.Ready))
            {
                Device dev = d;
                bool live = IsLive(dev.Serial);
                trayMenu.Items.Add(new ToolStripMenuItem((live ? "Stop " : "Mirror ") + NameOf(dev), null, (s, e) =>
                {
                    MirrorSession ss;
                    if (sessions.TryGetValue(dev.Serial, out ss) && ss.Running) ss.Stop();
                    else StartMirror(dev.Serial, NameOf(dev), OptionsFor(dev.Serial));
                }));
            }
            var profiles = new ToolStripMenuItem("Profile");
            foreach (object item in profileBox.Items)
            {
                string n = item.ToString();
                var mi = new ToolStripMenuItem(n, null, (s, e) => Theme.Select(profileBox, n));
                mi.Checked = profileBox.SelectedItem != null && profileBox.SelectedItem.ToString() == n;
                profiles.DropDownItems.Add(mi);
            }
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(profiles);
            trayMenu.Items.Add("Stop all mirrors", null, (s, e) => { foreach (var ss in sessions.Values.ToList()) ss.Stop(); });
            trayMenu.Items.Add("Open recordings folder", null, (s, e) => OpenFolder(MirrorSession.VideosDir));
            trayMenu.Items.Add("Setup guide", null, (s, e) => ShowSetupGuide());
            if (Links.BuyMeACoffee.Length > 0) trayMenu.Items.Add("☕ Buy me a coffee", null, (s, e) => OpenUrl(Links.CoffeeUrl));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Quit", null, (s, e) => QuitApp());
        }

        void Balloon(string title, string text, string path)
        {
            tray.Tag = path;
            tray.ShowBalloonTip(4000, title, text, ToolTipIcon.None);
        }

        public void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            RefreshDevices();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (settings.Get("TrayHintShown", "") != "1")
                {
                    settings.Set("TrayHintShown", "1");
                    Balloon("MirrorDock is still running", "It's in the tray by the clock. Right-click it to quit.", null);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        async void QuitApp()
        {
            if (quitting) return;
            quitting = true;
            foreach (var s in sessions.Values.ToList()) s.Stop();
            if (!await OfferWifiShutdown()) { quitting = false; return; }
            reconnecting.Clear();
            foreach (var s in sessions.Values.ToList()) s.Stop();
            tray.Visible = false;
            tray.Dispose();
            AppLog.Write("MirrorDock quit");
            var t = new Timer { Interval = 700 };   // give the phone engines a moment to clean up
            t.Tick += (s, e) => Application.Exit();
            t.Start();
        }

        static void OpenFolder(string dir)
        {
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        // ------------------------------------------------------------ start with Windows
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool StartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("MirrorDock") != null;
        }

        void SetStartup(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("MirrorDock", "\"" + Application.ExecutablePath + "\" --tray");
                else if (k.GetValue("MirrorDock") != null) k.DeleteValue("MirrorDock");
            }
            Status(on ? "MirrorDock will start in the tray when Windows starts." : "MirrorDock won't start with Windows.", false);
        }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Sub;
            base.OnRenderItemText(e);
        }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Theme.Border; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
        public override Color CheckBackground { get { return Theme.Accent; } }
        public override Color CheckSelectedBackground { get { return Theme.Accent; } }
        public override Color CheckPressedBackground { get { return Theme.Accent; } }
    }
}
