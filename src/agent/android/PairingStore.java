package app.cicibyte.cicipda;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * Holds the current session's pairing key encrypted with a non-exportable AES key in the Android Keystore
 * (the ciphertext lives in app-private preferences, which are excluded from backup). The key is session-scoped:
 * the PC creates a fresh one for every connection and the companion deletes it on STOP / UNPAIR / a failed start,
 * so a stale key can never keep the relay trusted after a disconnect.
 */
final class PairingStore {
    private static final String ALIAS = "cicipda-pairing-v1";
    private static final String PREFS = "pairing";

    private PairingStore() { }

    private static SecretKey wrappingKey() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if (ks.containsAlias(ALIAS)) return (SecretKey) ks.getKey(ALIAS, null);
        KeyGenerator g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        g.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).build());
        return g.generateKey();
    }

    static void save(Context c, byte[] key, int port) throws Exception {
        Cipher ci = Cipher.getInstance("AES/GCM/NoPadding");
        ci.init(Cipher.ENCRYPT_MODE, wrappingKey());
        byte[] iv = ci.getIV();
        byte[] ct = ci.doFinal(key);
        byte[] blob = new byte[1 + iv.length + ct.length];
        blob[0] = (byte) iv.length;
        System.arraycopy(iv, 0, blob, 1, iv.length);
        System.arraycopy(ct, 0, blob, 1 + iv.length, ct.length);
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString("blob", Base64.encodeToString(blob, Base64.NO_WRAP)).putInt("port", port).commit();
    }

    /** @return the stored key, or null when none is stored or it cannot be decrypted. */
    static byte[] load(Context c) {
        try {
            String s = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("blob", null);
            if (s == null) return null;
            byte[] blob = Base64.decode(s, Base64.NO_WRAP);
            int ivLen = blob[0] & 0xff;
            Cipher ci = Cipher.getInstance("AES/GCM/NoPadding");
            ci.init(Cipher.DECRYPT_MODE, wrappingKey(), new GCMParameterSpec(128, blob, 1, ivLen));
            return ci.doFinal(blob, 1 + ivLen, blob.length - 1 - ivLen);
        } catch (Exception e) {
            return null;
        }
    }

    static int port(Context c) { return c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getInt("port", 0); }

    /** Forget the pairing: delete the ciphertext and the Keystore wrapping key. */
    static void clear(Context c) {
        c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().commit();
        try { KeyStore ks = KeyStore.getInstance("AndroidKeyStore"); ks.load(null); if (ks.containsAlias(ALIAS)) ks.deleteEntry(ALIAS); } catch (Exception e) { }
    }
}
