// =============================================================================
// OSWTools — Extensions/BUEEngine.cs
//
// "Blow Up Emote" (module code: BUE) — core logic.
//
// ── CHANGELOG ────────────────────────────────────────────────────────────────
// v2.0.0  (REQUIRES BUEModels v2.0.0, BUEStore, BUEPayloads, OSWLib_Bue)
//   ADDED   CountMatchesForMessage now honors config.CountMode (PerEmote /
//           PerMessage) and config.MaxMatchesPerMessage (0 = no limit).
//   ADDED   NormalizeConfig()      — clamps/tidies settings (called on load AND save)
//   ADDED   ApplyEmoteSelection()  — "make this THE emote" (name, counted list, picture)
//   ADDED   IsBroadcaster()        — decides if a trigger came from the streamer
//   ADDED   PickEmote()            — finds the emote in a viewer's text
//   ADDED   LearnEmoteImages() / ResolveDisplayImage() — picture lookup now uses
//           BueStore (JSON file) instead of a JSON string in a global variable.
//   ADDED   DescribeSettings()     — plain-English preview shown in the settings window
//   REMOVED MergeEmoteImageCache() / FindEmoteImage() / ResolveEmoteImage()
//           (the string-cache versions). Nothing else used them.
// v1.0.2  ADDED FirstMatchedToken, ParseEmoteImages, image helpers.
// v1.0.1  ADDED IsPlatformEnabled, CountMatchesForMessage (HashSet kept in the DLL).
//
// LEARNING NOTE FOR YOU:
//   Most methods here are "pure": plain values in, plain values out, no CPH,
//   no files, no network. Pure functions are the easiest code to test and to
//   trust, because the answer depends only on the parameters you can see.
//   The few that need the outside world (ResolveMatchSet, PickEmote,
//   ResolveDisplayImage) take an OSWLib and are marked IMPURE.
//
// WHERE THIS FILE LIVES:
//   OSWTools DLL project, Extensions/BUEEngine.cs. A plain static class
//   (BlowUpEmoteEngine), not a partial OSWLib.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OSWTools.BlowUpEmote
{
    public static class BlowUpEmoteEngine
    {
        // ─────────────────────────────────────────────────────────────────
        // PURE: Count how many tokens in `message` are in `matchSet`.
        //
        // WHY TOKEN MATCHING (not substring/"Contains"):
        //   Emotes in chat are always their own whitespace-separated word —
        //   "LULW LULW" is two matches, but a substring check would also
        //   (wrongly) match "LULWOW". Splitting on whitespace and comparing
        //   whole tokens avoids that.
        //
        // maxMatchesPerMessage: pass 0 for "no cap".
        // ─────────────────────────────────────────────────────────────────
        public static int CountEmoteMatches(string message, HashSet<string> matchSet, int maxMatchesPerMessage)
        {
            if (string.IsNullOrWhiteSpace(message) || matchSet == null || matchSet.Count == 0)
                return 0;

            var tokens = message.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            int count = tokens.Count(t => matchSet.Contains(t));

            if (maxMatchesPerMessage > 0 && count > maxMatchesPerMessage)
                count = maxMatchesPerMessage;

            return count;
        }

        // ─────────────────────────────────────────────────────────────────
        // PURE: Apply one message's worth of matches to the balloon state.
        //
        // Mutates `state` in place (the caller owns it and saves it) and
        // returns a result describing what happened.
        //
        // ORDER OF OPERATIONS matters here:
        //   1. Cooldown check FIRST — a user on cooldown contributes nothing.
        //   2. Growth math — clamp so FillPercent can never leave [0, 100].
        //   3. Leaderboard bookkeeping — both dictionaries updated together.
        //   4. Pop check — >= 100 pops it. ResetOnPop decides whether the
        //      round leaderboard clears; AllTimeContributors NEVER clears.
        // ─────────────────────────────────────────────────────────────────
        public static BueContributionResult ProcessContribution(
            BueState state,
            BueTargetConfig config,
            string user,
            int matchCount,
            DateTime nowUtc,
            Random rng = null)
        {
            var result = new BueContributionResult { MatchCount = matchCount };

            if (state == null || config == null || string.IsNullOrWhiteSpace(user) || matchCount <= 0)
            {
                result.NewFillPercent = state?.FillPercent ?? 0;
                return result;
            }

            // ── 0. Rounds: is a round running? ─────────────────────────
            //   Balloon hidden -> chat emotes do nothing at all. Only StartRound()
            //   (the Start Round action) can turn it on, so nothing loops by itself.
            if (config.StartHidden && !state.Active)
            {
                result.Inactive = true;
                result.NewFillPercent = state.FillPercent;
                return result;
            }

            // ── 1. Cooldown check ──────────────────────────────────────
            if (config.PerUserCooldownSeconds > 0 &&
                state.LastContributionAtUtc.TryGetValue(user, out DateTime lastAt))
            {
                double secondsSince = (nowUtc - lastAt).TotalSeconds;
                if (secondsSince < config.PerUserCooldownSeconds)
                {
                    result.CooldownBlocked = true;
                    result.NewFillPercent = state.FillPercent;
                    return result;
                }
            }

            // ── 2. Growth math (clamped) ───────────────────────────────
            //    First let the balloon lose whatever it has drained since the last
            //    time we looked, THEN add this message's growth.
            ApplyDeflation(state, config, nowUtc);

            double growth = RollGrowth(config, matchCount, rng);
            double newFill = state.FillPercent + growth;
            if (newFill > 100) newFill = 100;
            if (newFill < 0) newFill = 0;

            // The amount that ACTUALLY applied might be less than `growth`
            // if we were already close to 100 — report the real delta.
            result.GrowthApplied = newFill - state.FillPercent;
            state.FillPercent = newFill;
            state.FillAsOfUtc = nowUtc;
            result.NewFillPercent = newFill;

            // ── 3. Leaderboard bookkeeping ─────────────────────────────
            state.RoundContributors[user] =
                state.RoundContributors.TryGetValue(user, out double roundVal) ? roundVal + result.GrowthApplied : result.GrowthApplied;

            state.AllTimeContributors[user] =
                state.AllTimeContributors.TryGetValue(user, out double allTimeVal) ? allTimeVal + result.GrowthApplied : result.GrowthApplied;

            state.LastContributionAtUtc[user] = nowUtc;

            // ── 4. Pop check ────────────────────────────────────────────
            if (newFill >= 100)
            {
                result.Popped = true;
                result.PopperUser = user;

                state.TotalPops++;
                state.LastPoppedBy = user;
                state.LastPoppedAtUtc = nowUtc;

                if (config.StartHidden)
                {
                    // Round over: hide the balloon and leave it off until someone
                    // starts the next round on purpose. Always start it empty.
                    state.Active = false;
                    state.FillPercent = 0;
                    state.RoundEmote = "";
                    state.RoundImageUrl = "";
                }

                if (config.ResetOnPop)
                {
                    state.FillPercent = 0;
                    state.RoundContributors.Clear();
                    // AllTimeContributors is intentionally left untouched.
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // RANDOM GROWTH
        //
        // PURE (given the Random): how many percentage points do `entries` entries
        // add? Fixed mode: entries * GrowthPerUse. Random mode: one dice roll PER
        // ENTRY, each between GrowthMinPercent and GrowthMaxPercent, rounded to
        // hundredths, then added up.
        //
        // LEARNING NOTE: the Random is a PARAMETER (null = use the shared one).
        // That keeps the maths testable: a test passes a Random with a fixed seed
        // and gets the same numbers every time.
        // ─────────────────────────────────────────────────────────────────
        private static readonly Random SharedRng = new Random();

        public static double RollGrowth(BueTargetConfig config, int entries, Random rng = null)
        {
            if (entries <= 0 || config == null) return 0;
            if (!config.RandomGrowth) return entries * config.GrowthPerUse;

            double lo = Math.Min(config.GrowthMinPercent, config.GrowthMaxPercent);
            double hi = Math.Max(config.GrowthMinPercent, config.GrowthMaxPercent);
            double total = 0;
            lock (SharedRng)   // System.Random is not thread-safe; chat actions can overlap
            {
                Random r = rng ?? SharedRng;
                for (int i = 0; i < entries; i++)
                    total += Math.Round(lo + r.NextDouble() * (hi - lo), 2);
            }
            return total;
        }

        // ─────────────────────────────────────────────────────────────────
        // DEFLATION
        //
        // PURE: apply the time that has passed since state.FillAsOfUtc.
        //   fill = max(0, fill - DeflatePerSecond * secondsPassed)
        // Only while a balloon is on screen (rounds off, or a round running).
        // Returns true if the fill changed. Always stamps FillAsOfUtc = now, so the
        // next call only counts NEW time and the drain is never counted twice.
        //
        // WHY CALCULATE INSTEAD OF A TIMER? Nothing needs to run every second on
        // the Streamer.bot side. The fill is a straight line over time, so
        // "what is it now?" is one subtraction whenever somebody asks. The widget
        // draws the same line smoothly between messages.
        // ─────────────────────────────────────────────────────────────────
        public static bool ApplyDeflation(BueState state, BueTargetConfig config, DateTime nowUtc)
        {
            if (state == null || config == null) return false;

            bool changed = false;
            bool onScreen = !config.StartHidden || state.Active;
            if (config.DeflatePerSecond > 0 && onScreen && state.FillPercent > 0 && state.FillAsOfUtc.HasValue)
            {
                double secs = (nowUtc - state.FillAsOfUtc.Value).TotalSeconds;
                if (secs > 0)
                {
                    double before = state.FillPercent;
                    state.FillPercent = Math.Max(0, before - config.DeflatePerSecond * secs);
                    changed = state.FillPercent != before;
                }
            }
            state.FillAsOfUtc = nowUtc;
            return changed;
        }

        // ─────────────────────────────────────────────────────────────────
        // IMPURE (needs Lib): Build the actual set of emote names to match
        // against, for this platform, right now.
        //
        //   Twitch  → live Helix data via Lib.GetChannelEmotes()/GetGlobalEmotes(),
        //             shaped by config.Mode, ALWAYS unioned with SpecificEmotes.
        //   YouTube/Kick → SpecificEmotes ONLY (no emote API for those).
        // ─────────────────────────────────────────────────────────────────
        public static HashSet<string> ResolveMatchSet(OSWLib lib, BueTargetConfig config, string platform)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (config?.SpecificEmotes != null)
                foreach (var e in config.SpecificEmotes)
                    if (!string.IsNullOrWhiteSpace(e))
                        result.Add(e.Trim());

            bool isTwitch = string.Equals(platform, "Twitch", StringComparison.OrdinalIgnoreCase);

            if (!isTwitch)
            {
                if (config != null && config.Mode != EmoteSourceMode.SpecificList)
                {
                    lib?.LogDebug(
                        $"[BUE] Target '{config.TargetId}': Mode={config.Mode} has no effect on {platform} " +
                        "(no emote API available) — using SpecificEmotes only.");
                }
                return result;
            }

            if (lib == null)
                return result;

            switch (config?.Mode)
            {
                case EmoteSourceMode.Channel:
                    result.UnionWith(lib.GetChannelEmotes());
                    break;
                case EmoteSourceMode.Global:
                    result.UnionWith(lib.GetGlobalEmotes());
                    break;
                case EmoteSourceMode.All:
                    result.UnionWith(lib.GetChannelEmotes());
                    result.UnionWith(lib.GetGlobalEmotes());
                    break;
                case EmoteSourceMode.SpecificList:
                default:
                    break;
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // "Facade" wrappers for the Streamer.bot side.
        //
        // LEARNING NOTE: Streamer.bot's code editor only knows the assemblies
        // in its References tab. Your DLL uses HashSet<T> (System.Core), the
        // editor doesn't reference that, so touching a HashSet in an action
        // file gave CS0012. The fix is design, not references: public methods
        // that actions call speak in SIMPLE types (string, int, bool), and
        // the fancy collections stay inside the DLL.
        // ─────────────────────────────────────────────────────────────────

        /// <summary>True if this target is configured to listen to the given platform.</summary>
        public static bool IsPlatformEnabled(BueTargetConfig config, string platform)
        {
            if (config?.EnabledPlatforms == null || string.IsNullOrWhiteSpace(platform))
                return false;

            // Case-insensitive on purpose: a hand-edited "twitch" should work.
            return config.EnabledPlatforms.Any(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// What counts RIGHT NOW. Rounds on: ONLY the running round's emote (the
        /// Settings emote and Mode do not restrict it). Rounds off: the Settings
        /// pool (ResolveMatchSet).
        /// </summary>
        public static HashSet<string> MatchSetFor(OSWLib lib, BueTargetConfig config, string platform, string roundEmote)
        {
            if (config != null && config.StartHidden)
            {
                var one = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(roundEmote)) one.Add(roundEmote.Trim());
                return one;
            }
            return ResolveMatchSet(lib, config, platform);
        }

        /// <summary>
        /// How many entries does this chat message earn?
        ///
        ///   PerEmote   → number of matching emotes, capped by MaxMatchesPerMessage
        ///                (0 = no cap).
        ///   PerMessage → 1 if the message has at least one matching emote, else 0.
        ///
        /// Returns 0 on any bad input.
        /// </summary>
        public static int CountMatchesForMessage(OSWLib lib, BueTargetConfig config, string platform, string message, string roundEmote = null)
        {
            if (config == null)
                return 0;

            var matchSet = MatchSetFor(lib, config, platform, roundEmote);

            // PerMessage is just "PerEmote with a cap of 1". Reusing the same
            // counting code means there is only one place that can have a bug.
            int cap = config.CountMode == GrowthCountMode.PerMessage ? 1 : config.MaxMatchesPerMessage;
            return CountEmoteMatches(message, matchSet, cap);
        }

        /// <summary>
        /// The first word in the message that is one of the counted emotes
        /// (exactly as typed in chat), or "" if none.
        /// </summary>
        public static string FirstMatchedToken(OSWLib lib, BueTargetConfig config, string platform, string message, string roundEmote = null)
        {
            if (config == null || string.IsNullOrWhiteSpace(message))
                return "";

            var matchSet = MatchSetFor(lib, config, platform, roundEmote);
            foreach (var t in message.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (matchSet.Contains(t))
                    return t;
            return "";
        }

        // ─────────────────────────────────────────────────────────────────
        // SETTINGS HELPERS
        // ─────────────────────────────────────────────────────────────────

        public static bool IsHttpsUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) &&
                   url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// PURE: tidy a config so bad values can't break the tool.
        /// Runs when a file is loaded AND before it is saved, so a typo in a
        /// hand-edited file (GrowthPerUse: -5) is corrected instead of used.
        /// </summary>
        public static void NormalizeConfig(BueTargetConfig c, string fallbackTargetId)
        {
            if (c == null) return;

            c.ConfigVersion = 2;

            if (string.IsNullOrWhiteSpace(c.TargetId))
                c.TargetId = string.IsNullOrWhiteSpace(fallbackTargetId) ? "default" : fallbackTargetId.Trim();

            // Lists: no nulls, trimmed, no blanks.
            var emotes = new List<string>();
            if (c.SpecificEmotes != null)
                foreach (var e in c.SpecificEmotes)
                    if (!string.IsNullOrWhiteSpace(e))
                        emotes.Add(e.Trim());
            c.SpecificEmotes = emotes;

            // Rebuild the platform set case-insensitive (loading from JSON
            // replaces it with a case-sensitive one).
            c.EnabledPlatforms = c.EnabledPlatforms == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Twitch", "YouTube", "Kick" }
                : new HashSet<string>(c.EnabledPlatforms, StringComparer.OrdinalIgnoreCase);

            c.DisplayEmote = (c.DisplayEmote ?? "").Trim();
            c.AllowedEmotes = CleanList(c.AllowedEmotes);
            c.BannedEmotes = CleanList(c.BannedEmotes);
            c.ImageOverrideUrl = IsHttpsUrl(c.ImageOverrideUrl) ? c.ImageOverrideUrl.Trim() : "";

            // Numbers: keep them in a range that makes sense.
            if (double.IsNaN(c.GrowthPerUse) || c.GrowthPerUse < 0.01) c.GrowthPerUse = 0.01;
            if (c.GrowthPerUse > 100) c.GrowthPerUse = 100;
            if (c.MaxMatchesPerMessage < 0) c.MaxMatchesPerMessage = 0;
            if (c.PerUserCooldownSeconds < 0) c.PerUserCooldownSeconds = 0;

            // Random growth range: both ends 0.01-100, and min never above max.
            if (double.IsNaN(c.GrowthMinPercent) || c.GrowthMinPercent < 0.01) c.GrowthMinPercent = 0.01;
            if (double.IsNaN(c.GrowthMaxPercent) || c.GrowthMaxPercent < 0.01) c.GrowthMaxPercent = 0.01;
            if (c.GrowthMinPercent > 100) c.GrowthMinPercent = 100;
            if (c.GrowthMaxPercent > 100) c.GrowthMaxPercent = 100;
            if (c.GrowthMinPercent > c.GrowthMaxPercent)
            { double t = c.GrowthMinPercent; c.GrowthMinPercent = c.GrowthMaxPercent; c.GrowthMaxPercent = t; }
            c.GrowthMinPercent = Math.Round(c.GrowthMinPercent, 2);
            c.GrowthMaxPercent = Math.Round(c.GrowthMaxPercent, 2);

            // Deflation: 0 (off) to 100 per second, hundredths.
            if (double.IsNaN(c.DeflatePerSecond) || c.DeflatePerSecond < 0) c.DeflatePerSecond = 0;
            if (c.DeflatePerSecond > 100) c.DeflatePerSecond = 100;
            c.DeflatePerSecond = Math.Round(c.DeflatePerSecond, 2);
        }

        /// <summary>
        /// PURE: cancel the current round WITHOUT a pop. The balloon empties and
        /// hides, the round leaderboard clears and per-user cooldowns are forgotten.
        /// Kept on purpose: all-time contributors and the pop count (a cancelled
        /// round isn't a pop, so nobody gets credit or an achievement for it).
        /// </summary>
        public static void CancelRound(BueState state)
        {
            if (state == null) return;
            state.Active = false;
            state.FillPercent = 0;
            state.RoundEmote = "";
            state.RoundImageUrl = "";
            state.RoundContributors.Clear();
            state.LastContributionAtUtc.Clear();
        }

        /// <summary>
        /// PURE: begin a round. Returns false (and changes nothing) if one is
        /// already running or if rounds are switched off. A round always starts empty.
        /// </summary>
        public static bool StartRound(BueState state, BueTargetConfig config, string emote, string imageUrl)
        {
            if (state == null || config == null || !config.StartHidden || state.Active) return false;
            if (string.IsNullOrWhiteSpace(emote)) return false;
            state.Active = true;
            state.FillPercent = 0;
            state.RoundEmote = emote.Trim();
            state.RoundImageUrl = IsHttpsUrl(imageUrl) ? imageUrl.Trim() : "";
            return true;
        }

        /// <summary>PURE: trim, drop blanks and duplicates (case-insensitive). Never null.</summary>
        public static List<string> CleanList(IEnumerable<string> raw)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            if (raw != null)
                foreach (var e in raw)
                {
                    string t = (e ?? "").Trim();
                    if (t.Length > 0 && seen.Add(t)) list.Add(t);
                }
            return list;
        }

        /// <summary>PURE: split typed text ("KEKW, LULW" or one per line) into a clean list.</summary>
        public static List<string> ParseEmoteList(string text)
        {
            return CleanList((text ?? "").Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool ListHas(List<string> list, string emote)
        {
            if (list == null) return false;
            foreach (var e in list)
                if (string.Equals((e ?? "").Trim(), emote, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// PURE: may this emote be PICKED for the round?
        ///   - The streamer is always allowed (trusted; their trigger is the gate).
        ///   - Banned beats everything.
        ///   - A non-empty allowed list means "only these"; an EMPTY one means any.
        /// </summary>
        public static bool IsEmotePickAllowed(BueTargetConfig c, string emote, bool fromStreamer, out string reason)
        {
            reason = "";
            if (fromStreamer) return true;
            if (ListHas(c == null ? null : c.BannedEmotes, emote)) { reason = "'" + emote + "' is on the banned list."; return false; }
            if (c != null && c.AllowedEmotes != null && c.AllowedEmotes.Count > 0 && !ListHas(c.AllowedEmotes, emote))
            { reason = "'" + emote + "' is not on the allowed list."; return false; }
            return true;
        }

        /// <summary>
        /// The emote chat has to type RIGHT NOW.
        ///   Rounds on  -> the running round's emote (the Settings emote is only a default).
        ///   Rounds off -> the Settings emote.
        /// </summary>
        public static string ActiveEmote(BueTargetConfig c, BueState s)
        {
            if (c == null) return "";
            if (c.StartHidden)
                return !string.IsNullOrWhiteSpace(s == null ? null : s.RoundEmote) ? s.RoundEmote.Trim() : CurrentEmote(c);
            return CurrentEmote(c);
        }

        /// <summary>IMPURE: the picture for ActiveEmote - the round's own link first, else the usual lookup.</summary>
        public static string ActiveImage(OSWLib lib, BueTargetConfig c, BueState s, string platform)
        {
            string emote = ActiveEmote(c, s);
            if (c != null && c.StartHidden && s != null && !string.IsNullOrWhiteSpace(s.RoundEmote))
            {
                if (IsHttpsUrl(s.RoundImageUrl)) return s.RoundImageUrl;
                // The Settings picture link belongs to the Settings emote only.
                bool tryTwitch = string.IsNullOrWhiteSpace(platform) || string.Equals(platform, "Twitch", StringComparison.OrdinalIgnoreCase);
                if (lib != null && tryTwitch)
                {
                    string url = lib.GetEmoteImageUrl(emote);
                    if (!string.IsNullOrEmpty(url)) return url;
                }
                return BueStore.GetImage(c.TargetId, emote);
            }
            return ResolveDisplayImage(lib, c, platform, emote);
        }

        /// <summary>Should the widget be showing the balloon right now?</summary>
        public static bool IsBalloonActive(BueTargetConfig config, BueState state)
        {
            return config == null || !config.StartHidden || (state != null && state.Active);
        }

        /// <summary>The emote the settings window should show: DisplayEmote, else the first counted one.</summary>
        public static string CurrentEmote(BueTargetConfig c)
        {
            if (c == null) return "";
            if (!string.IsNullOrWhiteSpace(c.DisplayEmote)) return c.DisplayEmote.Trim();
            if (c.SpecificEmotes != null)
                foreach (var e in c.SpecificEmotes)
                    if (!string.IsNullOrWhiteSpace(e)) return e.Trim();
            return "";
        }

        /// <summary>
        /// PURE: make `emote` THE emote — it is what counts AND what is shown.
        /// `imageUrl` is optional (https only; anything else is ignored).
        /// </summary>
        public static void ApplyEmoteSelection(BueTargetConfig c, string emote, string imageUrl)
        {
            if (c == null) return;

            emote = (emote ?? "").Trim();
            c.DisplayEmote = emote;
            c.SpecificEmotes = new List<string>();
            if (emote.Length > 0) c.SpecificEmotes.Add(emote);
            c.Mode = EmoteSourceMode.SpecificList;
            c.ImageOverrideUrl = IsHttpsUrl(imageUrl) ? imageUrl.Trim() : "";
        }

        /// <summary>
        /// PURE: is this trigger from the broadcaster?
        ///
        /// Streamer.bot gives every trigger arguments about WHO fired it and
        /// who the broadcaster is. We compare IDs when both are present
        /// (most reliable), otherwise names. Rules:
        ///   - No user info at all → it was run by hand from Streamer.bot (or
        ///     a hotkey / Stream Deck), which is the streamer. Allowed.
        ///   - Can't compare → NOT allowed. Failing closed is the safe
        ///     direction when the question is "may this person change my stream".
        /// </summary>
        public static bool IsBroadcaster(string userId, string broadcasterId, string userName, string broadcasterName)
        {
            bool haveUser = !string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(userName);
            if (!haveUser) return true;

            if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(broadcasterId))
                return string.Equals(userId.Trim(), broadcasterId.Trim(), StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(broadcasterName))
                return string.Equals(userName.Trim(), broadcasterName.Trim(), StringComparison.OrdinalIgnoreCase);

            return false;
        }

        /// <summary>First word in the text that is an https:// link, or "".</summary>
        public static string FindHttpsUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            foreach (var t in text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (IsHttpsUrl(t)) return t.Trim();
            return "";
        }

        /// <summary>
        /// IMPURE: find THE emote in a viewer's text (a reward's input text, a
        /// cheer message, a command argument...).
        ///
        ///   1. A word the message's own emote list confirms (platform-supplied).
        ///   2. A word Twitch's channel/global emote cache knows.
        ///   3. Only if allowAnyWord (the trusted streamer): the first word that
        ///      isn't a link — so the streamer can pick a 7TV/BTTV emote no API knows.
        ///
        /// Returns "" when nothing qualifies. For viewers (allowAnyWord = false)
        /// that means "no real emote in your message", so a viewer can't turn the
        /// balloon into the word "hello".
        /// </summary>
        public static string PickEmote(OSWLib lib, string text, Dictionary<string, string> emotesInMessage, bool allowAnyWord)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var tokens = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (emotesInMessage != null && emotesInMessage.Count > 0)
                foreach (var t in tokens)
                    if (emotesInMessage.ContainsKey(t)) return t;

            if (lib != null)
                foreach (var t in tokens)
                    if (!string.IsNullOrEmpty(lib.GetEmoteImageUrl(t))) return t;

            if (allowAnyWord)
                foreach (var t in tokens)
                    if (!IsHttpsUrl(t) && !t.StartsWith("!")) return t;   // skip links and chat commands

            return "";
        }

        /// <summary>
        /// PURE: a plain-English summary of what these settings do. The settings
        /// window shows it live so the numbers stop being abstract.
        /// </summary>
        public static string DescribeSettings(BueTargetConfig c)
        {
            if (c == null) return "";
            var inv = CultureInfo.InvariantCulture;

            double g = c.GrowthPerUse < 0.01 ? 0.01 : c.GrowthPerUse;
            double lo = Math.Min(c.GrowthMinPercent, c.GrowthMaxPercent), hi = Math.Max(c.GrowthMinPercent, c.GrowthMaxPercent);
            double avg = c.RandomGrowth ? (lo + hi) / 2.0 : g;
            if (avg < 0.01) avg = 0.01;
            string gs = c.RandomGrowth ? "a random " + lo.ToString("0.##", inv) + "-" + hi.ToString("0.##", inv) + "%" : g.ToString("0.##", inv) + "%";
            int needed = (int)Math.Ceiling(100.0 / avg - 1e-9);

            var sb = new StringBuilder();
            string avgNote = c.RandomGrowth ? " on average" : "";
            if (c.CountMode == GrowthCountMode.PerMessage)
            {
                sb.Append("Each chat message with the emote adds " + gs + " (counted once, however many emotes it has). ");
                sb.Append("About " + needed + " messages to fill" + avgNote + ". ");
            }
            else
            {
                sb.Append("Each emote adds " + gs + ". About " + needed + " emotes to fill" + avgNote + ". ");
                if (c.MaxMatchesPerMessage > 0)
                    sb.Append("One message counts at most " + c.MaxMatchesPerMessage + " emotes. ");
                else
                    sb.Append("No limit per message. ");
            }

            if (c.DeflatePerSecond > 0)
            {
                double perSec = c.DeflatePerSecond;
                double entriesPerSec = perSec / avg;
                sb.Append("It deflates " + perSec.ToString("0.##", inv) + "% every second (empty in " + (100.0 / perSec).ToString("0.#", inv) +
                          "s if nobody adds). Chat needs more than " + entriesPerSec.ToString("0.##", inv) + " entries per second just to keep it level. ");
            }

            sb.Append(c.StartHidden
                ? "The balloon is hidden until you start a round, and hides again after it pops. "
                : "The balloon is always on screen. ");

            int nAllow = c.AllowedEmotes == null ? 0 : c.AllowedEmotes.Count;
            int nBan = c.BannedEmotes == null ? 0 : c.BannedEmotes.Count;
            sb.Append(nAllow > 0 ? "Chat may only pick from " + nAllow + " allowed emote(s)" : "Chat may pick any real emote");
            if (nBan > 0) sb.Append(" (" + nBan + " banned)");
            sb.Append(". ");

            sb.Append(c.EmoteControl == EmoteControlMode.StreamerOnly
                ? "Only you can change the emote."
                : "Chat can change the emote through the triggers you attach.");
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────
        // EMOTE PICTURES
        //
        // We don't hard-code a list of picture URLs. Every chat message from
        // Streamer.bot carries an `emotes` list (name + image URL). We read it,
        // remember the pairs in images_<target>.json, and look them up later.
        // The first use of an emote teaches us its picture.
        //
        // ParseEmoteImages() is deliberately forgiving about property names
        // ("imageUrl", "ImageUrl", "image"...) because the format differs by
        // platform and we don't control it. Being forgiving about INPUT is a
        // good habit when you depend on someone else's data format.
        // ─────────────────────────────────────────────────────────────────

        private static string FirstString(JObject o, params string[] keys)
        {
            foreach (var k in keys)
            {
                JToken t;
                if (o.TryGetValue(k, StringComparison.OrdinalIgnoreCase, out t) &&
                    t.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)t))
                    return ((string)t).Trim();
            }
            return null;
        }

        /// <summary>Parse the `emotes` argument (a JSON array) into name -> https image URL.</summary>
        public static Dictionary<string, string> ParseEmoteImages(string emotesJson)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(emotesJson) || !emotesJson.TrimStart().StartsWith("["))
                return found;

            try
            {
                foreach (var token in JArray.Parse(emotesJson))
                {
                    var o = token as JObject;
                    if (o == null) continue;

                    string name = FirstString(o, "name", "text");
                    string url = FirstString(o, "imageUrl", "image", "url");

                    // Twitch fallback: emote ID but no URL → standard Twitch CDN address.
                    if (url == null)
                    {
                        string id = FirstString(o, "id");
                        if (id != null)
                            url = "https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/default/dark/3.0";
                    }

                    // Only https pictures; the widget will load whatever we send.
                    if (name != null && url != null && IsHttpsUrl(url))
                        found[name] = url;
                }
            }
            catch (Exception)
            {
                // Unparseable input just means "no pictures learned this time".
            }
            return found;
        }

        /// <summary>
        /// Read the pictures out of a message's `emotes` JSON and remember any new
        /// ones in the store. Returns what was found (also useful to the caller).
        /// </summary>
        public static Dictionary<string, string> LearnEmoteImages(string targetId, string emotesJson)
        {
            var found = ParseEmoteImages(emotesJson);
            if (found.Count > 0)
                BueStore.MergeImages(targetId, found);
            return found;
        }

        /// <summary>
        /// Which emote should the balloon SHOW? config.DisplayEmote wins; if it
        /// is empty, whichever counted emote appeared in the message.
        /// </summary>
        public static string ChooseDisplayEmote(BueTargetConfig config, string matchedToken)
        {
            if (config != null && !string.IsNullOrWhiteSpace(config.DisplayEmote))
                return config.DisplayEmote.Trim();
            return matchedToken ?? "";
        }

        /// <summary>
        /// IMPURE: best picture URL for the emote the balloon shows, or "".
        ///   1) config.ImageOverrideUrl (your manual link) - for the display emote
        ///   2) Twitch emote cache in the DLL (native channel/global emotes)
        ///   3) pictures learned from chat (7TV/BTTV, YouTube, Kick)
        ///
        /// `platform`: pass the chat platform. For triggers with no chat platform
        /// (commands, the settings window) pass "" and Twitch is tried anyway:
        /// the lookup is by name only, so a non-Twitch name just returns "".
        /// </summary>
        public static string ResolveDisplayImage(OSWLib lib, BueTargetConfig config, string platform, string emoteName)
        {
            if (config == null || string.IsNullOrWhiteSpace(emoteName))
                return "";

            if (!string.IsNullOrWhiteSpace(config.ImageOverrideUrl) &&
                string.Equals(emoteName.Trim(), config.DisplayEmote, StringComparison.OrdinalIgnoreCase))
                return config.ImageOverrideUrl;

            bool tryTwitch = string.IsNullOrWhiteSpace(platform) ||
                             string.Equals(platform, "Twitch", StringComparison.OrdinalIgnoreCase);
            if (lib != null && tryTwitch)
            {
                string url = lib.GetEmoteImageUrl(emoteName);
                if (!string.IsNullOrEmpty(url)) return url;
            }

            return BueStore.GetImage(config.TargetId, emoteName);
        }
    }
}
