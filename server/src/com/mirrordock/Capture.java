package com.mirrordock;

import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.IBinder;
import android.view.Surface;

import java.lang.reflect.Method;

/** Mirrors the phone's main display into a Surface (encoder input or ImageReader), using hidden APIs open to the shell user. */
final class Capture {
    int deviceWidth, deviceHeight, width, height;
    private Object virtualDisplay;   // android.hardware.display.VirtualDisplay (Android 14+ path)
    private IBinder surfaceDisplay;  // SurfaceControl display token (older path)
    private int layerStack;

    /** Reads the display size and picks the capture size (long side <= maxSize, multiple of align). */
    void measure(int maxSize, int align) throws Exception {
        int[] info = displayInfo();
        deviceWidth = info[0];
        deviceHeight = info[1];
        layerStack = info[3];
        double scale = 1.0;
        int longSide = Math.max(deviceWidth, deviceHeight);
        if (maxSize > 0 && longSide > maxSize) scale = (double) maxSize / longSide;
        width = Math.max(align, ((int) Math.round(deviceWidth * scale)) / align * align);
        height = Math.max(align, ((int) Math.round(deviceHeight * scale)) / align * align);
    }

    void attach(Surface surface) throws Exception {
        Exception first = null;
        if (Build.VERSION.SDK_INT >= 34) {
            try { startDisplayManager(surface); }
            catch (Exception e) { first = e; startSurfaceControl(surface); }
        } else {
            try { startSurfaceControl(surface); }
            catch (Exception e) { first = e; startDisplayManager(surface); }
        }
        if (first != null) Server.log("Capture fallback used (" + first.getClass().getSimpleName() + ")");
        Server.log("Capturing " + deviceWidth + "x" + deviceHeight + " as " + width + "x" + height);
    }

    private void startDisplayManager(Surface surface) throws Exception {
        Method m = DisplayManager.class.getMethod("createVirtualDisplay",
                String.class, int.class, int.class, int.class, Surface.class);
        virtualDisplay = m.invoke(null, "mirrordock", width, height, 0, surface);
    }

    private void startSurfaceControl(Surface surface) throws Exception {
        Class<?> sc = Class.forName("android.view.SurfaceControl");
        boolean secure = Build.VERSION.SDK_INT < 30;
        surfaceDisplay = (IBinder) sc.getMethod("createDisplay", String.class, boolean.class).invoke(null, "mirrordock", secure);
        sc.getMethod("openTransaction").invoke(null);
        try {
            sc.getMethod("setDisplaySurface", IBinder.class, Surface.class).invoke(null, surfaceDisplay, surface);
            sc.getMethod("setDisplayProjection", IBinder.class, int.class, Rect.class, Rect.class)
                    .invoke(null, surfaceDisplay, 0, new Rect(0, 0, deviceWidth, deviceHeight), new Rect(0, 0, width, height));
            sc.getMethod("setDisplayLayerStack", IBinder.class, int.class).invoke(null, surfaceDisplay, layerStack);
        } finally {
            sc.getMethod("closeTransaction").invoke(null);
        }
    }

    boolean displayChanged() {
        try {
            int[] info = displayInfo();
            return info[0] != deviceWidth || info[1] != deviceHeight;
        } catch (Exception e) {
            return false;
        }
    }

    synchronized void release() {
        try {
            if (virtualDisplay != null) virtualDisplay.getClass().getMethod("release").invoke(virtualDisplay);
        } catch (Exception ignored) { }
        virtualDisplay = null;
        try {
            if (surfaceDisplay != null)
                Class.forName("android.view.SurfaceControl").getMethod("destroyDisplay", IBinder.class).invoke(null, surfaceDisplay);
        } catch (Exception ignored) { }
        surfaceDisplay = null;
    }

    /** Returns {width, height, rotation, layerStack} of display 0. */
    static int[] displayInfo() throws Exception {
        Class<?> dmg = Class.forName("android.hardware.display.DisplayManagerGlobal");
        Object global = dmg.getMethod("getInstance").invoke(null);
        Object info = dmg.getMethod("getDisplayInfo", int.class).invoke(global, 0);
        Class<?> c = info.getClass();
        return new int[] {
                c.getField("logicalWidth").getInt(info),
                c.getField("logicalHeight").getInt(info),
                c.getField("rotation").getInt(info),
                c.getField("layerStack").getInt(info)
        };
    }
}
