package com.mirrordock;

import android.app.Application;
import android.content.Context;
import android.content.pm.ApplicationInfo;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;

/** app_process has no Android "app" around it. Some APIs (audio, clipboard) expect one, so fake a minimal one. */
final class Workarounds {
    static Object activityThread;
    static Context systemContext;

    static void apply() {
        try {
            Class<?> at = Class.forName("android.app.ActivityThread");
            Constructor<?> c = at.getDeclaredConstructor();
            c.setAccessible(true);
            activityThread = c.newInstance();
            Field cur = at.getDeclaredField("sCurrentActivityThread");
            cur.setAccessible(true);
            cur.set(null, activityThread);

            Class<?> bindData = Class.forName("android.app.ActivityThread$AppBindData");
            Constructor<?> bc = bindData.getDeclaredConstructor();
            bc.setAccessible(true);
            Object data = bc.newInstance();
            ApplicationInfo ai = new ApplicationInfo();
            ai.packageName = FakeContext.PACKAGE;
            Field appInfo = bindData.getDeclaredField("appInfo");
            appInfo.setAccessible(true);
            appInfo.set(data, ai);
            Field bound = at.getDeclaredField("mBoundApplication");
            bound.setAccessible(true);
            bound.set(activityThread, data);

            // Android 12+ (and Samsung's display code) read the configuration through this controller.
            try {
                Class<?> ccClass = Class.forName("android.app.ConfigurationController");
                Class<?> internal = Class.forName("android.app.ActivityThreadInternal");
                Constructor<?> cc = ccClass.getDeclaredConstructor(internal);
                cc.setAccessible(true);
                Object controller = cc.newInstance(activityThread);
                Field f = at.getDeclaredField("mConfigurationController");
                f.setAccessible(true);
                f.set(activityThread, controller);
            } catch (ClassNotFoundException ignored) { /* older Android */ }

            Method sys = at.getDeclaredMethod("getSystemContext");
            sys.setAccessible(true);
            systemContext = (Context) sys.invoke(activityThread);
        } catch (Throwable t) {
            Throwable c = t.getCause() != null ? t.getCause() : t;
            Server.log("Note: app workaround partly failed (" + c + ")");
        }
        try {
            Application app = new Application();
            Method attach = android.content.ContextWrapper.class.getDeclaredMethod("attachBaseContext", Context.class);
            attach.setAccessible(true);
            attach.invoke(app, FakeContext.get());
            Field init = Class.forName("android.app.ActivityThread").getDeclaredField("mInitialApplication");
            init.setAccessible(true);
            init.set(activityThread, app);
        } catch (Throwable t) {
            Server.log("Note: application workaround failed (" + t + ")");
        }
    }
}
