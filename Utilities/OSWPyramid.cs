// =============================================================================
// OSWTools — Utilities/OSWPyramid.cs
//
// Chat pyramid detection — a generic state machine that watches a stream of
// chat messages on a given platform and detects when the user has typed a
// full ascending → descending pyramid using the same emote/word.
//
// Example:
//   PhoenixHype
//   PhoenixHype PhoenixHype
//   PhoenixHype PhoenixHype PhoenixHype     ← peak (3)
//   PhoenixHype PhoenixHype
//   PhoenixHype                              ← pyramid completes here (peak=3)
//
// USAGE:
//   var result = OSWPyramid.ProcessMessage(
//       _CPH,
//       platform: "Twitch",
//       message:  msg,
//       validateToken: tok => Lib.IsKnownEmote(tok) || Lib.IsTwitchEmoteToken(tok, msg, ircEmotes));
//
//   if (result.Completed)
//       FireAchievement(result.Emote, result.Peak);
//
// State is stored as a session global per platform: osw_PyramidState_{Platform}.
// Sessions don't span SB restarts — that's fine; pyramids are ephemeral.
// =============================================================================

using System;
using System.Linq;
using Newtonsoft.Json;
using Streamer.bot.Plugin.Interface;

namespace OSWTools.Utilities
{
    // ─────────────────────────────────────────────────────────────────────────
    // Result returned by ProcessMessage().
    //   Completed = true ONLY on the message that finishes the descending arm.
    //   Otherwise the state machine quietly tracks progress.
    // ─────────────────────────────────────────────────────────────────────────
    public class PyramidResult
    {
        public bool   Completed { get; set; }
        public int    Peak      { get; set; }
        public string Emote     { get; set; }

        public static PyramidResult None => new PyramidResult { Completed = false };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal state — persisted between messages as JSON in a session global.
    // ─────────────────────────────────────────────────────────────────────────
    public class PyramidState
    {
        public string Emote { get; set; }
        public int CurrentCount { get; set; }
        public int Peak { get; set; }
        public bool Descending { get; set; }
    }

    public static class OSWPyramid
    {
        // Key namespace: per-platform session globals
        private const string StateVarPrefix = "osw_PyramidState_";

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Feed one chat message to the pyramid detector.
        //
        // Parameters:
        //   cph           — Streamer.bot proxy (for global var storage)
        //   platform      — "Twitch" / "YouTube" / "Kick" — keeps platforms isolated
        //   message       — raw chat message text
        //   validateToken — optional emote validator. If null, ANY uniform-token
        //                   message is treated as a candidate row. For Twitch you
        //                   typically want to require real emotes:
        //                       tok => Lib.IsKnownEmote(tok)
        //                              || Lib.IsTwitchEmoteToken(tok, message, ircEmotes)
        //
        // Returns: PyramidResult.None most of the time. .Completed = true only
        // on the single message that finishes the descending arm.
        // ─────────────────────────────────────────────────────────────────────
        public static PyramidResult ProcessMessage(
            IInlineInvokeProxy cph,
            string platform,
            string message,
            Func<string, bool> validateToken = null)
        {
            if (cph == null) return PyramidResult.None;
            if (string.IsNullOrWhiteSpace(platform)) platform = "Twitch";

            // 1) Try to extract a uniform-emote row from the message
            if (!TryExtractUniformToken(message, validateToken, out string token, out int count))
            {
                // Non-pyramid line — clear any in-progress state
                if (LoadState(cph, platform) != null)
                    ResetState(cph, platform);
                return PyramidResult.None;
            }

            var state = LoadState(cph, platform);

            // 2) No active pyramid yet
            if (state == null)
            {
                // A single token starts a new candidate; longer rows can't start one
                if (count == 1)
                    SaveState(cph, platform, new PyramidState { Emote = token, CurrentCount = 1 });
                return PyramidResult.None;
            }

            // 3) Different emote — abort or restart
            if (state.Emote != token)
            {
                if (count == 1)
                    SaveState(cph, platform, new PyramidState { Emote = token, CurrentCount = 1 });
                else
                    ResetState(cph, platform);
                return PyramidResult.None;
            }

            // 4) Same emote — advance the state machine
            return state.Descending
                ? HandleDescending(cph, platform, state, count)
                : HandleAscending(cph, platform, state, token, count);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Manually wipe pyramid state for a platform (e.g. on stream end)
        // ─────────────────────────────────────────────────────────────────────
        public static void Reset(IInlineInvokeProxy cph, string platform)
        {
            ResetState(cph, platform);
        }

        // ═════════════════════════════════════════════════════════════════════
        // INTERNAL state machine
        // ═════════════════════════════════════════════════════════════════════

        private static PyramidResult HandleAscending(
            IInlineInvokeProxy cph, string platform, PyramidState state, string token, int count)
        {
            // Continuing up: each step adds exactly 1
            if (count == state.CurrentCount + 1)
            {
                state.CurrentCount = count;
                SaveState(cph, platform, state);
                return PyramidResult.None;
            }

            // Switched to descending: count is one less than current
            if (count == state.CurrentCount - 1 && state.CurrentCount >= 2)
            {
                state.Peak = state.CurrentCount;
                state.CurrentCount = count;
                state.Descending = true;

                // Edge case: peak was 2, now we're at 1 — pyramid completes immediately
                if (count == 1)
                {
                    int peak = state.Peak;
                    string emote = state.Emote;
                    ResetState(cph, platform);
                    return new PyramidResult { Completed = true, Peak = peak, Emote = emote };
                }

                SaveState(cph, platform, state);
                return PyramidResult.None;
            }

            // Repeat at count=1 with current=1 — just a duplicate single, ignore
            if (count == 1 && state.CurrentCount == 1)
                return PyramidResult.None;

            // Anything else breaks the pattern — restart or reset
            if (count == 1)
                SaveState(cph, platform, new PyramidState { Emote = token, CurrentCount = 1 });
            else
                ResetState(cph, platform);

            return PyramidResult.None;
        }

        private static PyramidResult HandleDescending(
            IInlineInvokeProxy cph, string platform, PyramidState state, int count)
        {
            // Continuing down: each step subtracts exactly 1
            if (count == state.CurrentCount - 1)
            {
                state.CurrentCount = count;

                // Reached the bottom (count=1) — pyramid completes
                if (count == 1)
                {
                    int peak = state.Peak;
                    string emote = state.Emote;
                    ResetState(cph, platform);
                    return new PyramidResult { Completed = true, Peak = peak, Emote = emote };
                }

                SaveState(cph, platform, state);
                return PyramidResult.None;
            }

            // Pattern broken — restart on a single, otherwise reset
            if (count == 1)
                SaveState(cph, platform, new PyramidState { Emote = state.Emote, CurrentCount = 1 });
            else
                ResetState(cph, platform);

            return PyramidResult.None;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Token extraction — splits on whitespace, requires all tokens identical,
        // and (if validateToken supplied) requires the token to pass validation.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryExtractUniformToken(
            string message, Func<string, bool> validateToken, out string token, out int count)
        {
            token = null;
            count = 0;

            if (string.IsNullOrWhiteSpace(message))
                return false;

            var tokens = message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return false;

            string first = tokens[0];
            if (tokens.Any(t => t != first))
                return false;

            // Validation is opt-in. When provided, the token must be a real emote.
            if (validateToken != null && !validateToken(first))
                return false;

            token = first;
            count = tokens.Length;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Storage — session globals, namespaced per platform
        // ─────────────────────────────────────────────────────────────────────
        private static string StateKey(string platform) => StateVarPrefix + platform;

        private static PyramidState LoadState(IInlineInvokeProxy cph, string platform)
        {
            try
            {
                string json = cph.GetGlobalVar<string>(StateKey(platform), false) ?? "";
                if (string.IsNullOrWhiteSpace(json))
                    return null;
                return JsonConvert.DeserializeObject<PyramidState>(json);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveState(IInlineInvokeProxy cph, string platform, PyramidState state)
        {
            try
            {
                cph.SetGlobalVar(StateKey(platform), JsonConvert.SerializeObject(state), false);
            }
            catch { /* CPH failures shouldn't crash chat handling */ }
        }

        private static void ResetState(IInlineInvokeProxy cph, string platform)
        {
            try
            {
                cph.SetGlobalVar(StateKey(platform), "", false);
            }
            catch { }
        }
    }
}
