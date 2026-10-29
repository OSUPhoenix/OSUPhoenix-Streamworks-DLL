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
        /// The persisted user variable every OSW profile helper reads and writes.
        /// One name, defined once, so tools can't drift ("targetUserPic" vs
        /// "targetUserPicture" typos). New code should use this instead of the
        /// string literal.
        /// </summary>
        public const string ProfilePictureVar = "targetUserPic";

        /// <summary>
        /// Returns a stored-or-fetched profile picture for ONE exact user on ONE
        /// exact platform. Works anywhere: it does not depend on the current
        /// event's args, so it's safe for queues, mod commands, leaderboards,
        /// timers, etc.
        ///
        /// WHY THIS EXISTS (vs GetProfilePictureFromUserVars):
        ///   GetProfilePictureFromUserVars(userName) checks Twitch → YouTube → Kick
        ///   by name and returns the first hit. If "Alex" on Kick and a different
        ///   "Alex" on Twitch both exist, the Kick player gets the Twitch Alex's
        ///   picture. This method only ever looks on the platform you name, and
        ///   prefers the user ID, which never changes even when a name does.
        ///
        /// LOOKUP ORDER (cheapest and most exact first):
        ///   1. Stored var by user ID   — exact; survives name changes
        ///   2. Stored var by user name — for when you only have a name
        ///   3. Twitch only, if allowApiFetch: Twitch API (ID first, then login).
        ///      A successful fetch is saved back to the stored var.
        ///   YouTube and Kick have no on-demand lookup in Streamer.bot, so for
        ///   them the stored var (kept fresh by First Words / GetProfilePicture())
        ///   is the only source.
        ///
        /// Returns empty string if nothing usable is found. Never throws.
        ///
        /// USAGE:
        ///   var lib = new OSWLib(CPH, "My Tool");
        ///   string pic = lib.GetProfilePictureFor("kick", userId, userName);
        ///   string pic = lib.GetProfilePictureFor("youtube", userName: "Some Viewer");
        ///   string pic = lib.GetProfilePictureFor("twitch", userId, allowApiFetch: false); // stored only
        /// </summary>
        /// <param name="platform">"twitch", "youtube" or "kick" (any casing). Anything else returns "".</param>
        /// <param name="userId">Platform user ID. Preferred when you have it.</param>
        /// <param name="userName">Login / display name. Used when there's no ID, or the ID lookup misses.</param>
        /// <param name="allowApiFetch">Twitch only: allow a Twitch API call when nothing is stored. Default true.</param>
        public string GetProfilePictureFor(string platform, string userId = null, string userName = null, bool allowApiFetch = true)
        {
            // Strict platform check on purpose. NormalisePlatform() treats
            // unknown values as "twitch", which is the right default for
            // reading/writing vars, but for pictures it would silently show a
            // Twitch user's face for a typo like "kik".
            string p = (platform ?? string.Empty).Trim().ToLowerInvariant();
            if (p != "twitch" && p != "youtube" && p != "kick")
            {
                LogWarn("GetProfilePictureFor: unknown platform '" + platform + "'.");
                return string.Empty;
            }

            bool hasId   = !string.IsNullOrWhiteSpace(userId);
            bool hasName = !string.IsNullOrWhiteSpace(userName);
            if (!hasId && !hasName) return string.Empty;

            string pic;

            // 1) Stored, by ID
            if (hasId)
            {
                pic = CleanPictureUrl(GetUserVarById<string>(p, userId, ProfilePictureVar, string.Empty));
                if (pic.Length > 0) return pic;
            }

            // 2) Stored, by name
            if (hasName)
            {
                pic = CleanPictureUrl(GetUserVar<string>(p, userName, ProfilePictureVar, string.Empty));
                if (pic.Length > 0) return pic;
            }

            // 3) Twitch API fallback
            if (p == "twitch" && allowApiFetch)
            {
                // GetTwitchProfilePictureById already saves to the stored var.
                if (hasId)
                {
                    pic = CleanPictureUrl(GetTwitchProfilePictureById(userId));
                    if (pic.Length > 0) return pic;
                }
                if (hasName)
                {
                    // The by-login method doesn't save, so save it here.
                    pic = CleanPictureUrl(GetTwitchProfilePicture(userName));
                    if (pic.Length > 0)
                    {
                        if (hasId) SetUserVarById(p, userId, ProfilePictureVar, pic);
                        else       SetUserVar(p, userName, ProfilePictureVar, pic);
                        return pic;
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Shared cleanup for anything coming out of a stored var or an API:
        /// trims, strips surrogate junk, and rejects anything that isn't an
        /// http(s) URL (old values like "none", "null" or blanks count as missing).
        /// </summary>
        private static string CleanPictureUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string url = (Utilities.ProfileHelper.StripSurrogates(raw) ?? string.Empty).Trim();
            bool isWeb = url.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
                      || url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase);
            return isWeb ? url : string.Empty;
        }

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
        ///
        /// ⚠ If you KNOW the platform, use GetProfilePictureFor() instead.
        /// This method returns the first name match on ANY platform, so two
        /// different people sharing a name on different platforms can get mixed up.
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
