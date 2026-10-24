package com.pcdiagnostic.companion;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.net.http.SslError;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.CookieManager;
import android.webkit.SslErrorHandler;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.IOException;
import java.net.URL;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicReference;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * PC Diagnostic on the phone: pairing with the PC, then the PC's own dashboard in a WebView (pinned to the PC's
 * certificate), plus a one-tap "Capture incident" shortcut.
 */
public class MainActivity extends Activity {
    static final String ACTION_CAPTURE = "com.pcdiagnostic.companion.CAPTURE";

    private final ExecutorService bg = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private FrameLayout root;
    private WebView web;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        root = new FrameLayout(this);
        root.setBackgroundColor(getColor(R.color.bg));
        setContentView(root);
        route(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        route(intent);
    }

    @Override
    protected void onDestroy() {
        bg.shutdownNow();
        super.onDestroy();
    }

    private void route(Intent intent) {
        if (handlePairLink(intent)) return;
        if (intent != null && ACTION_CAPTURE.equals(intent.getAction())) {
            capture();
            return;
        }
        if (web == null) start();
    }

    // ---------------------------------------------------------------- finding the PC

    /** The paired PC (found again if its address changed), then the dashboard; or pairing first. */
    private void start() {
        Pc saved = Pc.load(this);
        if (saved == null) {
            showPairing(null);
            return;
        }
        showBusy("Connecting to " + saved.name + "…");
        bg.execute(() -> {
            Pc found = saved.reach();
            Pc.Hello hi = found == null ? null : Pc.hello(found.host, found.port, found.fingerprint, found.token);
            ui.post(() -> {
                if (found == null || hi == null || !hi.ok()) {
                    showUnreachable(saved, hi != null ? hi.problem : null);
                } else if (!hi.json.optBoolean("paired")) {
                    Pc.forget(this);
                    showPairing("This phone was removed on the PC (or not used for a long time). Pair it again with a new code.");
                } else {
                    saveQuietly(found);
                    showWeb(found);
                }
            });
        });
    }

    /** pcdiag://pair?host=…&port=…&code=…&id=…&fp=… (the QR code on the PC): the fingerprint comes from the PC's own screen. */
    private boolean handlePairLink(Intent intent) {
        Uri u = intent == null ? null : intent.getData();
        if (u == null || !"pcdiag".equals(u.getScheme())) return false;
        String host = u.getQueryParameter("host"), code = u.getQueryParameter("code"), fp = u.getQueryParameter("fp");
        int port = parsePort(u.getQueryParameter("port"));
        if (host == null || host.isEmpty() || code == null || fp == null || fp.isEmpty()) {
            showPairing("That QR code is not a PC Diagnostic pairing code.");
            return true;
        }
        pair(host, port, fp, code);
        return true;
    }

    private void pair(String host, int port, String fingerprint, String code) {
        showBusy("Pairing with PC Diagnostic at " + host + "…");
        bg.execute(() -> {
            try {
                Pc p = Pc.pair(host, port, fingerprint, code, Build.MANUFACTURER + " " + Build.MODEL);
                p.save(this);
                ui.post(() -> {
                    Toast.makeText(this, "Paired with " + p.name, Toast.LENGTH_SHORT).show();
                    showWeb(p);
                });
            } catch (Exception e) {
                ui.post(() -> showPairing(e.getMessage()));
            }
        });
    }

    /** Typed address or "Find my PC": the user confirms the certificate's fingerprint against the one on the PC first. */
    private void confirmAndPair(String host, int port, String knownFingerprint, String code) {
        showBusy("Checking " + host + "…");
        bg.execute(() -> {
            String fp = knownFingerprint != null && !knownFingerprint.isEmpty() ? Pinned.normalize(knownFingerprint) : peekFingerprint(host, port);
            ui.post(() -> {
                if (fp == null) {
                    showPairing("PC Diagnostic did not answer at " + host + ":" + port
                            + ". On the PC, is Settings > Access from other devices turned on, and is this phone on the same Wi-Fi?");
                    return;
                }
                new AlertDialog.Builder(this)
                        .setTitle("Is this your PC?")
                        .setMessage("Compare this fingerprint with the one in Settings > Access from other devices on the PC:\n\n"
                                + Pinned.readable(fp) + "\n\nOnly continue if they are the same.")
                        .setPositiveButton("They match", (d, i) -> pair(host, port, fp, code))
                        .setNegativeButton("Cancel", (d, i) -> showPairing(null))
                        .setCancelable(false)
                        .show();
            });
        });
    }

    /** The certificate the PC presents, read once without trusting it (the user then compares the fingerprint). */
    private static String peekFingerprint(String host, int port) {
        AtomicReference<String> seen = new AtomicReference<>();
        try {
            SSLContext tls = SSLContext.getInstance("TLS");
            tls.init(null, new TrustManager[]{new X509TrustManager() {
                @Override
                public void checkClientTrusted(X509Certificate[] chain, String authType) {
                }

                @Override
                public void checkServerTrusted(X509Certificate[] chain, String authType) {
                    try {
                        if (chain != null && chain.length > 0) seen.set(Pinned.fingerprint(chain[0]));
                    } catch (Exception ignored) {
                    }
                }

                @Override
                public X509Certificate[] getAcceptedIssuers() {
                    return new X509Certificate[0];
                }
            }}, new SecureRandom());
            HttpsURLConnection c = (HttpsURLConnection) new URL(Pc.base(host, port) + "/api/v1/devices/hello").openConnection();
            c.setSSLSocketFactory(tls.getSocketFactory());
            c.setHostnameVerifier(Pinned.ANY_NAME);
            c.setConnectTimeout(2500);
            c.setReadTimeout(5000);
            c.getResponseCode();  // nothing secret is sent: no token, no code
        } catch (Exception ignored) {
        }
        return seen.get();
    }

    // ---------------------------------------------------------------- one-tap capture

    private void capture() {
        Pc saved = Pc.load(this);
        if (saved == null) {
            showPairing("Pair this phone first; then the Capture shortcut captures an incident on the PC in one tap.");
            return;
        }
        Toast.makeText(this, "Capturing on " + saved.name + "…", Toast.LENGTH_SHORT).show();
        bg.execute(() -> {
            String message;
            try {
                Pc found = saved.reach();
                if (found == null) throw new IOException("Can't reach " + saved.name + ".");
                saveQuietly(found);
                message = "Captured " + found.capture() + " on " + found.name;
            } catch (Exception e) {
                message = "Not captured: " + e.getMessage();
            }
            String text = message;
            ui.post(() -> {
                Toast.makeText(this, text, Toast.LENGTH_LONG).show();
                if (web == null) start();
            });
        });
    }

    // ---------------------------------------------------------------- screens

    private void dropWeb() {
        root.removeAllViews();
        if (web != null) web.destroy();
        web = null;
    }

    private LinearLayout page() {
        dropWeb();
        ScrollView scroll = new ScrollView(this);
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(24);
        col.setPadding(pad, dp(48), pad, pad);
        scroll.addView(col);
        root.addView(scroll);
        TextView title = text("PC Diagnostic", 26, R.color.text);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        col.addView(title);
        return col;
    }

    private void showBusy(String what) {
        LinearLayout col = page();
        ProgressBar bar = new ProgressBar(this);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(40), dp(40));
        lp.topMargin = dp(32);
        col.addView(bar, lp);
        col.addView(spaced(text(what, 15, R.color.muted), 16));
    }

    private void showPairing(String problem) {
        LinearLayout col = page();
        col.addView(spaced(text("Pair this phone with PC Diagnostic on your PC. On the PC: Settings > Access from other devices > "
                + "turn it on > Show pairing code. Then scan its QR code with the phone's camera, or find the PC here and type the code.",
                15, R.color.muted), 12));
        if (problem != null) col.addView(spaced(text(problem, 15, R.color.danger), 16));
        EditText address = field("PC address (e.g. 192.168.1.151:47443)", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText code = field("8-digit code", InputType.TYPE_CLASS_NUMBER);
        TextView status = text("", 14, R.color.muted);
        String[] foundFingerprint = {null};
        Button find = button("Find my PC");
        find.setOnClickListener(v -> {
            find.setEnabled(false);
            status.setText("Looking for PC Diagnostic on this Wi-Fi…");
            bg.execute(() -> {
                List<Pc.Found> all = Pc.discover(3);
                ui.post(() -> {
                    find.setEnabled(true);
                    if (all.isEmpty()) {
                        status.setText("Not found. Check that the phone is on the same Wi-Fi and Access from other devices is on, "
                                + "or type the address shown in Settings on the PC.");
                    } else if (all.size() == 1) {
                        choose(all.get(0), address, status, foundFingerprint);
                    } else {
                        String[] names = new String[all.size()];
                        for (int i = 0; i < names.length; i++) names[i] = all.get(i).name + "  (" + all.get(i).host + ")";
                        new AlertDialog.Builder(this).setTitle("Which PC?")
                                .setItems(names, (d, i) -> choose(all.get(i), address, status, foundFingerprint)).show();
                    }
                });
            });
        });
        Button go = button("Pair");
        go.setOnClickListener(v -> {
            String a = address.getText().toString().trim().replaceFirst("^https?://", "").replaceAll("/.*$", "");
            String c = code.getText().toString().replace("-", "").replace(" ", "").trim();
            if (a.isEmpty() || c.length() != 8) {
                status.setText("Enter the PC's address and the 8-digit code shown on the PC.");
                return;
            }
            int colon = a.lastIndexOf(':');
            boolean hasPort = colon > 0 && a.indexOf(':') == colon;
            String host = hasPort ? a.substring(0, colon) : a;
            confirmAndPair(host, hasPort ? parsePort(a.substring(colon + 1)) : Pc.DEFAULT_PORT, foundFingerprint[0], c);
        });
        col.addView(spaced(find, 24));
        col.addView(spaced(status, 8));
        col.addView(spaced(address, 16));
        col.addView(spaced(code, 8));
        col.addView(spaced(go, 16));
        if (problem == null) find.performClick();  // most of the time it is simply found
    }

    private void choose(Pc.Found f, EditText address, TextView status, String[] foundFingerprint) {
        address.setText(f.host + ":" + f.port);
        foundFingerprint[0] = f.fingerprint;
        status.setText("Found " + f.name + ". Enter the code shown on the PC.");
    }

    private void showUnreachable(Pc saved, String problem) {
        LinearLayout col = page();
        col.addView(spaced(text("Can't reach PC Diagnostic on " + saved.name + " (" + saved.host + ").", 17, R.color.text), 16));
        col.addView(spaced(text(problem != null ? problem
                : "Is the PC on, PC Diagnostic running, Settings > Access from other devices on, and this phone on the same Wi-Fi?", 15, R.color.muted), 8));
        Button retry = button("Try again");
        retry.setOnClickListener(v -> start());
        Button forget = button("Pair with another PC");
        forget.setOnClickListener(v -> new AlertDialog.Builder(this).setTitle("Forget " + saved.name + "?")
                .setMessage("You will need a new pairing code from the PC to use it again.")
                .setPositiveButton("Forget", (d, i) -> {
                    Pc.forget(this);
                    showPairing(null);
                })
                .setNegativeButton("Cancel", null).show());
        col.addView(spaced(retry, 24));
        col.addView(spaced(forget, 8));
    }

    // ---------------------------------------------------------------- the dashboard

    private void showWeb(Pc p) {
        dropWeb();
        CookieManager cookies = CookieManager.getInstance();
        cookies.setAcceptCookie(true);
        cookies.setCookie(p.base(), Pc.COOKIE + "=" + p.token + "; Path=/; Secure; HttpOnly; SameSite=Strict");
        cookies.flush();
        WebView w = new WebView(this);
        web = w;
        cookies.setAcceptThirdPartyCookies(w, false);
        WebSettings s = w.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setUserAgentString(s.getUserAgentString() + " PcDiagnosticCompanion/" + Pc.BuildInfo.VERSION);
        w.setBackgroundColor(Color.parseColor("#0B0F14"));
        w.setWebViewClient(new WebViewClient() {
            @Override
            public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                // The PC's own certificate is expected (and only it): compare with the pinned fingerprint.
                X509Certificate certificate = Pinned.fromWebView(error.getCertificate());
                try {
                    if (certificate != null && p.fingerprint.equals(Pinned.fingerprint(certificate))) {
                        handler.proceed();
                        return;
                    }
                } catch (Exception ignored) {
                }
                handler.cancel();
                ui.post(() -> showUnreachable(p, "The PC's certificate does not match the one paired with. Pair again with the QR code on the PC."));
            }

            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest req) {
                Uri u = req.getUrl();
                if (p.host.equals(u.getHost())) return false;
                try {  // other sites open in the browser
                    startActivity(new Intent(Intent.ACTION_VIEW, u));
                } catch (ActivityNotFoundException ignored) {
                }
                return true;
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest req, WebResourceError err) {
                if (req.isForMainFrame()) ui.post(() -> showUnreachable(p, null));
            }

            @Override
            public void onReceivedHttpError(WebView view, WebResourceRequest req, WebResourceResponse res) {
                if (req.isForMainFrame() && res.getStatusCode() == 401) {
                    Pc.forget(MainActivity.this);
                    ui.post(() -> showPairing("This phone was removed on the PC. Pair it again with a new code."));
                }
            }
        });
        root.addView(w, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        w.loadUrl(p.base() + "/");
    }

    @Override
    @SuppressWarnings("deprecation")
    public void onBackPressed() {
        if (web != null && web.canGoBack()) {
            web.goBack();
        } else {
            super.onBackPressed();
        }
    }

    // ---------------------------------------------------------------- small helpers

    private void saveQuietly(Pc p) {
        try {
            p.save(this);
        } catch (Exception ignored) {
            // The token stays in memory for this session.
        }
    }

    private static int parsePort(String value) {
        try {
            int port = Integer.parseInt(value);
            return port > 0 && port < 65536 ? port : Pc.DEFAULT_PORT;
        } catch (Exception e) {
            return Pc.DEFAULT_PORT;
        }
    }

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics());
    }

    private TextView text(String s, int sp, int color) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(getColor(color));
        return t;
    }

    private EditText field(String hint, int type) {
        EditText e = new EditText(this);
        e.setHint(hint);
        e.setInputType(type);
        e.setSingleLine(true);
        e.setTextColor(getColor(R.color.text));
        e.setHintTextColor(getColor(R.color.muted));
        return e;
    }

    private Button button(String label) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setGravity(Gravity.CENTER);
        return b;
    }

    private View spaced(View v, int topDp) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(topDp);
        v.setLayoutParams(lp);
        return v;
    }
}
