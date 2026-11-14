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
        public const string Version = "1.2.0";

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
        CheckBox kAutoQ, kAudio, kCompat, kOnTop, kFull, kBorderless, kToolbar, kReconnect, kUpdates, kWifiStart, kNotify, kPad;
        ComboBox cWifiOff;
        CheckBox kScreenOff, kAwake, kTouches, kPowerOff, kViewOnly, kStartup;
        PillButton startBtn, logBtn;
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
            Shown += (s, e) => { ReconnectWifiPhones(); RefreshDevices(); refreshTimer.Start(); CheckForUpdates(); };
            Gamepad.Start(() => ActiveSession);
            InputCapture.Changed += () => BeginInvokeSafe(() => deviceList.Invalidate());
            var wifiTimer = new Timer { Interval = 60000 };
            wifiTimer.Tick += (s, e) => WifiAutoOff();
            wifiTimer.Start();
            HandleCreated += (s, e) => { Native.DarkTitleBar(Handle); Native.AddClipboardFormatListener(Handle); };
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; RefreshDevices(); refreshTimer.Start(); CheckForUpdates(); }
            base.SetVisibleCore(value);
        }

        // ------------------------------------------------------------ layout
        PillButton wifiBtn, controlBtn, filesBtn, moreBtn;
        Label tipLabel;
        int tipIndex;
        int hoverDevice = -1;

        static readonly string[] Tips = {
            "Click and drag on the mirror to tap and swipe. Right-click or Esc is Back, middle-click is Home.",
            "Hold Ctrl and drag to pinch-zoom maps and photos.",
            "Drop an APK on the mirror to install it, or any other file to send it to the phone's Download folder.",
            "F11 toggles fullscreen. F12 starts and stops recording.",
            "Control mode: use your PC's mouse and keyboard on the phone with no mirror window. Ctrl+Alt+M takes control, Ctrl+Alt gives it back.",
            "The keyboard button on the mirror's bar switches to a real keyboard for games and shortcuts.",
            "Right-click a phone for more: open an app in its own window, a second screen, rename, pin a profile.",
            "Copy text on the phone and paste it on the PC, or the other way round. The clipboard syncs by itself.",
            "On a slow Wi-Fi, quality drops by itself and comes back when the connection improves.",
            "Done with Wi-Fi? Turn off Wi-Fi closes the phone's debugging port. MirrorDock also does it after 30 idle minutes." };

        void BuildUi()
        {
            ClientSize = new Size(980, 624);

            // ---- header
            var logo = new PictureBox { Image = Theme.Logo(64), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(18, 14, 36, 36) };
            Controls.Add(logo);
            var title = Theme.MakeLabel("MirrorDock", Theme.Title, Theme.Text); title.Location = new Point(60, 10); Controls.Add(title);
            var sub = Theme.MakeLabel("Your phone on your PC  ·  v" + Version, Theme.Small, Theme.Sub); sub.Location = new Point(63, 38); Controls.Add(sub);
            updateLink = new LinkLabel { AutoSize = true, Location = new Point(260, 24), Font = Theme.UiBold, Visible = false,
                LinkColor = Theme.Green, ActiveLinkColor = Theme.Green, BackColor = Color.Transparent };
            updateLink.LinkBehavior = LinkBehavior.HoverUnderline;
            Controls.Add(updateLink);
            int hx = 964;
            if (Links.BuyMeACoffee.Length > 0)
            {
                var coffee = new PillButton("Buy me a coffee", false) { Glyph = "", Bounds = new Rectangle(hx - 170, 16, 170, 34), Font = Theme.UiBold,
                    Fill = Color.FromArgb(255, 221, 0), FillHover = Color.FromArgb(255, 232, 80), TextColor = Color.FromArgb(25, 25, 25) };
                tips.SetToolTip(coffee, "MirrorDock is free. If it helps you, a coffee keeps it going!");
                coffee.Click += (s, e) => OpenUrl(Links.CoffeeUrl);
                Controls.Add(coffee);
                hx -= 178;
            }
            var help = new PillButton("Help", false) { Glyph = "", Bounds = new Rectangle(hx - 96, 16, 96, 34) };
            help.Click += (s, e) => HelpMenu().Show(help, new Point(0, help.Height + 2));
            Controls.Add(help);

            // ---- phones (left)
            var phones = new CardPanel("Your phones") { Bounds = new Rectangle(16, 72, 340, 496) };
            Controls.Add(phones);
            var refresh = new IconButton("") { Bounds = new Rectangle(296, 6, 34, 32), Font = new Font("Segoe MDL2 Assets", 10f) };
            refresh.Click += (s, e) => { RefreshDevices(); Status("Looking for phones...", false); };
            tips.SetToolTip(refresh, "Look for phones again");
            phones.Controls.Add(refresh);
            deviceList = new ListBox();
            deviceList.BorderStyle = BorderStyle.None;
            deviceList.BackColor = Theme.Card;
            deviceList.ForeColor = Theme.Text;
            deviceList.DrawMode = DrawMode.OwnerDrawFixed;
            deviceList.ItemHeight = 62;
            deviceList.IntegralHeight = false;
            deviceList.Bounds = new Rectangle(8, 42, 324, 218);
            deviceList.DrawItem += DrawDevice;
            deviceList.SelectedIndexChanged += (s, e) => UpdateStartButton();
            deviceList.DoubleClick += (s, e) => RenameDevice();
            deviceList.MouseMove += (s, e) => { int i = deviceList.IndexFromPoint(e.Location); if (i != hoverDevice) { hoverDevice = i; deviceList.Invalidate(); } };
            deviceList.MouseLeave += (s, e) => { hoverDevice = -1; deviceList.Invalidate(); };
            deviceList.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = deviceList.IndexFromPoint(e.Location);
                if (i >= 0) { deviceList.SelectedIndex = i; DeviceMenu().Show(deviceList, e.Location); }
            };
            phones.Controls.Add(deviceList);
            emptyHint = Theme.MakeLabel("No phones yet.\r\n\r\nPlug one in with USB debugging on,\r\nor open Help > Setup guide.", Theme.Ui, Theme.Sub);
            emptyHint.BackColor = Theme.Card;
            emptyHint.Location = new Point(24, 70);
            phones.Controls.Add(emptyHint);
            emptyHint.BringToFront();

            startBtn = new PillButton("Mirror", true) { Glyph = "", Bounds = new Rectangle(14, 268, 312, 48) };
            startBtn.Click += (s, e) => ToggleSelected();
            phones.Controls.Add(startBtn);
            controlBtn = new PillButton("Control", false) { Glyph = "", Bounds = new Rectangle(14, 324, 100, 36) };
            controlBtn.Click += (s, e) => { Device d = Selected(); if (d != null) StartControl(d); };
            tips.SetToolTip(controlBtn, "Control mode: use the PC's mouse and keyboard on the phone, with no mirror window (no lag). Ctrl+Alt gives them back.");
            filesBtn = new PillButton("Files", false) { Glyph = "", Bounds = new Rectangle(120, 324, 100, 36) };
            filesBtn.Click += (s, e) => { Device d = Selected(); if (d != null && d.Ready) BrowseFiles(d); else Status("Pick a connected phone first.", true); };
            tips.SetToolTip(filesBtn, "Browse the phone's storage, copy files to the PC and send files to the phone.");
            moreBtn = new PillButton("More", false) { Glyph = "", Bounds = new Rectangle(226, 324, 100, 36) };
            moreBtn.Click += (s, e) => { if (Selected() != null) DeviceMenu().Show(moreBtn, new Point(0, moreBtn.Height + 2)); };
            tips.SetToolTip(moreBtn, "App in its own window, second screen, screenshot, install APK, rename...");
            phones.Controls.Add(controlBtn); phones.Controls.Add(filesBtn); phones.Controls.Add(moreBtn);

            phones.Controls.Add(new Panel { BackColor = Theme.Border, Bounds = new Rectangle(14, 376, 312, 1) });
            var ipLbl = Theme.MakeLabel("Connect over Wi-Fi", Theme.UiBold, Theme.Text); ipLbl.BackColor = Theme.Card;
            ipLbl.Location = new Point(14, 388); phones.Controls.Add(ipLbl);
            ipBox = new TextBox();
            ipBox.BackColor = Theme.Field; ipBox.ForeColor = Theme.Text; ipBox.BorderStyle = BorderStyle.FixedSingle;
            ipBox.Font = Theme.Big; ipBox.Bounds = new Rectangle(14, 414, 196, 30);
            ipBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            ipBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
            ipBox.AutoCompleteCustomSource.AddRange(settings.RecentIps.ToArray());
            if (settings.RecentIps.Count > 0) ipBox.Text = settings.RecentIps[0];
            tips.SetToolTip(ipBox, "Phone's IP, e.g. 192.168.1.20 (recent ones auto-complete)");
            ipBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ConnectIp(); } };
            phones.Controls.Add(ipBox);
            var connect = new PillButton("Connect", false) { Bounds = new Rectangle(216, 413, 110, 32) };
            connect.Click += (s, e) => ConnectIp();
            phones.Controls.Add(connect);
            var pair = new PillButton("Pair with code", false) { Bounds = new Rectangle(14, 452, 150, 32) };
            pair.Click += (s, e) => PairDevice();
            tips.SetToolTip(pair, "Android 11+: Settings > Developer options > Wireless debugging > Pair device with pairing code");
            wifiBtn = new PillButton("Make wireless", false) { Bounds = new Rectangle(170, 452, 156, 32) };
            wifiBtn.Click += (s, e) => { Device d = Selected(); if (d != null && d.IsWifi) DisconnectSelected(); else MakeWireless(); };
            phones.Controls.Add(pair); phones.Controls.Add(wifiBtn);

            // ---- profile + settings tabs (right)
            var pl = Theme.MakeLabel("Profile", Theme.Header, Theme.Text); pl.Location = new Point(372, 79); Controls.Add(pl);
            profileBox = Theme.MakeCombo(false); profileBox.Bounds = new Rectangle(440, 76, 240, 28);
            profileBox.SelectedIndexChanged += (s, e) =>
            {
                if (loadingUi || profileBox.SelectedItem == null) return;
                string name = profileBox.SelectedItem.ToString();
                Options o = FindProfile(name);
                if (o != null) { LoadOptions(o); settings.LastProfile = name; Status("Loaded profile \"" + name + "\".", false); }
            };
            Controls.Add(profileBox);
            var saveP = new PillButton("Save as...", false) { Bounds = new Rectangle(690, 74, 110, 32) };
            saveP.Click += (s, e) => SaveProfile();
            var delP = new PillButton("Delete", false) { Bounds = new Rectangle(806, 74, 90, 32) };
            delP.Click += (s, e) => DeleteProfile();
            Controls.Add(saveP); Controls.Add(delP);
            FillProfiles(settings.LastProfile);

            var tabs = new SegmentTabs("Picture & sound", "Window", "Phone", "Wi-Fi & app") { Bounds = new Rectangle(372, 118, 592, 40) };
            Controls.Add(tabs);
            var card = new CardPanel("") { Bounds = new Rectangle(372, 168, 592, 298) };
            Controls.Add(card);
            var pages = new Panel[4];
            for (int i = 0; i < 4; i++)
            {
                pages[i] = new Panel { Bounds = new Rectangle(2, 2, 588, 294), BackColor = Theme.Card, Visible = i == 0 };
                card.Controls.Add(pages[i]);
            }
            tabs.SelectedChanged += i => { for (int p = 0; p < 4; p++) pages[p].Visible = p == i; };

            // Picture & sound
            cSize = Pick(pages[0], "Resolution", Sizes, 18, 16);
            cBitrate = Pick(pages[0], "Quality", Bitrates, 210, 16);
            cFps = Pick(pages[0], "Frame rate", Fpss, 402, 16);
            kAudio = Switch(pages[0], "Phone sound on the PC", "Android 11 and newer", 0, -1);
            kAutoQ = Switch(pages[0], "Auto quality", "Adjusts to slow Wi-Fi by itself", 1, -1);
            kCompat = Switch(pages[0], "Compatibility mode", "If the mirror stays black", 0, 0);
            // Window
            kToolbar = Switch(pages[1], "Button bar on the mirror", "Back, Home, record, keyboard and more", 0, -2);
            kOnTop = Switch(pages[1], "Always on top", null, 1, -2);
            kFull = Switch(pages[1], "Start fullscreen", "F11 switches any time", 0, -1);
            kBorderless = Switch(pages[1], "No window border", null, 1, -1);
            kReconnect = Switch(pages[1], "Reconnect if the phone drops", null, 0, 0);
            // Phone
            kScreenOff = Switch(pages[2], "Turn phone screen off", "Saves battery while you mirror", 0, -2);
            kAwake = Switch(pages[2], "Keep phone awake", null, 1, -2);
            kTouches = Switch(pages[2], "Show finger touches", null, 0, -1);
            kPowerOff = Switch(pages[2], "Lock phone when closing", null, 1, -1);
            kNotify = Switch(pages[2], "Phone notifications on PC", null, 0, 0);
            kPad = Switch(pages[2], "PC game controllers", "Xbox / XInput pads show up on the phone", 1, 0);
            kViewOnly = Switch(pages[2], "View only", "The PC can't control the phone", 0, 1);
            // Wi-Fi & app
            var wl = Theme.MakeLabel("Turn off Wi-Fi debugging after", Theme.Ui, Theme.Text); wl.BackColor = Theme.Card; wl.Location = new Point(18, 26); pages[3].Controls.Add(wl);
            cWifiOff = Theme.MakeCombo(false); cWifiOff.Bounds = new Rectangle(250, 22, 150, 28);
            Theme.Fill(cWifiOff, new[] { new Choice("Never", "0"), new Choice("15 min idle", "15"), new Choice("30 min idle", "30"), new Choice("1 hour idle", "60"), new Choice("3 hours idle", "180") }, settings.Get("WifiAutoOff", "30"));
            cWifiOff.SelectedIndexChanged += (s, e) => settings.Set("WifiAutoOff", Theme.Value(cWifiOff));
            tips.SetToolTip(cWifiOff, "Closes the Wi-Fi debugging port on phones you haven't mirrored for this long.");
            pages[3].Controls.Add(cWifiOff);
            kWifiStart = Switch(pages[3], "Reconnect Wi-Fi phones at start", null, 0, -1);
            kWifiStart.Checked = settings.Get("WifiAtStart", "1") == "1";
            kWifiStart.CheckedChanged += (s, e) => settings.Set("WifiAtStart", kWifiStart.Checked ? "1" : "0");
            kUpdates = Switch(pages[3], "Check GitHub for updates", "Once a day, one-click install", 1, -1);
            kUpdates.Checked = settings.Get("UpdateCheck", "1") == "1";
            kUpdates.CheckedChanged += (s, e) => settings.Set("UpdateCheck", kUpdates.Checked ? "1" : "0");
            kStartup = Switch(pages[3], "Start with Windows", "Opens quietly in the tray", 0, 0);
            kStartup.Checked = StartupEnabled();
            kStartup.CheckedChanged += (s, e) => SetStartup(kStartup.Checked);

            foreach (var c in new[] { cSize, cBitrate, cFps })
                c.SelectedIndexChanged += (s, e) => OptionsChanged();
            foreach (var k in new[] { kNotify, kPad, kAudio, kAutoQ, kCompat, kOnTop, kFull, kBorderless, kToolbar, kReconnect, kScreenOff, kAwake, kTouches, kPowerOff, kViewOnly })
                k.CheckedChanged += (s, e) => OptionsChanged();

            // ---- tips card
            var tipCard = new CardPanel("") { Bounds = new Rectangle(372, 476, 592, 92), Cursor = Cursors.Hand };
            Controls.Add(tipCard);
            var bulb = Theme.MakeLabel("", new Font("Segoe MDL2 Assets", 18f), Theme.Amber); bulb.BackColor = Theme.Card; bulb.Location = new Point(18, 28);
            tipCard.Controls.Add(bulb);
            var tipTitle = Theme.MakeLabel("Tip", Theme.UiBold, Theme.Text); tipTitle.BackColor = Theme.Card; tipTitle.Location = new Point(60, 14);
            tipCard.Controls.Add(tipTitle);
            tipLabel = new Label { AutoSize = false, Bounds = new Rectangle(60, 36, 470, 48), Font = Theme.Ui, ForeColor = Theme.Sub, BackColor = Theme.Card, Cursor = Cursors.Hand };
            tipCard.Controls.Add(tipLabel);
            var next = new IconButton("") { Bounds = new Rectangle(540, 28, 38, 36), Font = new Font("Segoe MDL2 Assets", 11f) };
            tips.SetToolTip(next, "Next tip");
            tipCard.Controls.Add(next);
            EventHandler nextTip = (s, e) => { tipIndex = (tipIndex + 1) % Tips.Length; tipLabel.Text = Tips[tipIndex]; };
            next.Click += nextTip; tipLabel.Click += nextTip; tipCard.Click += nextTip;
            tipIndex = new Random().Next(Tips.Length);
            tipLabel.Text = Tips[tipIndex];
            var tipTimer = new Timer { Interval = 12000 };
            tipTimer.Tick += nextTip;
            tipTimer.Start();

            // ---- bottom bar
            statusLabel = Theme.MakeLabel("Ready.", Theme.Small, Theme.Sub);
            statusLabel.AutoSize = false; statusLabel.AutoEllipsis = true;
            statusLabel.Bounds = new Rectangle(18, 588, 480, 20);
            Controls.Add(statusLabel);
            int bx = 964;
            var quit = new PillButton("Quit", false) { Bounds = new Rectangle(bx - 80, 580, 80, 32) };
            tips.SetToolTip(quit, "Stop all mirrors and close MirrorDock (closing the window keeps it in the tray).");
            quit.Click += (s, e) => QuitApp();
            logBtn = new PillButton("Log", false) { Glyph = "", Bounds = new Rectangle(bx - 168, 580, 82, 32) };
            logBtn.Click += (s, e) => ToggleLog();
            var recs = new PillButton("Recordings", false) { Glyph = "", Bounds = new Rectangle(bx - 300, 580, 126, 32) };
            recs.Click += (s, e) => OpenFolder(MirrorSession.VideosDir);
            var shot = new PillButton("Screenshot", false) { Glyph = "", Bounds = new Rectangle(bx - 432, 580, 126, 32) };
            shot.Click += (s, e) => { Device d = Selected(); if (d != null) TakeScreenshot(d.Serial, NameOf(d)); else Status("Pick a phone first.", true); };
            Controls.Add(shot); Controls.Add(recs); Controls.Add(logBtn); Controls.Add(quit);

            logBox = new TextBox();
            logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Theme.LogBg; logBox.ForeColor = Theme.Sub;
            logBox.BorderStyle = BorderStyle.None; logBox.Font = new Font("Consolas", 8.5f);
            logBox.Bounds = new Rectangle(16, 628, 948, 180);
            logBox.Visible = false;
            Controls.Add(logBox);
        }

        // A labelled dropdown placed in a settings page (label above the box).
        ComboBox Pick(Panel page, string label, Choice[] items, int x, int y)
        {
            var l = Theme.MakeLabel(label, Theme.Small, Theme.Sub); l.BackColor = Theme.Card;
            l.Location = new Point(x, y); page.Controls.Add(l);
            var c = Theme.MakeCombo(false); c.Bounds = new Rectangle(x, y + 20, 174, 28);
            Theme.Fill(c, items, items[0].Value);
            page.Controls.Add(c);
            return c;
        }

        // An on/off switch in a two-column grid. Rows count from the bottom area: row 0 = middle, -2 = top.
        CheckBox Switch(Panel page, string text, string hint, int col, int row)
        {
            var k = new ToggleSwitch(text) { Hint = hint, BackColor = Theme.Card };
            k.Bounds = new Rectangle(18 + col * 290, 150 + row * 56, 270, 44);
            page.Controls.Add(k);
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
            var theme = new ToolStripMenuItem("Theme");
            string cur = settings.Get("Theme", "system");
            foreach (var t in new[] { new[] { "system", "Match Windows" }, new[] { "dark", "Dark" }, new[] { "light", "Light" } })
            {
                string mode = t[0];
                theme.DropDownItems.Add(new ToolStripMenuItem(t[1], null, (s, e) => SetTheme(mode)) { Checked = cur == mode });
            }
            m.Items.Add(theme);
            m.Items.Add("Open screenshots folder", null, (s, e) => OpenFolder(MirrorSession.ShotsDir));
            m.Items.Add("Change adb folder...", null, (s, e) => PickAdbFolder());
            m.Items.Add("Check for updates now", null, (s, e) => CheckForUpdates(true));
            m.Items.Add("MirrorDock on GitHub", null, (s, e) => OpenUrl(Links.RepoUrl));
            if (Links.BuyMeACoffee.Length > 0) m.Items.Add("☕ Buy me a coffee", null, (s, e) => OpenUrl(Links.CoffeeUrl));
            m.Items.Add(new ToolStripMenuItem("MirrorDock " + Version + "  |  adb: " + Adb.AdbPath) { Enabled = false });
            return m;
        }

        static void OpenUrl(string url) { try { Process.Start(url); } catch { } }

        void SetTheme(string mode)
        {
            if (settings.Get("Theme", "system") == mode) return;
            settings.Set("Theme", mode);
            if (MessageBox.Show(this, "Restart MirrorDock now to switch the theme?", "MirrorDock", MessageBoxButtons.YesNo) != DialogResult.Yes)
            {
                Status("The new theme applies the next time MirrorDock starts.", false);
                return;
            }
            foreach (var s in sessions.Values.ToList()) s.Stop();
            Process.Start(Application.ExecutablePath, "--restarted");
            quitting = true;
            tray.Visible = false;
            Application.Exit();
        }

        // ------------------------------------------------------------ options
        static readonly Dictionary<string, string> JpegQualityFor = new Dictionary<string, string> { { "3", "55" }, { "8", "75" }, { "12", "82" }, { "16", "88" }, { "24", "95" } };

        Options ReadOptions()
        {
            var o = new Options();
            o.MaxSize = Theme.Value(cSize); o.BitRate = Theme.Value(cBitrate); o.MaxFps = Theme.Value(cFps);
            string q; o.Quality = JpegQualityFor.TryGetValue(o.BitRate, out q) ? q : "75";
            o.Audio = kAudio.Checked; o.AutoQuality = kAutoQ.Checked; o.Notifications = kNotify.Checked; o.Gamepad = kPad.Checked; o.Codec = kCompat.Checked ? "jpeg" : "h264";
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
            kAudio.Checked = o.Audio; kAutoQ.Checked = o.AutoQuality; kNotify.Checked = o.Notifications; kPad.Checked = o.Gamepad; kCompat.Checked = o.Codec == "jpeg";
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
            bool hov = e.Index == hoverDevice;
            Rectangle r = e.Bounds;
            using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, r);
            if (sel || hov)
                using (var path = Theme.Rounded(new RectangleF(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6), 10 * sc))
                {
                    using (var br = new SolidBrush(sel ? Theme.Selected : Theme.Field)) g.FillPath(br, path);
                    if (sel) using (var pen = new Pen(Color.FromArgb(120, Theme.Accent), 1.5f)) g.DrawPath(pen, path);
                }

            MirrorSession s;
            bool live = sessions.TryGetValue(d.Serial, out s) && s.Running;
            MirrorSession ctl;
            bool controlling = sessions.TryGetValue(d.Serial + "#control", out ctl) && ctl.Running;
            bool recon = reconnecting.Contains(d.Serial);
            Color state = live ? (s.Recording ? Theme.Red : Theme.Green) : controlling ? Theme.Accent : recon || !d.Ready ? Theme.Amber : Theme.Sub;

            // phone icon in a soft circle, tinted by state
            float ix = r.X + 12 * sc, iy = r.Y + r.Height / 2f - 19 * sc, isz = 38 * sc;
            using (var br = new SolidBrush(Color.FromArgb(live || controlling ? 60 : 35, state))) g.FillEllipse(br, ix, iy, isz, isz);
            using (var f = new Font("Segoe MDL2 Assets", 13f))
                TextRenderer.DrawText(g, "", f, new Rectangle((int)ix, (int)iy, (int)isz, (int)isz), live || controlling ? state : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            string prof = settings.PhoneProfile(d.Serial);
            string line2 = prof.Length > 0 ? "Profile: " + prof : (d.Model.Length > 0 && NameOf(d) != d.Model ? d.Model : "Ready");
            if (live) line2 = (s.Recording ? "Recording" : "Mirroring") + (s.H264 ? "" : " (compatibility)");
            else if (controlling) line2 = InputCapture.IsTarget(ctl) ? "Controlling with mouse + keyboard" : "Control mode";
            else if (recon) line2 = "Reconnecting...";
            else if (d.State == "unauthorized") line2 = "Tap Allow on the phone";
            else if (d.State == "offline") line2 = "Offline - replug or reconnect";
            int tx = (int)(r.X + 60 * sc);
            TextRenderer.DrawText(g, NameOf(d), Theme.UiBold, new Rectangle(tx, r.Y + (int)(12 * sc), r.Width - tx - (int)(70 * sc), (int)(20 * sc)),
                Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, line2, Theme.Small, new Rectangle(tx, r.Y + (int)(33 * sc), r.Width - tx - (int)(12 * sc), (int)(18 * sc)),
                live || controlling ? state : Theme.Sub, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // connection pill on the right
            string link = d.IsWifi ? "Wi-Fi" : "USB";
            Size ps = TextRenderer.MeasureText(link, Theme.Small);
            var pill = new RectangleF(r.Right - ps.Width - 22 * sc, r.Y + 12 * sc, ps.Width + 12 * sc, 20 * sc);
            using (var path = Theme.Rounded(pill, 10 * sc))
            using (var br = new SolidBrush(d.IsWifi ? Color.FromArgb(45, Theme.Accent) : Theme.Field)) g.FillPath(br, path);
            TextRenderer.DrawText(g, link, Theme.Small, Rectangle.Round(pill), d.IsWifi ? Theme.Accent : Theme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        ContextMenuStrip DeviceMenu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkRenderer() };
            Device d = Selected();
            if (d == null) return m;
            m.Items.Add(IsLive(d.Serial) ? "Stop mirroring" : "Start mirroring", null, (s, e) => ToggleSelected());
            m.Items.Add("Control with mouse && keyboard (no mirror)", null, (s, e) => StartControl(d));
            m.Items.Add("Open an app in its own window...", null, (s, e) => OpenAppWindow(d));
            m.Items.Add("Open a second screen (desktop)", null, (s, e) => OpenSecondScreen(d));
            m.Items.Add(new ToolStripSeparator());
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
            m.Items.Add("Browse phone files...", null, (s, e) => BrowseFiles(d));
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
            startBtn.Text = live ? "Stop mirroring" : d == null ? "Mirror" : "Mirror " + Shorten(NameOf(d), 18);
            startBtn.Glyph = live ? "" : "";
            startBtn.Enabled = d != null && (live || d.Ready);
            startBtn.Fill = live ? (Color?)Theme.Red : null;
            startBtn.FillHover = live ? (Color?)Color.FromArgb(240, 100, 105) : null;
            startBtn.Invalidate();
            bool ready = d != null && d.Ready;
            controlBtn.Enabled = ready; filesBtn.Enabled = ready; moreBtn.Enabled = d != null;
            wifiBtn.Text = d != null && d.IsWifi ? "Turn off Wi-Fi" : "Make wireless";
            tips.SetToolTip(wifiBtn, d != null && d.IsWifi
                ? "Stops Wi-Fi debugging on this phone and closes its network port. Plug in USB to use Wi-Fi again."
                : "Switch the selected USB phone to Wi-Fi. You can unplug the cable afterwards.");
        }

        // Control mode: no mirror, the PC's mouse and keyboard work the phone directly.
        void StartControl(Device d)
        {
            if (!d.Ready) { Status("Connect the phone first.", true); return; }
            MirrorSession cur;
            if (sessions.TryGetValue(d.Serial + "#control", out cur) && cur.Running) { if (cur.Bar != null) cur.Bar.Activate(); return; }
            Options o = OptionsFor(d.Serial).Clone();
            o.Audio = false; o.ScreenOff = false; o.Notifications = false; o.ViewOnly = false;
            StartMirror(d.Serial, NameOf(d), o, "control");
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

        public async void StartMirror(string serial, string name, Options o, string screenSpec = null)
        {
            if (!File.Exists(Adb.AdbPath)) { Status("adb.exe is missing. Use Help > Change adb folder.", true); return; }
            if (screenSpec == null && IsLive(serial)) return;
            var s = new MirrorSession { Serial = serial, Name = name, Opts = o, ScreenSpec = screenSpec };
            s.PhoneNotification += (ss, app, title, text) => BeginInvokeSafe(() => ShowPhoneNotification(ss, app, title, text));
            if (screenSpec == null) wifiLastUsed[serial] = DateTime.Now;
            s.Line += (ss, line) => BeginInvokeSafe(() => Log(ss.Name, line));
            s.Ended += (ss, dropped) => BeginInvokeSafe(() => OnEnded(ss, dropped));
            s.PhoneClipboard += (ss, text) => BeginInvokeSafe(() => SetPcClipboard(text));
            s.RecordingChanged += ss => BeginInvokeSafe(() => deviceList.Invalidate());
            sessions[s.SessionKey] = s;
            ActiveSession = s;
            deviceList.Invalidate();
            UpdateStartButton();
            Status("Starting " + name + "...", false);

            if (s.IsControl) { s.Bar = new ControlBar(s); s.Bar.Show(); }   // control mode: no mirror window
            else { s.Win = new MirrorWindow(this, s); s.Win.Show(); }
            try { await s.Start(screenSpec == null && forceJpeg.Contains(serial)); }
            catch (Exception ex)
            {
                s.LastError = ex.Message;
                s.Stop();
                return;
            }
            if (s.Stopping) return;
            if (s.IsControl)
            {
                Status(name + ": control mode is on. Use the bar at the top of the screen (or Ctrl+Alt+M) to take control.", false);
                deviceList.Invalidate();
                return;
            }
            s.Win.StartReading();   // the window also shows its built-in button bar
            if (o.ScreenOff && !o.ViewOnly) { await Task.Delay(1200); s.ScreenPower(false); }
            Status(name + " is mirroring" + (s.H264 ? "" : " (compatibility mode)") + ".", false);
            deviceList.Invalidate();
            UpdateStartButton();
        }

        void OnEnded(MirrorSession s, bool dropped)
        {
            if (s.Bar != null) { s.Bar.Close(); s.Bar = null; }
            if (ActiveSession == s) ActiveSession = sessions.Values.FirstOrDefault(x => x != s && x.Running);
            if (!s.IsExtraScreen) wifiLastUsed[s.Serial] = DateTime.Now;
            if (s.IsExtraScreen)
            {
                if (s.Win != null && !s.Win.IsDisposed) s.Win.Close();
                MirrorSession cur;
                if (sessions.TryGetValue(s.SessionKey, out cur) && cur == s) sessions.Remove(s.SessionKey);
                if (s.LastError != null && (DateTime.Now - s.Started).TotalSeconds < 8) Status(s.Name + ": " + s.LastError, true); else Status(s.Name + " closed.", false);
                return;
            }
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

        // ------------------------------------------------------------ v1.1: extra screens, files, notifications, Wi-Fi timer
        public MirrorSession ActiveSession;   // the mirror you used last: it gets the game controllers
        readonly Dictionary<string, DateTime> wifiLastUsed = new Dictionary<string, DateTime>();

        void BrowseFiles(Device d)
        {
            if (!d.Ready) { Status("Connect the phone first.", true); return; }
            new FileBrowser(d.Serial, NameOf(d)).Show();
        }

        async Task<int[]> PhoneScreen(string serial)
        {
            int w = 1080, h = 2340, dpi = 420;
            ProcResult r = await Adb.Run("-s " + serial + " shell wm size", 5000);
            var m = System.Text.RegularExpressions.Regex.Matches(r.Out, @"(\d+)x(\d+)");
            if (m.Count > 0) { var last = m[m.Count - 1]; w = int.Parse(last.Groups[1].Value); h = int.Parse(last.Groups[2].Value); }
            r = await Adb.Run("-s " + serial + " shell wm density", 5000);
            var dm = System.Text.RegularExpressions.Regex.Matches(r.Out, @"density: (\d+)");
            if (dm.Count > 0) dpi = int.Parse(dm[dm.Count - 1].Groups[1].Value);
            return new[] { Math.Min(w, h), Math.Max(w, h), dpi };
        }

        async void OpenAppWindow(Device d)
        {
            if (!d.Ready) { Status("Connect the phone first.", true); return; }
            string pkg, label;
            using (var p = new AppPicker(d.Serial, NameOf(d)))
            {
                if (p.ShowDialog(this) != DialogResult.OK) return;
                pkg = p.ChosenPackage; label = p.ChosenName;
            }
            int[] sc = await PhoneScreen(d.Serial);
            Options o = OptionsFor(d.Serial).Clone();
            int max; int.TryParse(o.MaxSize, out max);
            double f = max > 0 && sc[1] > max ? (double)max / sc[1] : 1.0;
            int w = (int)(sc[0] * f) / 8 * 8, h = (int)(sc[1] * f) / 8 * 8, dpi = Math.Max(120, (int)(sc[2] * f));
            o.Audio = false; o.ScreenOff = false; o.Notifications = false; o.Fullscreen = false;
            StartMirror(d.Serial, NameOf(d) + " - " + label, o, "app:" + w + "x" + h + "/" + dpi + ":" + pkg);
        }

        void OpenSecondScreen(Device d)
        {
            if (!d.Ready) { Status("Connect the phone first.", true); return; }
            Options o = OptionsFor(d.Serial).Clone();
            o.Audio = false; o.ScreenOff = false; o.Notifications = false; o.Fullscreen = false;
            StartMirror(d.Serial, NameOf(d) + " - second screen", o, "desktop:1920x1080/240");
            Status("Opening a second screen on " + NameOf(d) + ". Its home screen appears in the new window (works best on Android 13+).", false);
        }

        void ShowPhoneNotification(MirrorSession s, string app, string title, string text)
        {
            if (!s.Opts.Notifications) return;
            string head = string.IsNullOrEmpty(title) ? app : app + ": " + title;
            if (head.Length > 60) head = head.Substring(0, 59) + "…";
            if (text.Length > 200) text = text.Substring(0, 199) + "…";
            Log(s.Name, "Notification - " + app);
            tray.Tag = "mirror:" + s.SessionKey;
            tray.ShowBalloonTip(5000, head, string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.None);
        }

        // Quietly reconnects phones that were made wireless before (if their Wi-Fi debugging is still on).
        async void ReconnectWifiPhones()
        {
            if (settings.Get("WifiAtStart", "1") != "1") return;
            foreach (string ip in settings.RecentIps.Take(4))
            {
                try { await Adb.Run("connect " + ip, 3000); } catch { }
            }
            RefreshDevices();
        }

        async void WifiAutoOff()
        {
            int minutes;
            if (!int.TryParse(settings.Get("WifiAutoOff", "30"), out minutes) || minutes <= 0) return;
            foreach (Device d in devices.Where(x => x.IsWifi && x.Ready).ToList())
            {
                if (sessions.Values.Any(s => s.Serial == d.Serial && s.Running)) { wifiLastUsed[d.Serial] = DateTime.Now; continue; }
                DateTime last;
                if (!wifiLastUsed.TryGetValue(d.Serial, out last)) { wifiLastUsed[d.Serial] = DateTime.Now; continue; }
                if ((DateTime.Now - last).TotalMinutes < minutes) continue;
                wifiLastUsed.Remove(d.Serial);
                bool closed = await Adb.TurnOffWifi(d.Serial);
                Status("Turned off Wi-Fi debugging on " + NameOf(d) + " after " + minutes + " idle minutes" + (closed ? " (port closed)." : "."), false);
                RefreshDevices();
            }
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
                    pendingUpdate = new Updates.Info { Version = settings.Get("UpdateVersion", ""), Url = settings.Get("UpdateUrl", ""),
                        SetupUrl = settings.Get("UpdateSetupUrl", null), SumsUrl = settings.Get("UpdateSumsUrl", null) };
                    ShowUpdate();
                    return;
                }
            }
            Updates.Info info = await Updates.Check();
            settings.Set("LastUpdateCheck", DateTime.Now.ToString("o"));
            settings.Set("UpdateVersion", info == null ? null : info.Version);
            settings.Set("UpdateUrl", info == null ? null : info.Url);
            settings.Set("UpdateSetupUrl", info == null ? null : info.SetupUrl);
            settings.Set("UpdateSumsUrl", info == null ? null : info.SumsUrl);
            pendingUpdate = info;
            if (info != null) { ShowUpdate(); Balloon("MirrorDock " + info.Version + " is out", "Click to update.", null); }
            else if (manual) Status("You have the latest version (" + Version + ").", false);
        }

        Updates.Info pendingUpdate;
        bool updating;

        void ShowUpdate()
        {
            Updates.Info u = pendingUpdate;
            if (u == null || string.IsNullOrEmpty(u.Version) || !Updates.Newer(u.Version, Version)) { updateLink.Visible = false; return; }
            updateLink.Text = "⬆ Update to v" + u.Version;
            updateLink.Visible = true;
            updateLink.Click -= UpdateClick;
            updateLink.Click += UpdateClick;
        }

        static bool IsInstalledCopy
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath).TrimEnd('\\').Equals(InstallInfo.InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        }

        // One click: download the new installer, check its SHA-256, install silently and reopen MirrorDock.
        async void UpdateClick(object sender, EventArgs e)
        {
            Updates.Info u = pendingUpdate;
            if (u == null || updating) return;
            if (!IsInstalledCopy || u.SetupUrl == null) { OpenUrl(string.IsNullOrEmpty(u.Url) ? Links.RepoUrl + "/releases/latest" : u.Url); return; }
            if (MessageBox.Show(this, "Update MirrorDock to version " + u.Version + " now?\r\n\r\nMirrors will close, then MirrorDock reopens by itself in a few seconds.",
                "Update MirrorDock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            updating = true;
            try
            {
                string setup = await Updates.Download(u, p => BeginInvokeSafe(() => Status("Downloading update... " + p + "%", false)));
                Status("Installing update...", false);
                foreach (var s in sessions.Values.ToList()) s.Stop();
                await Task.Delay(700);
                Process.Start(new ProcessStartInfo(setup, "/S --relaunch") { UseShellExecute = true });
                quitting = true;
                tray.Visible = false;
                Application.Exit();
            }
            catch (Exception ex)
            {
                updating = false;
                Status("Update failed: " + ex.Message + " You can download it from GitHub instead.", true);
            }
        }

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
            logBtn.Text = logBox.Visible ? "Hide" : "Log";
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
                if (path.StartsWith("mirror:"))
                {   // a phone notification: bring its mirror forward
                    MirrorSession ms;
                    if (sessions.TryGetValue(path.Substring(7), out ms) && ms.Win != null && !ms.Win.IsDisposed) { ms.Win.Show(); ms.Win.Activate(); }
                    return;
                }
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
