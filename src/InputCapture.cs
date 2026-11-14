using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace MirrorDock
{
    // Control mode: while active, the PC's mouse and keyboard drive the phone (through its virtual mouse + keyboard)
    // instead of Windows. Press Ctrl+Alt to give them back. Windows also drops these hooks if the app stops responding,
    // and they disappear when the app exits, so the PC can never stay locked.
    static class InputCapture
    {
        public static bool Active { get; private set; }
        public static event Action Changed;

        static MirrorSession target;
        static IntPtr mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
        static readonly HookProc mouseProc = MouseProc, keyProc = KeyProc;   // kept alive while hooked
        static Point anchor;
        static readonly object gate = new object();
        static int accX, accY, accWheel, accPan;
        static byte buttons;
        static bool dirty;
        static Thread flusher;
        static readonly List<byte> keysDown = new List<byte>();
        static byte modifiers;
        static bool ctrl, alt;

        public static void Start(MirrorSession s)
        {
            if (Active) Stop();
            target = s;
            GetCursorPos(out anchor);
            IntPtr mod = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(14, mouseProc, mod, 0);   // WH_MOUSE_LL
            keyHook = SetWindowsHookEx(13, keyProc, mod, 0);       // WH_KEYBOARD_LL
            if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero) { Stop(); throw new Exception("Windows didn't allow capturing the mouse and keyboard."); }
            lock (gate) { accX = accY = accWheel = accPan = 0; buttons = 0; dirty = false; keysDown.Clear(); modifiers = 0; ctrl = alt = false; }
            Active = true;
            s.Key(224);   // KEYCODE_WAKEUP: make sure the phone screen is on
            if (flusher == null)
            {
                flusher = new Thread(FlushLoop) { IsBackground = true, Name = "control flush" };
                flusher.Start();
            }
            if (Changed != null) Changed();
        }

        public static void Stop()
        {
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            bool was = Active;
            Active = false;
            MirrorSession s = target;
            if (s != null && s.Running)
            {
                // Let go of anything still held so the phone doesn't see a stuck key or button.
                lock (gate) { keysDown.Clear(); modifiers = 0; buttons = 0; accX = accY = accWheel = accPan = 0; dirty = false; }
                try { s.InputReport(MirrorSession.KeyboardSlot, new byte[8]); s.InputReport(MirrorSession.MouseSlot, new byte[7]); } catch { }
            }
            if (was && Changed != null) Changed();
        }

        public static bool IsTarget(MirrorSession s) { return Active && target == s; }

        // Mouse movement is gathered and sent every 4 ms (up to 250 updates a second), so a fast mouse doesn't flood the phone.
        static void FlushLoop()
        {
            while (true)
            {
                Thread.Sleep(4);
                MirrorSession s = target;
                byte[] r = null;
                lock (gate)
                {
                    if (!dirty || !Active) continue;
                    int dx = Clamp(accX, 32767), dy = Clamp(accY, 32767), w = Clamp(accWheel, 127), p = Clamp(accPan, 127);
                    accX -= dx; accY -= dy; accWheel -= w; accPan -= p;
                    dirty = accX != 0 || accY != 0 || accWheel != 0 || accPan != 0;
                    r = new byte[] { buttons, (byte)dx, (byte)(dx >> 8), (byte)dy, (byte)(dy >> 8), (byte)(sbyte)w, (byte)(sbyte)p };
                }
                if (s != null && s.Running) s.InputReport(MirrorSession.MouseSlot, r);
                else if (Active) InvokeStop();
            }
        }

        static int Clamp(int v, int max) { return v > max ? max : v < -max ? -max : v; }

        static void InvokeStop()
        {
            // Hooks must be removed on the thread that set them (the UI thread): post it there.
            var f = System.Windows.Forms.Application.OpenForms.Count > 0 ? System.Windows.Forms.Application.OpenForms[0] : null;
            if (f != null) try { f.BeginInvoke((Action)Stop); } catch { }
        }

        static IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || !Active) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            lock (gate)
            {
                switch (msg)
                {
                    case 0x0200: accX += m.pt.X - anchor.X; accY += m.pt.Y - anchor.Y; break;   // move: the cursor stays put, we send the difference
                    case 0x0201: buttons |= 1; break;
                    case 0x0202: buttons &= unchecked((byte)~1); break;
                    case 0x0204: buttons |= 2; break;
                    case 0x0205: buttons &= unchecked((byte)~2); break;
                    case 0x0207: buttons |= 4; break;
                    case 0x0208: buttons &= unchecked((byte)~4); break;
                    case 0x020B: buttons |= (byte)((m.mouseData >> 16) == 1 ? 8 : 16); break;
                    case 0x020C: buttons &= unchecked((byte)~((m.mouseData >> 16) == 1 ? 8 : 16)); break;
                    case 0x020A: accWheel += ((short)(m.mouseData >> 16)) / 120; break;
                    case 0x020E: accPan += ((short)(m.mouseData >> 16)) / 120; break;
                }
                dirty = true;
            }
            return (IntPtr)1;   // swallow: Windows doesn't see it
        }

        static IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || !Active) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            bool down = (k.flags & 0x80) == 0;
            int vk = (int)k.vkCode;
            if (vk == 0xA2 || vk == 0xA3 || vk == 0x11) ctrl = down;
            if (vk == 0xA4 || vk == 0xA5 || vk == 0x12) alt = down;
            if (ctrl && alt) { InvokeStop(); return (IntPtr)1; }   // Ctrl+Alt: give the mouse and keyboard back
            byte mod = ModifierBit(vk);
            byte[] report;
            lock (gate)
            {
                if (mod != 0) { if (down) modifiers |= mod; else modifiers &= (byte)~mod; }
                else
                {
                    byte usage = Usage(vk, (k.flags & 1) != 0);
                    if (usage == 0) return (IntPtr)1;
                    if (down) { if (!keysDown.Contains(usage)) keysDown.Add(usage); }
                    else keysDown.Remove(usage);
                }
                report = KeyboardReport(modifiers, keysDown);
            }
            MirrorSession s = target;
            if (s != null && s.Running) s.InputReport(MirrorSession.KeyboardSlot, report);
            return (IntPtr)1;
        }

        public static byte[] KeyboardReport(byte modifiers, List<byte> keys)
        {
            var r = new byte[8];
            r[0] = modifiers;
            for (int i = 0; i < keys.Count && i < 6; i++) r[2 + i] = keys[i];
            return r;
        }

        public static byte ModifierBit(int vk)
        {
            switch (vk)
            {
                case 0xA2: case 0x11: return 0x01;   // left Ctrl
                case 0xA0: case 0x10: return 0x02;   // left Shift
                case 0xA4: case 0x12: return 0x04;   // left Alt
                case 0x5B: return 0x08;              // left Windows
                case 0xA3: return 0x10;
                case 0xA1: return 0x20;
                case 0xA5: return 0x40;
                case 0x5C: return 0x80;
                default: return 0;
            }
        }

        // Windows virtual-key code -> USB HID keyboard usage (US layout positions; the phone applies its own layout).
        public static byte Usage(int vk, bool extended)
        {
            if (vk >= 0x41 && vk <= 0x5A) return (byte)(0x04 + vk - 0x41);          // A-Z
            if (vk >= 0x31 && vk <= 0x39) return (byte)(0x1E + vk - 0x31);          // 1-9
            if (vk == 0x30) return 0x27;                                            // 0
            if (vk >= 0x70 && vk <= 0x7B) return (byte)(0x3A + vk - 0x70);          // F1-F12
            if (vk >= 0x61 && vk <= 0x69) return (byte)(0x59 + vk - 0x61);          // numpad 1-9
            switch (vk)
            {
                case 0x0D: return extended ? (byte)0x58 : (byte)0x28;               // Enter / numpad Enter
                case 0x1B: return 0x29; case 0x08: return 0x2A; case 0x09: return 0x2B; case 0x20: return 0x2C;
                case 0xBD: return 0x2D; case 0xBB: return 0x2E; case 0xDB: return 0x2F; case 0xDD: return 0x30;
                case 0xDC: return 0x31; case 0xBA: return 0x33; case 0xDE: return 0x34; case 0xC0: return 0x35;
                case 0xBC: return 0x36; case 0xBE: return 0x37; case 0xBF: return 0x38; case 0x14: return 0x39;
                case 0x2C: return 0x46; case 0x91: return 0x47; case 0x13: return 0x48; case 0x2D: return 0x49;
                case 0x24: return 0x4A; case 0x21: return 0x4B; case 0x2E: return 0x4C; case 0x23: return 0x4D;
                case 0x22: return 0x4E; case 0x27: return 0x4F; case 0x25: return 0x50; case 0x28: return 0x51;
                case 0x26: return 0x52; case 0x90: return 0x53; case 0x6F: return 0x54; case 0x6A: return 0x55;
                case 0x6D: return 0x56; case 0x6B: return 0x57; case 0x60: return 0x62; case 0x6E: return 0x63;
                case 0xE2: return 0x64; case 0x5D: return 0x65;
                case 0xAD: return 0x7F; case 0xAF: return 0x80; case 0xAE: return 0x81;   // mute, volume up/down
                default: return 0;
            }
        }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public int mouseData, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    }
}
