using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MirrorDock
{
    // First-run guide: walks through Developer options and USB debugging, and checks the connection live.
    class SetupGuide : Form
    {
        readonly Label status, statusDetail;
        readonly Panel statusDot;
        readonly Timer poll = new Timer();
        readonly Button doneBtn;
        bool checking;

        public SetupGuide()
        {
            Text = "Set up your phone - MirrorDock";
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Ui;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            ClientSize = new Size(560, 560);

            var title = Theme.MakeLabel("Let's connect your phone", Theme.Title, Theme.Text); title.Location = new Point(22, 16); Controls.Add(title);
            var sub = Theme.MakeLabel("This takes about a minute and you only do it once.", Theme.Ui, Theme.Sub); sub.Location = new Point(24, 50); Controls.Add(sub);

            string[][] steps = {
                new[] { "Turn on Developer options", "Settings > About phone > Software information > tap Build number 7 times.\r\n(On Pixel and most others: Settings > About phone > Build number.)" },
                new[] { "Turn on USB debugging", "Settings > Developer options > USB debugging: On.\r\nSamsung: Auto Blocker must be off (Settings > Security), it blocks USB debugging." },
                new[] { "Plug the phone into this PC", "Use a cable that carries data, not a charge-only one.\r\nPlug into the PC directly, not a hub." },
                new[] { "Unlock the phone and tap Allow", "A popup asks \"Allow USB debugging?\".\r\nTick \"Always allow from this computer\" and tap Allow." } };
            int y = 84;
            for (int i = 0; i < steps.Length; i++)
            {
                var card = new CardPanel("") { Bounds = new Rectangle(20, y, 520, 76) };
                Controls.Add(card);
                var num = Theme.MakeLabel((i + 1).ToString(), Theme.Title, Theme.Accent); num.BackColor = Theme.Card; num.Location = new Point(14, 18); card.Controls.Add(num);
                var h = Theme.MakeLabel(steps[i][0], Theme.Header, Theme.Text); h.BackColor = Theme.Card; h.Location = new Point(48, 10); card.Controls.Add(h);
                var d = Theme.MakeLabel(steps[i][1], Theme.Small, Theme.Sub); d.BackColor = Theme.Card; d.Location = new Point(49, 34); card.Controls.Add(d);
                y += 86;
            }

            var statusCard = new CardPanel("") { Bounds = new Rectangle(20, y + 4, 520, 84) };
            Controls.Add(statusCard);
            statusDot = new Panel { BackColor = Theme.Sub, Bounds = new Rectangle(18, 22, 12, 12) };
            statusCard.Controls.Add(statusDot);
            status = Theme.MakeLabel("Checking...", Theme.Header, Theme.Text); status.BackColor = Theme.Card; status.Location = new Point(40, 14); statusCard.Controls.Add(status);
            statusDetail = Theme.MakeLabel("", Theme.Small, Theme.Sub);
            statusDetail.BackColor = Theme.Card; statusDetail.AutoSize = false; statusDetail.Bounds = new Rectangle(41, 40, 470, 40);
            statusCard.Controls.Add(statusDetail);

            doneBtn = Theme.MakeButton("Close", true); doneBtn.Font = Theme.UiBold;
            doneBtn.Bounds = new Rectangle(400, y + 100, 140, 36);
            doneBtn.Click += (s, e) => Close();
            Controls.Add(doneBtn);
            var help = new LinkLabel { Text = "Still stuck? Ask on GitHub", AutoSize = true, Location = new Point(22, y + 110), Font = Theme.Small,
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover, BackColor = Color.Transparent };
            help.Click += (s, e) => { try { Process.Start(Links.RepoUrl + "/issues"); } catch { } };
            Controls.Add(help);
            ResumeLayout(false);

            HandleCreated += (s, e) => Native.DarkTitleBar(Handle);
            poll.Interval = 2000;
            poll.Tick += (s, e) => Check();
            Shown += (s, e) => { Check(); poll.Start(); };
            FormClosed += (s, e) => poll.Stop();
        }

        async void Check()
        {
            if (checking) return;
            checking = true;
            try
            {
                List<Device> list = await Adb.ListDevices();
                if (list.Any(d => d.Ready))
                {
                    Show(Theme.Green, "Connected: " + list.First(d => d.Ready).Model, "All set. Close this and press Start mirroring.");
                    doneBtn.Text = "Start using it";
                    return;
                }
                if (list.Any(d => d.State == "unauthorized")) { Show(Theme.Amber, "Waiting for you to tap Allow", "Unlock the phone. If there's no popup, unplug and plug the cable back in."); return; }
                if (list.Any(d => d.State == "offline")) { Show(Theme.Amber, "Phone is offline", "Unplug the cable, wait 2 seconds, plug it back in."); return; }

                string usb = await Task.Run(() => UsbPhoneState());
                if (usb == "driver") Show(Theme.Amber, "Windows needs the phone's USB driver",
                    "Samsung: install \"Samsung USB Driver for Mobile Phones\" from Samsung's developer site. Others: Google USB Driver.");
                else if (usb == "phone") Show(Theme.Amber, "Phone found, but USB debugging is off", "Windows sees the phone for files only. Do steps 1 and 2, then replug.");
                else Show(Theme.Red, "No phone found on USB", "Try another cable (many only charge) or another USB port on the back of the PC.");
            }
            finally { checking = false; }
        }

        void Show(Color c, string head, string detail)
        {
            statusDot.BackColor = c;
            status.Text = head;
            statusDetail.Text = detail;
        }

        // "phone" = a phone is plugged in for files (MTP) only, "driver" = an ADB device is missing its driver, "" = nothing.
        static string UsbPhoneState()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name, PNPClass, ConfigManagerErrorCode, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'"))
                {
                    bool phone = false;
                    foreach (ManagementObject o in q.Get())
                    {
                        string name = (o["Name"] as string ?? "").ToLowerInvariant();
                        string cls = (o["PNPClass"] as string ?? "");
                        uint err = o["ConfigManagerErrorCode"] is uint ? (uint)o["ConfigManagerErrorCode"] : 0;
                        if ((name.Contains("adb") || name.Contains("android")) && err != 0) return "driver";
                        if (cls == "WPD" || name.Contains("galaxy") || name.Contains("android") || name.Contains("mtp") || name.Contains("pixel")) phone = true;
                    }
                    return phone ? "phone" : "";
                }
            }
            catch { return ""; }
        }
    }
}
