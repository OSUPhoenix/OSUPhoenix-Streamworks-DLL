// OSWTools/Scheduling/OmsCrypto.cs
//
// DPAPI encryption/decryption for the Google OAuth refresh token.
//
// WHY DPAPI:
//   Windows Data Protection API encrypts using a key derived from the current
//   Windows user account credentials. The encrypted blob is only decryptable
//   on the same machine, by the same Windows user. Copying config.json to
//   another machine renders the refresh token unreadable.
//
// SCOPE:
//   DataProtectionScope.CurrentUser — tied to the logged-in Windows user.
//   If the user reinstalls Windows or switches accounts, decryption returns ""
//   and OmsOAuthManager treats that as "not yet authorized" → triggers a fresh
//   Google auth flow. The user just clicks Connect Google again. No crash.
//
// ENTROPY:
//   An optional secondary key baked into this DLL via OmsCredentials.DpapiEntropy.
//   Means that even another process running as the same Windows user can't decrypt
//   our blobs without having those exact entropy bytes. Adds meaningful resistance
//   to automated extraction tools that target DPAPI blobs generically.
//
// WHAT THIS DOES NOT PROTECT AGAINST:
//   A determined attacker with full access to the machine and the ability to run
//   code as the same Windows user can always decrypt DPAPI data. That's an
//   acceptable limitation — we're protecting against casual inspection and
//   automated credential scrapers, not a targeted attack on a specific machine.
//
// USAGE:
//   Only called from OmsConfigManager. Nothing else in the codebase touches this.
//   Keep it that way — single responsibility.

using System;
using System.Security.Cryptography;
using System.Text;

namespace OSWTools.Scheduling
{
    internal static class OmsCrypto
    {
        // Entropy bytes from OmsCredentials (the gitignore'd source file).
        // These are static and consistent across all distributed DLL builds —
        // they're baked in at compile time, not generated per-machine.
        // If you ever rotate these, all existing encrypted tokens become unreadable
        // and users will need to re-authorize. Treat rotation as a breaking change.
        private static readonly byte[] _entropy = OmsCredentials.DpapiEntropy;

        // ── Encrypt ──────────────────────────────────────────────────────────────
        // Takes a plaintext string (e.g. the raw refresh token from Google's response)
        // and returns a base64-encoded ciphertext string safe for JSON storage.
        //
        // Returns "" if input is null or empty — callers don't need null checks.
        // Throws InvalidOperationException if DPAPI itself fails (rare — would indicate
        // a serious system-level problem). OmsConfigManager catches and logs this.
        internal static string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return "";

            byte[] data      = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(
                data,
                _entropy,
                DataProtectionScope.CurrentUser
            );

            return Convert.ToBase64String(encrypted);
        }

        // ── Decrypt ──────────────────────────────────────────────────────────────
        // Takes a base64-encoded ciphertext string from config.json and returns
        // the original plaintext string.
        //
        // Returns "" (never throws) in these cases:
        //   - Input is null or empty           → token not yet set, treat as missing
        //   - Input is not valid base64         → corrupted or old unencrypted value
        //   - DPAPI decryption fails            → wrong machine/user, treat as missing
        //
        // In all "" cases, OmsOAuthManager will detect the missing token and trigger
        // a fresh authorization flow. The tool degrades gracefully.
        internal static string Decrypt(string ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext))
                return "";

            // Guard: if the value doesn't look like base64, it may be a legacy
            // plain-text token from an old config.json before encryption was added.
            // Return "" so the user re-auths rather than crashing on bad input.
            byte[] data;
            try
            {
                data = Convert.FromBase64String(ciphertext);
            }
            catch (FormatException)
            {
                return "";
            }

            try
            {
                byte[] decrypted = ProtectedData.Unprotect(
                    data,
                    _entropy,
                    DataProtectionScope.CurrentUser
                );
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // Wrong machine, wrong user, wrong entropy, or corrupted blob.
                // All of these are handled the same way: treat as missing.
                return "";
            }
        }

        // ── IsEncrypted ───────────────────────────────────────────────────────────
        // Quick check used by OmsConfigManager to detect whether a stored value
        // is already encrypted (base64) or is a legacy plain-text value.
        // Used during migration from old config formats if needed in the future.
        // Not used in normal load/save flow — Decrypt() handles both cases already.
        internal static bool IsEncrypted(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            try
            {
                // Valid base64 and long enough to be a real DPAPI blob
                byte[] bytes = Convert.FromBase64String(value);
                return bytes.Length > 32;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
