using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MirrorDock
{
    // Browse the phone's storage: open, copy to PC, send from PC (buttons or drag & drop), new folder, delete.
    class FileBrowser : Form
    {
        readonly string serial;
        string path = "/sdcard";
        readonly ListView view;
        readonly TextBox pathBox;
        readonly Label status;
        readonly ImageList icons = new ImageList();
        bool busy;

        class Entry { public string Name; public bool Dir; public long Size; public DateTime Time; }

        static readonly string[][] Places = {
            new[] { "Phone storage", "/sdcard" }, new[] { "Camera", "/sdcard/DCIM/Camera" }, new[] { "Screenshots", "/sdcard/DCIM/Screenshots" },
            new[] { "Download", "/sdcard/Download" }, new[] { "Pictures", "/sdcard/Pictures" }, new[] { "Documents", "/sdcard/Documents" },
            new[] { "Movies", "/sdcard/Movies" }, new[] { "Music", "/sdcard/Music" } };

        public static string PcFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MirrorDock"); }
        }

        public FileBrowser(string serial, string phoneName)
        {
            this.serial = serial;
            Text = "Files on " + phoneName + " - MirrorDock";
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Ui;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(700, 420);
            SuspendLayout();
            ClientSize = new Size(900, 560);

            var places = new ListBox { BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Ui,
                Bounds = new Rectangle(12, 52, 160, 452), IntegralHeight = false, ItemHeight = 26, DrawMode = DrawMode.OwnerDrawFixed,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left };
            foreach (var p in Places) places.Items.Add(new Choice(p[0], p[1]));
            places.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(sel ? Theme.Hover : Theme.Card)) e.Graphics.FillRectangle(br, e.Bounds);
                TextRenderer.DrawText(e.Graphics, places.Items[e.Index].ToString(), Theme.Ui, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter);
            };
            places.Click += (s, e) => { var c = places.SelectedItem as Choice; if (c != null) Go(c.Value); };
            Controls.Add(places);

            var up = Theme.MakeButton("↑ Up", false); up.Bounds = new Rectangle(12, 12, 70, 30);
            up.Click += (s, e) => { if (path != "/" && path.Length > 1) Go(path.Substring(0, Math.Max(1, path.TrimEnd('/').LastIndexOf('/')))); };
            Controls.Add(up);
            pathBox = new TextBox { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Ui,
                Bounds = new Rectangle(88, 15, 420, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            pathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(pathBox.Text.Trim()); } };
            Controls.Add(pathBox);
            int bx = 516;
            foreach (var b in new[] {
                Tuple.Create("Copy to PC", (Action)CopyToPc), Tuple.Create("Send files...", (Action)SendFromPc),
                Tuple.Create("New folder", (Action)NewFolder), Tuple.Create("Delete", (Action)DeleteSelected) })
            {
                var btn = Theme.MakeButton(b.Item1, false);
                btn.Bounds = new Rectangle(bx, 12, b.Item1.Length > 9 ? 90 : 78, 30);
                btn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                Action act = b.Item2;
                btn.Click += (s, e) => act();
                Controls.Add(btn);
                bx += btn.Width + 6;
            }

            view = new ListView { View = View.Details, FullRowSelect = true, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
                Bounds = new Rectangle(180, 52, 708, 452), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowDrop = true, HideSelection = false, SmallImageList = icons };
            view.OwnerDraw = true;
            view.DrawColumnHeader += (s, e) =>
            {
                using (var br = new SolidBrush(Theme.Field)) e.Graphics.FillRectangle(br, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.UiBold, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Theme.Sub,
                    TextFormatFlags.VerticalCenter | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left));
            };
            view.DrawItem += (s, e) => e.DrawDefault = true;
            view.DrawSubItem += (s, e) => e.DrawDefault = true;
            view.Columns.Add("Name", 400); view.Columns.Add("Size", 100, HorizontalAlignment.Right); view.Columns.Add("Modified", 180);
            view.Resize += (s, e) => { if (view.Columns.Count == 3) view.Columns[2].Width = Math.Max(120, view.ClientSize.Width - view.Columns[0].Width - view.Columns[1].Width); };
            view.DoubleClick += (s, e) => OpenSelected();
            view.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) OpenSelected();
                else if (e.KeyCode == Keys.Delete) DeleteSelected();
                else if (e.KeyCode == Keys.Back) up.PerformClick();
                else if (e.KeyCode == Keys.F5) Go(path);
                else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in view.Items) i.Selected = true;
            };
            view.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            view.DragDrop += (s, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) Send(f); };
            view.ItemDrag += (s, e) => DragOut();
            Controls.Add(view);
            icons.ColorDepth = ColorDepth.Depth32Bit; icons.ImageSize = new Size(16, 16);
            icons.Images.Add("dir", Glyph("", Theme.Amber)); icons.Images.Add("file", Glyph("", Theme.Sub));

            status = Theme.MakeLabel("", Theme.Small, Theme.Sub);
            status.AutoSize = false; status.AutoEllipsis = true;
            status.Bounds = new Rectangle(12, 514, 876, 34); status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(status);
            ResumeLayout(false);
            HandleCreated += (s, e) => Native.DarkTitleBar(Handle);
            Shown += (s, e) => Go(path);
        }

        static Bitmap Glyph(string g, Color c)
        {
            var b = new Bitmap(16, 16);
            using (var gr = Graphics.FromImage(b))
            using (var f = new Font("Segoe MDL2 Assets", 10f))
                TextRenderer.DrawText(gr, g, f, new Rectangle(0, 0, 16, 16), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return b;
        }

        // Quotes a phone path for the adb shell.
        static string Q(string p) { return "'" + p.Replace("'", "'\\''") + "'"; }
        static string Join(string dir, string name) { return dir.TrimEnd('/') + "/" + name; }

        async void Go(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (busy) return;
            busy = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                // stat prints one line per entry: type|size|mtime|name (toybox stat, Android 7+)
                ProcResult r = await Adb.Run("-s " + serial + " shell \"cd " + Q(p).Replace("\"", "\\\"") + " && stat -L -c '%F|%s|%Y|%n' -- * 2>/dev/null\"", 30000);
                if (r.Code != 0 && r.Out.Trim().Length == 0) { Say(r.Err.Contains("No such") ? "That folder doesn't exist." : "Couldn't read the folder: " + (r.All.Length > 0 ? r.All : "is the phone still connected?"), true); return; }
                var entries = new List<Entry>();
                foreach (string raw in r.Out.Split('\n'))
                {
                    string[] f = raw.TrimEnd('\r').Split(new[] { '|' }, 4);
                    if (f.Length < 4 || f[3] == "*") continue;
                    long size, t;
                    long.TryParse(f[1], out size); long.TryParse(f[2], out t);
                    entries.Add(new Entry { Name = f[3], Dir = f[0].Contains("directory"), Size = size, Time = new DateTime(1970, 1, 1).AddSeconds(t).ToLocalTime() });
                }
                path = p;
                pathBox.Text = p;
                view.BeginUpdate();
                view.Items.Clear();
                foreach (Entry e in entries.OrderBy(x => !x.Dir).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var it = new ListViewItem(e.Name, e.Dir ? "dir" : "file") { Tag = e };
                    it.SubItems.Add(e.Dir ? "" : Size(e.Size));
                    it.SubItems.Add(e.Time.ToString("yyyy-MM-dd HH:mm"));
                    view.Items.Add(it);
                }
                view.EndUpdate();
                Say(entries.Count + " items. Double-click to open, drag files in to send, drag out to copy to the PC.", false);
            }
            catch (Exception ex) { Say(ex.Message, true); }
            finally { busy = false; Cursor = Cursors.Default; }
        }

        static string Size(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1048576) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1073741824) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }

        List<Entry> Selected() { return view.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag).ToList(); }

        void Say(string t, bool warn) { status.Text = t; status.ForeColor = warn ? Theme.Amber : Theme.Sub; }

        async void OpenSelected()
        {
            var sel = Selected();
            if (sel.Count != 1) return;
            Entry e = sel[0];
            if (e.Dir) { Go(Join(path, e.Name)); return; }
            // Files open on the PC with their usual app (copied to a temp folder first).
            string tmp = Path.Combine(Path.GetTempPath(), "MirrorDock");
            Directory.CreateDirectory(tmp);
            string local = Path.Combine(tmp, e.Name);
            Say("Opening " + e.Name + "...", false);
            ProcResult r = await Adb.Run("-s " + serial + " pull \"" + Join(path, e.Name) + "\" \"" + local + "\"", 600000);
            if (r.Code != 0) { Say("Couldn't open " + e.Name + ": " + r.All, true); return; }
            try { Process.Start(local); Say("Opened " + e.Name + ".", false); } catch (Exception ex) { Say(ex.Message, true); }
        }

        async void CopyToPc()
        {
            var sel = Selected();
            if (sel.Count == 0) { Say("Select files or folders first.", true); return; }
            Directory.CreateDirectory(PcFolder);
            int done = 0;
            foreach (Entry e in sel)
            {
                Say("Copying " + e.Name + " (" + (done + 1) + " of " + sel.Count + ")...", false);
                ProcResult r = await Adb.Run("-s " + serial + " pull \"" + Join(path, e.Name) + "\" \"" + PcFolder + "\"", 3600000);
                if (r.Code != 0) { Say("Couldn't copy " + e.Name + ": " + r.All, true); return; }
                done++;
            }
            Say("Copied " + done + " item" + (done == 1 ? "" : "s") + " to " + PcFolder, false);
            try { Process.Start("explorer.exe", "\"" + PcFolder + "\""); } catch { }
        }

        // Dragging items out of the list: copy them to a temp folder, then hand Windows the files.
        void DragOut()
        {
            var sel = Selected().Where(x => !x.Dir).ToList();
            if (sel.Count == 0) return;
            string tmp = Path.Combine(Path.GetTempPath(), "MirrorDock", "drag");
            Directory.CreateDirectory(tmp);
            var files = new List<string>();
            Cursor = Cursors.WaitCursor;
            foreach (Entry e in sel)
            {
                string local = Path.Combine(tmp, e.Name);
                ProcResult r = Adb.Run("-s " + serial + " pull \"" + Join(path, e.Name) + "\" \"" + local + "\"", 600000).Result;
                if (r.Code == 0) files.Add(local);
            }
            Cursor = Cursors.Default;
            if (files.Count > 0) view.DoDragDrop(new DataObject(DataFormats.FileDrop, files.ToArray()), DragDropEffects.Copy);
        }

        void SendFromPc()
        {
            using (var f = new OpenFileDialog { Multiselect = true, Title = "Send files to " + path })
                if (f.ShowDialog(this) == DialogResult.OK) Send(f.FileNames);
        }

        async void Send(string[] files)
        {
            int done = 0;
            foreach (string f in files)
            {
                Say("Sending " + Path.GetFileName(f) + " (" + (done + 1) + " of " + files.Length + ")...", false);
                ProcResult r = await Adb.Run("-s " + serial + " push \"" + f.TrimEnd('\\') + "\" \"" + path + "/\"", 3600000);
                if (r.Code != 0) { Say("Couldn't send " + Path.GetFileName(f) + ": " + r.All, true); return; }
                done++;
            }
            Go(path);
            Say("Sent " + done + " item" + (done == 1 ? "" : "s") + " to " + path, false);
        }

        async void NewFolder()
        {
            string[] r = Prompt.Ask(this, "New folder", new[] { "Folder name" }, new[] { "New folder" });
            if (r == null || r[0].Length == 0) return;
            await Adb.Run("-s " + serial + " shell \"mkdir -p " + Q(Join(path, r[0])).Replace("\"", "\\\"") + "\"");
            Go(path);
        }

        async void DeleteSelected()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            string what = sel.Count == 1 ? "\"" + sel[0].Name + "\"" : sel.Count + " items";
            if (MessageBox.Show(this, "Delete " + what + " from the phone?\r\n\r\nThis can't be undone.", "Delete from phone",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (Entry e in sel)
                await Adb.Run("-s " + serial + " shell \"rm -rf -- " + Q(Join(path, e.Name)).Replace("\"", "\\\"") + "\"");
            Say("Deleted " + what + ".", false);
            Go(path);
        }
    }
}
