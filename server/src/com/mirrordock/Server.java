package com.mirrordock;

import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.media.Image;
import android.media.ImageReader;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Looper;
import android.view.Surface;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * MirrorDock phone-side engine. Started by the PC through:
 *   adb shell CLASSPATH=/data/local/tmp/mirrordock.jar app_process / com.mirrordock.Server
 *       <socket> <maxSize> <jpegQuality> <fps> <codec h264|jpeg> <bitrateMbps> <audio 0|1> <screen spec|-> <notifications 0|1>
 *   ... com.mirrordock.Server --list-apps      (prints APP|package|label lines and exits)
 *
 * Screen spec: "-" mirrors the phone; "app:WxH/dpi:package" opens one app on its own screen; "desktop:WxH/dpi" a second screen.
 * Sockets, accepted in order: video, control, then audio (only when audio=1).
 * Video (phone -> PC): "MDK2", byte codec (0 jpeg, 1 h264), then packets:
 *   int length, int deviceW, int deviceH, int frameW, int frameH, long ptsUs, byte flags (1 config, 2 keyframe), data
 * Control: PC -> phone input (see Controller). Phone -> PC: byte 1 clipboard (int len, UTF-8);
 *   byte 2 notification (4 strings: package, app, title, text; each int len + UTF-8).
 * Audio (phone -> PC): "MDA1", byte status (1 ok, 0 unavailable), then int length + raw PCM s16le 48 kHz stereo.
 */
public final class Server {

    static volatile boolean running = true;
    static volatile boolean keyFrameRequested;
    static volatile int requestedBitrate;   // set by the PC (auto quality); 0 = no change
    static boolean controlOnly;              // "control" mode: no video, only the virtual mouse and keyboard
    static final Object videoLock = new Object();
    static VirtualScreen screen;

    public static void main(String[] args) {
        try { Looper.prepareMainLooper(); } catch (Throwable ignored) { }
        Workarounds.apply();
        if (args.length > 0 && args[0].equals("--list-apps")) { VirtualScreen.listApps(); System.exit(0); }

        String socketName = args.length > 0 ? args[0] : "mirrordock";
        int maxSize = arg(args, 1, 1280);
        int quality = arg(args, 2, 75);
        int fps = arg(args, 3, 60);
        boolean h264 = args.length <= 4 || !"jpeg".equals(args[4]);
        int bitrate = arg(args, 5, 8) * 1000000;
        boolean audio = arg(args, 6, 0) == 1;
        controlOnly = args.length > 7 && args[7].equals("control");
        try { screen = controlOnly ? null : VirtualScreen.parse(args.length > 7 ? args[7] : "-"); } catch (Exception e) { log("ERROR: bad screen spec"); }
        boolean notifications = arg(args, 8, 0) == 1;
        if (screen != null) h264 = true;   // separate screens always use the encoder

        log("MirrorDock engine " + Version.NAME + " starting (" + (h264 ? "H.264 " + (bitrate / 1000000) + " Mbps" : "JPEG q" + quality)
                + ", max " + maxSize + ", " + fps + " fps, audio " + (audio ? "on" : "off")
                + (screen != null ? ", " + (screen.desktop ? "second screen" : "app " + screen.app) : "") + ")");

        final Capture capture = new Capture();
        final Controller controller = new Controller();
        Runtime.getRuntime().addShutdownHook(new Thread() {
            @Override public void run() { capture.release(); if (screen != null) screen.release(); controller.restore(); }
        });

        try {
            LocalServerSocket server = new LocalServerSocket(socketName);
            LocalSocket video = server.accept();
            DataOutputStream out = new DataOutputStream(new BufferedOutputStream(video.getOutputStream(), 1 << 16));
            out.writeBytes("MDK2");
            out.writeByte(h264 ? 1 : 0);
            out.flush();
            LocalSocket control = server.accept();
            LocalSocket audioSock = audio ? server.accept() : null;
            server.close();
            log("PC connected");

            final DataInputStream in = new DataInputStream(new BufferedInputStream(control.getInputStream()));
            final DataOutputStream ctrlOut = new DataOutputStream(new BufferedOutputStream(control.getOutputStream()));
            controller.setOutput(ctrlOut);
            Thread ct = new Thread(new Runnable() {
                @Override public void run() {
                    try { controller.loop(in); } catch (IOException e) { /* PC went away */ }
                    running = false;
                }
            }, "control");
            ct.setDaemon(true);
            ct.start();
            if (screen == null) controller.startClipboardWatch();
            if (notifications && screen == null) Notifications.startThread(new Notifications.Sink() {
                @Override public void send(String pkg, String app, String title, String text) {
                    controller.sendToPc(2, pkg, app, title, text);
                }
            });

            if (audioSock != null) AudioCapture.startThread(new DataOutputStream(new BufferedOutputStream(audioSock.getOutputStream(), 1 << 14)));

            if (controlOnly) {
                log("Control mode: no video, mouse and keyboard only");
                while (running) Thread.sleep(200);
            } else if (h264) {
                try { streamH264(capture, controller, out, maxSize, fps, bitrate); }
                catch (IOException e) { throw e; }
                catch (Exception e) {
                    log("ERROR: H.264 encoder failed: " + e);
                    throw e;
                }
            } else {
                streamJpeg(capture, out, maxSize, quality, fps);
            }
        } catch (IOException e) {
            // PC disconnected
        } catch (Throwable t) {
            log("ERROR: " + t);
            t.printStackTrace();
        } finally {
            capture.release();
            if (screen != null) screen.release();
            controller.restore();
            log("Engine stopped");
            System.exit(0);
        }
    }

    static void writeHeader(DataOutputStream out, int len, int dw, int dh, int w, int h, long pts, int flags) throws IOException {
        out.writeInt(len);
        out.writeInt(dw);
        out.writeInt(dh);
        out.writeInt(w);
        out.writeInt(h);
        out.writeLong(pts);
        out.writeByte(flags);
    }

    // ------------------------------------------------------------ H.264
    static void streamH264(Capture capture, Controller controller, DataOutputStream out, int maxSize, int fps, int bitrate) throws Exception {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        while (running) {
            int w, h, dw, dh;
            if (screen != null) { w = dw = screen.width; h = dh = screen.height; }
            else { capture.measure(maxSize, 8); w = capture.width; h = capture.height; dw = capture.deviceWidth; dh = capture.deviceHeight; }
            MediaFormat f = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h);
            f.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            f.setInteger(MediaFormat.KEY_BIT_RATE, bitrate);
            f.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
            f.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10);
            f.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100000);
            f.setFloat("max-fps-to-encoder", fps);
            f.setInteger(MediaFormat.KEY_PRIORITY, 0);
            MediaCodec codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
            codec.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            Surface surface = codec.createInputSurface();
            if (screen != null) { screen.attach(surface); controller.setDisplayId(screen.displayId()); }
            else capture.attach(surface);
            codec.start();
            long lastCheck = 0;
            boolean restart = false;
            try {
                while (running && !restart) {
                    long now = System.nanoTime();
                    if (screen == null && now - lastCheck > 400000000L) {
                        lastCheck = now;
                        if (capture.displayChanged()) { restart = true; break; }
                    }
                    int nb = requestedBitrate;
                    if (nb > 0) {
                        requestedBitrate = 0;
                        bitrate = nb;
                        android.os.Bundle p = new android.os.Bundle();
                        p.putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, nb);
                        try { codec.setParameters(p); log("Quality now " + (nb / 100000) / 10.0 + " Mbps"); } catch (Exception e) { log("Note: live quality change not supported"); }
                    }
                    if (keyFrameRequested) {
                        // A still screen produces no frames, so a sync-frame request alone may never be answered.
                        // Restarting the encoder always sends fresh setup data plus a keyframe straight away.
                        keyFrameRequested = false;
                        restart = true;
                        break;
                    }
                    int idx = codec.dequeueOutputBuffer(info, 50000);
                    if (idx < 0) continue;
                    ByteBuffer buf = codec.getOutputBuffer(idx);
                    if (info.size > 0 && buf != null) {
                        int flags = 0;
                        if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) flags |= 1;
                        if ((info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0) flags |= 2;
                        byte[] data = new byte[info.size];
                        buf.position(info.offset);
                        buf.get(data, 0, info.size);
                        synchronized (videoLock) {
                            writeHeader(out, data.length, dw, dh, w, h, info.presentationTimeUs, flags);
                            out.write(data);
                            out.flush();
                        }
                    }
                    codec.releaseOutputBuffer(idx, false);
                }
            } finally {
                try { codec.stop(); } catch (Exception ignored) { }
                codec.release();
                if (screen == null) capture.release();
                surface.release();
            }
        }
    }

    // ------------------------------------------------------------ JPEG (fallback)
    static void streamJpeg(Capture capture, DataOutputStream out, int maxSize, int quality, int fps) throws Exception {
        long frameNs = 1000000000L / Math.max(1, fps);
        ByteArrayOutputStream jpeg = new ByteArrayOutputStream(1 << 18);
        while (running) {
            capture.measure(maxSize, 2);
            ImageReader reader = ImageReader.newInstance(capture.width, capture.height, PixelFormat.RGBA_8888, 2);
            capture.attach(reader.getSurface());
            Bitmap bmp = null;
            ByteBuffer copy = null;
            long lastSent = 0, lastCheck = 0;
            try {
                while (running) {
                    long now = System.nanoTime();
                    if (now - lastCheck > 400000000L) { lastCheck = now; if (capture.displayChanged()) break; }
                    long wait = lastSent + frameNs - now;
                    if (wait > 0) Thread.sleep(wait / 1000000L, (int) (wait % 1000000L));
                    Image img = reader.acquireLatestImage();
                    if (img == null) { Thread.sleep(4); continue; }
                    int w = img.getWidth(), h = img.getHeight();
                    Image.Plane plane = img.getPlanes()[0];
                    int stride = plane.getRowStride() / plane.getPixelStride();
                    ByteBuffer buf = plane.getBuffer();
                    int need = stride * h * 4;
                    if (buf.remaining() < need) {
                        if (copy == null || copy.capacity() < need) copy = ByteBuffer.allocateDirect(need);
                        copy.clear(); copy.put(buf); copy.position(0);
                        buf = copy;
                    }
                    if (bmp == null || bmp.getWidth() != stride || bmp.getHeight() != h)
                        bmp = Bitmap.createBitmap(stride, h, Bitmap.Config.ARGB_8888);
                    bmp.copyPixelsFromBuffer(buf);
                    img.close();
                    Bitmap frame = stride == w ? bmp : Bitmap.createBitmap(bmp, 0, 0, w, h);
                    jpeg.reset();
                    frame.compress(Bitmap.CompressFormat.JPEG, quality, jpeg);
                    if (frame != bmp) frame.recycle();
                    synchronized (videoLock) {
                        writeHeader(out, jpeg.size(), capture.deviceWidth, capture.deviceHeight, capture.width, capture.height, System.nanoTime() / 1000, 2);
                        jpeg.writeTo(out);
                        out.flush();
                    }
                    lastSent = System.nanoTime();
                }
            } finally {
                capture.release();
                reader.close();
            }
        }
    }

    static int arg(String[] a, int i, int def) {
        try { return a.length > i ? Integer.parseInt(a[i]) : def; } catch (NumberFormatException e) { return def; }
    }

    static void log(String s) {
        System.out.println(s);
        System.out.flush();
    }
}
