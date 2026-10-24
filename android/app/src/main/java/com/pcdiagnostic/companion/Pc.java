package com.pcdiagnostic.companion;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLHandshakeException;

/** The PC this phone is paired with: where it is, its certificate, its token, and talking to it (never on the UI thread). */
final class Pc {
    static final int DEFAULT_PORT = 47443;
    static final int DISCOVERY_PORT = 47444;
    static final String HEADER = "X-PCDiag-Device";
    static final String CLIENT_HEADER = "X-PCDiag-Client";
    static final String COOKIE = "pcdiag_device";
    static final String APP = "pcdiagnostic";

    final String host;
    final int port;
    final String id;
    final String name;
    final String fingerprint;
    final String token;

    Pc(String host, int port, String id, String name, String fingerprint, String token) {
        this.host = host;
        this.port = port;
        this.id = id;
        this.name = name;
        this.fingerprint = Pinned.normalize(fingerprint);
        this.token = token;
    }

    String base() {
        return base(host, port);
    }

    static String base(String host, int port) {
        return "https://" + (host.contains(":") ? "[" + host + "]" : host) + ":" + port;
    }

    // ---------------------------------------------------------------- kept on the phone

    private static SharedPreferences prefs(Context c) {
        return c.getSharedPreferences("pc", Context.MODE_PRIVATE);
    }

    /** The paired PC, or null when there is none (or its token cannot be decrypted any more). */
    static Pc load(Context c) {
        SharedPreferences p = prefs(c);
        String sealed = p.getString("token", null);
        String host = p.getString("host", null);
        if (sealed == null || host == null) return null;
        String token = Pinned.open(sealed);
        if (token == null) return null;
        return new Pc(host, p.getInt("port", DEFAULT_PORT), p.getString("id", ""), p.getString("name", "PC"), p.getString("fp", ""), token);
    }

    void save(Context c) throws Exception {
        prefs(c).edit().putString("host", host).putInt("port", port).putString("id", id).putString("name", name)
                .putString("fp", fingerprint).putString("token", Pinned.seal(token)).apply();
    }

    Pc at(String newHost, int newPort) {
        return new Pc(newHost, newPort, id, name, fingerprint, token);
    }

    static void forget(Context c) {
        prefs(c).edit().clear().apply();
    }

    // ---------------------------------------------------------------- talking to it

    /** The answer from /api/v1/devices/hello: {app, id, name, paired, version}. */
    static final class Hello {
        final JSONObject json;
        final String problem;

        Hello(JSONObject json, String problem) {
            this.json = json;
            this.problem = problem;
        }

        boolean ok() {
            return json != null;
        }
    }

    /** GET /api/v1/devices/hello over the pinned connection. */
    static Hello hello(String host, int port, String fingerprint, String token) {
        try {
            HttpsURLConnection c = open(base(host, port) + "/api/v1/devices/hello", fingerprint, token, 2500);
            if (c.getResponseCode() != 200) return new Hello(null, "PC Diagnostic answered with " + c.getResponseCode() + ".");
            JSONObject j = new JSONObject(read(c.getInputStream()));
            return APP.equals(j.optString("app")) ? new Hello(j, null) : new Hello(null, "That is not PC Diagnostic.");
        } catch (SSLHandshakeException e) {
            return new Hello(null, "The PC's certificate does not match. Scan the pairing QR code on the PC again, or compare the fingerprint.");
        } catch (Exception e) {
            return new Hello(null, null);
        }
    }

    /** POST /api/v1/devices/pair with the code shown on the PC: the paired PC (saved by the caller). */
    static Pc pair(String host, int port, String fingerprint, String code, String deviceName) throws IOException {
        Hello hi = hello(host, port, fingerprint, null);
        if (!hi.ok()) {
            throw new IOException(hi.problem != null ? hi.problem : "PC Diagnostic did not answer at " + host + ":" + port
                    + ". On the PC, is Settings > Access from other devices turned on, and is this phone on the same Wi-Fi?");
        }
        HttpsURLConnection c = open(base(host, port) + "/api/v1/devices/pair", fingerprint, null, 8000);
        c.setRequestMethod("POST");
        c.setDoOutput(true);
        c.setRequestProperty("Content-Type", "application/json");
        c.setRequestProperty(CLIENT_HEADER, "android/" + BuildInfo.VERSION);
        JSONObject body = new JSONObject();
        try {
            body.put("code", code.replace("-", "").replace(" ", "").trim());
            body.put("name", deviceName);
        } catch (Exception ignored) {
        }
        try (OutputStream out = c.getOutputStream()) {
            out.write(body.toString().getBytes(StandardCharsets.UTF_8));
        }
        int status = c.getResponseCode();
        if (status != 200) throw new IOException(detail(c, "Pairing failed (" + status + ")"));
        try {
            JSONObject j = new JSONObject(read(c.getInputStream()));
            return new Pc(host, port, j.optString("id", hi.json.optString("id")), hi.json.optString("name", "PC"), fingerprint, j.getString("token"));
        } catch (org.json.JSONException e) {
            throw new IOException("Unexpected answer from PC Diagnostic");
        }
    }

    /** Where the PC answers now: its saved address, or (its address changed) found again on the network by its install ID. */
    Pc reach() {
        Hello hi = hello(host, port, fingerprint, token);
        if (hi.ok() && (id.isEmpty() || id.equals(hi.json.optString("id")))) return this;
        for (Found f : discover(2)) {
            // The same install, presenting the same certificate (checked again by every connection).
            if (id.equals(f.id) && fingerprint.equals(f.fingerprint)) return at(f.host, f.port);
        }
        return null;
    }

    /** POST /api/v1/incidents/capture: the new incident's key. */
    String capture() throws IOException {
        HttpsURLConnection c = open(base() + "/api/v1/incidents/capture", fingerprint, token, 8000);
        c.setRequestMethod("POST");
        c.setDoOutput(true);
        c.setRequestProperty("Content-Type", "application/json");
        try (OutputStream out = c.getOutputStream()) {
            out.write("{\"note\":\"Captured from the phone\"}".getBytes(StandardCharsets.UTF_8));
        }
        int status = c.getResponseCode();
        if (status != 200) throw new IOException(detail(c, "Capture failed (" + status + ")"));
        try {
            return new JSONObject(read(c.getInputStream())).getJSONObject("capture").getString("incidentKey");
        } catch (org.json.JSONException e) {
            throw new IOException("Unexpected answer from PC Diagnostic");
        }
    }

    /** A PC Diagnostic answering on the home network. */
    static final class Found {
        final String host;
        final int port;
        final String id;
        final String name;
        final String fingerprint;

        Found(String host, int port, String id, String name, String fingerprint) {
            this.host = host;
            this.port = port;
            this.id = id;
            this.name = name;
            this.fingerprint = Pinned.normalize(fingerprint);
        }
    }

    /** Ask the home network who is PC Diagnostic (a broadcast on UDP 47444; each PC answers with its address and fingerprint). */
    static List<Found> discover(int rounds) {
        Map<String, Found> found = new LinkedHashMap<>();
        try (DatagramSocket s = new DatagramSocket()) {
            s.setBroadcast(true);
            s.setSoTimeout(700);
            byte[] ask = "PCDIAG?".getBytes(StandardCharsets.US_ASCII);
            for (int r = 0; r < rounds && found.isEmpty(); r++) {
                s.send(new DatagramPacket(ask, ask.length, InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT));
                long until = System.currentTimeMillis() + 1500;
                while (System.currentTimeMillis() < until) {
                    byte[] buf = new byte[1024];
                    DatagramPacket p = new DatagramPacket(buf, buf.length);
                    try {
                        s.receive(p);
                    } catch (SocketTimeoutException e) {
                        continue;
                    }
                    try {
                        JSONObject j = new JSONObject(new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8));
                        if (!APP.equals(j.optString("app"))) continue;
                        String host = j.optString("host", p.getAddress().getHostAddress());
                        if (host.isEmpty() || "null".equals(host)) host = p.getAddress().getHostAddress();
                        found.put(j.optString("id", host), new Found(host, j.optInt("port", DEFAULT_PORT), j.optString("id"), j.optString("name", host), j.optString("fp")));
                    } catch (org.json.JSONException ignored) {
                    }
                }
            }
        } catch (IOException ignored) {
        }
        return new ArrayList<>(found.values());
    }

    static HttpsURLConnection open(String url, String fingerprint, String token, int timeoutMs) throws IOException {
        HttpsURLConnection c = (HttpsURLConnection) new URL(url).openConnection();
        try {
            c.setSSLSocketFactory(Pinned.factory(fingerprint));
        } catch (Exception e) {
            throw new IOException("TLS is not available: " + e.getMessage());
        }
        c.setHostnameVerifier(Pinned.ANY_NAME);
        c.setConnectTimeout(timeoutMs);
        c.setReadTimeout(Math.max(timeoutMs, 15000));
        c.setRequestProperty("Accept", "application/json");
        if (token != null) c.setRequestProperty(HEADER, token);
        return c;
    }

    static String read(InputStream in) throws IOException {
        try (InputStream i = in) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = i.read(buf)) > 0) out.write(buf, 0, n);
            return out.toString("UTF-8");
        }
    }

    /** The server's own explanation of an error (problem details: "detail", else "title"), else the fallback. */
    static String detail(HttpsURLConnection c, String fallback) {
        try {
            InputStream err = c.getErrorStream();
            if (err == null) return fallback;
            JSONObject j = new JSONObject(read(err));
            String d = j.optString("detail");
            if (d.isEmpty()) d = j.optString("title");
            return d.isEmpty() ? fallback : d;
        } catch (Exception e) {
            return fallback;
        }
    }

    /** The app's version, for the client header. */
    static final class BuildInfo {
        static final String VERSION = "1.0";

        private BuildInfo() {
        }
    }
}
