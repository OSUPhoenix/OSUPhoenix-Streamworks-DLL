namespace OSWTools
{
    /// <summary>
    /// YouTube-specific helper methods.
    ///
    /// IMPORTANT — YouTube profile pictures work differently from Twitch.
    /// YouTube does NOT have an API method like TwitchGetExtendedUserInfoByLogin.
    /// Instead, Streamer.bot exposes the profile image URL directly as event args:
    ///   "userProfileUrl"        — plain URL
    ///   "userProfileUrlEscaped" — URL-encoded version (fallback)
    ///
    /// These helpers read from the current event args, so they must be called
    /// inside an action triggered by a YouTube event.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   string pic    = lib.GetYouTubeProfilePicture();   // from current event args
    ///   string userId = lib.GetYouTubeUserId();
    /// </summary>
    public partial class OSWLib
    {
        // ── Profile ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the profile image URL for the current YouTube event's user.
        /// Reads from "userProfileUrl" arg (falls back to "userProfileUrlEscaped").
        /// Also stores the result in the user's persisted var "targetUserPic".
        /// Returns empty string if neither arg is present.
        /// </summary>
        public string GetYouTubeProfilePicture()
        {
            try
            {
                string userId;
                _CPH.TryGetArg("userId", out userId);

                string rawUrl;
                if (!_CPH.TryGetArg("userProfileUrl", out rawUrl) || string.IsNullOrWhiteSpace(rawUrl))
                    _CPH.TryGetArg("userProfileUrlEscaped", out rawUrl);

                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    // Try reading from the stored user var if available
                    if (!string.IsNullOrWhiteSpace(userId))
                        rawUrl = _CPH.GetYouTubeUserVarById<string>(userId, "targetUserPic", true) ?? string.Empty;
                }

                string safeUrl = Utilities.ProfileHelper.StripSurrogates(rawUrl ?? string.Empty);

                // Persist for future use
                if (!string.IsNullOrWhiteSpace(safeUrl) && !string.IsNullOrWhiteSpace(userId))
                    _CPH.SetYouTubeUserVarById(userId, "targetUserPic", safeUrl, true);

                return safeUrl;
            }
            catch
            {
                LogWarn("GetYouTubeProfilePicture failed.");
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the YouTube user/channel ID from the current event args.
        /// Returns empty string if not present.
        /// </summary>
        public string GetYouTubeUserId()
        {
            try
            {
                string userId;
                _CPH.TryGetArg("userId", out userId);
                return userId ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Returns the display name of the current YouTube event's user.
        /// Returns empty string if not present in args.
        /// </summary>
        public string GetYouTubeDisplayName()
        {
            try
            {
                string name;
                if (!_CPH.TryGetArg("userName", out name) || string.IsNullOrWhiteSpace(name))
                    _CPH.TryGetArg("user", out name);
                return name ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
