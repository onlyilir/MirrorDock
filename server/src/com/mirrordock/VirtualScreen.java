package com.mirrordock;

import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.os.Build;
import android.view.Surface;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;

/**
 * An extra screen on the phone that only MirrorDock shows: either one app in its own window,
 * or a whole second "desktop" with its own home screen. Spec: "app:WxH/dpi:package" or "desktop:WxH/dpi".
 */
final class VirtualScreen {
    final int width, height, dpi;
    final boolean desktop;
    final String app;
    private VirtualDisplay display;

    // VirtualDisplay flag values (DisplayManager.VIRTUAL_DISPLAY_FLAG_*).
    private static final int PUBLIC = 1, OWN_CONTENT_ONLY = 1 << 3, SUPPORTS_TOUCH = 1 << 6, ROTATES_WITH_CONTENT = 1 << 7,
            DESTROY_CONTENT_ON_REMOVAL = 1 << 8, SHOULD_SHOW_SYSTEM_DECORATIONS = 1 << 9, TRUSTED = 1 << 10,
            OWN_DISPLAY_GROUP = 1 << 11, ALWAYS_UNLOCKED = 1 << 12, TOUCH_FEEDBACK_DISABLED = 1 << 13,
            OWN_FOCUS = 1 << 14, DEVICE_DISPLAY_GROUP = 1 << 15;

    static VirtualScreen parse(String spec) {
        if (spec == null || spec.equals("-") || spec.equals("none") || spec.length() == 0) return null;
        String[] p = spec.split(":", 3);
        String[] size = p[1].split("/");
        String[] wh = size[0].split("x");
        return new VirtualScreen(Integer.parseInt(wh[0]), Integer.parseInt(wh[1]), Integer.parseInt(size[1]),
                p[0].equals("desktop"), p.length > 2 ? p[2] : null);
    }

    private VirtualScreen(int w, int h, int dpi, boolean desktop, String app) {
        this.width = w & ~7; this.height = h & ~7; this.dpi = dpi; this.desktop = desktop; this.app = app;
    }

    int displayId() { return display == null ? 0 : display.getDisplay().getDisplayId(); }

    /** Creates the display on first use; later calls just move it to a new encoder surface (keeps the apps running). */
    void attach(Surface surface) throws Exception {
        if (display != null) { display.setSurface(surface); return; }
        int flags = PUBLIC | OWN_CONTENT_ONLY | SUPPORTS_TOUCH | ROTATES_WITH_CONTENT | DESTROY_CONTENT_ON_REMOVAL;
        if (desktop) flags |= SHOULD_SHOW_SYSTEM_DECORATIONS;
        if (Build.VERSION.SDK_INT >= 33) flags |= TRUSTED | OWN_DISPLAY_GROUP | ALWAYS_UNLOCKED | TOUCH_FEEDBACK_DISABLED;
        if (Build.VERSION.SDK_INT >= 34) flags |= OWN_FOCUS | DEVICE_DISPLAY_GROUP;
        DisplayManager dm = DisplayManager.class.getDeclaredConstructor(Context.class).newInstance(FakeContext.get());
        display = dm.createVirtualDisplay("MirrorDock", width, height, dpi, surface, flags);
        Server.log("Opened " + (desktop ? "second screen" : "app window") + " " + width + "x" + height + " (display " + displayId() + ")");
        if (app != null) launch(app, displayId());
    }

    void release() {
        if (display != null) { try { display.release(); } catch (Exception ignored) { } display = null; }
    }

    static void launch(String pkg, int displayId) {
        try {
            String component = null;
            Process p = Runtime.getRuntime().exec(new String[] { "cmd", "package", "resolve-activity", "--brief",
                    "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", pkg });
            BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream()));
            for (String line; (line = r.readLine()) != null; ) if (line.contains("/")) component = line.trim();
            p.waitFor();
            if (component == null) { Server.log("ERROR: " + pkg + " has no app to open"); return; }
            Process s = Runtime.getRuntime().exec(new String[] { "am", "start", "--display", String.valueOf(displayId),
                    "--windowingMode", "1", "-f", "0x10000000", "-n", component });
            s.waitFor();
            Server.log("Started " + component);
        } catch (Exception e) {
            Server.log("ERROR: couldn't start " + pkg + ": " + e);
        }
    }

    /** Prints "package|label" for every app with a launcher icon (used by the PC's app picker). */
    static void listApps() {
        try {
            PackageManager pm = FakeContext.get().getPackageManager();
            Intent i = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
            List<ResolveInfo> list = pm.queryIntentActivities(i, 0);
            final PackageManager fpm = pm;
            Collections.sort(list, new Comparator<ResolveInfo>() {
                @Override public int compare(ResolveInfo a, ResolveInfo b) {
                    return a.loadLabel(fpm).toString().compareToIgnoreCase(b.loadLabel(fpm).toString());
                }
            });
            for (ResolveInfo ri : list)
                System.out.println("APP|" + ri.activityInfo.packageName + "|" + ri.loadLabel(pm).toString().replace('|', ' '));
        } catch (Throwable t) {
            System.out.println("ERROR: couldn't list apps: " + t);
        }
        System.out.flush();
    }
}
