// =============================================================================
// OSWTools — Core/UserVarsExtensions.cs
//
// Extra UserVars helpers. Adds ResetUserVarsAcrossUsers() which clears the
// specified vars for every user on a platform — useful for "reset everyone's
// achievements" admin commands.
// =============================================================================

using System;
using System.Linq;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: For each user with any of the specified vars on `platform`,
        // unset (delete) those vars. Returns the count of (user × var) pairs
        // that were processed.
        //
        // Equivalent to running:
        //   foreach var in varKeys:
        //     foreach user in GetUsersVar(platform, var):
        //       UnsetUserVar(platform, user, var)
        //
        // Use this for admin commands like "reset all Twitch achievements".
        //
        // Errors per-user are caught and logged so one bad user doesn't abort
        // the whole sweep.
        // ─────────────────────────────────────────────────────────────────────
        public int ResetUserVarsAcrossUsers(string platform, params string[] varKeys)
        {
            if (string.IsNullOrWhiteSpace(platform) || varKeys == null || varKeys.Length == 0)
                return 0;

            string p = platform.ToLowerInvariant();
            int processed = 0;

            foreach (var varKey in varKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                try
                {
                    // Pull every user who has ANY value for this var (string read is universal)
                    var users = GetUsersVar<string>(p, varKey);
                    foreach (var entry in users)
                    {
                        try
                        {
                            UnsetUserVar(p, entry.UserName, varKey);
                            processed++;
                        }
                        catch (Exception inner)
                        {
                            LogWarn($"[ResetUserVars] Failed to unset {p}/{entry.UserName}/{varKey}: {inner.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError($"[ResetUserVars] Failed to enumerate {p}/{varKey}: {ex.Message}");
                }
            }

            LogInfo($"[ResetUserVars] {p}: cleared {processed} (user × var) entries.");
            return processed;
        }
    }
}
