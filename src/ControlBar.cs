using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MirrorDock
{
    // Control mode's only window: a small bar at the top of the screen. No mirror, no video, no lag:
    // you look at the phone itself and use the PC's mouse and keyboard on it.
    class ControlBar : Form
    {
        readonly MirrorSession session;
        readonly Label title, hint;
        readonly Button take, stop;
        readonly Panel dot;
        const int HotkeyId = 0x4D44;

        public ControlBar(MirrorSession s)
        {
            session = s;
            Text = "Control mode - " + s.Name;
            Icon = Theme.AppIcon();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            ClientSize = new Size(680, 64);
            dot = new Panel { Bounds = new Rectangle(16, 26, 12, 12), BackColor = Theme.Sub };
            dot.Paint += (o, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(Theme.Card); using (var b = new SolidBrush(dotColor)) e.Graphics.FillEllipse(b, 0, 0, 11, 11); };
            title = Theme.MakeLabel("", Theme.Header, Theme.Text); title.Location = new Point(38, 10); title.BackColor = Theme.Card;
            title.AutoSize = false; title.AutoEllipsis = true; title.Size = new Size(420, 24);
            hint = Theme.MakeLabel("", Theme.Small, Theme.Sub); hint.Location = new Point(39, 36); hint.BackColor = Theme.Card;
            hint.AutoSize = false; hint.AutoEllipsis = true; hint.Size = new Size(420, 20);
            take = Theme.MakeButton("Take control", true); take.Font = Theme.UiBold; take.Bounds = new Rectangle(472, 14, 136, 36);
            take.Click += (o, e) => Toggle();
            stop = Theme.MakeButton("✕", false); stop.Font = Theme.Header; stop.Bounds = new Rectangle(616, 14, 52, 36);
            stop.Click += (o, e) => session.Stop();
            new ToolTip().SetToolTip(stop, "End control mode");
            foreach (Control c in new Control[] { dot, title, hint, take, stop }) Controls.Add(c);
            ResumeLayout(false);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 12);
            // Drag the bar anywhere by its background.
            MouseDown += Drag; title.MouseDown += Drag; hint.MouseDown += Drag;
            InputCapture.Changed += Refresh2;
            FormClosed += (o, e) => { InputCapture.Changed -= Refresh2; if (InputCapture.IsTarget(session)) InputCapture.Stop(); UnregisterHotKey(Handle, HotkeyId); };
            HandleCreated += (o, e) => RegisterHotKey(Handle, HotkeyId, 0x0003 | 0x4000, (uint)Keys.M);   // Ctrl+Alt+M, no repeat
            Shown += (o, e) => Refresh2();
        }

        Color dotColor = Theme.Sub;

        void Refresh2()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)Refresh2); return; }
            bool on = InputCapture.IsTarget(session);
            dotColor = on ? Theme.Green : session.Running ? Theme.Accent : Theme.Sub;
            dot.Invalidate();
            title.Text = (on ? "Controlling " : "Control mode: ") + session.Name;
            hint.Text = on ? "Your mouse and keyboard now work the phone. Ctrl+Alt gives them back."
                           : "Look at your phone. Click Take control (or press Ctrl+Alt+M).";
            take.Text = on ? "Release" : "Take control";
            Theme.SetPrimaryColor(take, on ? Theme.Green : Theme.Accent, on ? Theme.Green : Theme.AccentHover);
            Invalidate();
        }

        void Toggle()
        {
            if (InputCapture.IsTarget(session)) { InputCapture.Stop(); return; }
            try { InputCapture.Start(session); }
            catch (Exception ex) { hint.Text = ex.Message; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) Toggle();   // WM_HOTKEY
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(InputCapture.IsTarget(session) ? Theme.Green : Theme.Border, 2)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
        }

        void Drag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
}
