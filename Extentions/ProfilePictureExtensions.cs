namespace OSWTools
{
    /// <summary>
    /// Unified profile picture helper — detects the current platform automatically
    /// and calls the correct platform-specific method.
    ///
    /// This is the method to call from most actions. You don't need to know
    /// which platform triggered the event — just call GetProfilePicture().
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///   string pic = lib.GetProfilePicture();
    ///
    /// The result is also stored in the user's persisted var "targetUserPic"
    /// automatically by the underlying platform methods.
    ///
    /// For Kick: requires "Kick → User → Get User Info for Target" to have
    /// run earlier in the same action.
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Returns the profile image URL for the user in the current event.
        /// Detects the platform from event args and routes accordingly.
        ///
        /// Platform detection order (same as ProfileHelper.DetectPlatform):
        ///   1. "platform" arg override
        ///   2. "commandSource" arg
        ///   3. "userType" arg
        ///   4. Heuristics (YouTube URL args, Kick URL args)
        ///   5. Default → Twitch
        /// </summary>
        public string GetProfilePicture()
        {
            string platform = Utilities.ProfileHelper.DetectPlatform(
                name =>
                {
                    string val;
                    return _CPH.TryGetArg(name, out val) ? val : null;
                });

            switch (platform)
            {
                case "youtube": return GetYouTubeProfilePicture();
                case "kick":    return GetKickProfilePicture();
                default:        // twitch
                    string userId;
                    _CPH.TryGetArg("userId", out userId);
                    return !string.IsNullOrWhiteSpace(userId)
                        ? GetTwitchProfilePictureById(userId)
                        : GetTwitchProfilePictureFromArgs();
            }
        }

        /// <summary>
        /// Lazy profile picture fetch — only calls the platform API if "targetUserPic"
        /// is not already stored for this user. Use this in high-frequency handlers
        /// (like OnChatMessage) to avoid hammering the Twitch API every message.
        ///
        /// For Kick: still requires "Kick → User → Get User Info for Target" to have
        /// run first, since we can't pull Kick pictures on demand.
        /// </summary>
        public string GetProfilePictureIfMissing(string platform, string userName)
        {
            if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(userName))
                return string.Empty;

            // Check stored var first
            string existing = GetUserVar<string>(platform, userName, "targetUserPic", string.Empty);
            if (!string.IsNullOrWhiteSpace(existing)) return existing;

            // Not stored — fetch
            switch (NormalisePlatform(platform))
            {
                case "youtube": return GetYouTubeProfilePicture();
                case "kick":    return GetKickProfilePicture();
                default:        return GetTwitchProfilePicture(userName);
            }
        }

        /// <summary>
        /// Looks up a stored profile picture for a username across all three
        /// platform user var stores. Does NOT call any API — purely reads stored vars.
        ///
        /// Useful when you have a donor name or merch buyer name from a third-party
        /// platform (DonorDrive, Ko-fi, etc.) and want to find their picture if they
        /// are a known viewer on any platform.
        ///
        /// Tries: Twitch → YouTube → Kick → Twitch ProfileImageUrl fallback.
        /// Returns empty string if not found anywhere.
        /// </summary>
        public string GetProfilePictureFromUserVars(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return string.Empty;

            string pic;

            try
            {
                pic = _CPH.GetTwitchUserVar<string>(userName, "targetUserPic", true);
                if (!string.IsNullOrWhiteSpace(pic)) return pic;
            }
            catch { }

            try
            {
                pic = _CPH.GetYouTubeUserVar<string>(userName, "targetUserPic", true);
                if (!string.IsNullOrWhiteSpace(pic)) return pic;
            }
            catch { }

            try
            {
                pic = _CPH.GetKickUserVar<string>(userName, "targetUserPic", true);
                if (!string.IsNullOrWhiteSpace(pic)) return pic;
            }
            catch { }

            // Last resort — check Twitch API-stored ProfileImageUrl
            try
            {
                pic = _CPH.GetTwitchUserVar<string>(userName, "ProfileImageUrl", true);
                if (!string.IsNullOrWhiteSpace(pic)) return pic;
            }
            catch { }

            return string.Empty;
        }

        /// <summary>
        /// Twitch profile picture fetch driven entirely by current event args.
        /// Uses "userName" arg to look up the user via the Twitch API.
        /// </summary>
        private string GetTwitchProfilePictureFromArgs()
        {
            try
            {
                string userName;
                if (!_CPH.TryGetArg("userName", out userName) || string.IsNullOrWhiteSpace(userName))
                {
                    LogWarn("GetProfilePicture (Twitch): missing userName arg.");
                    return string.Empty;
                }
                return GetTwitchProfilePicture(userName);
            }
            catch { return string.Empty; }
        }
    }
}
