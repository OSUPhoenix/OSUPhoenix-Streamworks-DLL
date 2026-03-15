using System;
using Streamer.bot.Plugin.Interface.Model;

namespace OSWTools
{
    /// <summary>
    /// Twitch-specific helper methods.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   string pic      = lib.GetTwitchProfilePicture("osuphoenix");
    ///   string name     = lib.GetTwitchDisplayName("osuphoenix");
    ///   int    tier     = lib.GetTwitchSubTier();     // from current event args
    ///   string rewardId = lib.GetTwitchRewardId();    // from current event args
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Returns the broadcaster's user info (the channel owner).
        /// Used for operations that require the broadcaster's userId,
        /// such as fetching channel emotes from the Twitch API.
        /// Returns null if the call fails.
        /// </summary>
        public Streamer.bot.Plugin.Interface.Model.TwitchUserInfo GetTwitchBroadcaster()
        {
            try { return _CPH.TwitchGetBroadcaster(); }
            catch
            {
                LogWarn("GetTwitchBroadcaster failed.");
                return null;
            }
        }

        // ── Profile ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the profile image URL for a Twitch user by login name.
        /// Returns empty string if the user is not found.
        /// </summary>
        public string GetTwitchProfilePicture(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return string.Empty;
            try
            {
                TwitchUserInfoEx user = _CPH.TwitchGetExtendedUserInfoByLogin(userName);
                if (user == null) return string.Empty;
                return Utilities.ProfileHelper.StripSurrogates(user.ProfileImageUrl ?? string.Empty);
            }
            catch
            {
                LogWarn("GetTwitchProfilePicture failed for: " + userName);
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the profile image URL for a Twitch user by their user ID.
        /// Stores the result in the user's persisted var "targetUserPic" automatically.
        /// Returns empty string if the user is not found.
        /// </summary>
        public string GetTwitchProfilePictureById(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return string.Empty;
            try
            {
                TwitchUserInfoEx user = _CPH.TwitchGetExtendedUserInfoById(userId);
                if (user == null) return string.Empty;
                string url = Utilities.ProfileHelper.StripSurrogates(user.ProfileImageUrl ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(url))
                    _CPH.SetTwitchUserVarById(userId, "targetUserPic", url, true);
                return url;
            }
            catch
            {
                LogWarn("GetTwitchProfilePictureById failed for ID: " + userId);
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the full extended user info for a Twitch user by login name.
        /// Returns null if the user is not found.
        /// </summary>
        public TwitchUserInfoEx GetTwitchUserInfo(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            try { return _CPH.TwitchGetExtendedUserInfoByLogin(userName); }
            catch
            {
                LogWarn("GetTwitchUserInfo failed for: " + userName);
                return null;
            }
        }

        /// <summary>
        /// Returns the display name for a Twitch user by login name.
        /// Falls back to the login name itself if the API call fails.
        /// </summary>
        public string GetTwitchDisplayName(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return userName ?? string.Empty;
            try
            {
                TwitchUserInfoEx user = _CPH.TwitchGetExtendedUserInfoByLogin(userName);
                if (user != null && !string.IsNullOrWhiteSpace(user.UserName))
                    return user.UserName;
                return userName;
            }
            catch { return userName; }
        }

        // ── Subscriptions ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the subscription tier from the current event args as an integer.
        ///   Tier 1 → 1000  |  Tier 2 → 2000  |  Tier 3 → 3000  |  Prime → 1000
        /// Returns 0 if no tier information is present in args.
        /// </summary>
        public int GetTwitchSubTier()
        {
            try
            {
                string tier;
                if (!_CPH.TryGetArg("subTier", out tier) || string.IsNullOrWhiteSpace(tier))
                    return 0;

                switch (tier.ToLowerInvariant())
                {
                    case "tier1":
                    case "prime":  return 1000;
                    case "tier2":  return 2000;
                    case "tier3":  return 3000;
                    default:
                        int parsed;
                        return int.TryParse(tier, out parsed) ? parsed : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>
        /// Returns the subscription tier as a human-readable label.
        ///   1000 → "Tier 1"  |  2000 → "Tier 2"  |  3000 → "Tier 3"  |  Prime → "Prime"
        /// Returns "Unknown" if the tier cannot be determined.
        /// </summary>
        public string GetTwitchSubTierLabel()
        {
            try
            {
                string tier;
                if (!_CPH.TryGetArg("subTier", out tier) || string.IsNullOrWhiteSpace(tier))
                    return "Unknown";

                switch (tier.ToLowerInvariant())
                {
                    case "tier1":  return "Tier 1";
                    case "tier2":  return "Tier 2";
                    case "tier3":  return "Tier 3";
                    case "prime":  return "Prime";
                    default:       return tier;
                }
            }
            catch { return "Unknown"; }
        }

        /// <summary>
        /// Returns the dollar value of the current subscription tier.
        ///   Tier 1 / Prime → 4.99  |  Tier 2 → 9.99  |  Tier 3 → 24.99
        /// Returns 0 if the tier cannot be determined.
        /// </summary>
        public double GetTwitchSubValue()
        {
            switch (GetTwitchSubTier())
            {
                case 1000: return 4.99;
                case 2000: return 9.99;
                case 3000: return 24.99;
                default:   return 0;
            }
        }

        // ── Channel Points / Rewards ──────────────────────────────────────────────

        /// <summary>
        /// Returns the reward ID from the current Channel Points redemption event.
        /// Returns empty string if not present in args.
        /// </summary>
        public string GetTwitchRewardId()
        {
            try
            {
                string id;
                _CPH.TryGetArg("rewardId", out id);
                return id ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Returns the reward title from the current Channel Points redemption event.
        /// Returns empty string if not present in args.
        /// </summary>
        public string GetTwitchRewardTitle()
        {
            try
            {
                string title;
                _CPH.TryGetArg("rewardName", out title);
                return title ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Returns the cost (in channel points) of the current redemption event.
        /// Returns 0 if not present in args.
        /// </summary>
        public int GetTwitchRewardCost()
        {
            try
            {
                int cost;
                _CPH.TryGetArg("rewardCost", out cost);
                return cost;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Returns the user-supplied input text from the current Channel Points
        /// redemption (for rewards that require text input).
        /// Returns empty string if no input was provided.
        /// </summary>
        public string GetTwitchRewardInput()
        {
            try
            {
                string input;
                _CPH.TryGetArg("rawInput", out input);
                return input ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
