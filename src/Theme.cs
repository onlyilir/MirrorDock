using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MirrorDock
{
    class Choice
    {
        public string Text, Value;
        public Choice(string t, string v) { Text = t; Value = v; }
        public override string ToString() { return Text; }
    }

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(22, 24, 29);
        public static readonly Color Card = Color.FromArgb(31, 35, 43);
        public static readonly Color Field = Color.FromArgb(40, 45, 55);
        public static readonly Color Border = Color.FromArgb(52, 58, 70);
        public static readonly Color Hover = Color.FromArgb(50, 57, 70);
        public static readonly Color Text = Color.FromArgb(230, 232, 236);
        public static readonly Color Sub = Color.FromArgb(150, 158, 172);
        public static readonly Color Accent = Color.FromArgb(61, 139, 253);
        public static readonly Color AccentHover = Color.FromArgb(90, 160, 255);
        public static readonly Color Green = Color.FromArgb(62, 207, 142);
        public static readonly Color Amber = Color.FromArgb(240, 180, 60);
        public static readonly Color Red = Color.FromArgb(229, 72, 77);

        public static readonly Font Ui = new Font("Segoe UI", 9.5f);
        public static readonly Font UiBold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Header = new Font("Segoe UI Semibold", 10.5f);
        public static readonly Font Big = new Font("Segoe UI Semibold", 12f);
        public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 12f);

        public static Button MakeButton(string text, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Hover;
            b.FlatAppearance.MouseDownBackColor = primary ? Accent : Border;
            b.BackColor = primary ? Accent : Field;
            b.ForeColor = primary ? Color.White : Text;
            b.Font = primary ? Big : Ui;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static void SetPrimaryColor(Button b, Color c, Color hover)
        {
            b.BackColor = c;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = c;
        }

        public static Label MakeLabel(string text, Font f, Color c)
        {
            var l = new Label();
            l.Text = text; l.Font = f; l.ForeColor = c; l.BackColor = Color.Transparent;
            l.AutoSize = true;
            return l;
        }

        public static CheckBox MakeCheck(string text)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Font = Ui;
            c.ForeColor = Text;
            c.BackColor = Color.Transparent;
            c.FlatStyle = FlatStyle.Flat;
            c.FlatAppearance.BorderColor = Sub;
            c.FlatAppearance.CheckedBackColor = Accent;
            c.FlatAppearance.MouseOverBackColor = Hover;
            c.AutoSize = true;
            c.Cursor = Cursors.Hand;
            return c;
        }

        public static ComboBox MakeCombo(bool editable)
        {
            var c = new ComboBox();
            c.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Field;
            c.ForeColor = Text;
            c.Font = Ui;
            if (!editable)
            {
                c.DrawMode = DrawMode.OwnerDrawFixed;
                c.DrawItem += (s, e) =>
                {
                    if (e.Index < 0) return;
                    bool sel = (e.State & DrawItemState.Selected) != 0;
                    bool box = (e.State & DrawItemState.ComboBoxEdit) != 0;
                    using (var br = new SolidBrush(sel && !box ? Accent : Field)) e.Graphics.FillRectangle(br, e.Bounds);
                    TextRenderer.DrawText(e.Graphics, c.Items[e.Index].ToString(), c.Font,
                        new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                        sel && !box ? Color.White : Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                };
            }
            return c;
        }

        public static void Fill(ComboBox c, Choice[] items, string value)
        {
            c.Items.Clear();
            c.Items.AddRange(items);
            Select(c, value);
        }

        public static void Select(ComboBox c, string value)
        {
            for (int i = 0; i < c.Items.Count; i++)
                if (((Choice)c.Items[i]).Value == value) { c.SelectedIndex = i; return; }
            if (c.Items.Count > 0) c.SelectedIndex = 0;
        }

        public static string Value(ComboBox c)
        {
            var ch = c.SelectedItem as Choice;
            return ch == null ? "" : ch.Value;
        }

        public static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The app logo: a phone with a mirrored copy behind it, on a blue tile.
        public static Bitmap Logo(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = size;
                using (var tile = Rounded(new RectangleF(0, 0, s - 1, s - 1), s * 0.22f))
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                    Color.FromArgb(70, 150, 255), Color.FromArgb(40, 90, 220)))
                    g.FillPath(br, tile);
                float pw = s * 0.36f, ph = s * 0.58f;
                var back = new RectangleF(s * 0.40f, s * 0.15f, pw, ph);
                var front = new RectangleF(s * 0.22f, s * 0.27f, pw, ph);
                using (var path = Rounded(back, s * 0.07f))
                using (var br = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) g.FillPath(br, path);
                using (var path = Rounded(front, s * 0.07f))
                using (var br = new SolidBrush(Color.White)) g.FillPath(br, path);
                var screen = new RectangleF(front.X + s * 0.04f, front.Y + s * 0.06f, pw - s * 0.08f, ph - s * 0.14f);
                using (var path = Rounded(screen, s * 0.03f))
                using (var br = new LinearGradientBrush(screen, Color.FromArgb(40, 90, 220), Color.FromArgb(80, 170, 255), 90f))
                    g.FillPath(br, path);
            }
            return bmp;
        }

        public static Icon AppIcon()
        {
            try
            {
                Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) return ic;
            }
            catch { }
            using (Bitmap b = Logo(32)) return Icon.FromHandle(b.GetHicon());
        }
    }

    // A rounded "card" panel with a title, used to group settings.
    class CardPanel : Panel
    {
        public string Title;
        public CardPanel(string title)
        {
            Title = title;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Padding = new Padding(0);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96f;
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * scale))
            {
                using (var br = new SolidBrush(Theme.Card)) e.Graphics.FillPath(br, path);
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Title, Theme.Header, new Point((int)(14 * scale), (int)(10 * scale)), Theme.Text, TextFormatFlags.NoPrefix);
        }
    }

    // A small text-entry dialog in the dark theme.
    static class Prompt
    {
        public static string[] Ask(IWin32Window owner, string title, string[] labels, string[] defaults)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = Theme.Bg; f.ForeColor = Theme.Text; f.Font = Theme.Ui;
                f.AutoScaleDimensions = new SizeF(96f, 96f);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.SuspendLayout();
                int y = 14;
                var boxes = new TextBox[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    var l = Theme.MakeLabel(labels[i], Theme.Ui, Theme.Sub);
                    l.Location = new Point(14, y); f.Controls.Add(l); y += 22;
                    var t = new TextBox();
                    t.BackColor = Theme.Field; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle;
                    t.Location = new Point(14, y); t.Width = 330;
                    t.Text = defaults != null && i < defaults.Length ? defaults[i] : "";
                    f.Controls.Add(t); boxes[i] = t; y += 36;
                }
                var ok = Theme.MakeButton("OK", true);
                ok.Font = Theme.UiBold; ok.Size = new Size(100, 32); ok.Location = new Point(244, y + 4);
                ok.DialogResult = DialogResult.OK;
                var cancel = Theme.MakeButton("Cancel", false);
                cancel.Size = new Size(100, 32); cancel.Location = new Point(136, y + 4);
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.ClientSize = new Size(358, y + 50);
                f.ResumeLayout(false);
                f.HandleCreated += (s, e) => Native.DarkTitleBar(f.Handle);
                if (f.ShowDialog(owner) != DialogResult.OK) return null;
                var res = new string[boxes.Length];
                for (int i = 0; i < boxes.Length; i++) res[i] = boxes[i].Text.Trim();
                return res;
            }
        }
    }
}
