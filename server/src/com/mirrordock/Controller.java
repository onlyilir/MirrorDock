package com.mirrordock;

import android.content.ClipData;
import android.content.IOnPrimaryClipChangedListener;
import android.os.IBinder;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.lang.reflect.Method;

/**
 * Reads input messages from the PC and injects them into Android.
 *  1 touch : byte action (0 down, 1 up, 2 move), int x, int y   (device pixels)
 *  2 key   : byte action (0 down, 1 up), int keycode, int metaState
 *  3 text  : short length, UTF-8 bytes
 *  4 scroll: int x, int y, float h, float v
 *  5 screen power: byte mode (0 off, 2 on)
 *  6 set clipboard: int length, UTF-8 bytes, byte paste (1 = also paste)
 *  7 pinch : byte action, int x, int y, int centerX, int centerY  (second finger mirrored around the center)
 *  8 request keyframe
 * Phone -> PC on the same socket: byte 1 (clipboard), int length, UTF-8 bytes.
 */
final class Controller {
    private static final int KEYCODE_PASTE = 279;
    private Object inputManager;
    private Method inject;
    private long touchDown;
    private boolean screenTurnedOff;
    private final KeyCharacterMap charMap = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);
    private DataOutputStream out;
    private Object clipboard;
    private Method getClip, setClip, addListener;
    private volatile String lastClip;

    Controller() {
        try {
            Class<?> c = Class.forName("android.hardware.input.InputManagerGlobal"); // Android 14+
            inputManager = c.getMethod("getInstance").invoke(null);
            inject = c.getMethod("injectInputEvent", InputEvent.class, int.class);
        } catch (Exception e) {
            try {
                Class<?> c = Class.forName("android.hardware.input.InputManager");
                inputManager = c.getMethod("getInstance").invoke(null);
                inject = c.getMethod("injectInputEvent", InputEvent.class, int.class);
            } catch (Exception e2) {
                Server.log("ERROR: input injection unavailable: " + e2);
            }
        }
        try {
            Class<?> sm = Class.forName("android.os.ServiceManager");
            IBinder b = (IBinder) sm.getMethod("getService", String.class).invoke(null, "clipboard");
            clipboard = Class.forName("android.content.IClipboard$Stub").getMethod("asInterface", IBinder.class).invoke(null, b);
            for (Method m : clipboard.getClass().getMethods()) {
                if (m.getName().equals("getPrimaryClip") && (getClip == null || m.getParameterTypes().length > getClip.getParameterTypes().length)) getClip = m;
                if (m.getName().equals("setPrimaryClip") && (setClip == null || m.getParameterTypes().length > setClip.getParameterTypes().length)) setClip = m;
                if (m.getName().equals("addPrimaryClipChangedListener")) addListener = m;
            }
        } catch (Exception e) {
            Server.log("Note: clipboard unavailable (" + e + ")");
        }
    }

    void setOutput(DataOutputStream o) { out = o; }

    void loop(DataInputStream in) throws IOException {
        while (true) {
            int type = in.readUnsignedByte();
            switch (type) {
                case 1: touch(in.readUnsignedByte(), in.readInt(), in.readInt()); break;
                case 2: key(in.readUnsignedByte() == 0 ? KeyEvent.ACTION_DOWN : KeyEvent.ACTION_UP, in.readInt(), in.readInt()); break;
                case 3: {
                    byte[] b = new byte[in.readUnsignedShort()];
                    in.readFully(b);
                    text(new String(b, "UTF-8"));
                    break;
                }
                case 4: scroll(in.readInt(), in.readInt(), in.readFloat(), in.readFloat()); break;
                case 5: setScreenPower(in.readUnsignedByte()); break;
                case 6: {
                    byte[] b = new byte[in.readInt()];
                    in.readFully(b);
                    boolean paste = in.readUnsignedByte() == 1;
                    String s = new String(b, "UTF-8");
                    lastClip = s;
                    setClipboard(s);
                    if (paste) pressKey(KEYCODE_PASTE);
                    break;
                }
                case 7: pinch(in.readUnsignedByte(), in.readInt(), in.readInt(), in.readInt(), in.readInt()); break;
                case 8: Server.keyFrameRequested = true; break;
                case 9: gamepads.create(in.readUnsignedByte()); break;
                case 10: {
                    int idx = in.readUnsignedByte();
                    byte[] r = new byte[13];
                    in.readFully(r);
                    gamepads.report(idx, r);
                    break;
                }
                case 11: gamepads.destroy(in.readUnsignedByte()); break;
                default: throw new IOException("Unknown message " + type);
            }
        }
    }

    private final Gamepads gamepads = new Gamepads();
    private volatile int displayId;
    private volatile Method setDisplay;
    private int injectLogs;

    void setDisplayId(int id) {
        displayId = id;
        if (id != 0 && setDisplay == null) {
            try { setDisplay = InputEvent.class.getMethod("setDisplayId", int.class); } catch (Exception e) { Server.log("Note: can't target screen " + id); }
        }
    }

    /** Sends a message of strings to the PC: byte type, then for one string int len + UTF-8; for several, each the same way. */
    void sendToPc(int type, String... fields) {
        if (out == null) return;
        try {
            synchronized (out) {
                out.writeByte(type);
                for (String f : fields) {
                    byte[] b = (f == null ? "" : f).getBytes("UTF-8");
                    out.writeInt(b.length);
                    out.write(b);
                }
                out.flush();
            }
        } catch (IOException ignored) { }
    }

    private void injectEvent(InputEvent e) {
        if (inject == null) return;
        if (Server.screen != null)
        {
            // Own-screen mode: the event must go to that screen. Never let it fall through to the phone's real screen.
            int id = displayId;
            if (id == 0 || setDisplay == null) return;
            try { setDisplay.invoke(e, id); } catch (Exception ex) { if (injectLogs++ < 3) Server.log("ERROR: can't aim input at screen " + id + ": " + ex); return; }
        }
        try {
            Object ok = inject.invoke(inputManager, e, 0);
            if (Boolean.FALSE.equals(ok) && injectLogs++ < 5) Server.log("Note: phone ignored an input event (" + e.getClass().getSimpleName() + ", display " + displayId + ")");
        } catch (Exception ex) { Server.log("inject failed: " + ex); }
    }


    private static MotionEvent.PointerProperties props(int id, int tool) {
        MotionEvent.PointerProperties p = new MotionEvent.PointerProperties();
        p.id = id;
        p.toolType = tool;
        return p;
    }

    private static MotionEvent.PointerCoords coords(float x, float y, float pressure) {
        MotionEvent.PointerCoords c = new MotionEvent.PointerCoords();
        c.x = x; c.y = y; c.pressure = pressure; c.size = 1f;
        return c;
    }

    private void touch(int action, int x, int y) {
        long now = SystemClock.uptimeMillis();
        if (action == MotionEvent.ACTION_DOWN) touchDown = now;
        MotionEvent e = MotionEvent.obtain(touchDown, now, action, 1,
                new MotionEvent.PointerProperties[] { props(0, MotionEvent.TOOL_TYPE_FINGER) },
                new MotionEvent.PointerCoords[] { coords(x, y, action == MotionEvent.ACTION_UP ? 0f : 1f) },
                0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0);
        injectEvent(e);
        e.recycle();
    }

    private void pinch(int action, int x, int y, int cx, int cy) {
        long now = SystemClock.uptimeMillis();
        MotionEvent.PointerProperties[] p = { props(0, MotionEvent.TOOL_TYPE_FINGER), props(1, MotionEvent.TOOL_TYPE_FINGER) };
        MotionEvent.PointerCoords[] c = { coords(x, y, 1f), coords(2 * cx - x, 2 * cy - y, 1f) };
        if (action == 0) {
            touchDown = now;
            send(MotionEvent.obtain(touchDown, now, MotionEvent.ACTION_DOWN, 1, new MotionEvent.PointerProperties[] { p[0] },
                    new MotionEvent.PointerCoords[] { c[0] }, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0));
            send(MotionEvent.obtain(touchDown, now, MotionEvent.ACTION_POINTER_DOWN | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT),
                    2, p, c, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0));
        } else if (action == 2) {
            send(MotionEvent.obtain(touchDown, now, MotionEvent.ACTION_MOVE, 2, p, c, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0));
        } else {
            send(MotionEvent.obtain(touchDown, now, MotionEvent.ACTION_POINTER_UP | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT),
                    2, p, c, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0));
            c[0].pressure = 0f;
            send(MotionEvent.obtain(touchDown, now, MotionEvent.ACTION_UP, 1, new MotionEvent.PointerProperties[] { p[0] },
                    new MotionEvent.PointerCoords[] { c[0] }, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0));
        }
    }

    private void send(MotionEvent e) { injectEvent(e); e.recycle(); }

    private void scroll(int x, int y, float h, float v) {
        long now = SystemClock.uptimeMillis();
        MotionEvent.PointerCoords c = coords(x, y, 0f);
        c.setAxisValue(MotionEvent.AXIS_HSCROLL, h);
        c.setAxisValue(MotionEvent.AXIS_VSCROLL, v);
        send(MotionEvent.obtain(now, now, MotionEvent.ACTION_SCROLL, 1,
                new MotionEvent.PointerProperties[] { props(0, MotionEvent.TOOL_TYPE_MOUSE) },
                new MotionEvent.PointerCoords[] { c }, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_MOUSE, 0));
    }

    private void key(int action, int code, int meta) {
        long now = SystemClock.uptimeMillis();
        injectEvent(new KeyEvent(now, now, action, code, 0, meta, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0,
                InputDevice.SOURCE_KEYBOARD));
    }

    private void pressKey(int code) { key(KeyEvent.ACTION_DOWN, code, 0); key(KeyEvent.ACTION_UP, code, 0); }

    private void text(String s) {
        // Plain keys when every character can be typed; otherwise go through the clipboard (é, ë, emoji...).
        java.util.List<KeyEvent[]> all = new java.util.ArrayList<KeyEvent[]>();
        for (int i = 0; i < s.length(); i++) {
            KeyEvent[] ev = charMap.getEvents(new char[] { s.charAt(i) });
            if (ev == null) { lastClip = s; if (setClipboard(s)) pressKey(KEYCODE_PASTE); return; }
            all.add(ev);
        }
        for (KeyEvent[] ev : all) for (KeyEvent e : ev) injectEvent(e);
    }

    // ---- clipboard ----
    private Object[] args(Method m, Object first) {
        Class<?>[] t = m.getParameterTypes();
        Object[] a = new Object[t.length];
        boolean pkgSet = false;
        for (int i = 0; i < t.length; i++) {
            if (t[i] == ClipData.class || t[i].getName().equals("android.content.IOnPrimaryClipChangedListener")) a[i] = first;
            else if (t[i] == String.class) { a[i] = pkgSet ? null : FakeContext.PACKAGE; pkgSet = true; }
            else if (t[i] == int.class) a[i] = 0;
            else if (t[i] == boolean.class) a[i] = false;
            else a[i] = null;
        }
        return a;
    }

    boolean setClipboard(String s) {
        if (setClip == null) return false;
        try {
            setClip.invoke(clipboard, args(setClip, ClipData.newPlainText(null, s)));
            return true;
        } catch (Exception e) {
            Server.log("Note: couldn't set phone clipboard (" + e + ")");
            return false;
        }
    }

    String getClipboard() {
        if (getClip == null) return null;
        try {
            ClipData cd = (ClipData) getClip.invoke(clipboard, args(getClip, null));
            if (cd == null || cd.getItemCount() == 0) return null;
            CharSequence t = cd.getItemAt(0).getText();
            return t == null ? null : t.toString();
        } catch (Exception e) {
            return null;
        }
    }

    void startClipboardWatch() {
        if (addListener == null || out == null) return;
        try {
            IOnPrimaryClipChangedListener l = new IOnPrimaryClipChangedListener.Stub() {
                @Override public void dispatchPrimaryClipChanged() { onClipChanged(); }
            };
            addListener.invoke(clipboard, args(addListener, l));
            Server.log("Clipboard sync on");
        } catch (Throwable t) {
            Server.log("Note: clipboard sync unavailable (" + t + ")");
        }
    }

    private void onClipChanged() {
        String s = getClipboard();
        if (s == null || s.equals(lastClip)) return;
        lastClip = s;
        sendToPc(1, s);
    }

    // ---- screen off while mirroring ----
    private void setScreenPower(int mode) {
        try {
            Class<?> sc = Class.forName("android.view.SurfaceControl");
            IBinder token = displayToken(sc);
            sc.getMethod("setDisplayPowerMode", IBinder.class, int.class).invoke(null, token, mode);
            screenTurnedOff = mode == 0;
            Server.log(mode == 0 ? "Phone screen off (still mirroring)" : "Phone screen on");
        } catch (Throwable t) {
            Server.log("ERROR: screen off not supported on this phone: " + t);
        }
    }

    private static IBinder displayToken(Class<?> sc) throws Exception {
        try { return (IBinder) sc.getMethod("getInternalDisplayToken").invoke(null); }   // Android 10-13
        catch (NoSuchMethodException ignored) { }
        try {
            long[] ids = (long[]) sc.getMethod("getPhysicalDisplayIds").invoke(null);
            return (IBinder) sc.getMethod("getPhysicalDisplayToken", long.class).invoke(null, ids[0]);
        } catch (NoSuchMethodException ignored) { }
        // Android 14+: the methods moved into services.jar (DisplayControl).
        ClassLoader cl = new dalvik.system.PathClassLoader("/system/framework/services.jar", ClassLoader.getSystemClassLoader());
        Class<?> dc = cl.loadClass("com.android.server.display.DisplayControl");
        Method load = Runtime.class.getDeclaredMethod("loadLibrary0", Class.class, String.class);
        load.setAccessible(true);
        load.invoke(Runtime.getRuntime(), dc, "android_servers");
        long[] ids = (long[]) dc.getMethod("getPhysicalDisplayIds").invoke(null);
        return (IBinder) dc.getMethod("getPhysicalDisplayToken", long.class).invoke(null, ids[0]);
    }

    void restore() {
        gamepads.destroyAll();
        if (screenTurnedOff) { screenTurnedOff = false; setScreenPower(2); }
    }
}
