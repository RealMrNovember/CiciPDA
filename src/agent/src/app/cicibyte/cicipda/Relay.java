package app.cicibyte.cicipda;

import java.io.*;
import java.net.*;
import java.security.SecureRandom;
import java.util.concurrent.atomic.AtomicLong;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * CiciPDA phone-side relay, protocol v3. Shared by the companion APK service and the legacy app_process launcher.
 *
 * Per TCP connection:
 *   client -> relay : 16 random bytes (clientSalt, plaintext)
 *   relay  -> client: 16 random bytes (serverNonce, plaintext)
 *   kC2S = HMAC-SHA256(pairingKey, "c2s" || clientSalt || serverNonce)   kS2C likewise with "s2c"
 *   frame           : u16 big-endian N, then N bytes AES-256-GCM(ciphertext || 16-byte tag); nonce = 4 zero bytes || u64 counter
 *   first c2s frame : command  "T:host:port" (TCP stream) | "U" (UDP datagram relay) | "S" (status JSON)
 *   first s2c frame : T: one status byte (0 ok, 1 failed)   U: one byte 0   S: UTF-8 JSON
 *   UDP frames (both directions): u8 family (4 or 6) | 4 or 16 address bytes | u16 port | payload
 * Keys are unique per connection and direction, counters only increase, and the relay contributes fresh randomness,
 * so captured traffic cannot be replayed into a new connection and a nonce is never reused.
 */
public final class Relay {
    public static final int PROTOCOL = 3;
    public static final int MAX_PLAIN = 15000;
    public interface StatusProvider { String extra(); }

    final byte[] key;
    final SecureRandom rnd = new SecureRandom();
    ServerSocket server;
    volatile boolean running;
    public final AtomicLong activeConns = new AtomicLong(), totalConns = new AtomicLong(), failedConns = new AtomicLong();
    public final AtomicLong bytesUp = new AtomicLong(), bytesDown = new AtomicLong(), udpDatagrams = new AtomicLong();
    final long started = System.currentTimeMillis();
    final String version;
    final StatusProvider provider;

    public Relay(byte[] key, String version, StatusProvider provider) { this.key = key; this.version = version; this.provider = provider; }

    public int start(int port) throws IOException {
        server = new ServerSocket(port, 64, InetAddress.getByName("127.0.0.1"));
        running = true;
        Thread t = new Thread(new Runnable() { public void run() { acceptLoop(); } }, "relay-accept");
        t.setDaemon(true);
        t.start();
        return server.getLocalPort();
    }

    public void stop() {
        running = false;
        try { if (server != null) server.close(); } catch (Exception e) { }
    }

    void acceptLoop() {
        while (running) {
            try {
                final Socket c = server.accept();
                new Thread(new Runnable() { public void run() { handle(c); } }, "relay-conn").start();
            } catch (IOException e) { if (running) { try { Thread.sleep(50); } catch (Exception x) { } } }
        }
    }

    byte[] derive(String label, byte[] a, byte[] b) throws Exception {
        Mac m = Mac.getInstance("HmacSHA256");
        m.init(new SecretKeySpec(key, "HmacSHA256"));
        m.update(label.getBytes("US-ASCII")); m.update(a); m.update(b);
        return m.doFinal();
    }

    static byte[] nonce(long ctr) {
        byte[] n = new byte[12];
        for (int i = 0; i < 8; i++) n[11 - i] = (byte) (ctr >>> (8 * i));
        return n;
    }

    final class Chan {
        final DataInputStream in; final OutputStream out; final SecretKeySpec kSend, kRecv; long tx, rx;
        Chan(Socket s) throws Exception {
            in = new DataInputStream(new BufferedInputStream(s.getInputStream()));
            out = s.getOutputStream();
            byte[] salt = new byte[16]; in.readFully(salt);
            byte[] sn = new byte[16]; rnd.nextBytes(sn);
            out.write(sn); out.flush();
            kRecv = new SecretKeySpec(derive("c2s", salt, sn), "AES");
            kSend = new SecretKeySpec(derive("s2c", salt, sn), "AES");
        }
        synchronized void send(byte[] b, int off, int len) throws Exception {
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.ENCRYPT_MODE, kSend, new GCMParameterSpec(128, nonce(tx++)));
            byte[] ct = c.doFinal(b, off, len);
            byte[] f = new byte[2 + ct.length];
            f[0] = (byte) (ct.length >> 8); f[1] = (byte) ct.length;
            System.arraycopy(ct, 0, f, 2, ct.length);
            out.write(f); out.flush();
        }
        byte[] recv() throws Exception {
            int l = in.readUnsignedShort();
            if (l < 16 || l > MAX_PLAIN + 16) throw new IOException("bad frame");
            byte[] ct = new byte[l]; in.readFully(ct);
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.DECRYPT_MODE, kRecv, new GCMParameterSpec(128, nonce(rx++)));
            return c.doFinal(ct);
        }
    }

    String statusJson() {
        String extra = provider != null ? provider.extra() : "";
        return "{\"protocol\":" + PROTOCOL + ",\"version\":\"" + version + "\",\"uptimeMs\":" + (System.currentTimeMillis() - started)
            + ",\"activeConnections\":" + activeConns.get() + ",\"totalConnections\":" + totalConns.get() + ",\"failedConnections\":" + failedConns.get()
            + ",\"bytesUp\":" + bytesUp.get() + ",\"bytesDown\":" + bytesDown.get() + ",\"udpDatagrams\":" + udpDatagrams.get()
            + (extra.length() > 0 ? "," + extra : "") + "}";
    }

    void handle(final Socket c) {
        final Socket[] r = new Socket[1];
        activeConns.incrementAndGet(); totalConns.incrementAndGet();
        try {
            c.setTcpNoDelay(true);
            c.setSoTimeout(10000);
            final Chan ch = new Chan(c);
            String cmd = new String(ch.recv(), "UTF-8");
            c.setSoTimeout(0);
            if (cmd.equals("S")) { byte[] j = statusJson().getBytes("UTF-8"); ch.send(j, 0, j.length); return; }
            if (cmd.equals("U")) { udp(c, ch); return; }
            if (!cmd.startsWith("T:")) { return; }
            String t = cmd.substring(2);
            int i = t.lastIndexOf(':');
            if (i <= 0) return;
            Socket s = new Socket();
            r[0] = s;
            try {
                s.connect(new InetSocketAddress(t.substring(0, i), Integer.parseInt(t.substring(i + 1))), 15000);
            } catch (Exception e) {
                failedConns.incrementAndGet();
                ch.send(new byte[] { 1 }, 0, 1);
                return;
            }
            s.setTcpNoDelay(true);
            ch.send(new byte[] { 0 }, 0, 1);
            final Socket rs = s;
            final OutputStream rout = s.getOutputStream();
            Thread up = new Thread(new Runnable() {
                public void run() {
                    try { while (true) { byte[] p = ch.recv(); rout.write(p); rout.flush(); bytesUp.addAndGet(p.length); } } catch (Exception e) { }
                    close(c, rs);
                }
            });
            up.start();
            InputStream rin = s.getInputStream();
            byte[] buf = new byte[MAX_PLAIN];
            try { int n; while ((n = rin.read(buf)) >= 0) { ch.send(buf, 0, n); bytesDown.addAndGet(n); } } catch (Exception e) { }
        } catch (Exception e) {
            /* authentication failures and resets end here silently by design */
        } finally {
            close(c, r[0]);
            activeConns.decrementAndGet();
        }
    }

    void udp(final Socket c, final Chan ch) throws Exception {
        final DatagramSocket ds = new DatagramSocket();
        ch.send(new byte[] { 0 }, 0, 1);
        Thread down = new Thread(new Runnable() {
            public void run() {
                byte[] buf = new byte[65535];
                try {
                    while (true) {
                        DatagramPacket p = new DatagramPacket(buf, buf.length);
                        ds.receive(p);
                        byte[] a = p.getAddress().getAddress();
                        int len = p.getLength();
                        if (len > MAX_PLAIN - 19) continue;
                        byte[] f = new byte[1 + a.length + 2 + len];
                        f[0] = (byte) (a.length == 4 ? 4 : 6);
                        System.arraycopy(a, 0, f, 1, a.length);
                        f[1 + a.length] = (byte) (p.getPort() >> 8); f[2 + a.length] = (byte) p.getPort();
                        System.arraycopy(p.getData(), 0, f, 3 + a.length, len);
                        ch.send(f, 0, f.length);
                    }
                } catch (Exception e) { }
                close(c, null);
            }
        });
        down.setDaemon(true);
        down.start();
        try {
            while (true) {
                byte[] f = ch.recv();
                int fam = f[0] == 4 ? 4 : 16;
                if (f.length < 1 + fam + 2) continue;
                byte[] a = new byte[fam];
                System.arraycopy(f, 1, a, 0, fam);
                int port = ((f[1 + fam] & 0xff) << 8) | (f[2 + fam] & 0xff);
                int off = 3 + fam;
                ds.send(new DatagramPacket(f, off, f.length - off, new InetSocketAddress(InetAddress.getByAddress(a), port)));
                udpDatagrams.incrementAndGet(); bytesUp.addAndGet(f.length - off);
            }
        } catch (Exception e) { }
        ds.close();
    }

    static void close(Socket a, Socket b) {
        try { a.close(); } catch (Exception e) { }
        try { if (b != null) b.close(); } catch (Exception e) { }
    }
}
