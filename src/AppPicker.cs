using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MirrorDock
{
    // Lists the phone's apps so one can be opened in its own window.
    class AppPicker : Form
    {
        readonly ListBox list;
        readonly TextBox search;
        readonly Label status;
        List<KeyValuePair<string, string>> apps = new List<KeyValuePair<string, string>>();
        public string ChosenPackage, ChosenName;

        public AppPicker(string serial, string phoneName)
        {
            Text = "Open an app in its own window - " + phoneName;
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Ui;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            ClientSize = new Size(420, 520);
            var hint = Theme.MakeLabel("The app opens on its own screen, so you can keep using the phone normally.", Theme.Small, Theme.Sub);
            hint.Location = new Point(14, 12); Controls.Add(hint);
            search = new TextBox { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Big, Bounds = new Rectangle(14, 36, 392, 28) };
            search.TextChanged += (s, e) => Fill();
            search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.SelectedIndex = 0; } if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Pick(); } };
            Controls.Add(search);
            list = new ListBox { BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Ui, Bounds = new Rectangle(14, 74, 392, 380), IntegralHeight = false };
            list.DoubleClick += (s, e) => Pick();
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) Pick(); };
            Controls.Add(list);
            status = Theme.MakeLabel("Loading apps...", Theme.Small, Theme.Sub); status.Location = new Point(14, 470); Controls.Add(status);
            var open = Theme.MakeButton("Open", true); open.Font = Theme.UiBold; open.Bounds = new Rectangle(296, 470, 110, 36);
            open.Click += (s, e) => Pick();
            Controls.Add(open);
            var cancel = Theme.MakeButton("Cancel", false); cancel.Bounds = new Rectangle(180, 470, 110, 36);
            cancel.Click += (s, e) => Close();
            Controls.Add(cancel);
            CancelButton = cancel;
            ResumeLayout(false);
            HandleCreated += (s, e) => Native.DarkTitleBar(Handle);
            Shown += async (s, e) => { search.Focus(); await LoadApps(serial); };
        }

        async Task LoadApps(string serial)
        {
            try
            {
                await MirrorSession.PushEngine(serial);
                ProcResult r = await Adb.Run("-s " + serial + " shell CLASSPATH=/data/local/tmp/mirrordock.jar app_process / com.mirrordock.Server --list-apps", 30000);
                apps = r.Out.Split('\n').Where(l => l.StartsWith("APP|")).Select(l =>
                {
                    string[] p = l.Trim().Split(new[] { '|' }, 3);
                    return new KeyValuePair<string, string>(p[1], p.Length > 2 ? p[2] : p[1]);
                }).ToList();
                status.Text = apps.Count + " apps. Double-click one to open it.";
                Fill();
            }
            catch (Exception ex) { status.Text = "Couldn't list apps: " + ex.Message; }
        }

        void Fill()
        {
            string q = search.Text.Trim().ToLowerInvariant();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var a in apps.Where(a => q.Length == 0 || a.Value.ToLowerInvariant().Contains(q) || a.Key.Contains(q)))
                list.Items.Add(new Choice(a.Value, a.Key));
            list.EndUpdate();
        }

        void Pick()
        {
            var c = list.SelectedItem as Choice;
            if (c == null && list.Items.Count == 1) c = (Choice)list.Items[0];
            if (c == null) return;
            ChosenPackage = c.Value; ChosenName = c.Text;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
