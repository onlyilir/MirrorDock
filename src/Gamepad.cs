using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MirrorDock
{
    // Reads Xbox / XInput controllers on the PC (up to 4) and forwards them to the phone as virtual Xbox 360 pads.
    // PlayStation and other pads work through Steam Input or DS4Windows, which present them as XInput.
    static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int GetState14(int idx, out XINPUT_STATE s);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int GetState910(int idx, out XINPUT_STATE s);

        static Thread thread;
        static volatile Func<MirrorSession> target;   // which mirror gets the controller (the one you used last)
        static bool useOld;

        public static void Start(Func<MirrorSession> getTarget)
        {
            target = getTarget;
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "gamepad" };
            thread.Start();
        }

        static int Get(int i, out XINPUT_STATE s)
        {
            if (!useOld)
            {
                try { return GetState14(i, out s); }
                catch (DllNotFoundException) { useOld = true; }
                catch (EntryPointNotFoundException) { useOld = true; }
            }
            try { return GetState910(i, out s); } catch { s = new XINPUT_STATE(); return -1; }
        }

        static void Loop()
        {
            var last = new uint[4];
            var connected = new bool[4];
            MirrorSession lastTarget = null;
            int idleScan = 0;
            while (true)
            {
                MirrorSession t = target != null ? target() : null;
                if (t != lastTarget)
                {   // controllers move to the newly used mirror
                    if (lastTarget != null && lastTarget.Running) lastTarget.ReleaseGamepads();
                    for (int i = 0; i < 4; i++) { connected[i] = false; last[i] = uint.MaxValue; }
                    lastTarget = t;
                }
                if (t == null || !t.Running || !t.Opts.Gamepad) { Thread.Sleep(250); continue; }
                bool any = false;
                for (int i = 0; i < 4; i++)
                {
                    if (!connected[i] && idleScan % 30 != 0) continue;   // probe missing pads only twice a second
                    XINPUT_STATE s;
                    bool ok = Get(i, out s) == 0;
                    if (!ok)
                    {
                        if (connected[i]) { connected[i] = false; t.GamepadState(i, false, null); }
                        continue;
                    }
                    any = true;
                    if (!connected[i] || s.dwPacketNumber != last[i])
                    {
                        connected[i] = true;
                        last[i] = s.dwPacketNumber;
                        t.GamepadState(i, true, Report(s));
                    }
                }
                idleScan++;
                Thread.Sleep(any ? 4 : 16);
            }
        }

        // Builds the 13-byte HID report the phone's virtual pad expects.
        static byte[] Report(XINPUT_STATE s)
        {
            ushort w = s.wButtons;
            int buttons = 0;
            if ((w & 0x1000) != 0) buttons |= 1 << 0;    // A
            if ((w & 0x2000) != 0) buttons |= 1 << 1;    // B
            if ((w & 0x4000) != 0) buttons |= 1 << 3;    // X
            if ((w & 0x8000) != 0) buttons |= 1 << 4;    // Y
            if ((w & 0x0100) != 0) buttons |= 1 << 6;    // LB
            if ((w & 0x0200) != 0) buttons |= 1 << 7;    // RB
            if ((w & 0x0020) != 0) buttons |= 1 << 10;   // Back
            if ((w & 0x0010) != 0) buttons |= 1 << 11;   // Start
            if ((w & 0x0040) != 0) buttons |= 1 << 13;   // left stick click
            if ((w & 0x0080) != 0) buttons |= 1 << 14;   // right stick click
            bool up = (w & 1) != 0, down = (w & 2) != 0, left = (w & 4) != 0, right = (w & 8) != 0;
            int hat = 0;
            if (up && right) hat = 2; else if (right && down) hat = 4; else if (down && left) hat = 6; else if (left && up) hat = 8;
            else if (up) hat = 1; else if (right) hat = 3; else if (down) hat = 5; else if (left) hat = 7;
            var r = new byte[13];
            r[0] = (byte)buttons; r[1] = (byte)(buttons >> 8);
            r[2] = (byte)hat;
            Put16(r, 3, s.sThumbLX); Put16(r, 5, Flip(s.sThumbLY));      // XInput "up" is positive, Android "down" is
            Put16(r, 7, s.sThumbRX); Put16(r, 9, Flip(s.sThumbRY));
            r[11] = s.bLeftTrigger; r[12] = s.bRightTrigger;
            return r;
        }

        static short Flip(short v) { return v == short.MinValue ? short.MaxValue : (short)-v; }
        static void Put16(byte[] b, int o, short v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    }
}
