package app.cicibyte.cicipda;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;

/**
 * Foreground relay service. Started and stopped by the paired Windows PC through adb (the shell holds android.permission.DUMP).
 *   am start-foreground-service -n app.cicibyte.cicipda/.RelayService -a app.cicibyte.cicipda.START --es key HEX --ei port N
 * Android limits background services, so this runs as a visible foreground service for as long as the user is connected.
 * If the system restarts it, it resumes with the stored pairing key and port; it never exposes anything beyond 127.0.0.1.
 */
public class RelayService extends Service {
    static final String TAG = "CiciPDA";
    public static final String ACTION_START = "app.cicibyte.cicipda.START";
    public static final String ACTION_STOP = "app.cicibyte.cicipda.STOP";
    public static final String VERSION = "2.0.0";
    static final String CHANNEL = "relay";
    static volatile Relay relay;
    static volatile int port;

    ConnectivityManager cm;

    @Override public IBinder onBind(Intent i) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
        SharedPreferences sp = getSharedPreferences("pairing", MODE_PRIVATE);
        String action = intent != null ? intent.getAction() : ACTION_START; // null intent = system restart
        showForeground();
        if (ACTION_STOP.equals(action)) { stopRelay(); stopForeground(true); stopSelf(); return START_NOT_STICKY; }

        String hex = intent != null ? intent.getStringExtra("key") : null;
        if (hex != null) {
            if (hex.length() != 64) { Log.w(TAG, "rejected pairing key"); return START_NOT_STICKY; }
            sp.edit().putString("key", hex).apply();           // (re)pair: the adb-authorized PC is the trust root
        } else hex = sp.getString("key", null);
        if (hex == null) { Log.w(TAG, "no pairing key"); stopSelf(); return START_NOT_STICKY; }

        int want = intent != null ? intent.getIntExtra("port", 0) : sp.getInt("port", 0);
        stopRelay();
        try {
            byte[] k = new byte[32];
            for (int i = 0; i < 32; i++) k[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
            relay = new Relay(k, VERSION, new Relay.StatusProvider() { public String extra() { return networkJson(); } });
            port = relay.start(want);
            sp.edit().putInt("port", port).apply();
            Log.i(TAG, "relay listening on 127.0.0.1:" + port);
        } catch (Exception e) {
            Log.e(TAG, "relay start failed: " + e);
            stopSelf();
            return START_NOT_STICKY;
        }
        return START_STICKY;
    }

    void stopRelay() { Relay r = relay; if (r != null) r.stop(); relay = null; }

    @Override public void onDestroy() { stopRelay(); super.onDestroy(); }

    void showForeground() {
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Connection sharing", NotificationManager.IMPORTANCE_LOW));
            b = new Notification.Builder(this, CHANNEL);
        } else b = new Notification.Builder(this);
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent pi = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification n = b.setContentTitle("CiciPDA is sharing this phone's connection")
            .setContentText("Paired Windows PC via USB debugging. Disconnect from the PC to stop.")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth).setOngoing(true).setContentIntent(pi).build();
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        else startForeground(1, n);
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
