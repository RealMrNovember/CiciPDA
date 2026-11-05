import java.io.*;
import java.net.*;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

public class TunnelMain {
    static SecretKeySpec key;

    public static void main(String[] a) throws Exception {
        int port = Integer.parseInt(a[0]);
        final BufferedReader br = new BufferedReader(new InputStreamReader(System.in));
        String hex = br.readLine();
        byte[] k = new byte[hex.length() / 2];
        for (int i = 0; i < k.length; i++) k[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        key = new SecretKeySpec(k, "AES");
        ServerSocket ss = new ServerSocket(port, 64, InetAddress.getByName("127.0.0.1"));
        Thread w = new Thread(new Runnable() {
            public void run() {
                try { while (br.readLine() != null) { } } catch (Exception e) { }
                System.exit(0);
            }
        });
        w.setDaemon(true);
        w.start();
        System.out.println("READY");
        System.out.flush();
        while (true) {
            final Socket c = ss.accept();
            new Thread(new Runnable() { public void run() { handle(c); } }).start();
        }
    }

    static byte[] nonce(int dir, long ctr) {
        byte[] n = new byte[12];
        n[3] = (byte) dir;
        for (int i = 0; i < 8; i++) n[11 - i] = (byte) (ctr >>> (8 * i));
        return n;
    }

    static class Chan {
        final DataInputStream in;
        final OutputStream out;
        long tx, rx;

        Chan(Socket s) throws IOException {
            in = new DataInputStream(new BufferedInputStream(s.getInputStream()));
            out = s.getOutputStream();
        }

        synchronized void send(byte[] b, int off, int len) throws Exception {
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.ENCRYPT_MODE, key, new GCMParameterSpec(128, nonce(1, tx++)));
            byte[] ct = c.doFinal(b, off, len);
            byte[] f = new byte[2 + ct.length];
            f[0] = (byte) (ct.length >> 8);
            f[1] = (byte) ct.length;
            System.arraycopy(ct, 0, f, 2, ct.length);
            out.write(f);
            out.flush();
        }

        byte[] recv() throws Exception {
            int l = in.readUnsignedShort();
            byte[] ct = new byte[l];
            in.readFully(ct);
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(128, nonce(0, rx++)));
            return c.doFinal(ct);
        }
    }

    static void handle(final Socket c) {
        final Socket[] r = new Socket[1];
        try {
            c.setTcpNoDelay(true);
            final Chan ch = new Chan(c);
            String t = new String(ch.recv(), "UTF-8");
            int i = t.lastIndexOf(':');
            Socket s = new Socket();
            r[0] = s;
            try {
                s.connect(new InetSocketAddress(t.substring(0, i), Integer.parseInt(t.substring(i + 1))), 15000);
            } catch (Exception e) {
                ch.send(new byte[] { 1 }, 0, 1);
                c.close();
                return;
            }
            s.setTcpNoDelay(true);
            ch.send(new byte[] { 0 }, 0, 1);
            final Socket rs = s;
            final OutputStream rout = s.getOutputStream();
            Thread up = new Thread(new Runnable() {
                public void run() {
                    try {
                        while (true) { byte[] p = ch.recv(); rout.write(p); rout.flush(); }
                    } catch (Exception e) { }
                    close(c, rs);
                }
            });
            up.start();
            InputStream rin = s.getInputStream();
            byte[] buf = new byte[16000];
            try {
                int n;
                while ((n = rin.read(buf)) >= 0) ch.send(buf, 0, n);
            } catch (Exception e) { }
            close(c, s);
        } catch (Exception e) {
            close(c, r[0]);
        }
    }

    static void close(Socket a, Socket b) {
        try { a.close(); } catch (Exception e) { }
        try { if (b != null) b.close(); } catch (Exception e) { }
    }
}
