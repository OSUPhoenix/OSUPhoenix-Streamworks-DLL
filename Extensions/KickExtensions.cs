namespace OSWTools
{
    /// <summary>
    /// Kick-specific helper methods.
    ///
    /// IMPORTANT — Kick profile pictures require a setup step.
    /// Before calling GetKickProfilePicture(), your Streamer.bot action must
    /// include the built-in sub-action:
    ///   Kick → User → Get User Info for Target
    ///
    /// That populates the following args which these helpers then read:
    ///   "targetUserId"                    — Kick user ID
    ///   "targetUserProfileImageUrl"       — plain profile image URL
    ///   "targetUserProfileImageUrlEscaped"— URL-encoded fallback
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // In your action, add "Kick → User → Get User Info for Target" FIRST
    ///   string pic = lib.GetKickProfilePicture();
    /// </summary>
    public partial class OSWLib
    {
        // ── Profile ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the profile image URL for the current Kick event's user.
        /// Requires "Kick → User → Get User Info for Target" to have run first.
        /// Also stores the result in the user's persisted var "targetUserPic".
        /// Returns empty string if the URL is not available.
        /// </summary>
        public string GetKickProfilePicture()
        {
            try
            {
                // Get user ID — try targetUserId first, fall back to userId
                string userId;
                if (!_CPH.TryGetArg("targetUserId", out userId) || string.IsNullOrWhiteSpace(userId))
                    _CPH.TryGetArg("userId", out userId);

                if (string.IsNullOrWhiteSpace(userId))
                {
                    LogWarn("GetKickProfilePicture: missing targetUserId/userId arg. " +
                            "Add 'Kick → User → Get User Info for Target' before this action.");
                    return string.Empty;
                }

                // Get URL — try plain first, fall back to escaped
                string rawUrl;
                if (!_CPH.TryGetArg("targetUserProfileImageUrl", out rawUrl) || string.IsNullOrWhiteSpace(rawUrl))
                    _CPH.TryGetArg("targetUserProfileImageUrlEscaped", out rawUrl);

                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    LogWarn("GetKickProfilePicture: profile image URL was empty for userId '" + userId + "'. " +
                            "Ensure 'Kick → User → Get User Info for Target' ran successfully.");
                    return string.Empty;
                }

                string safeUrl = Utilities.ProfileHelper.StripSurrogates(rawUrl);

                // Persist for future use
                _CPH.SetKickUserVarById(userId, "targetUserPic", safeUrl, true);

                return safeUrl;
            }
            catch
            {
                LogWarn("GetKickProfilePicture failed.");
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the Kick user ID from the current event args.
        /// Tries "targetUserId" first, then "userId".
        /// Returns empty string if not present.
        /// </summary>
        public string GetKickUserId()
        {
            try
            {
                string userId;
                if (!_CPH.TryGetArg("targetUserId", out userId) || string.IsNullOrWhiteSpace(userId))
                    _CPH.TryGetArg("userId", out userId);
                return userId ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Returns the display name of the current Kick event's user.
        /// Returns empty string if not present in args.
        /// </summary>
        public string GetKickDisplayName()
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
