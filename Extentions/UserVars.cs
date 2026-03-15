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

        /// <summary>
        /// Sets a user variable by user ID rather than username.
        /// Useful when you have the ID (e.g. from Twitch API) but not the current display name.
        /// </summary>
        public void SetUserVarById<T>(string platform, string userId, string key, T value, bool persisted = true)
        {
            try
            {
                switch (NormalisePlatform(platform))
                {
                    case "youtube":
                        _CPH.SetYouTubeUserVarById(userId, key, value, persisted);
                        break;
                    case "kick":
                        _CPH.SetKickUserVarById(userId, key, value, persisted);
                        break;
                    default:
                        _CPH.SetTwitchUserVarById(userId, key, value, persisted);
                        break;
                }
            }
            catch
            {
                LogError("SetUserVarById failed — platform: " + platform + " id: " + userId + " key: " + key);
            }
        }

        // ── Bulk (GetUsersVar) ────────────────────────────────────────────────────

        /// <summary>
        /// Returns all users who have a value stored for the given variable key on
        /// the specified platform. Returns an empty list on error.
        ///
        /// The returned list contains UserVariableValue&lt;T&gt; objects with:
        ///   .UserId, .Username, .Value
        ///
        /// USAGE:
        ///   var allProgress = lib.GetUsersVar&lt;string&gt;("twitch", "OSUP_SAS_Progress");
        ///   foreach (var entry in allProgress)
        ///       CPH.LogInfo(entry.Username + " → " + entry.Value);
        /// </summary>
        public System.Collections.Generic.List<Streamer.bot.Plugin.Interface.Model.UserVariableValue<T>>
            GetUsersVar<T>(string platform, string key, bool persisted = true)
        {
            try
            {
                switch (NormalisePlatform(platform))
                {
                    case "youtube":
                        return _CPH.GetYouTubeUsersVar<T>(key, persisted)
                            ?? new System.Collections.Generic.List<Streamer.bot.Plugin.Interface.Model.UserVariableValue<T>>();
                    case "kick":
                        return _CPH.GetKickUsersVar<T>(key, persisted)
                            ?? new System.Collections.Generic.List<Streamer.bot.Plugin.Interface.Model.UserVariableValue<T>>();
                    default:
                        return _CPH.GetTwitchUsersVar<T>(key, persisted)
                            ?? new System.Collections.Generic.List<Streamer.bot.Plugin.Interface.Model.UserVariableValue<T>>();
                }
            }
            catch
            {
                return new System.Collections.Generic.List<Streamer.bot.Plugin.Interface.Model.UserVariableValue<T>>();
            }
        }

        // ── Platform resolution ───────────────────────────────────────────────────

        /// <summary>
        /// Identifies which platform a user belongs to by checking whether they
        /// have a stored value for a given variable key across all three platforms.
        ///
        /// This is the pattern used throughout SAS when a donation or merch event
        /// provides a username but not a platform — we check all three and return
        /// the first hit.
        ///
        /// Returns "Twitch", "YouTube", "Kick", or empty string if not found on any.
        ///
        /// USAGE:
        ///   string platform = lib.ResolveUserPlatform("OSUPhoenix", "OSUP_SAS_Progress");
        ///   if (string.IsNullOrEmpty(platform))
        ///       // user not known on any platform
        /// </summary>
        public string ResolveUserPlatform(string userName, string varKey, bool persisted = true)
        {
            if (string.IsNullOrWhiteSpace(userName)) return string.Empty;
            try
            {
                if (_CPH.GetTwitchUserVar<string>(userName, varKey, persisted) != null)
                    return "Twitch";
            }
            catch { }
            try
            {
                if (_CPH.GetYouTubeUserVar<string>(userName, varKey, persisted) != null)
                    return "YouTube";
            }
            catch { }
            try
            {
                if (_CPH.GetKickUserVar<string>(userName, varKey, persisted) != null)
                    return "Kick";
            }
            catch { }
            return string.Empty;
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
