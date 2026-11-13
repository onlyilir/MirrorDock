package com.mirrordock;

import android.system.Os;
import android.system.OsConstants;

import java.io.FileDescriptor;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Virtual game controllers on the phone through the Linux UHID driver (/dev/uhid).
 * Each one looks like an Xbox 360 controller, so Android and games map its buttons and sticks automatically.
 * Report (13 bytes): u16 buttons, u8 hat (1-8, 0 = centered), s16 LX, LY, RX, RY, u8 LT, RT.
 */
final class Gamepads {
    private static final int UHID_DESTROY = 1, UHID_CREATE2 = 11, UHID_INPUT2 = 12;
    private static final int EVENT_SIZE = 4376;  // sizeof(struct uhid_event)
    private final FileDescriptor[] fds = new FileDescriptor[4];

    private static final byte[] DESCRIPTOR = {
            0x05, 0x01, 0x09, 0x05, (byte) 0xA1, 0x01,                  // Generic Desktop, Game Pad, Collection(Application)
            0x05, 0x09, 0x19, 0x01, 0x29, 0x10, 0x15, 0x00, 0x25, 0x01, // 16 buttons
            0x75, 0x01, (byte) 0x95, 0x10, (byte) 0x81, 0x02,
            0x05, 0x01, 0x09, 0x39, 0x15, 0x01, 0x25, 0x08,             // hat switch 1..8
            0x35, 0x00, 0x46, 0x3B, 0x01, 0x65, 0x14,
            0x75, 0x04, (byte) 0x95, 0x01, (byte) 0x81, 0x42,
            0x65, 0x00, 0x45, 0x00, 0x75, 0x04, (byte) 0x95, 0x01, (byte) 0x81, 0x01, // padding
            0x09, 0x30, 0x09, 0x31, 0x09, 0x33, 0x09, 0x34,             // X, Y, Rx, Ry
            0x16, 0x00, (byte) 0x80, 0x26, (byte) 0xFF, 0x7F,
            0x75, 0x10, (byte) 0x95, 0x04, (byte) 0x81, 0x02,
            0x09, 0x32, 0x09, 0x35, 0x15, 0x00, 0x26, (byte) 0xFF, 0x00, // Z, Rz triggers 0..255
            0x75, 0x08, (byte) 0x95, 0x02, (byte) 0x81, 0x02,
            (byte) 0xC0
    };

    synchronized void create(int idx) {
        if (idx < 0 || idx >= fds.length || fds[idx] != null) return;
        try {
            FileDescriptor fd = Os.open("/dev/uhid", OsConstants.O_RDWR, 0);
            ByteBuffer b = event(UHID_CREATE2);
            put(b, "Xbox 360 Controller (MirrorDock " + (idx + 1) + ")", 128);
            put(b, "mirrordock/" + idx, 64);
            put(b, "md" + idx, 64);
            b.putShort((short) DESCRIPTOR.length);
            b.putShort((short) 0x03);          // BUS_USB
            b.putInt(0x045E);                  // Microsoft
            b.putInt(0x028E);                  // Xbox 360 controller
            b.putInt(0x0110);
            b.putInt(0);
            b.put(DESCRIPTOR);
            write(fd, b);
            fds[idx] = fd;
            Server.log("Game controller " + (idx + 1) + " connected");
        } catch (Exception e) {
            Server.log("ERROR: game controller unavailable on this phone: " + e);
        }
    }

    synchronized void report(int idx, byte[] report) {
        if (idx < 0 || idx >= fds.length || fds[idx] == null) return;
        try {
            ByteBuffer b = event(UHID_INPUT2);
            b.putShort((short) report.length);
            b.put(report);
            write(fds[idx], b);
        } catch (Exception e) {
            Server.log("gamepad write failed: " + e);
        }
    }

    synchronized void destroy(int idx) {
        if (idx < 0 || idx >= fds.length || fds[idx] == null) return;
        try { write(fds[idx], event(UHID_DESTROY)); } catch (Exception ignored) { }
        try { Os.close(fds[idx]); } catch (Exception ignored) { }
        fds[idx] = null;
        Server.log("Game controller " + (idx + 1) + " disconnected");
    }

    void destroyAll() { for (int i = 0; i < fds.length; i++) destroy(i); }

    private static ByteBuffer event(int type) {
        ByteBuffer b = ByteBuffer.allocate(EVENT_SIZE).order(ByteOrder.LITTLE_ENDIAN);
        b.putInt(type);
        return b;
    }

    private static void put(ByteBuffer b, String s, int size) {
        byte[] d = s.getBytes();
        int n = Math.min(d.length, size - 1);
        b.put(d, 0, n);
        for (int i = n; i < size; i++) b.put((byte) 0);
    }

    private static void write(FileDescriptor fd, ByteBuffer b) throws Exception {
        byte[] a = b.array();
        int off = 0;
        while (off < a.length) off += Os.write(fd, a, off, a.length - off);
    }
}
