import android.os.SystemClock;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;

/**
 * Runs on the phone via app_process (shell uid has INJECT_EVENTS) and injects input through
 * InputManager, so it works on Android versions where /dev/input is not writable.
 * Reads one command per line from stdin:
 *   d X Y | m X Y | u X Y   touch down / move / up (display pixels)
 *   k CODE                  key press (Android keycode)
 *   t BASE64                type UTF-8 text
 *   p                       ping, answers "__pv_pong"
 */
public final class AdbViewInput {
    static Object im;
    static Method inject;
    static long downTime;

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
        System.out.println("__pv_ready");
        System.out.flush();
        if (args.length > 0 && args[0].equals("--check")) return;

        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String line;
        while ((line = in.readLine()) != null) {
            try { handle(line.trim()); } catch (Throwable t) { /* keep going */ }
        }
    }

    static void handle(String l) throws Exception {
        if (l.isEmpty()) return;
        String[] a = l.split(" ");
        switch (a[0]) {
            case "d": touch(MotionEvent.ACTION_DOWN, a); break;
            case "m": touch(MotionEvent.ACTION_MOVE, a); break;
            case "u": touch(MotionEvent.ACTION_UP, a); break;
            case "k": key(Integer.parseInt(a[1])); break;
            case "t": text(new String(android.util.Base64.decode(a[1], 0), StandardCharsets.UTF_8)); break;
            case "p": System.out.println("__pv_pong"); System.out.flush(); break;
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
        KeyCharacterMap map = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);
        KeyEvent[] evs = map.getEvents(s.toCharArray());
        if (evs != null) {
            for (KeyEvent e : evs) send(e);
            return;
        }
        // Characters the virtual keyboard can't produce (e.g. Vietnamese): one by one as raw characters
        long now = SystemClock.uptimeMillis();
        send(new KeyEvent(now, s, KeyCharacterMap.VIRTUAL_KEYBOARD, 0));
    }

    static void send(InputEvent e) throws Exception {
        inject.invoke(im, e, 0); // INJECT_INPUT_EVENT_MODE_ASYNC: never wait for the app
    }
}
