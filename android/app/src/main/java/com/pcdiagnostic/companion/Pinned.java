package com.pcdiagnostic.companion;

import android.net.http.SslCertificate;
import android.os.Build;
import android.os.Bundle;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.Locale;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * Trust and secrets. The PC makes its own HTTPS certificate, so instead of a certificate authority the app trusts
 * exactly that certificate: its SHA-256 fingerprint comes from the pairing QR code (or "Find my PC", after the user
 * compares it with the one shown on the PC) and every connection must present it. The device token is encrypted
 * with a key that never leaves the Android Keystore.
 */
final class Pinned {
    private static final String KEY_ALIAS = "pcdiag-token";

    private Pinned() {
    }

    /** SHA-256 of a certificate, upper-case hex without separators (as in the pairing link). */
    static String fingerprint(X509Certificate certificate) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded());
        StringBuilder hex = new StringBuilder(digest.length * 2);
        for (byte b : digest) hex.append(String.format(Locale.ROOT, "%02X", b));
        return hex.toString();
    }

    /** "AB:CD:…" as the PC's Settings page shows it, for the user to compare. */
    static String readable(String fp) {
        if (fp == null) return "";
        StringBuilder out = new StringBuilder();
        for (int i = 0; i + 1 < fp.length(); i += 2) {
            if (out.length() > 0) out.append(':');
            out.append(fp, i, i + 2);
        }
        return out.toString();
    }

    static String normalize(String fp) {
        return fp == null ? "" : fp.replace(":", "").replace(" ", "").toUpperCase(Locale.ROOT);
    }

    /** A TLS socket factory that accepts the PC's certificate and nothing else. */
    static SSLSocketFactory factory(String expected) throws Exception {
        String want = normalize(expected);
        TrustManager pin = new X509TrustManager() {
            @Override
            public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                throw new CertificateException("Not used");
            }

            @Override
            public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                if (chain == null || chain.length == 0) throw new CertificateException("No certificate");
                String got;
                try {
                    got = fingerprint(chain[0]);
                } catch (Exception e) {
                    throw new CertificateException(e);
                }
                if (want.isEmpty() || !MessageDigest.isEqual(want.getBytes(StandardCharsets.US_ASCII), got.getBytes(StandardCharsets.US_ASCII))) {
                    throw new CertificateException("This is not the paired PC's certificate (" + readable(got) + ")");
                }
            }

            @Override
            public X509Certificate[] getAcceptedIssuers() {
                return new X509Certificate[0];
            }
        };
        SSLContext tls = SSLContext.getInstance("TLS");
        tls.init(null, new TrustManager[]{pin}, null);
        return tls.getSocketFactory();
    }

    /** The certificate is pinned, so the name in it does not matter (the PC's address can change). */
    static final HostnameVerifier ANY_NAME = (hostname, session) -> true;

    /** The certificate a WebView was shown, for checking against the pin. */
    static X509Certificate fromWebView(SslCertificate certificate) {
        try {
            if (Build.VERSION.SDK_INT >= 29) return certificate.getX509Certificate();
            Bundle state = SslCertificate.saveState(certificate);
            byte[] der = state.getByteArray("x509-certificate");
            if (der == null) return null;
            return (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(der));
        } catch (Exception e) {
            return null;
        }
    }

    // ---------------------------------------------------------------- the token, encrypted at rest

    private static SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (!store.containsAlias(KEY_ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build());
            return generator.generateKey();
        }
        return ((KeyStore.SecretKeyEntry) store.getEntry(KEY_ALIAS, null)).getSecretKey();
    }

    static String seal(String plain) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] sealed = cipher.doFinal(plain.getBytes(StandardCharsets.UTF_8));
        return Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":" + Base64.encodeToString(sealed, Base64.NO_WRAP);
    }

    static String open(String sealed) {
        try {
            String[] parts = sealed.split(":", 2);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
            return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
        } catch (Exception e) {
            return null;
        }
    }
}
