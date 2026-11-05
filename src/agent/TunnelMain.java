import java.io.*;
import java.net.*;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * CiciPDA phone-side relay (runs via app_process, no root).
 *
 * Protocol v2 (per TCP connection):
 *   client -> agent : 16 random bytes (salt, plaintext)
 *   both sides derive  kC2S = HMAC-SHA256(sessionKey, "c2s" || salt)
 *                      kS2C = HMAC-SHA256(sessionKey, "s2c" || salt)
 *   frame            : u16 big-endian length N, then N bytes AES-256-GCM (ciphertext||tag)
 *                      nonce = 4 zero bytes || u64 big-endian counter (counter per direction, from 0)
 *   frame 1 (c2s)    : UTF-8 "host:port" target
 *   frame 1 (s2c)    : one byte status, 0 = connected, 1 = failed
 *   then data frames in both directions (plaintext up to MAX_PLAIN bytes).
 * Distinct keys per direction and per connection mean a nonce is never reused.
 * The session key is read from stdin (never from argv) and the process exits when stdin closes.
 */
public class TunnelMain {
    static final int MAX_PLAIN = 16000;
    static byte[] sessionKey;

    public static void main(String[] a) throws Exception {
        int port = Integer.parseInt(a[0]);
        final BufferedReader br = new BufferedReader(new InputStreamReader(System.in));
        String hex = br.readLine();
        if (hex == null || hex.length() != 64) { System.out.println("ERR key"); System.exit(2); }
        sessionKey = new byte[32];
        for (int i = 0; i < 32; i++) sessionKey[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        ServerSocket ss = new ServerSocket(port, 64, InetAddress.getByName("127.0.0.1"));
        Thread w = new Thread(new Runnable() {
            public void run() {
                try { while (br.readLine() != null) { } } catch (Exception e) { }
                System.exit(0);
            }
        });
        w.setDaemon(true);
        w.start();
        System.out.println("READY " + ss.getLocalPort());
        System.out.flush();
        while (true) {
            final Socket c = ss.accept();
            new Thread(new Runnable() { public void run() { handle(c); } }).start();
        }
    }

    static byte[] derive(String label, byte[] salt) throws Exception {
        Mac m = Mac.getInstance("HmacSHA256");
        m.init(new SecretKeySpec(sessionKey, "HmacSHA256"));
        m.update(label.getBytes("US-ASCII"));
        m.update(salt);
        return m.doFinal();
    }

    static byte[] nonce(long ctr) {
        byte[] n = new byte[12];
        for (int i = 0; i < 8; i++) n[11 - i] = (byte) (ctr >>> (8 * i));
        return n;
    }

    static class Chan {
        final DataInputStream in;
        final OutputStream out;
        final SecretKeySpec kSend, kRecv;
        long tx, rx;

        Chan(Socket s) throws Exception {
            in = new DataInputStream(new BufferedInputStream(s.getInputStream()));
            out = s.getOutputStream();
            byte[] salt = new byte[16];
            in.readFully(salt);
            kRecv = new SecretKeySpec(derive("c2s", salt), "AES");
            kSend = new SecretKeySpec(derive("s2c", salt), "AES");
        }

        synchronized void send(byte[] b, int off, int len) throws Exception {
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.ENCRYPT_MODE, kSend, new GCMParameterSpec(128, nonce(tx++)));
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
            if (l < 16 || l > MAX_PLAIN + 16) throw new IOException("bad frame");
            byte[] ct = new byte[l];
            in.readFully(ct);
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.DECRYPT_MODE, kRecv, new GCMParameterSpec(128, nonce(rx++)));
            return c.doFinal(ct);
        }
    }

    static void handle(final Socket c) {
        final Socket[] r = new Socket[1];
        try {
            c.setTcpNoDelay(true);
            c.setSoTimeout(10000); // handshake timeout
            final Chan ch = new Chan(c);
            String t = new String(ch.recv(), "UTF-8");
            c.setSoTimeout(0);
            int i = t.lastIndexOf(':');
            if (i <= 0) { close(c, null); return; }
            Socket s = new Socket();
            r[0] = s;
            try {
                s.connect(new InetSocketAddress(t.substring(0, i), Integer.parseInt(t.substring(i + 1))), 15000);
            } catch (Exception e) {
                ch.send(new byte[] { 1 }, 0, 1);
                close(c, s);
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
            byte[] buf = new byte[MAX_PLAIN];
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
