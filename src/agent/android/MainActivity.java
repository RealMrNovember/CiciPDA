package app.cicibyte.cicipda;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * Companion screen: logo, what this app does, live status, the one runtime permission (notifications) and the publisher at the bottom.
 * Built in code (no layout XML) to keep the APK tiny and dependency-free.
 */
public class MainActivity extends Activity {
    static final int BG = Color.parseColor("#07090C"), PANEL = Color.parseColor("#0E141B"), BORDER = Color.parseColor("#1C2631");
    static final int TEXT = Color.parseColor("#E7EEF5"), MUTED = Color.parseColor("#7B8CA1"), GREEN = Color.parseColor("#19E08B"), AMBER = Color.parseColor("#F5B84B"), DIM = Color.parseColor("#4A586A");

    TextView notifValue, relayValue, noteView;
    Button allow;
    float d;

    int dp(int v) { return Math.round(v * d); }

    TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    LinearLayout row(String label, TextView value) {
        LinearLayout r = new LinearLayout(this);
        r.setOrientation(LinearLayout.HORIZONTAL);
        r.setPadding(0, dp(6), 0, dp(6));
        TextView l = text(label, 14, MUTED, false);
        r.addView(l, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        value.setGravity(Gravity.END);
        r.addView(value);
        return r;
    }

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        d = getResources().getDisplayMetrics().density;
        Window w = getWindow();
        w.setStatusBarColor(BG);
        if (Build.VERSION.SDK_INT >= 27) w.setNavigationBarColor(BG);

        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(BG);
        sv.setFillViewport(true);                       // lets the footer sit at the bottom of tall screens

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(24), dp(48), dp(24), dp(20));
        sv.addView(root, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        ImageView logo = new ImageView(this);
        logo.setImageResource(R.drawable.logo);
        logo.setContentDescription("CiciPDA logo");
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(104), dp(104));
        lp.gravity = Gravity.CENTER_HORIZONTAL;
        root.addView(logo, lp);

        TextView title = text("CiciPDA Companion", 24, TEXT, true);
        title.setGravity(Gravity.CENTER);
        title.setPadding(0, dp(16), 0, 0);
        root.addView(title);
        TextView ver = text("Version " + RelayService.VERSION, 13, MUTED, false);
        ver.setGravity(Gravity.CENTER);
        root.addView(ver);

        TextView about = text("Lets your paired Windows PC use this phone's internet connection while it is connected by USB. "
            + "Nothing runs until you press Connect in CiciPDA on the PC.", 15, MUTED, false);
        about.setGravity(Gravity.CENTER);
        about.setPadding(dp(8), dp(18), dp(8), dp(18));
        about.setLineSpacing(0, 1.15f);
        root.addView(about);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(12), dp(18), dp(12));
        GradientDrawable cg = new GradientDrawable();
        cg.setColor(PANEL); cg.setCornerRadius(dp(14)); cg.setStroke(dp(1), BORDER);
        card.setBackground(cg);
        notifValue = text("", 14, TEXT, true);
        relayValue = text("", 14, TEXT, true);
        card.addView(row("Notifications", notifValue));
        card.addView(row("Sharing service", relayValue));
        root.addView(card, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        noteView = text("", 13, MUTED, false);
        noteView.setPadding(dp(4), dp(14), dp(4), dp(4));
        root.addView(noteView);

        allow = new Button(this);
        allow.setText("Allow notifications");
        allow.setAllCaps(false);
        allow.setTextColor(Color.parseColor("#04110B"));
        GradientDrawable bg = new GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, new int[] { GREEN, Color.parseColor("#22D3EE") });
        bg.setCornerRadius(dp(12));
        allow.setBackground(bg);
        allow.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                if (Build.VERSION.SDK_INT >= 33) requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 1);
            }
        });
        LinearLayout.LayoutParams bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(50));
        bp.topMargin = dp(12);
        root.addView(allow, bp);

        // flexible space pushes the publisher block to the bottom of the screen
        root.addView(new View(this), new LinearLayout.LayoutParams(0, 0, 1f));

        View line = new View(this);
        line.setBackgroundColor(BORDER);
        LinearLayout.LayoutParams ll = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1));
        ll.topMargin = dp(28); ll.bottomMargin = dp(14);
        root.addView(line, ll);

        TextView company = text("CiciByte Technology, Inc.", 14, TEXT, true);
        company.setGravity(Gravity.CENTER);
        root.addView(company);
        TextView site = text("cicibyte.com", 14, GREEN, false);
        site.setGravity(Gravity.CENTER);
        site.setPadding(0, dp(4), 0, dp(4));
        site.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://cicibyte.com"))); } catch (Exception e) { } }
        });
        root.addView(site);
        TextView copy = text("© 2026 CiciByte Technology, Inc. All rights reserved.", 11, DIM, false);
        copy.setGravity(Gravity.CENTER);
        root.addView(copy);

        setContentView(sv);
    }

    @Override protected void onResume() {
        super.onResume();
        boolean notif = Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED;
        boolean running = RelayService.relay != null;
        notifValue.setText(notif ? "Allowed" : "Not allowed");
        notifValue.setTextColor(notif ? GREEN : AMBER);
        relayValue.setText(running ? "Running" : "Stopped");
        relayValue.setTextColor(running ? GREEN : MUTED);
        allow.setVisibility(notif ? View.GONE : View.VISIBLE);
        noteView.setText(notif
            ? (running ? "Connected to your PC. Disconnect from CiciPDA on the PC to stop sharing."
                       : "Ready. Open CiciPDA on your PC and press Connect.")
            : "Android asks once so the \"sharing\" notice can be shown while your PC uses this phone. Sharing still works without it, but the notice may be hidden.");
    }

    @Override public void onRequestPermissionsResult(int c, String[] p, int[] r) { onResume(); }
}
