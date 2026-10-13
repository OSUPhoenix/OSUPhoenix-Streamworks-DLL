////TwitchAdsExtensions.cs//
// =============================================================================
// NEW FILE — drop into the OSWTools project at:  Extensions\TwitchAdsExtensions.cs
// The .csproj already compiles  Extensions\**\*.cs  so no project edit needed.
// =============================================================================
using System;
using System.Globalization;
using OSWTools.Utilities;

namespace OSWTools
{
    // =========================================================================
    //  TwitchAdBreakState — the shared, persisted picture of "what's going on
    //  with ads right now". Stored as JSON in ONE persisted global
    //  (osw_TwitchAds_State) so EVERY OSW tool reads the same truth:
    //  the Info Panel today, and later anything else that wants to react to
    //  ads (pause Cider music, have Felix say "ads are rolling", mute a
    //  noisy source, etc.) without re-implementing any of this.
    //
    //  Times are stored as ISO-8601 UTC strings ("o" format), not DateTime,
    //  because the whole OSW ecosystem already learned the hard way that
    //  string-based JSON round-trips reliably while richer types can not.
    // =========================================================================
    public class TwitchAdBreakState
    {
        /// <summary>New GUID every time an Ad Run fires. Lets overlays and
        /// other tools tell "the same ad break" apart from "a new one".</summary>
        public string Nonce { get; set; } = "";

        /// <summary>When the current/last ad break started (UTC, ISO-8601).</summary>
        public string StartedUtc { get; set; } = "";

        /// <summary>When the current/last ad break ends (UTC, ISO-8601).
        /// Streamer.bot has NO "ad ended" trigger — this is calculated as
        /// StartedUtc + adLength, which is what makes an end alert possible.</summary>
        public string EndsUtc { get; set; } = "";

        /// <summary>Length of the current/last ad break in seconds.</summary>
        public int LengthSeconds { get; set; }

        /// <summary>True if Twitch's Ad Manager scheduled it (vs. run manually).</summary>
        public bool Scheduled { get; set; }

        /// <summary>Next scheduled ad time from the most recent Upcoming Ad trigger.</summary>
        public string NextAdAtUtc { get; set; } = "";

        /// <summary>Length (seconds) of the next scheduled ad, from Upcoming Ad.</summary>
        public int NextAdLengthSeconds { get; set; }
    }

    /// <summary>
    /// What ReadTwitchUpcomingAd() hands back to the calling tool.
    /// </summary>
    public class TwitchUpcomingAdInfo
    {
        /// <summary>Minutes until the ad (Streamer.bot sends 5, 4, 3, 2, 1).</summary>
        public int MinutesUntil { get; set; }

        /// <summary>Length of the upcoming ad break in seconds.</summary>
        public int AdLengthSeconds { get; set; }

        /// <summary>Exact UTC start time of the upcoming ad, if it parsed.</summary>
        public DateTime? NextAdAtUtc { get; set; }

        /// <summary>
        /// True exactly ONCE per upcoming ad, per tool — the first time this
        /// trigger fires at or below the tool's chosen warning threshold.
        /// Streamer.bot fires Upcoming Ad every minute from 5 minutes out,
        /// so without this every tool would warn five times per ad.
        /// </summary>
        public bool ShouldWarn { get; set; }

        /// <summary>
        /// NEW — plain-English reason for ShouldWarn, e.g. "warned",
        /// "above threshold (5 > 1)", "already warned for this ad". Exists so
        /// callers can LOG why nothing happened instead of failing silently.
        /// </summary>
        public string Decision { get; set; } = "";
    }

    /// <summary>
    /// Twitch ad-break helpers.
    ///
    /// WIRING (per tool): add the Twitch → Ads → "Upcoming Ad" and "Ad Run"
    /// triggers to an action, then in that action's C#:
    ///
    ///   switch (CPH.GetEventType())
    ///   {
    ///       case EventType.TwitchUpcomingAd:
    ///           var up = lib.ReadTwitchUpcomingAd(warnAtOrBelowMinutes: 1);
    ///           if (up != null &amp;&amp; up.ShouldWarn) { /* warn chat / overlay */ }
    ///           break;
    ///       case EventType.TwitchAdRun:
    ///           var ad = lib.BeginTwitchAdRun();
    ///           /* ad.EndsUtc tells you exactly when it finishes */
    ///           break;
    ///   }
    ///
    /// And on any repeating Timer (there is no native "ad ended" trigger):
    ///
    ///   var ended = lib.ConsumeTwitchAdEnded();
    ///   if (ended != null) { /* ads just finished — resume music, etc. */ }
    ///
    /// Anywhere, any time:
    ///   bool running  = lib.IsTwitchAdRunning();
    ///   int  secsLeft = lib.GetTwitchAdSecondsRemaining();
    /// </summary>
    public partial class OSWLib
    {
        // One shared, persisted key for the whole ecosystem (see class comment).
        private const string TwitchAdStateGlobal = "osw_TwitchAds_State";

        // Per-tool, session-only "have I already handled this?" markers. These
        // are per tool ON PURPOSE: if the Info Panel "consumes" an ad-ended
        // event, a music-pause tool must still get to see it too.
        private string TwitchAdWarnedKey  { get { return "osw_TwitchAds_Warned_"  + SafeKeyPart(_ToolName); } }
        private string TwitchAdEndSeenKey { get { return "osw_TwitchAds_EndSeen_" + SafeKeyPart(_ToolName); } }

        // An ended ad older than this is ignored by ConsumeTwitchAdEnded().
        // Without it, restarting Streamer.bot (which wipes session globals)
        // would make every tool announce "ads ended!" for an ad from hours ago.
        private static readonly TimeSpan TwitchAdEndGraceWindow = TimeSpan.FromMinutes(3);

        // ── State access ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns the shared ad state. Never null — returns an empty state
        /// if nothing has been recorded yet or the stored JSON is unreadable.
        /// </summary>
        public TwitchAdBreakState GetTwitchAdState()
        {
            string json = GetGlobal(TwitchAdStateGlobal, "");
            return JsonHelper.SafeDeserialize(json, new TwitchAdBreakState());
        }

        private void SaveTwitchAdState(TwitchAdBreakState state)
        {
            SetGlobal(TwitchAdStateGlobal, JsonHelper.Serialize(state ?? new TwitchAdBreakState()), true);
        }

        /// <summary>Wipes the shared ad state (e.g. when the stream goes offline).</summary>
        public void ClearTwitchAdState()
        {
            UnsetGlobal(TwitchAdStateGlobal);
        }

        // ── Upcoming Ad ───────────────────────────────────────────────────────

        /// <summary>
        /// Call from the Twitch "Upcoming Ad" trigger. Reads its args
        /// (minutes, adLength, nextAdAt), records them in the shared state,
        /// and decides whether THIS tool should warn right now.
        ///
        /// warnAtOrBelowMinutes: 1–5. Streamer.bot fires this trigger at the
        /// 5, 4, 3, 2 and 1 minute marks; ShouldWarn becomes true the first
        /// time minutes &lt;= this value for a given ad, and never again for
        /// that same ad. A snoozed ad gets a NEW nextAdAt, so it correctly
        /// earns a fresh warning.
        ///
        /// Returns null if the trigger's args couldn't be read.
        /// </summary>
        public TwitchUpcomingAdInfo ReadTwitchUpcomingAd(int warnAtOrBelowMinutes)
        {
            try
            {
                int minutes;
                if (!_CPH.TryGetArg("minutes", out minutes))
                {
                    LogWarn("ReadTwitchUpcomingAd: no 'minutes' arg — is this running from the Upcoming Ad trigger?");
                    return null;
                }

                int adLength;
                if (!_CPH.TryGetArg("adLength", out adLength)) adLength = 0;

                // CHANGED — nextAdAt is now sanity-checked against "minutes"
                // before anything trusts it. See ValidateNextAdAt below.
                DateTime? nextAt = ValidateNextAdAt(ReadUtcDateArg("nextAdAt"), minutes);

                // "Which ad is this?" key used for once-per-ad de-duplication.
                // Prefer Twitch's exact schedule time; if it didn't parse, fall
                // back to "now + minutes" rounded to the nearest 5-minute slot,
                // which stays stable across the 5 triggers for the same ad.
                string adKey;
                if (nextAt.HasValue)
                    adKey = nextAt.Value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                else
                {
                    DateTime approx = DateTime.UtcNow.AddMinutes(minutes);
                    long slot = approx.Ticks / TimeSpan.FromMinutes(5).Ticks;
                    adKey = "approx-" + slot.ToString(CultureInfo.InvariantCulture);
                }

                // Update the shared state (everyone benefits from knowing this).
                var state = GetTwitchAdState();
                state.NextAdAtUtc = nextAt.HasValue ? nextAt.Value.ToString("o") : "";
                state.NextAdLengthSeconds = adLength;
                SaveTwitchAdState(state);

                // Per-tool once-only warning decision.
                int threshold = Math.Max(1, Math.Min(5, warnAtOrBelowMinutes));
                bool shouldWarn = false;
                string decision;
                if (minutes > threshold)
                {
                    decision = "above threshold (" + minutes + " > " + threshold + ")";
                }
                else
                {
                    string lastWarned = GetGlobalSession(TwitchAdWarnedKey, "");
                    if (lastWarned != adKey)
                    {
                        shouldWarn = true;
                        SetGlobal(TwitchAdWarnedKey, adKey, false);
                        decision = "warned (ad key " + adKey + ")";
                    }
                    else
                    {
                        decision = "already warned for this ad (key " + adKey + ")";
                    }
                }

                return new TwitchUpcomingAdInfo
                {
                    MinutesUntil = minutes,
                    AdLengthSeconds = adLength,
                    NextAdAtUtc = nextAt,
                    ShouldWarn = shouldWarn,
                    Decision = decision
                };
            }
            catch (Exception ex)
            {
                // CHANGED — full exception (type + stack), at ERROR level, so it
                // can't hide in a log that filters warnings.
                LogError("ReadTwitchUpcomingAd failed: " + ex);
                return null;
            }
        }

        // ── Ad Run ────────────────────────────────────────────────────────────

        /// <summary>
        /// Call from the Twitch "Ad Run" trigger (fires at the START of the
        /// break). Records start/end times and a fresh Nonce in the shared
        /// state and returns it. Returns null if no usable length was found.
        ///
        /// Prefers adLengthMs (more precise) and falls back to adLength.
        /// </summary>
        public TwitchAdBreakState BeginTwitchAdRun()
        {
            try
            {
                int lengthMs;
                if (!_CPH.TryGetArg("adLengthMs", out lengthMs) || lengthMs <= 0)
                {
                    int lengthSec;
                    lengthMs = _CPH.TryGetArg("adLength", out lengthSec) ? lengthSec * 1000 : 0;
                }
                if (lengthMs <= 0) return null;

                bool scheduled;
                if (!_CPH.TryGetArg("adScheduled", out scheduled)) scheduled = false;

                DateTime now = DateTime.UtcNow;
                var state = GetTwitchAdState(); // keep NextAdAt info already known
                state.Nonce = Guid.NewGuid().ToString("N");
                state.StartedUtc = now.ToString("o");
                state.EndsUtc = now.AddMilliseconds(lengthMs).ToString("o");
                state.LengthSeconds = (int)Math.Round(lengthMs / 1000.0);
                state.Scheduled = scheduled;
                // The upcoming ad has now arrived — it's no longer "next".
                state.NextAdAtUtc = "";
                state.NextAdLengthSeconds = 0;
                SaveTwitchAdState(state);
                return state;
            }
            catch (Exception ex)
            {
                LogWarn("BeginTwitchAdRun failed: " + ex.Message);
                return null;
            }
        }

        // ── Queries ───────────────────────────────────────────────────────────

        /// <summary>True while an ad break is currently playing.</summary>
        public bool IsTwitchAdRunning()
        {
            return IsTwitchAdRunning(GetTwitchAdState());
        }

        /// <summary>Overload for when you already hold a state object (saves a read).</summary>
        public bool IsTwitchAdRunning(TwitchAdBreakState state)
        {
            DateTime? ends = ParseUtc(state == null ? null : state.EndsUtc);
            return ends.HasValue && DateTime.UtcNow < ends.Value;
        }

        /// <summary>Whole seconds left in the current ad break (0 if none running).</summary>
        public int GetTwitchAdSecondsRemaining()
        {
            DateTime? ends = ParseUtc(GetTwitchAdState().EndsUtc);
            if (!ends.HasValue) return 0;
            double left = (ends.Value - DateTime.UtcNow).TotalSeconds;
            return left > 0 ? (int)Math.Ceiling(left) : 0;
        }

        /// <summary>
        /// Edge detector for "the ad break just finished". Call it on any
        /// repeating Timer. Returns the finished break's state exactly ONCE
        /// per break, per tool; returns null every other time.
        ///
        /// Timing precision equals your Timer interval (a 15s timer reports
        /// the end up to 15s late). Overlays that need a to-the-second end
        /// should compute it themselves from EndsUtc instead.
        /// </summary>
        public TwitchAdBreakState ConsumeTwitchAdEnded()
        {
            try
            {
                var state = GetTwitchAdState();
                if (string.IsNullOrEmpty(state.Nonce)) return null;

                DateTime? ends = ParseUtc(state.EndsUtc);
                if (!ends.HasValue) return null;

                DateTime now = DateTime.UtcNow;
                if (now < ends.Value) return null;                              // still running
                if (now - ends.Value > TwitchAdEndGraceWindow) return null;     // stale (see grace comment)

                if (GetGlobalSession(TwitchAdEndSeenKey, "") == state.Nonce) return null; // already reported
                SetGlobal(TwitchAdEndSeenKey, state.Nonce, false);
                return state;
            }
            catch (Exception ex)
            {
                LogWarn("ConsumeTwitchAdEnded failed: " + ex.Message);
                return null;
            }
        }

        // ── Small shared helpers ──────────────────────────────────────────────

        /// <summary>
        /// Parses an ISO-8601 string produced by DateTime.ToString("o") back
        /// into a UTC DateTime. Returns null for empty/invalid input.
        /// </summary>
        public static DateTime? ParseUtc(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return null;
            DateTime dt;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                return dt;
            return null;
        }

        /// <summary>
        /// Reads a DateTime-typed trigger arg as UTC. Handles both a real
        /// DateTime value and a string, since args can surface either way.
        ///
        /// CONFIDENCE NOTE: Streamer.bot's docs describe nextAdAt as a UTC
        /// DateTime (example "2024-03-03 01:10:20"). If it ever arrives with
        /// Kind = Unspecified we treat it as UTC, per those docs. If warnings
        /// ever show the wrong time, check the real value via Inspect
        /// Variables on a live Upcoming Ad and adjust here.
        /// </summary>
        private DateTime? ReadUtcDateArg(string argName)
        {
            object raw;
            if (!_CPH.TryGetArg(argName, out raw) || raw == null) return null;

            if (raw is DateTime)
            {
                var dt = (DateTime)raw;
                if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            return ParseUtc(raw.ToString());
        }

        /// <summary>
        /// NEW — cross-checks nextAdAt against the trigger's own "minutes"
        /// arg. They describe the same moment two ways, so they should agree
        /// to within a minute or so. Returns the trustworthy UTC time, or
        /// null if neither reading makes sense (callers then fall back to
        /// "now + minutes", which is always right to within a minute).
        ///
        /// Catches two real problems:
        ///   1. Streamer.bot's "Test Trigger" fills nextAdAt with the docs'
        ///      example value (2024-03-03 01:10:20) — years in the past.
        ///   2. If nextAdAt actually arrives as LOCAL time, reading it as UTC
        ///      puts it hours off (5–6 hours for US Central). It's
        ///      re-tried as local time before being given up on, so this
        ///      also tells us which one Streamer.bot really sends.
        /// </summary>
        private DateTime? ValidateNextAdAt(DateTime? candidate, int minutes)
        {
            if (!candidate.HasValue) return null;
            DateTime expected = DateTime.UtcNow.AddMinutes(minutes);
            TimeSpan tolerance = TimeSpan.FromMinutes(2);

            if ((candidate.Value - expected).Duration() <= tolerance)
                return candidate.Value; // UTC, as the docs say

            DateTime asLocal = DateTime.SpecifyKind(candidate.Value, DateTimeKind.Local).ToUniversalTime();
            if ((asLocal - expected).Duration() <= tolerance)
            {
                LogDebug("nextAdAt looked like LOCAL time, not UTC — converted.");
                return asLocal;
            }

            LogDebug("nextAdAt ignored (" + candidate.Value.ToString("o") +
                     ") — doesn't match 'minutes'. Normal when using Test Trigger.");
            return null;
        }

        // Global-var keys should stay simple — strip anything odd from a tool name.
        private static string SafeKeyPart(string s)
        {
            if (string.IsNullOrEmpty(s)) return "tool";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars);
        }
    }
}
