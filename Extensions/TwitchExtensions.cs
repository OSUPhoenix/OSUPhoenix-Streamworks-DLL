using System;
using Streamer.bot.Plugin.Interface.Model;

namespace OSWTools
{
    // =========================================================================
    //  TwitchRevenueSplit enum
    //  Defined at the namespace level (outside the class) so callers can write
    //  TwitchRevenueSplit.Standard instead of OSWLib.TwitchRevenueSplit.Standard.
    // =========================================================================

    /// <summary>
    /// Represents the revenue split tier between Twitch and the streamer.
    ///
    ///   Standard      → 50% streamer / 50% Twitch  (all Affiliates and Partners)
    ///   PartnerPlusL1 → 60% streamer / 40% Twitch  (requires 100+ paid sub points
    ///                     for 3 consecutive months)
    ///   PartnerPlusL2 → 70% streamer / 30% Twitch  (requires 350+ paid sub points
    ///                     for 3 consecutive months; capped at first $100,000/yr —
    ///                     reverts to L1 above that)
    ///
    /// Pass this into GetTwitchSubStreamerGain() to get the correct payout.
    /// </summary>
    public enum TwitchRevenueSplit
    {
        Standard,        // Default for all Affiliates and Partners
        PartnerPlusL1,   // 60/40 — Partner Plus level 1
        PartnerPlusL2    // 70/30 — Partner Plus level 2 (first $100k/yr only)
    }


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


        // ── Profile ───────────────────────────────────────────────────────────

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
        /// Returns the full extended user info for a Twitch user by their user ID.
        /// Returns null if the user is not found.
        ///
        /// Prefer this over GetTwitchUserInfo() when you already have a userId —
        /// ID lookups are unambiguous and slightly faster than login lookups.
        /// </summary>
        public TwitchUserInfoEx GetTwitchUserInfoById(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            try { return _CPH.TwitchGetExtendedUserInfoById(userId); }
            catch
            {
                LogWarn("GetTwitchUserInfoById failed for ID: " + userId);
                return null;
            }
        }

        /// <summary>
        /// Flexible extended user info lookup — accepts either a userId or a
        /// userLogin and returns a TwitchUserInfoEx with all available fields.
        ///
        /// ── Resolution order ─────────────────────────────────────────────────
        ///   1. userId supplied    → TwitchGetExtendedUserInfoById()   (preferred)
        ///   2. userLogin supplied → TwitchGetExtendedUserInfoByLogin() (fallback)
        ///   3. Neither supplied   → logs a warning and returns null
        ///
        /// Use this when the identifier you have depends on the trigger type —
        /// for example, Channel Points events supply userId, chat commands may
        /// only supply a login name.
        ///
        /// Returns null if the lookup fails or the user is not found.
        /// </summary>
        /// <param name="userId">Twitch numeric user ID (preferred if available).</param>
        /// <param name="userLogin">Twitch login name, lowercase (fallback).</param>
        public TwitchUserInfoEx GetExtendedUserInfo(string userId = null, string userLogin = null)
        {
            // Prefer ID — it's unambiguous and doesn't require a name→ID resolution step
            if (!string.IsNullOrWhiteSpace(userId))
            {
                try { return _CPH.TwitchGetExtendedUserInfoById(userId); }
                catch
                {
                    LogWarn("GetExtendedUserInfo (by ID) failed for: " + userId);
                    return null;
                }
            }

            // Fall back to login name if no ID was provided
            if (!string.IsNullOrWhiteSpace(userLogin))
            {
                try { return _CPH.TwitchGetExtendedUserInfoByLogin(userLogin); }
                catch
                {
                    LogWarn("GetExtendedUserInfo (by login) failed for: " + userLogin);
                    return null;
                }
            }

            // Neither was usable — log and bail cleanly
            LogWarn("GetExtendedUserInfo called with no userId or userLogin.");
            return null;
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


        // ── Subscription Tier ─────────────────────────────────────────────────

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

        // ── Subscription Value ────────────────────────────────────────────────

        /// <summary>
        /// Returns the viewer-facing price of the current subscription tier.
        ///   Tier 1 → $5.99  |  Tier 2 → $9.99  |  Tier 3 → $24.99
        ///   Returns 0 if the tier cannot be determined.
        ///
        /// Kept for backward compatibility — delegates to GetTwitchSubViewerCost()
        /// so the price table is only defined in one place.
        /// </summary>
        public double GetTwitchSubValue()
        {
            return GetTwitchSubViewerCost();
        }

        /// <summary>
        /// Returns the price the viewer pays for the current subscription tier.
        ///   Tier 1 → $5.99  |  Tier 2 → $9.99  |  Tier 3 → $24.99
        ///   Prime  → $0.00  (covered by Amazon Prime; viewer pays nothing extra)
        ///   Returns 0 if the tier cannot be determined.
        ///
        /// Use this when you want to display or log what the viewer spent.
        /// </summary>
        public double GetTwitchSubViewerCost()
        {
            switch (GetTwitchSubTier())
            {
                case 1000: return 5.99;   // Tier 1
                case 2000: return 9.99;   // Tier 2
                case 3000: return 24.99;  // Tier 3
                default:   return 0;
            }
        }

        /// <summary>
        /// Returns the estimated revenue the streamer earns from the current
        /// subscription tier, based on their revenue split level with Twitch.
        ///
        /// ── Split amounts by tier ────────────────────────────────────────────
        ///   Tier 1  ($5.99 viewer price):
        ///     Standard       → $3.00
        ///     PartnerPlusL1  → $2.99
        ///     PartnerPlusL2  → $3.49
        ///
        ///   Tier 2  ($9.99 viewer price):
        ///     Standard       → $5.00
        ///     PartnerPlusL1  → $5.99
        ///     PartnerPlusL2  → $6.99
        ///
        ///   Tier 3  ($24.99 viewer price):
        ///     Standard       → $12.50
        ///     PartnerPlusL1  → $14.99
        ///     PartnerPlusL2  → $17.49
        ///
        ///   Prime (any split) → $2.25 flat  (Twitch pays a fixed rate, no %)
        /// ────────────────────────────────────────────────────────────────────
        ///
        /// NOTE: These are pre-tax estimates. Twitch deducts taxes and payment
        /// processing fees before the split, so real payouts may be slightly
        /// lower depending on the viewer's region.
        /// </summary>
        /// <param name="split">
        ///   The revenue split tier. Defaults to Standard (50/50) if omitted.
        /// </param>
        /// <param name="isPrime">
        ///   True if this is a Prime Gaming subscription. Prime subs pay a flat
        ///   $2.25 regardless of split tier. Both Tier 1 and Prime share sub-tier
        ///   code 1000 in Streamer.bot, so this flag tells them apart.
        /// </param>
        public double GetTwitchSubStreamerGain(
            TwitchRevenueSplit split   = TwitchRevenueSplit.Standard,
            bool               isPrime = false)
        {
            // Prime Gaming subs bypass the split — flat payout always.
            if (isPrime) return 2.25;

            switch (GetTwitchSubTier())
            {
                case 1000: // Tier 1
                    switch (split)
                    {
                        case TwitchRevenueSplit.PartnerPlusL1: return 2.99;
                        case TwitchRevenueSplit.PartnerPlusL2: return 3.49;
                        default:                               return 3.00;
                    }

                case 2000: // Tier 2
                    switch (split)
                    {
                        case TwitchRevenueSplit.PartnerPlusL1: return 5.99;
                        case TwitchRevenueSplit.PartnerPlusL2: return 6.99;
                        default:                               return 5.00;
                    }

                case 3000: // Tier 3
                    switch (split)
                    {
                        case TwitchRevenueSplit.PartnerPlusL1: return 14.99;
                        case TwitchRevenueSplit.PartnerPlusL2: return 17.49;
                        default:                               return 12.50;
                    }

                default:
                    return 0;
            }
        }

        /// <summary>
        /// Returns the dollar value of a Bits cheer.
        /// Streamers receive 100% of Bits value at exactly $0.01 per Bit.
        /// Example: 1000 Bits → $10.00
        ///
        /// No split applies — Bits revenue goes entirely to the streamer.
        /// </summary>
        /// <param name="bits">The number of Bits cheered in the event.</param>
        public double GetTwitchBitsValue(int bits)
        {
            // Math.Round prevents floating-point drift (e.g. 999 * 0.01 = $9.9999...)
            return Math.Round(bits * 0.01, 2);
        }


        // ── Channel Points / Rewards ──────────────────────────────────────────

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
