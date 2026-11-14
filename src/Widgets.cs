using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MirrorDock
{
    // Small custom controls with smooth hover/press animations, used across the app.

    // Fades between two colors over a few frames; shared by the buttons below.
    class Fader
    {
        readonly Control owner;
        readonly Timer timer = new Timer { Interval = 15 };
        float value, target;
        public Fader(Control c) { owner = c; timer.Tick += (s, e) => Step(); }
        public float Value { get { return value; } }
        public void To(float t) { target = t; if (!timer.Enabled) timer.Start(); }
        void Step()
        {
            value += (target - value) * 0.35f;
            if (Math.Abs(target - value) < 0.02f) { value = target; timer.Stop(); }
            owner.Invalidate();
        }
        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    // Rounded button with text, primary (accent) or secondary style, hover fade and a press dip.
    class PillButton : Control
    {
        readonly Fader hover;
        bool pressed;
        public bool Primary;
        public Color? Fill, FillHover, TextColor;
        public string Glyph;   // optional Segoe MDL2 icon before the text

        public PillButton(string text, bool primary)
        {
            Text = text; Primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Font = primary ? Theme.Big : Theme.Ui;
            Cursor = Cursors.Hand;
            hover = new Fader(this);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); pressed = false; }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color baseC = Fill ?? (Primary ? Theme.Accent : Theme.Field);
            Color hoverC = FillHover ?? (Primary ? Theme.AccentHover : Theme.Hover);
            Color c = Enabled ? Fader.Mix(baseC, hoverC, hover.Value) : Fader.Mix(baseC, Theme.Bg, 0.5f);
            if (pressed) c = Fader.Mix(c, Color.Black, 0.12f);
            float r = Math.Min(Height / 2f, 10f * g.DpiX / 96f);
            var rect = new RectangleF(0.5f, pressed ? 1.5f : 0.5f, Width - 1.5f, Height - (pressed ? 2.5f : 1.5f));
            using (var path = Theme.Rounded(rect, r))
            {
                using (var br = new SolidBrush(c)) g.FillPath(br, path);
                if (!Primary && Fill == null) using (var pen = new Pen(Fader.Mix(Theme.Border, Theme.Accent, hover.Value * 0.6f))) g.DrawPath(pen, path);
            }
            Color tc = TextColor ?? (Primary ? Color.White : Theme.Text);
            if (!Enabled) tc = Fader.Mix(tc, c, 0.5f);
            string label = Text;
            Rectangle tr = Rectangle.Round(rect);
            if (!string.IsNullOrEmpty(Glyph))
            {
                Size ts = TextRenderer.MeasureText(label, Font);
                int gw = (int)(20 * g.DpiX / 96f);
                int total = gw + ts.Width;
                int x = (Width - total) / 2;
                using (var gf = new Font("Segoe MDL2 Assets", Font.Size * 0.95f))
                    TextRenderer.DrawText(g, Glyph, gf, new Rectangle(x, tr.Y, gw, tr.Height), tc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, label, Font, new Rectangle(x + gw, tr.Y, ts.Width + 4, tr.Height), tc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
            else TextRenderer.DrawText(g, label, Font, tr, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // An on/off switch with a sliding knob. It is still a CheckBox (Checked / CheckedChanged), just drawn differently.
    class ToggleSwitch : CheckBox
    {
        readonly Fader knob, hover;
        public string Hint;   // small grey line under the label

        public ToggleSwitch(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.CheckedBackColor = Color.Transparent;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            Font = Theme.Ui;
            Cursor = Cursors.Hand;
            knob = new Fader(this);
            hover = new Fader(this);
        }

        protected override bool ShowFocusCues { get { return false; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }   // OnPaint fills everything

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); knob.To(Checked ? 1 : 0); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); knob.To(Checked ? 1 : 0); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Parent != null ? Parent.BackColor : Theme.Card;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            float s = g.DpiX / 96f;
            float tw = 36 * s, th = 20 * s, ty = (Height - th) / 2f;
            float tx = Width - tw - 2 * s;
            float k = knob.Value;
            Color off = Fader.Mix(Theme.Border, Theme.Sub, hover.Value * 0.4f);
            Color on = Fader.Mix(Theme.Accent, Theme.AccentHover, hover.Value);
            Color track = Enabled ? Fader.Mix(off, on, k) : Fader.Mix(Theme.Border, bg, 0.5f);
            using (var path = Theme.Rounded(new RectangleF(tx, ty, tw, th), th / 2))
            using (var br = new SolidBrush(track)) g.FillPath(br, path);
            float d = th - 6 * s;
            float kx = tx + 3 * s + (tw - d - 6 * s) * k;
            using (var br = new SolidBrush(Enabled ? Color.White : Theme.Field)) g.FillEllipse(br, kx, ty + 3 * s, d, d);
            Color tc = Enabled ? Theme.Text : Theme.Sub;
            if (string.IsNullOrEmpty(Hint))
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, (int)(tx - 8 * s), Height), tc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            else
            {
                int half = Height / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, (int)(tx - 8 * s), half + 2), tc, TextFormatFlags.Bottom | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Hint, Theme.Small, new Rectangle(0, half + 1, (int)(tx - 8 * s), half), Theme.Sub, TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    // A row of pill-shaped tabs; the selected one slides into place.
    class SegmentTabs : Control
    {
        readonly string[] items;
        int selected, hoverIndex = -1;
        float slide;
        readonly Timer anim = new Timer { Interval = 15 };
        public event Action<int> SelectedChanged;

        public SegmentTabs(params string[] items)
        {
            this.items = items;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.UiBold;
            Cursor = Cursors.Hand;
            anim.Tick += (s, e) =>
            {
                slide += (selected - slide) * 0.35f;
                if (Math.Abs(selected - slide) < 0.01f) { slide = selected; anim.Stop(); }
                Invalidate();
            };
        }

        public int Selected
        {
            get { return selected; }
            set { if (value == selected) return; selected = value; anim.Start(); if (SelectedChanged != null) SelectedChanged(value); }
        }

        float ItemWidth { get { return (float)(Width - 8) / items.Length; } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = (int)((e.X - 4) / ItemWidth); if (i != hoverIndex) { hoverIndex = i; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIndex = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int i = (int)((e.X - 4) / ItemWidth); if (i >= 0 && i < items.Length) Selected = i; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Bg)) g.FillRectangle(b, ClientRectangle);
            float r = (Height - 1) / 2f;
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), r))
            using (var br = new SolidBrush(Theme.Field)) g.FillPath(br, path);
            float w = ItemWidth;
            using (var path = Theme.Rounded(new RectangleF(4 + slide * w, 4, w, Height - 8), (Height - 8) / 2f))
            using (var br = new SolidBrush(Theme.Accent)) g.FillPath(br, path);
            for (int i = 0; i < items.Length; i++)
            {
                var rc = new Rectangle((int)(4 + i * w), 0, (int)w, Height);
                Color c = i == selected ? Color.White : i == hoverIndex ? Theme.Text : Theme.Sub;
                TextRenderer.DrawText(g, items[i], Font, rc, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    // Square icon button (Segoe MDL2 glyph) for button bars; can stay "on" (toggled) with an accent tint.
    class IconButton : Control
    {
        readonly Fader hover;
        bool pressed, toggled;
        public Color OnColor = Theme.Accent;

        public IconButton(string glyph)
        {
            Text = glyph;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Glyph;
            Cursor = Cursors.Hand;
            hover = new Fader(this);
        }

        public bool Toggled { get { return toggled; } set { toggled = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0); pressed = false; }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent == null || Parent is CardPanel ? Theme.Card : Parent.BackColor)) g.FillRectangle(bg, ClientRectangle);
            Color fill = toggled ? Fader.Mix(Color.FromArgb(60, OnColor), Color.FromArgb(95, OnColor), hover.Value)
                                 : Color.FromArgb((int)(hover.Value * 255), Theme.Hover);
            if (pressed) fill = Color.FromArgb(150, Theme.Accent);
            float r = 8f * g.DpiX / 96f;
            using (var path = Theme.Rounded(new RectangleF(2, 2, Width - 4, Height - 4), r))
            using (var br = new SolidBrush(fill)) g.FillPath(br, path);
            Color tc = toggled ? OnColor : Fader.Mix(Theme.Sub, Theme.Text, hover.Value);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
