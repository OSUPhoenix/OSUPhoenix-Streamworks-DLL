// =============================================================================
// OSWTools — Extensions/BUEModels.cs
//
// "Blow Up Emote" (module code: BUE) — data models.
//
// ── CHANGELOG ────────────────────────────────────────────────────────────────
// v2.3.0
//   ADDED   BueTargetConfig.RandomGrowth / GrowthMinPercent / GrowthMaxPercent —
//           every entry adds a random amount between min and max (unpredictable pops).
//   ADDED   BueTargetConfig.DeflatePerSecond — the balloon loses this many percentage
//           points every second (hundredths supported), so chat has to keep up.
//   ADDED   BueState.FillAsOfUtc — when FillPercent was last true, so the drain
//           can be calculated for the time that has passed since.
// v2.2.0
//   ADDED   BueTargetConfig.AllowedEmotes / BannedEmotes — which emotes chat may
//           pick for a round. Allowed empty = ANY real emote. Banned always wins.
//   ADDED   BueState.RoundEmote / RoundImageUrl — the emote of the round that is
//           running. With rounds on, THIS is what chat has to type; the emote in
//           Settings is only the default used when a Start has no emote.
//   CHANGED CancelRound / a pop clears the round emote.
// v2.1.0
//   ADDED   BueTargetConfig.StartHidden — rounds: the balloon is hidden and
//                                         inactive until a round is STARTED on
//                                         purpose (Start Round action), and
//                                         hides again after it pops.
//   ADDED   BueState.Active             — is a round running right now?
//   ADDED   BueContributionResult.Inactive — "no round running, message ignored"
// v2.0.0
//   ADDED   BueTargetConfig.EmoteControl   — who may change the emote
//                                            (StreamerOnly / ChatTriggered)
//   ADDED   BueTargetConfig.CountMode      — PerEmote or PerMessage growth
//   ADDED   BueTargetConfig.ShowProgress   — widget progress bar + percentage
//   ADDED   BueTargetConfig.ImageOverrideUrl — optional fixed picture URL
//   ADDED   BueTargetConfig.ConfigVersion  — so future versions can migrate
//   CHANGED Enums now save to JSON as NAMES ("Explosion") not numbers (1).
//           Old files with numbers still load fine.
//   FIXED   EnabledPlatforms / SpecificEmotes now REPLACE their defaults when
//           loaded from JSON. Before, Newtonsoft merged the file's list into
//           the built-in default list, so deleting "YouTube" from the file
//           did nothing. (See the [JsonProperty] notes below.)
// v1.0.1  ADDED DisplayEmote.
//
// LEARNING NOTE FOR YOU:
//   This file has ZERO logic in it on purpose. It only describes SHAPES of
//   data — what settings look like, what state looks like. Splitting "what
//   the data looks like" from "what we do with the data" (that's Engine.cs)
//   makes both halves much easier to read, test, and change later. This is
//   the same split you already use in AchievementSystem.cs: Achievement /
//   ProgState / ExclusiveClaim are shape-only classes; AchievementManager
//   is where the logic lives.
//
// WHERE THIS FILE LIVES:
//   OSWTools DLL project under Extensions/, next to EmoteExtensions.cs.
//   It doesn't depend on CPH, OBS, or Streamer.bot — just C# classes.
//
// WHERE THE DATA IS SAVED (see BUEStore.cs):
//   {Streamer.bot folder}/OSWData/BlowUpEmote/config_default.json
//   {Streamer.bot folder}/OSWData/BlowUpEmote/state_default.json
//   {Streamer.bot folder}/OSWData/BlowUpEmote/images_default.json
// =============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace OSWTools.BlowUpEmote
{
    // ─────────────────────────────────────────────────────────────────────
    // ENUM: Where does the "allowed emote list" for this target come from?
    //
    //   Channel      — only this channel's own subscriber/bit emotes (Twitch
    //                  only — pulled live from Lib.GetChannelEmotes()).
    //   Global       — only Twitch's global emote set (Kappa, PogChamp, etc).
    //   All          — Channel + Global combined (Twitch only).
    //   SpecificList — exactly the emote names listed in SpecificEmotes.
    //
    //   YouTube and Kick have no queryable emote API, so for those platforms
    //   SpecificEmotes is ALWAYS used regardless of Mode (see Engine.cs).
    //
    // LEARNING NOTE: [JsonConverter(typeof(StringEnumConverter))] tells
    // Newtonsoft to write/read the enum's NAME in JSON. Without it you would
    // see "Mode": 3 in the file, which is unreadable when you hand-edit.
    // ─────────────────────────────────────────────────────────────────────
    [JsonConverter(typeof(StringEnumConverter))]
    public enum EmoteSourceMode
    {
        Channel = 0,
        Global = 1,
        All = 2,
        SpecificList = 3
    }

    // What happens visually when the balloon hits 100%?
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PopBehavior
    {
        SimplePop = 0,
        Explosion = 1
    }

    // ─────────────────────────────────────────────────────────────────────
    // ENUM (NEW): Who is allowed to change WHICH emote the balloon is?
    //
    //   StreamerOnly  — locked. Only the broadcaster can change it (from the
    //                   settings window, or by running the Set Emote action
    //                   themselves). Anyone else who triggers it is ignored.
    //   ChatTriggered — viewers can change it. You attach ANY trigger you like
    //                   to the "Set Emote" action in Streamer.bot (channel point
    //                   reward, bits, sub, command, power-up...) and Streamer.bot
    //                   itself decides who may fire it. The viewer's text must
    //                   contain a real emote.
    // ─────────────────────────────────────────────────────────────────────
    [JsonConverter(typeof(StringEnumConverter))]
    public enum EmoteControlMode
    {
        StreamerOnly = 0,
        ChatTriggered = 1
    }

    // ─────────────────────────────────────────────────────────────────────
    // ENUM (NEW): How is growth counted for one chat message?
    //
    //   PerEmote   — every matching emote in the message counts.
    //                "KEKW KEKW KEKW" = 3 entries.
    //   PerMessage — a message counts ONCE if it has at least one matching
    //                emote, however many it contains. "KEKW KEKW KEKW" = 1.
    // ─────────────────────────────────────────────────────────────────────
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GrowthCountMode
    {
        PerEmote = 0,
        PerMessage = 1
    }

    // ─────────────────────────────────────────────────────────────────────
    // CONFIG: One of these describes one "balloon" — one growable target.
    //
    //   Most streamers only ever have ONE (TargetId = "default"). Giving every
    //   target an Id up front means a second balloon later needs no redesign.
    //
    //   Edited from the Settings window (BUE Settings action). You can also
    //   hand-edit config_default.json while Streamer.bot is running — changes
    //   are picked up automatically on the next chat message.
    // ─────────────────────────────────────────────────────────────────────
    public class BueTargetConfig
    {
        /// <summary>Lets future versions upgrade old files safely. Don't edit.</summary>
        public int ConfigVersion { get; set; } = 2;

        /// <summary>Stable key used in file names and widget messages. Keep it short, no spaces.</summary>
        public string TargetId { get; set; } = "default";

        /// <summary>Human-readable name, shown on the leaderboard / settings UI.</summary>
        public string DisplayName { get; set; } = "Blow Up Emote";

        // ── Which emote is it, and who may change it ─────────────────────

        /// <summary>
        /// Which emote's PICTURE the balloon shows. This is separate from "which
        /// emotes count" (Mode/SpecificEmotes) on purpose: you might let any
        /// channel emote inflate the balloon but always want it to look like one.
        ///   ""        = the balloon shows whichever matching emote was used last.
        ///   "KEKW"    = always show KEKW.
        /// </summary>
        public string DisplayEmote { get; set; } = "";

        /// <summary>
        /// Optional https:// picture link for DisplayEmote. When set, it wins over
        /// every automatic source. Use it for emotes no API knows about.
        /// </summary>
        public string ImageOverrideUrl { get; set; } = "";

        public EmoteControlMode EmoteControl { get; set; } = EmoteControlMode.StreamerOnly;

        // ── Which emotes may be PICKED for a round (rounds on) ───────────
        // These only filter what a Start / Set Emote may choose. The streamer is
        // trusted and is never blocked by them.

        /// <summary>Emotes a viewer may choose. EMPTY = any real emote.</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AllowedEmotes { get; set; } = new List<string>();

        /// <summary>Emotes a viewer may never choose. Beats the allowed list.</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> BannedEmotes { get; set; } = new List<string>();

        // ── Which emotes COUNT ───────────────────────────────────────────

        public EmoteSourceMode Mode { get; set; } = EmoteSourceMode.SpecificList;

        /// <summary>
        /// Emote names to match. Required for YouTube/Kick (always used).
        /// For Twitch these are ADDED on top of whatever Mode resolves to, so
        /// you can always include a BTTV/7TV name Twitch's API won't know.
        ///
        /// LEARNING NOTE — ObjectCreationHandling.Replace: by default Newtonsoft
        /// loads a JSON list INTO the list that already exists on a new object
        /// (adding to it). For a list with built-in defaults that means your
        /// file can never remove a default. "Replace" makes it build a fresh
        /// list from exactly what the file says.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> SpecificEmotes { get; set; } = new List<string>();

        /// <summary>Which platforms feed this target. Values: "Twitch", "YouTube", "Kick".</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public HashSet<string> EnabledPlatforms { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Twitch", "YouTube", "Kick" };

        // ── How growth works ─────────────────────────────────────────────

        public GrowthCountMode CountMode { get; set; } = GrowthCountMode.PerEmote;

        /// <summary>
        /// How many percentage points ONE valid entry adds (an "entry" is one
        /// emote in PerEmote mode, or one message in PerMessage mode).
        /// e.g. 2.5 means about 40 entries to pop.
        /// </summary>
        public double GrowthPerUse { get; set; } = 2.0;

        /// <summary>
        /// false = every entry adds exactly GrowthPerUse.
        /// true  = every entry adds a RANDOM amount between GrowthMinPercent and
        ///         GrowthMaxPercent (GrowthPerUse is ignored). A message worth
        ///         3 entries rolls 3 times. Makes the pop point unpredictable.
        /// </summary>
        public bool RandomGrowth { get; set; } = false;

        /// <summary>Random growth: smallest amount one entry can add (percentage points).</summary>
        public double GrowthMinPercent { get; set; } = 1.0;

        /// <summary>Random growth: largest amount one entry can add (percentage points).</summary>
        public double GrowthMaxPercent { get; set; } = 5.0;

        /// <summary>
        /// Percentage points the balloon loses EVERY SECOND while it is on screen.
        /// 0 = no deflation. Hundredths work (0.25 = a quarter point per second).
        /// Chat has to out-run it: at 0.5/s a full balloon empties in 200 seconds.
        /// </summary>
        public double DeflatePerSecond { get; set; } = 0.0;

        /// <summary>
        /// PerEmote mode only: the most emotes counted from ONE chat message.
        /// Stops one person typing "KEKW KEKW ... x50" and popping it alone.
        /// 0 = no limit.
        /// </summary>
        public int MaxMatchesPerMessage { get; set; } = 5;

        /// <summary>Seconds a single user must wait between contributions. 0 = no cooldown.</summary>
        public int PerUserCooldownSeconds { get; set; } = 2;

        // ── Pop and display ──────────────────────────────────────────────

        public PopBehavior PopBehavior { get; set; } = PopBehavior.SimplePop;

        /// <summary>If true, FillPercent snaps back to 0 and the round leaderboard clears after a pop.</summary>
        public bool ResetOnPop { get; set; } = true;

        /// <summary>Show the progress bar + percentage under the balloon in the widget.</summary>
        public bool ShowProgress { get; set; } = true;

        // ── Rounds (the "game" loop) ─────────────────────────────────────

        /// <summary>
        /// true  = the balloon is hidden and INACTIVE (chat emotes do nothing)
        ///         until a round is started on purpose with the "Start Round"
        ///         action. After the pop it hides again and stays off until the
        ///         next start. Nothing restarts it automatically.
        /// false = the balloon is always on screen (the original behavior).
        /// </summary>
        public bool StartHidden { get; set; } = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // STATE: The live, persisted progress for one target. Saved to
    // state_<target>.json after every contribution.
    //
    //   Two separate leaderboards on purpose:
    //     RoundContributors   — resets every pop. "Who's helping THIS balloon."
    //     AllTimeContributors — never resets.
    // ─────────────────────────────────────────────────────────────────────
    public class BueState
    {
        public string TargetId { get; set; } = "default";

        /// <summary>0-100. Clamped in Engine.cs — never trust a stored value blindly.</summary>
        public double FillPercent { get; set; } = 0;

        public Dictionary<string, double> RoundContributors { get; set; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, double> AllTimeContributors { get; set; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-user cooldown tracking — when did each user last successfully contribute?</summary>
        public Dictionary<string, DateTime> LastContributionAtUtc { get; set; } =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public int TotalPops { get; set; } = 0;
        public string LastPoppedBy { get; set; } = "";
        public DateTime? LastPoppedAtUtc { get; set; } = null;
        public string LastPopPlatform { get; set; } = "";

        /// <summary>
        /// Is a round running? Only meaningful when config.StartHidden is true.
        /// Missing from older state files, so it loads as false; BueStore.FixState
        /// turns it on if the file already holds a half-filled balloon.
        /// </summary>
        public bool Active { get; set; } = false;

        /// <summary>The emote of the round in progress (what chat must type). "" between rounds.</summary>
        public string RoundEmote { get; set; } = "";

        /// <summary>Optional https picture link for RoundEmote (only the streamer may supply one).</summary>
        public string RoundImageUrl { get; set; } = "";

        /// <summary>When FillPercent was last known to be exact (UTC). Used to work out how much has drained since.</summary>
        public DateTime? FillAsOfUtc { get; set; } = null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // RESULT: What ProcessContribution() hands back after one chat message.
    // Not persisted — just "here's what just happened".
    // ─────────────────────────────────────────────────────────────────────
    public class BueContributionResult
    {
        public int MatchCount { get; set; }
        public double GrowthApplied { get; set; }
        public double NewFillPercent { get; set; }
        public bool Popped { get; set; }
        public string PopperUser { get; set; } = "";
        /// <summary>True if the user WAS on cooldown, so this message contributed nothing.</summary>
        public bool CooldownBlocked { get; set; }
        /// <summary>True if no round is running (balloon hidden), so the message was ignored.</summary>
        public bool Inactive { get; set; }
    }
}
