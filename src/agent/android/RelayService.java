package app.cicibyte.cicipda;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

/**
 * Foreground relay service, driven only by the paired Windows PC through adb (the adb shell holds android.permission.DUMP,
 * which the manifest requires of any caller; ordinary apps cannot start, stop, re-pair or unpair it).
 *
 *   START  --es key HEX64 --ei port N   store the session key (Keystore-wrapped), start the relay on 127.0.0.1:port
 *   STOP                                stop the relay AND delete the stored key
 *   UNPAIR                              same as STOP; explicit "forget this PC"
 *
 * The key is session-scoped. It is never logged. If the system restarts the service (null intent) it resumes only if a
 * session key is still stored, i.e. only inside a session the PC has not ended. Android may stop foreground services at any
 * time and, from Android 12, may refuse to start one from the background; both cases end in a clean stop and the PC
 * reconnects with a new session key.
 */
public class RelayService extends Service {
    static final String TAG = "CiciPDA";
    public static final String ACTION_START = "app.cicibyte.cicipda.START";
    public static final String ACTION_STOP = "app.cicibyte.cicipda.STOP";
    public static final String ACTION_UNPAIR = "app.cicibyte.cicipda.UNPAIR";
    public static final String VERSION = "2.1.3";
    static final String CHANNEL = "relay";
    static volatile Relay relay;

    ConnectivityManager cm;
    final Handler handler = new Handler(Looper.getMainLooper());
    static final long IDLE_LIMIT_MS = 180000;   // the PC polls every 5 s; silence this long means the PC is gone (USB unplugged, app killed)
    final Runnable watchdog = new Runnable() {
        public void run() {
            Relay r = relay;
            if (r == null) return;
            if (System.currentTimeMillis() - r.lastAuthenticated > IDLE_LIMIT_MS && r.activeConns.get() == 0) {
                Log.i(TAG, "no authenticated PC for " + (IDLE_LIMIT_MS / 1000) + " s; ending the session");
                endSession();
            } else handler.postDelayed(this, 30000);
        }
    };

    @Override public IBinder onBind(Intent i) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
        String action = intent != null ? intent.getAction() : ACTION_START;   // null intent = system restart of a sticky service

        if (ACTION_STOP.equals(action) || ACTION_UNPAIR.equals(action)) {
            // startForegroundService() contract: a started service must call startForeground() before it can stop itself.
            if (!showForeground()) { endSession(); return START_NOT_STICKY; }
            endSession();
            return START_NOT_STICKY;
        }

        byte[] key = null;
        int want;
        try {
            String hex = intent != null ? intent.getStringExtra("key") : null;
            if (hex != null) {
                if (hex.length() != 64) { Log.w(TAG, "rejected malformed pairing key"); endSession(); return START_NOT_STICKY; }
                key = new byte[32];
                for (int i = 0; i < 32; i++) key[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
                want = intent.getIntExtra("port", 0);
                PairingStore.save(this, key, want);          // survives a system restart of this service, nothing else
            } else {
                key = PairingStore.load(this);
                want = PairingStore.port(this);
            }
        } catch (Exception e) {
            Log.e(TAG, "pairing key handling failed: " + e.getClass().getSimpleName());
            endSession();
            return START_NOT_STICKY;
        }
        if (key == null) { Log.w(TAG, "no active session key; stopping"); endSession(); return START_NOT_STICKY; }
        if (!showForeground()) { endSession(); return START_NOT_STICKY; }

        stopRelay();
        try {
            relay = new Relay(key, VERSION, new Relay.StatusProvider() { public String extra() { return networkJson(); } });
            int port = relay.start(want);
            PairingStore.save(this, key, port);
            Log.i(TAG, "relay listening on 127.0.0.1:" + port);
            handler.removeCallbacks(watchdog);
            handler.postDelayed(watchdog, 30000);
        } catch (Exception e) {
            Log.e(TAG, "relay start failed: " + e.getClass().getSimpleName());
            endSession();
            return START_NOT_STICKY;
        } finally {
            java.util.Arrays.fill(key, (byte) 0);
        }
        return START_STICKY;
    }

    void stopRelay() { Relay r = relay; if (r != null) r.stop(); relay = null; }

    /** Ends the session: relay down, stored key deleted, notification removed. */
    void endSession() {
        handler.removeCallbacks(watchdog);
        stopRelay();
        PairingStore.clear(this);
        stopForeground(true);
        stopSelf();
    }

    @Override public void onDestroy() { stopRelay(); super.onDestroy(); }

    /** @return false if Android refused to promote the service to the foreground. */
    boolean showForeground() {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            Notification.Builder b;
            if (Build.VERSION.SDK_INT >= 26) {
                nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Connection sharing", NotificationManager.IMPORTANCE_LOW));
                b = new Notification.Builder(this, CHANNEL);
            } else b = new Notification.Builder(this);
            Intent open = new Intent(this, MainActivity.class);
            int piFlags = Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT : PendingIntent.FLAG_UPDATE_CURRENT;
            PendingIntent pi = PendingIntent.getActivity(this, 0, open, piFlags);
            Notification n = b.setContentTitle("CiciPDA is sharing this phone's connection")
                .setContentText("Paired Windows PC via USB debugging. Disconnect from the PC to stop.")
                .setSmallIcon(R.drawable.ic_stat).setOngoing(true).setContentIntent(pi).build();
            if (Build.VERSION.SDK_INT >= 34) startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
            else startForeground(1, n);
            return true;
        } catch (RuntimeException e) {
            // e.g. ForegroundServiceStartNotAllowedException (Android 12+), SecurityException for a missing FGS type permission
            Log.e(TAG, "cannot enter foreground: " + e.getClass().getSimpleName());
            return false;
        }
    }

    /** Structured network diagnostics reported through the authenticated status command. */
    String networkJson() {
        String transport = "none"; boolean validated = false, vpn = false, metered = true;
        try {
            Network n = cm.getActiveNetwork();
            NetworkCapabilities c = n != null ? cm.getNetworkCapabilities(n) : null;
            if (c != null) {
                if (c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) transport = "cellular";
                else if (c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) transport = "wifi";
                else if (c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) transport = "ethernet";
                else transport = "other";
                vpn = c.hasTransport(NetworkCapabilities.TRANSPORT_VPN);
                validated = c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
                metered = !c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
            }
        } catch (Exception e) { /* report what we know */ }
        return "\"network\":{\"transport\":\"" + transport + "\",\"validated\":" + validated + ",\"vpn\":" + vpn + ",\"metered\":" + metered + "}"
            + ",\"sdk\":" + Build.VERSION.SDK_INT + ",\"model\":\"" + Build.MODEL.replace("\"", "") + "\"";
    }
}
