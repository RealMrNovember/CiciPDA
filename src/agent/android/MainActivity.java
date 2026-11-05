package app.cicibyte.cicipda;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Minimal screen: explains the companion and asks for the one runtime permission (notifications) the user must grant. */
public class MainActivity extends Activity {
    TextView status;

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.VERTICAL);
        l.setPadding(48, 96, 48, 48);
        TextView title = new TextView(this);
        title.setText("CiciPDA Companion " + RelayService.VERSION);
        title.setTextSize(22);
        l.addView(title);
        status = new TextView(this);
        status.setTextSize(16);
        status.setPadding(0, 32, 0, 32);
        l.addView(status);
        Button allow = new Button(this);
        allow.setText("Allow notifications");
        allow.setGravity(Gravity.CENTER);
        allow.setOnClickListener(new android.view.View.OnClickListener() {
            public void onClick(android.view.View v) {
                if (Build.VERSION.SDK_INT >= 33) requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 1);
            }
        });
        l.addView(allow);
        setContentView(l);
    }

    @Override protected void onResume() {
        super.onResume();
        boolean notif = Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED;
        boolean running = RelayService.relay != null;
        status.setText("This app lets your paired Windows PC use this phone's internet connection while it is connected by USB.\n\n"
            + "Notifications: " + (notif ? "allowed" : "not allowed (tap the button; the sharing notice stays visible while connected)")
            + "\nRelay: " + (running ? "running" : "stopped") + "\n\nOpen CiciPDA on the PC to connect.");
    }

    @Override public void onRequestPermissionsResult(int c, String[] p, int[] r) { onResume(); }
}
