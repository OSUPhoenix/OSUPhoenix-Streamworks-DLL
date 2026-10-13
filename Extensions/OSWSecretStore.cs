////OSWSecretStore.cs//
// =============================================================================
// NEW FILE — Extensions\OSWSecretStore.cs
// Needs a reference to System.Security (part of .NET Framework).
//
// Stores sign-in tokens and secrets in Streamer.bot persisted globals, but
// ENCRYPTED with Windows' Data Protection API (DPAPI).
//
// WHY: Streamer.bot's globals are visible in its UI and saved to a file in
// its folder. The old multi-scheduler kept its Twitch client secret and
// Google tokens there as plain text — anyone who saw the Variables window,
// or a copy of that file (a backup, a support zip), had them.
//
// DPAPI encrypts with a key tied to THIS Windows user account on THIS PC.
// Nothing for us to manage or lose: Windows holds the key. The catch is the
// point: a copied file can't be decrypted anywhere else, so after moving to
// a new PC the streamer simply signs in again.
// =============================================================================
using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace OSWTools
{
    internal static class OSWSecretStore
    {
        // Extra "salt" mixed into the encryption, so these blobs can only be
        // decrypted by code that knows to pass it (not by any random app
        // running as the same user that calls DPAPI blindly).
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OSWTools.SecretStore.v1");
        private const string Prefix = "dpapi1:";

        internal static void Save<T>(OSWLib lib, string key, T value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
            byte[] sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            lib.SetGlobal(key, Prefix + Convert.ToBase64String(sealedBytes), true);
        }

        /// <summary>Null if missing, or if it can't be decrypted (e.g. copied from another PC).</summary>
        internal static T Load<T>(OSWLib lib, string key) where T : class
        {
            string raw = lib.GetGlobal(key, "");
            if (string.IsNullOrEmpty(raw) || !raw.StartsWith(Prefix)) return null;
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(raw.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex)
            {
                lib.LogWarn("Saved sign-in \"" + key + "\" couldn't be unlocked (" + ex.Message + "). It was probably saved on another PC or Windows account — sign in again.");
                return null;
            }
        }

        internal static void Delete(OSWLib lib, string key) => lib.UnsetGlobal(key);
    }
}
