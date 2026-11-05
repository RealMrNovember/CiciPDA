package app.cicibyte.cicipda;

import java.io.*;

/** Legacy launcher (adb shell app_process) used when the companion APK is not installed. Key arrives on stdin. */
public class TunnelMain {
    public static void main(String[] a) throws Exception {
        int port = Integer.parseInt(a[0]);
        final BufferedReader br = new BufferedReader(new InputStreamReader(System.in));
        String hex = br.readLine();
        if (hex == null || hex.length() != 64) { System.out.println("ERR key"); System.exit(2); }
        byte[] k = new byte[32];
        for (int i = 0; i < 32; i++) k[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        Relay relay = new Relay(k, "shell", null);
        int p = relay.start(port);
        Thread w = new Thread(new Runnable() {
            public void run() {
                try { while (br.readLine() != null) { } } catch (Exception e) { }
                System.exit(0);
            }
        });
        w.setDaemon(true);
        w.start();
        System.out.println("READY " + p);
        System.out.flush();
        Thread.currentThread().join();
    }
}
