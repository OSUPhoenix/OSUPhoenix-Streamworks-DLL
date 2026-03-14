namespace OSWTools
{
    /// <summary>
    /// Platform-aware user variable helpers.
    ///
    /// WHY THIS EXISTS:
    /// Streamer.bot stores user variables per-platform. Every script that
    /// touched user vars had its own if/else chain:
    ///   if (platform == "twitch") CPH.GetTwitchUserVar(...)
    ///   else if (platform == "youtube") CPH.GetYouTubeUserVar(...)
    ///   else ...
    ///
    /// These methods centralise that routing so you never write it again.
    /// Platform strings are always lowercase: "twitch", "youtube", "kick".
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "Viewer Last Seen");
    ///
    ///   // Get a user variable (persisted)
    ///   string lastSeen = lib.GetUserVar&lt;string&gt;(platform, userName, "lastSeenDate", "");
    ///
    ///   // Set a user variable (persisted)
    ///   lib.SetUserVar(platform, userName, "lastSeenDate", DateTime.UtcNow.ToString("o"));
    ///
    ///   // Unset a user variable
    ///   lib.UnsetUserVar(platform, userName, "lastSeenDate");
    /// </summary>
    public partial class OSWLib
    {
        // ── Get ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets a user variable for the given platform and username.
        /// Returns <paramref name="fallback"/> if not found or on error.
        /// Persisted by default.
        /// </summary>
        public T GetUserVar<T>(string platform, string userName, string key, T fallback = default(T))
        {
            try
            {
                T value;
                switch (NormalisePlatform(platform))
                {
                    case "youtube":
                        value = _CPH.GetYouTubeUserVar<T>(userName, key, true);
                        break;
                    case "kick":
                        value = _CPH.GetKickUserVar<T>(userName, key, true);
                        break;
                    default: // twitch
                        value = _CPH.GetTwitchUserVar<T>(userName, key, true);
                        break;
                }
                if (value == null) return fallback;
                return value;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Gets a user variable by user ID rather than username.
        /// Useful when you have an ID but not the current display name.
        /// </summary>
        public T GetUserVarById<T>(string platform, string userId, string key, T fallback = default(T))
        {
            try
            {
                T value;
                switch (NormalisePlatform(platform))
                {
                    case "youtube":
                        value = _CPH.GetYouTubeUserVarById<T>(userId, key, true);
                        break;
                    case "kick":
                        value = _CPH.GetKickUserVarById<T>(userId, key, true);
                        break;
                    default: // twitch
                        value = _CPH.GetTwitchUserVarById<T>(userId, key, true);
                        break;
                }
                if (value == null) return fallback;
                return value;
            }
            catch
            {
                return fallback;
            }
        }

        // ── Set ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets a user variable for the given platform and username.
        /// Persisted by default.
        /// </summary>
        public void SetUserVar<T>(string platform, string userName, string key, T value, bool persisted = true)
        {
            try
            {
                switch (NormalisePlatform(platform))
                {
                    case "youtube":
                        _CPH.SetYouTubeUserVar(userName, key, value, persisted);
                        break;
                    case "kick":
                        _CPH.SetKickUserVar(userName, key, value, persisted);
                        break;
                    default: // twitch
                        _CPH.SetTwitchUserVar(userName, key, value, persisted);
                        break;
                }
            }
            catch
            {
                LogError("SetUserVar failed — platform: " + platform + " user: " + userName + " key: " + key);
            }
        }

        // ── Unset ─────────────────────────────────────────────────────────────────

        /// <summary>Removes a user variable for the given platform and username.</summary>
        public void UnsetUserVar(string platform, string userName, string key, bool persisted = true)
        {
            try
            {
                switch (NormalisePlatform(platform))
                {
                    case "youtube":
                        _CPH.UnsetYouTubeUserVar(userName, key, persisted);
                        break;
                    case "kick":
                        _CPH.UnsetKickUserVar(userName, key, persisted);
                        break;
                    default:
                        _CPH.UnsetTwitchUserVar(userName, key, persisted);
                        break;
                }
            }
            catch { /* best effort */ }
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Normalises a platform string to lowercase.
        /// Accepts "Twitch", "TWITCH", "twitch" — all become "twitch".
        /// Unknown values fall through to "twitch" as the default.
        /// </summary>
        private string NormalisePlatform(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return "twitch";
            return platform.ToLowerInvariant();
        }
    }
}
