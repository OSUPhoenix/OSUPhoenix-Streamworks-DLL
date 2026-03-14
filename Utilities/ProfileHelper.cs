using System;
using System.Linq;
using System.Text;

namespace OSWTools.Utilities
{
    // =========================================================================
    // ProfileHelper
    // Pure utility class — no CPH or Streamer.bot dependencies.
    // Provides username normalization and profile URL sanitization helpers
    // shared across all OSW tools via OSWTools.dll.
    //
    // Previously these methods were duplicated inside each Streamer.bot inline
    // script (Achievement System, Profile Snatcher, etc.). Moving them here
    // means a single fix benefits every tool that references the DLL.
    // =========================================================================
    public static class ProfileHelper
    {
        // Zero-width / invisible Unicode characters commonly injected into
        // chat usernames to create look-alike accounts or bypass string
        // comparisons. We strip these during handle normalization.
        private static readonly char[] ZeroWidth =
        {
            '\u200B', // ZERO WIDTH SPACE
            '\u200C', // ZERO WIDTH NON-JOINER
            '\u200D', // ZERO WIDTH JOINER
            '\uFEFF', // ZERO WIDTH NO-BREAK SPACE (byte-order mark)
            '\u200E', // LEFT-TO-RIGHT MARK
            '\u200F', // RIGHT-TO-LEFT MARK
            '\u2060', // WORD JOINER
        };

        // ─── StripSurrogates ─────────────────────────────────────────────────
        // Removes Unicode surrogate characters from a string.
        //
        // WHY: Profile image URLs returned by some platforms occasionally
        // contain lone surrogates (invalid in UTF-16). These cause crashes in
        // Newtonsoft.Json serialization and certain file I/O calls.
        // ─────────────────────────────────────────────────────────────────────
        public static string StripSurrogates(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return new string(input.Where(c => !char.IsSurrogate(c)).ToArray());
        }

        // ─── CleanHandle ─────────────────────────────────────────────────────
        // Normalizes a display name or login for consistent string comparison.
        //
        // Steps performed (in order):
        //   1. Trim leading / trailing whitespace
        //   2. Strip leading @ (e.g. "@osuphoenix" -> "osuphoenix")
        //   3. Remove control characters and zero-width Unicode chars
        //   4. Unicode NFKC normalization (collapses look-alike characters)
        //   5. Lowercase
        //
        // This matches the normalization applied by the exclusion-list checker
        // in the Achievement System, so lookups are reliable even when display
        // names arrive with decorative Unicode or formatting chars.
        // ─────────────────────────────────────────────────────────────────────
        public static string CleanHandle(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            var trimmed = s.Trim().TrimStart('@');

            var filtered = new string(
                trimmed.Where(ch => !char.IsControl(ch) && !ZeroWidth.Contains(ch)).ToArray()
            );

            return filtered.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        }

        // ─── DetectPlatform ───────────────────────────────────────────────────
        // Identifies the streaming platform from Streamer.bot event arguments.
        // Returns "twitch", "youtube", or "kick" (always lowercase).
        //
        // Detection order:
        //   1. Explicit "platform" arg (override)
        //   2. "commandSource" arg (set by Command triggers)
        //   3. "userType" arg (set by some core triggers)
        //   4. Heuristic: YouTube exposes userProfileUrl;
        //                 Kick exposes targetUserProfileImageUrl
        //   5. Default fallback: "twitch"
        //
        // USAGE (from Streamer.bot inline code):
        //   string plat = ProfileHelper.DetectPlatform(
        //       name => args.ContainsKey(name) ? args[name]?.ToString() : null);
        // ─────────────────────────────────────────────────────────────────────
        public static string DetectPlatform(Func<string, string> getArg)
        {
            if (getArg == null)
                return "twitch";

            // 1) Explicit override
            string p = getArg("platform");
            if (!string.IsNullOrWhiteSpace(p))
                return p.ToLowerInvariant();

            // 2) Command source
            p = getArg("commandSource");
            if (!string.IsNullOrWhiteSpace(p))
                return p.ToLowerInvariant();

            // 3) User type
            p = getArg("userType");
            if (!string.IsNullOrWhiteSpace(p))
                return p.ToLowerInvariant();

            // 4) Heuristics
            if (!string.IsNullOrWhiteSpace(getArg("userProfileUrl")) ||
                !string.IsNullOrWhiteSpace(getArg("userProfileUrlEscaped")))
                return "youtube";

            if (!string.IsNullOrWhiteSpace(getArg("targetUserProfileImageUrl")) ||
                !string.IsNullOrWhiteSpace(getArg("targetUserProfileImageUrlEscaped")))
                return "kick";

            return "twitch";
        }
    }
}
