package com.mirrordock;

import android.app.Notification;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.os.IBinder;
import android.service.notification.StatusBarNotification;

import java.lang.reflect.Method;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Watches the phone's notifications (shell may read them) and forwards new ones to the PC. */
final class Notifications {
    interface Sink { void send(String pkg, String app, String title, String text); }

    static void startThread(final Sink sink) {
        Thread t = new Thread(new Runnable() {
            @Override public void run() { loop(sink); }
        }, "notifications");
        t.setDaemon(true);
        t.start();
    }

    static void loop(Sink sink) {
        Object nm;
        Method get;
        try {
            Class<?> sm = Class.forName("android.os.ServiceManager");
            IBinder b = (IBinder) sm.getMethod("getService", String.class).invoke(null, "notification");
            nm = Class.forName("android.app.INotificationManager$Stub").getMethod("asInterface", IBinder.class).invoke(null, b);
            get = null;
            for (Method m : nm.getClass().getMethods()) {
                if (m.getName().equals("getActiveNotificationsWithAttribution")
                        || (get == null && m.getName().equals("getActiveNotifications") && m.getParameterTypes().length == 1)) get = m;
            }
            if (get == null) throw new NoSuchMethodException("getActiveNotifications");
        } catch (Throwable t) {
            Server.log("ERROR: notifications unavailable: " + t);
            return;
        }
        Set<String> seen = new HashSet<String>();
        Map<String, String> labels = new HashMap<String, String>();
        PackageManager pm = null;
        try { pm = FakeContext.get().getPackageManager(); } catch (Throwable ignored) { }
        boolean first = true;
        boolean useDump = false;
        Server.log("Notification sync on");
        while (Server.running) {
            if (useDump) {
                // Fallback for phones that refuse the direct call: read "dumpsys notification" (allowed for the shell user).
                try {
                    Set<String> now = new HashSet<String>();
                    for (String[] n : fromDumpsys()) {
                        String key = n[0];
                        now.add(key);
                        if (first || seen.contains(key)) continue;
                        String pkg = n[1];
                        String label = labels.get(pkg);
                        if (label == null) {
                            label = pkg;
                            try { ApplicationInfo ai = pm.getApplicationInfo(pkg, 0); label = pm.getApplicationLabel(ai).toString(); } catch (Throwable ignored) { }
                            labels.put(pkg, label);
                        }
                        sink.send(pkg, label, n[2], n[3]);
                    }
                    seen = now;
                    first = false;
                } catch (Throwable t) {
                    Server.log("ERROR: notifications stopped: " + t);
                    return;
                }
                try { Thread.sleep(2500); } catch (InterruptedException e) { return; }
                continue;
            }
            try {
                Object[] args = new Object[get.getParameterTypes().length];
                args[0] = FakeContext.PACKAGE;
                Object r = get.invoke(nm, args);
                StatusBarNotification[] list;
                if (r instanceof StatusBarNotification[]) list = (StatusBarNotification[]) r;
                else {
                    List<?> l = (List<?>) r.getClass().getMethod("getList").invoke(r);
                    list = l.toArray(new StatusBarNotification[0]);
                }
                Set<String> now = new HashSet<String>();
                for (StatusBarNotification sbn : list) {
                    String key = sbn.getKey() + "|" + sbn.getPostTime();
                    now.add(key);
                    if (first || seen.contains(key)) continue;
                    Notification n = sbn.getNotification();
                    if ((n.flags & (Notification.FLAG_ONGOING_EVENT | Notification.FLAG_GROUP_SUMMARY | Notification.FLAG_FOREGROUND_SERVICE)) != 0) continue;
                    Bundle x = n.extras;
                    String title = str(x.getCharSequence(Notification.EXTRA_TITLE));
                    String text = str(x.getCharSequence(Notification.EXTRA_BIG_TEXT));
                    if (text.length() == 0) text = str(x.getCharSequence(Notification.EXTRA_TEXT));
                    if (title.length() == 0 && text.length() == 0) continue;
                    String pkg = sbn.getPackageName();
                    String label = labels.get(pkg);
                    if (label == null) {
                        label = pkg;
                        try { ApplicationInfo ai = pm.getApplicationInfo(pkg, 0); label = pm.getApplicationLabel(ai).toString(); } catch (Throwable ignored) { }
                        labels.put(pkg, label);
                    }
                    sink.send(pkg, label, title, text);
                }
                seen = now;
                first = false;
            } catch (Throwable t) {
                Throwable c = t.getCause() != null ? t.getCause() : t;
                if (c instanceof SecurityException) { useDump = true; continue; }
                Server.log("ERROR: notifications stopped: " + c);
                return;
            }
            try { Thread.sleep(1500); } catch (InterruptedException e) { return; }
        }
    }

    private static final java.util.regex.Pattern RECORD = java.util.regex.Pattern.compile(
            "NotificationRecord\\(0x[0-9a-f]+: pkg=(\\S+) .*?key=(\\S+): Notification\\(.*?\\bflags=(0x[0-9a-fA-F]+|\\d+)");
    private static final java.util.regex.Pattern WHEN = java.util.regex.Pattern.compile("^\\s+when=(\\d+)");
    private static final java.util.regex.Pattern EXTRA = java.util.regex.Pattern.compile("^\\s+android\\.(title|text|bigText)=\\w+ \\((.*)");

    /** Returns {id, package, title, text} for each current, non-ongoing notification. */
    static java.util.List<String[]> fromDumpsys() throws Exception {
        java.util.List<String[]> out = new java.util.ArrayList<String[]>();
        Process p = Runtime.getRuntime().exec(new String[] { "dumpsys", "notification", "--noredact" });
        java.io.BufferedReader r = new java.io.BufferedReader(new java.io.InputStreamReader(p.getInputStream(), "UTF-8"));
        String pkg = null, key = null, when = "", title = "", text = "", big = "";
        boolean skip = false;
        Set<String> keys = new HashSet<String>();
        for (String line; (line = r.readLine()) != null; ) {
            java.util.regex.Matcher m = RECORD.matcher(line);
            if (m.find()) {
                add(out, keys, pkg, key, when, title, text, big, skip);
                pkg = m.group(1); key = m.group(2); when = ""; title = ""; text = ""; big = "";
                String f = m.group(3);
                long flags = f.startsWith("0x") ? Long.parseLong(f.substring(2), 16) : Long.parseLong(f);
                skip = (flags & (Notification.FLAG_ONGOING_EVENT | Notification.FLAG_GROUP_SUMMARY | Notification.FLAG_FOREGROUND_SERVICE)) != 0;
                continue;
            }
            if (pkg == null) continue;
            m = WHEN.matcher(line);
            if (m.find() && when.length() == 0) { when = m.group(1); continue; }
            m = EXTRA.matcher(line);
            if (m.find()) {
                String v = m.group(2);
                if (v.endsWith(")")) v = v.substring(0, v.length() - 1);
                if (m.group(1).equals("title")) title = v; else if (m.group(1).equals("text")) text = v; else big = v;
            }
        }
        add(out, keys, pkg, key, when, title, text, big, skip);
        p.waitFor();
        return out;
    }

    private static void add(java.util.List<String[]> out, Set<String> keys, String pkg, String key, String when,
                            String title, String text, String big, boolean skip) {
        if (pkg == null || skip) return;
        if (big.length() > 0) text = big;
        if (title.length() == 0 && text.length() == 0) return;
        if (!keys.add(key + "|" + when)) return;
        out.add(new String[] { key + "|" + when, pkg, title, text });
    }

    private static String str(CharSequence s) { return s == null ? "" : s.toString(); }
}
