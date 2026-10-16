import android.content.ClipData;
import android.os.IBinder;
import android.os.SystemClock;
import android.util.Base64;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;

/**
 * Runs on the phone via app_process (shell uid has INJECT_EVENTS) and injects input through
 * InputManager, so it works on Android versions where /dev/input is not writable.
 * Also keeps the clipboard in sync with the PC.
 * Reads one command per line from stdin:
 *   d X Y | m X Y | u X Y   touch down / move / up (display pixels)
 *   k CODE                  key press (Android keycode)
 *   t BASE64                type UTF-8 text (Vietnamese etc. via clipboard + paste)
 *   c BASE64                set the phone clipboard (from the PC)
 *   p                       ping, answers "__pv_pong"
 * Writes "__pv_clip BASE64" when the phone clipboard changes.
 */
public final class AdbViewInput {
    static final String PKG = "com.android.shell";
    static Object im;
    static Method inject;
    static long downTime, lastPaste;
    static Object clipboard;
    static Method getClip, setClip;
    static String lastClip; // last text seen on / written to the phone clipboard (avoids echo)
    static final PrintStream out = new PrintStream(System.out, true, StandardCharsets.UTF_8);

    public static void main(String[] args) throws Exception {
        try {
            Class<?> c = Class.forName("android.hardware.input.InputManagerGlobal"); // Android 14+
            im = c.getMethod("getInstance").invoke(null);
            inject = c.getMethod("injectInputEvent", InputEvent.class, int.class);
        } catch (Throwable t) {
            Class<?> c = Class.forName("android.hardware.input.InputManager");
            im = c.getMethod("getInstance").invoke(null);
            inject = c.getMethod("injectInputEvent", InputEvent.class, int.class);
        }
        initClipboard();
        say("__pv_ready");
        if (args.length > 0 && args[0].equals("--check")) {
            say("__pv_clipboard " + (clipboard != null ? "yes" : "no") + " " + (getClip != null ? b64(readClip()) : ""));
            return;
        }
        lastClip = readClip();
        if (getClip != null) {
            Thread t = new Thread(AdbViewInput::watchClipboard);
            t.setDaemon(true);
            t.start();
        }

        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String line;
        while ((line = in.readLine()) != null) {
            try { handle(line.trim()); } catch (Throwable t) { /* keep going */ }
        }
    }

    static synchronized void say(String s) { out.println(s); }

    static void handle(String l) throws Exception {
        if (l.isEmpty()) return;
        String[] a = l.split(" ");
        switch (a[0]) {
            case "d": touch(MotionEvent.ACTION_DOWN, a); break;
            case "m": touch(MotionEvent.ACTION_MOVE, a); break;
            case "u": touch(MotionEvent.ACTION_UP, a); break;
            case "k": key(Integer.parseInt(a[1])); break;
            case "t": text(unb64(a[1])); break;
            case "c": writeClip(a.length > 1 ? unb64(a[1]) : ""); break;
            case "p": say("__pv_pong"); break;
        }
    }

    static void touch(int action, String[] a) throws Exception {
        long now = SystemClock.uptimeMillis();
        if (action == MotionEvent.ACTION_DOWN) downTime = now;
        MotionEvent e = MotionEvent.obtain(downTime, now, action, Float.parseFloat(a[1]), Float.parseFloat(a[2]),
                action == MotionEvent.ACTION_UP ? 0f : 1f, 1f, 0, 1f, 1f, 0, 0);
        e.setSource(InputDevice.SOURCE_TOUCHSCREEN);
        send(e);
        e.recycle();
    }

    static void key(int code) throws Exception {
        long now = SystemClock.uptimeMillis();
        send(new KeyEvent(now, now, KeyEvent.ACTION_DOWN, code, 0, 0, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD));
        send(new KeyEvent(now, now, KeyEvent.ACTION_UP, code, 0, 0, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD));
    }

    static void text(String s) throws Exception {
        KeyEvent[] evs = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD).getEvents(s.toCharArray());
        if (evs != null) { // plain ASCII: real key presses
            for (KeyEvent e : evs) send(e);
            return;
        }
        // Vietnamese / emoji / any Unicode: the virtual keyboard can't type it, so paste it.
        if (setClip != null) {
            // The app reads the clipboard a moment after the paste key: don't overwrite it before that.
            long wait = lastPaste + 40 - SystemClock.uptimeMillis();
            if (wait > 0) SystemClock.sleep(wait);
            writeClip(s);
            key(KeyEvent.KEYCODE_PASTE);
            lastPaste = SystemClock.uptimeMillis();
        }
    }

    static void send(InputEvent e) throws Exception {
        inject.invoke(im, e, 0); // INJECT_INPUT_EVENT_MODE_ASYNC: never wait for the app
    }

    // ---------- clipboard (IClipboard binder, signatures differ per Android version) ----------
    static void initClipboard() {
        try {
            IBinder b = (IBinder) Class.forName("android.os.ServiceManager").getMethod("getService", String.class).invoke(null, "clipboard");
            clipboard = Class.forName("android.content.IClipboard$Stub").getMethod("asInterface", IBinder.class).invoke(null, b);
            for (Method m : clipboard.getClass().getMethods()) {
                if (m.getName().equals("getPrimaryClip") && (getClip == null || m.getParameterCount() > getClip.getParameterCount())) getClip = m;
                if (m.getName().equals("setPrimaryClip") && (setClip == null || m.getParameterCount() > setClip.getParameterCount())) setClip = m;
            }
        } catch (Throwable t) { clipboard = null; }
    }

    /** Fills a hidden-API call: ClipData → clip, first String → package, other Strings → null, ints → 0 (user / device). */
    static Object[] fill(Method m, ClipData clip) {
        Class<?>[] p = m.getParameterTypes();
        Object[] v = new Object[p.length];
        boolean pkgDone = false;
        for (int i = 0; i < p.length; i++) {
            if (p[i] == ClipData.class) v[i] = clip;
            else if (p[i] == String.class) { v[i] = pkgDone ? null : PKG; pkgDone = true; }
            else if (p[i] == int.class) v[i] = 0;
            else if (p[i] == boolean.class) v[i] = false;
        }
        return v;
    }

    static String readClip() {
        try {
            ClipData c = (ClipData) getClip.invoke(clipboard, fill(getClip, null));
            if (c == null || c.getItemCount() == 0) return null;
            CharSequence t = c.getItemAt(0).getText();
            return t == null ? null : t.toString();
        } catch (Throwable t) { return null; }
    }

    static synchronized void writeClip(String s) {
        try {
            lastClip = s;
            setClip.invoke(clipboard, fill(setClip, ClipData.newPlainText("adbView", s)));
        } catch (Throwable t) { /* ignore */ }
    }

    static void watchClipboard() {
        while (true) {
            SystemClock.sleep(500);
            String now = readClip();
            synchronized (AdbViewInput.class) {
                if (now == null || now.equals(lastClip)) continue;
                lastClip = now;
            }
            say("__pv_clip " + b64(now));
        }
    }

    static String b64(String s) { return s == null ? "" : Base64.encodeToString(s.getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP); }
    static String unb64(String s) { return new String(Base64.decode(s, 0), StandardCharsets.UTF_8); }
}
