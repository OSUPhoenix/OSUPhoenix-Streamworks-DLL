// OSWTools/Scheduling/OmsConfigManager.cs
//
// Single point of contact for reading and writing OMS configuration.
// Everything that touches OSWData goes through here.
//
// Responsibilities:
//   1. Load config from OSWData/OMS/config.json, decrypt the refresh token on the way in
//   2. Save config to OSWData/OMS/config.json, encrypt the refresh token on the way out
//   3. Handle avatar image files (store in OSWData/OMS/, track filename in config)
//   4. Provide a cached in-memory copy so we're not hitting disk on every API call
//
// Encryption boundary — this is the ONLY place encryption/decryption happens:
//   Disk  →  Load()   →  Decrypt(refreshToken)  →  in-memory OmsConfig (plaintext)
//   in-memory OmsConfig (plaintext)  →  Encrypt(refreshToken)  →  Save()  →  Disk
//
// Nothing outside this class calls OmsCrypto directly.
// Nothing outside this class calls OSWData for OMS files directly.
//
// Thread safety:
//   SB runs actions on MTA threads. The cached config is protected by a simple lock.
//   This is sufficient because config reads/writes are infrequent (settings save,
//   token refresh) and never happen concurrently in normal operation.

using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;

namespace OSWTools.Scheduling
{
    internal static class OmsConfigManager
    {
        // OSWData identifiers — these determine the folder and filename:
        // Result: {StreamerBot folder}/OSWData/OMS/config.json
        private const string TOOL = "OMS";
        private const string FILE = "config";

        // Avatar images live in the same OSWData/OMS/ folder as config.json.
        // We store only the filename in config, not the full path.
        // OmsDiscordPoster reconstructs the full path when it needs to send the image.
        private const string AVATAR_FILENAME = "avatar";

        // In-memory cache. Null until first Load() call.
        // Avoids repeated disk reads during a single scheduler session.
        private static OmsConfig _cached = null;
        private static readonly object _lock = new object();

        // ── Load ─────────────────────────────────────────────────────────────────
        // Returns the current config. Uses cached copy if available.
        // First call reads from disk and decrypts. Subsequent calls return the cache.
        //
        // Force = true bypasses the cache (used after Save() to ensure consistency,
        // and at the start of the settings form to always show current values).
        internal static OmsConfig Load(bool force = false)
        {
            lock (_lock)
            {
                if (_cached != null && !force)
                    return _cached;

                // OSWData.LoadOrDefault never returns null — if the file doesn't exist
                // it returns a new OmsConfig() with all the safe defaults we defined.
                OmsConfig raw = OSWData.LoadOrDefault(TOOL, FILE, new OmsConfig());

                // Decrypt the refresh token coming off disk.
                // OmsCrypto.Decrypt returns "" if the value is missing, corrupted,
                // or from a different machine — all safe fallbacks.
                raw.Google.RefreshToken = OmsCrypto.Decrypt(raw.Google.RefreshToken);

                _cached = raw;
                return _cached;
            }
        }

        // ── Save ─────────────────────────────────────────────────────────────────
        // Encrypts the refresh token and writes to disk.
        // Updates the in-memory cache with the plaintext version (the caller's object).
        //
        // We deep-copy before encrypting so the in-memory config always holds
        // the plaintext value — only the copy that goes to disk gets encrypted.
        // This means callers can keep using their config object normally after Save().
        internal static void Save(OmsConfig config)
        {
            lock (_lock)
            {
                // Deep copy via JSON round-trip (uses JsonHelper which is already in the DLL)
                string json        = JsonHelper.Serialize(config);
                OmsConfig toWrite  = JsonHelper.SafeDeserialize<OmsConfig>(json)
                                     ?? new OmsConfig();

                // Encrypt only the refresh token. Everything else writes as-is.
                toWrite.Google.RefreshToken = OmsCrypto.Encrypt(config.Google.RefreshToken);

                // OSWData.Save handles atomic write with .tmp → .json swap and .bak creation.
                // If the write fails partway through, the old config.json is preserved.
                OSWData.Save(TOOL, FILE, toWrite);

                // Update cache with the plaintext original (not the encrypted copy)
                _cached = config;
            }
        }

        // ── SaveRefreshToken ─────────────────────────────────────────────────────
        // Convenience method for OmsOAuthManager to update just the refresh token
        // without having to load the full config, modify it, and save it back.
        // Used after a successful OAuth exchange or token refresh.
        internal static void SaveRefreshToken(string refreshToken)
        {
            OmsConfig config = Load();
            config.Google.RefreshToken = refreshToken;
            Save(config);
        }

        // ── SaveBroadcasterId ────────────────────────────────────────────────────
        // Same pattern for Twitch broadcaster ID — auto-fetched at settings save time,
        // stored without requiring a full form round-trip.
        internal static void SaveBroadcasterId(string broadcasterId)
        {
            OmsConfig config = Load();
            config.Twitch.BroadcasterId = broadcasterId;
            Save(config);
        }

        // ── SaveYouTubeChannelId ─────────────────────────────────────────────────
        // Same pattern for YouTube channel ID — fetched after first successful Google auth.
        internal static void SaveYouTubeChannelId(string channelId)
        {
            OmsConfig config = Load();
            config.YouTube.ChannelId = channelId;
            Save(config);
        }

        // ── InvalidateCache ──────────────────────────────────────────────────────
        // Forces the next Load() call to re-read from disk.
        // Called if something outside normal flow modifies config.json directly,
        // or as a safety measure at the start of each SB session.
        internal static void InvalidateCache()
        {
            lock (_lock)
            {
                _cached = null;
            }
        }

        // ── SaveAvatarImage ──────────────────────────────────────────────────────
        // Handles the case where the user provides a local image file path for their
        // Discord avatar instead of a URL.
        //
        // What this does:
        //   1. Reads the source image from the user-provided path
        //   2. Saves a copy to OSWData/OMS/avatar.{ext}
        //   3. Updates config.Discord.AvatarSource to the filename only (not full path)
        //   4. Saves config
        //
        // OmsDiscordPoster later calls GetAvatarFilePath() to reconstruct the full path.
        //
        // Returns true if successful, false if the source file couldn't be read.
        // Caller (settings form) shows an error message on false.
        internal static bool SaveAvatarImage(string sourceFilePath, OmsConfig config)
        {
            try
            {
                // Determine extension from source file
                string ext      = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                string filename = AVATAR_FILENAME + ext; // e.g. "avatar.png"

                // Build the destination path inside OSWData/OMS/
                // OSWData.GetFilePath() gives us the correct folder location
                string folder   = OSWData.GetFolderPath(TOOL);
                string destPath = Path.Combine(folder, filename);

                // Ensure the folder exists (OSWData.Save normally handles this,
                // but we're writing a file directly here)
                Directory.CreateDirectory(folder);

                // Copy the image file
                File.Copy(sourceFilePath, destPath, overwrite: true);

                // Store just the filename in config — not the full path
                config.Discord.AvatarSource = filename;
                Save(config);

                return true;
            }
            catch (Exception)
            {
                // File not found, access denied, unsupported format, etc.
                // Return false so the settings form can show a user-friendly message.
                return false;
            }
        }

        // ── GetAvatarFilePath ─────────────────────────────────────────────────────
        // Reconstructs the full path to the stored avatar image.
        // Returns null if AvatarSource is empty or doesn't look like a filename.
        // OmsDiscordPoster calls this when preparing a Discord webhook post.
        internal static string GetAvatarFilePath(OmsConfig config)
        {
            string source = config.Discord.AvatarSource;

            if (string.IsNullOrEmpty(source))
                return null;

            // If it's a URL, there's no file path to return
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return null;

            // It's a filename — reconstruct the full path
            string folder = OSWData.GetFolderPath(TOOL);
            string full   = Path.Combine(folder, source);

            return File.Exists(full) ? full : null;
        }

        // ── IsAvatarUrl ───────────────────────────────────────────────────────────
        // Quick check for OmsDiscordPoster to decide how to send the avatar.
        // True  → AvatarSource is a URL, send it directly in the webhook payload
        // False → AvatarSource is a filename, load from disk and send as attachment
        internal static bool IsAvatarUrl(OmsConfig config)
        {
            string source = config.Discord.AvatarSource;
            return !string.IsNullOrEmpty(source) &&
                   (source.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
