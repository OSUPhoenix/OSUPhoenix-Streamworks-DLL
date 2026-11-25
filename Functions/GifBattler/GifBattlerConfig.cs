// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerConfig.cs     DLL v1.3.0
//
//  The ONE definition of the GIF Battler's settings.
//
//  WHY THIS FILE EXISTS:
//    Before the move, every Streamer.bot action carried its own copy
//    of a GifDisplayConfig class — six copies — and they had drifted
//    apart (AutoBattleWindowSeconds was 30 in some and 10 in another,
//    GlobalCooldownSeconds existed in some and not others). Whichever
//    action ran first and wrote config.json "won". Now there's exactly
//    one class, so every part of the engine agrees on the defaults.
//
//  STORAGE:
//    Saved by OSWData at  .../OSWData/GifDisplay/config.json
//    The folder name stays "GifDisplay" on purpose so existing users
//    keep their current settings after upgrading.
//
//  ADDING A SETTING LATER:
//    Just add a property with a default value. Newtonsoft fills in
//    defaults for any property missing from an older config.json,
//    so old files upgrade themselves without a migration. Then add a
//    clamp for it in Sanitize() and a control in the Settings window
//    (GifBattlerSettingsForm.cs).
//
//  STEP 3 (v1.3.0): the Display / Look settings below are edited from
//  the Settings window instead of by hand in config.json.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using OSWTools.Data;

namespace OSWTools.Functions.GifBattler
{
    public class GifBattlerConfig
    {
        // ═════════════════════════════════════════════════════════════
        //  DISPLAY  (★ NEW in step 3 — edited from the Settings window)
        //
        //  All of these travel to the overlay inside every gif_show
        //  message (see GifBattlerOverlay.DisplayBlock), so a change
        //  applies to the very next GIF — nothing is cached overlay-side.
        // ═════════════════════════════════════════════════════════════

        // ── Screen area ──────────────────────────────────────────────
        /// <summary>
        /// false (default) = "Auto": lay GIFs out across whatever size your
        /// OBS browser source really is.
        /// true = "Custom": the overlay pretends the screen is
        /// StageWidth x StageHeight, lays everything out in that space, then
        /// scales the result to fit the real browser source. Smaller or
        /// bigger than 1920x1080 both work, and nothing lands off-screen.
        /// </summary>
        public bool UseCustomStageSize { get; set; } = false;
        public int  StageWidth         { get; set; } = 1920;
        public int  StageHeight        { get; set; } = 1080;

        // ── Size ─────────────────────────────────────────────────────
        /// <summary>
        /// The biggest box a GIF may fill. A GIF is scaled so it fits INSIDE
        /// this box with its proportions kept: a tall skinny GIF hits the
        /// height limit first, a wide one hits the width limit first. It is
        /// never stretched to fill the box.
        /// </summary>
        public int MaxGifWidth  { get; set; } = 420;
        public int MaxGifHeight { get; set; } = 420;

        /// <summary>
        /// true  = a GIF smaller than the box is enlarged to fill it (the
        ///         old behaviour: every GIF was forced to one width).
        /// false = small GIFs stay at their natural size; the box only
        ///         ever shrinks big ones.
        /// </summary>
        public bool EnlargeSmallGifs { get; set; } = true;

        /// <summary>
        /// true = the box above is scaled per viewer role (mods biggest).
        /// The defaults 100 / 70 / 50 match the old fixed widths
        /// (420 / 300 / 220). false = every GIF uses the full box.
        /// </summary>
        public bool SizeByRole        { get; set; } = true;
        public int  ScaleModPercent    { get; set; } = 100;  // broadcaster + mods
        public int  ScaleSubPercent    { get; set; } = 70;   // subscribers
        public int  ScaleViewerPercent { get; set; } = 50;   // everyone else

        // ── Placement ────────────────────────────────────────────────
        /// <summary>
        /// Where GIFs appear. One of:
        ///   "random"        anywhere inside the screen area
        ///   "random-corner" a random one of the four corners
        ///   "top-left" "top-center" "top-right"
        ///   "middle-left" "middle-center" "middle-right"
        ///   "bottom-left" "bottom-center" "bottom-right"
        ///   "fullscreen"    large and centred (~85% of the screen)
        /// The property keeps its old name (DisplayMode) so existing
        /// config.json files load unchanged. Old values are translated by
        /// NormalizeDisplayMode: "fixed" -> "middle-center",
        /// "corners" -> "random-corner".
        /// </summary>
        public string DisplayMode { get; set; } = "random";

        /// <summary>
        /// Random placement keeps at least this many pixels between a GIF
        /// (including its glow and name label) and every screen edge.
        /// </summary>
        public int EdgePadding { get; set; } = 40;

        /// <summary>
        /// Fixed placements only. On an EDGE axis this is the distance in
        /// pixels from that edge, measured inward (left/right for X,
        /// top/bottom for Y). On a CENTRED axis it is a nudge: positive
        /// moves right (X) or down (Y), negative moves left or up.
        /// </summary>
        public int OffsetX { get; set; } = 40;
        public int OffsetY { get; set; } = 40;

        // ── Time on screen ───────────────────────────────────────────
        /// <summary>How long a single GIF stays on screen.</summary>
        public int DurationSeconds { get; set; } = 8;

        /// <summary>
        /// true = ignore DurationSeconds. The GIF stays until you clear it
        /// (your "!cleargif" action, or the overlay's X button). The next GIF
        /// simply replaces it — the overlay shows one GIF at a time.
        /// </summary>
        public bool StayUntilCleared { get; set; } = false;

        // ── Look ─────────────────────────────────────────────────────
        /// <summary>
        /// "role"   gold for mods, purple for subs, none for everyone else
        ///          (the original look)
        /// "off"    no glow or ring
        /// "color"  GlowColor on every GIF
        /// "random" a fresh vivid colour for each GIF
        /// </summary>
        public string GlowMode { get; set; } = "role";

        /// <summary>#RRGGBB — used when GlowMode is "color".</summary>
        public string GlowColor { get; set; } = "#DC2828";

        /// <summary>Soft glow radius in pixels (0 = ring only).</summary>
        public int GlowSize { get; set; } = 24;

        /// <summary>Solid ring thickness in pixels (0 = glow only).</summary>
        public int BorderWidth { get; set; } = 3;

        /// <summary>Rounded corners on the GIF, in pixels.</summary>
        public int CornerRadius { get; set; } = 8;

        /// <summary>10-100. Lower = more see-through.</summary>
        public int OpacityPercent { get; set; } = 100;

        /// <summary>Show the "@username" under the GIF.</summary>
        public bool ShowUsername { get; set; } = true;

        /// <summary>
        /// How the GIF arrives: "auto" (bounce, or fade for centred /
        /// zoom for fullscreen — the original behaviour), "bounce",
        /// "fade" or "zoom".
        /// </summary>
        public string Entrance { get; set; } = "auto";

        // ── Cooldowns ────────────────────────────────────────────────
        /// <summary>
        /// Seconds between ANY two GIFs from anyone. 0 = off.
        /// ★ NEW: this field existed in some old copies but was never
        ///   actually checked. It is now enforced (default stays 0 = off).
        /// </summary>
        public int GlobalCooldownSeconds { get; set; } = 0;

        /// <summary>
        /// Seconds before the same viewer can post another GIF.
        /// ★ STEP 3: default lowered 10 -> 2. It is only a spam / double-fire
        ///   guard now; for real cooldowns use Streamer.bot's own cooldown
        ///   settings on the Router A / Router B actions.
        ///   (Existing config.json files keep whatever number they saved.)
        /// </summary>
        public int UserCooldownSeconds { get; set; } = 2;

        // ── Giphy search: the optional !gif command and channel points reward ──
        //  Both run the same engine method (GiphySearch). They are optional:
        //  to turn one off, just disable its Streamer.bot action.

        /// <summary>
        /// Your own free Giphy API key (developers.giphy.com). Empty = search
        /// is not set up, and GiphySearch politely says so. Stays on THIS
        /// PC: it is never sent to the overlay and never written to the log.
        /// </summary>
        public string GiphyApiKey { get; set; } = "";

        /// <summary>
        /// Giphy's content rating filter: "g", "pg", "pg-13" or "r".
        /// A search never returns anything stricter than this allows.
        /// </summary>
        public string GiphyRating { get; set; } = "pg";

        /// <summary>
        /// Pick randomly from the top N matches (1 = always the best match).
        /// More than 1 means redeeming the same words twice can give a
        /// different GIF.
        /// </summary>
        public int GiphyPickFromTop { get; set; } = 3;

        /// <summary>
        /// Words or phrases that cannot be searched for. Checked before
        /// Giphy is even asked. Case-insensitive, whole words.
        /// </summary>
        public List<string> SearchBlockedWords { get; set; } = new List<string>();

        /// <summary>
        /// When on, a window opens on this PC showing the GIF with Approve /
        /// Deny buttons, and nothing reaches the stream until you choose.
        /// </summary>
        public bool ApprovalEnabled { get; set; } = false;

        /// <summary>Seconds the approval window waits before it counts as a Deny.</summary>
        public int ApprovalTimeoutSeconds { get; set; } = 45;

        /// <summary>
        /// A channel points redemption that ends with no GIF on screen (no
        /// match, denied, on cooldown...) is cancelled, which refunds the
        /// points. Only works for rewards created inside Streamer.bot.
        /// </summary>
        public bool RefundFailedRewards { get; set; } = true;

        // ── Battles ──────────────────────────────────────────────────
        public bool AutoBattleEnabled { get; set; } = true;

        /// <summary>
        /// How long after Challenger A posts that Challenger B can post to
        /// start a battle. Your "GIF - Battle Window Reset" timer interval
        /// should match this number.
        /// </summary>
        public int AutoBattleWindowSeconds { get; set; } = 30;

        /// <summary>Length of the Twitch poll.</summary>
        public int BattleVotingSeconds { get; set; } = 60;

        /// <summary>
        /// ★ NEW (was hard-coded 300 inside the script).
        /// How long a challenged viewer has to answer. Your
        /// "GIF - Challenge Expiry" timer interval should match this.
        /// </summary>
        public int ChallengeWindowSeconds { get; set; } = 300;

        // ── Who can play ─────────────────────────────────────────────
        /// <summary>
        /// ★ NEW. When true, only subs / VIPs / mods / broadcaster can post.
        /// Old behaviour was inconsistent: Router A let everyone in but
        /// Router B blocked non-subs, so a non-sub could START a battle
        /// window but never JOIN one. Now it's one switch for both.
        /// Default false = everyone can play (matches Router A + Challenge).
        /// </summary>
        public bool RestrictToSubsAndVips { get; set; } = false;

        /// <summary>
        /// ★ NEW. When true, accounts in Bot Eliminator are ignored on GIF
        /// posts too. (Challenges ALWAYS skip bots, same as before.)
        /// Default false matches the old commented-out router code.
        /// </summary>
        public bool IgnoreBotsOnGifPosts { get; set; } = false;

        // ── Twitch native chat GIFs (Tier 2 / Tier 3 GIF Keyboard) ───
        /// <summary>
        /// ★ NEW (step 2). When true, GIFs sent with Twitch's built-in
        /// GIF Keyboard appear on screen just like pasted Giphy links.
        /// Works on any Streamer.bot version through the overlay's
        /// WebSocket bridge; 1.1.0+ also passes %gif.0.url% to actions.
        /// </summary>
        public bool TwitchChatGifsEnabled { get; set; } = true;

        /// <summary>
        /// ★ NEW (step 2). When true, a native chat GIF counts exactly like
        /// a pasted link: it can open a battle window, join one, or answer
        /// a challenge. When false, native GIFs only pop up on screen and
        /// never touch battle state.
        /// </summary>
        public bool TwitchChatGifsCanBattle { get; set; } = true;

        /// <summary>
        /// ★ CHANGED (step 2b). Hosts a native Twitch GIF URL may come from.
        /// Native GIFs can now arrive through the overlay's WebSocket bridge
        /// (a DoAction request), and anything on your PC that can reach the
        /// WebSocket can send a DoAction — so the URL is checked against
        /// this short list instead of being trusted blindly. "giphy.com"
        /// also covers media0–media4.giphy.com etc. (subdomain match).
        /// If Twitch ever serves GIFs from another host, add it here.
        /// </summary>
        public List<string> TwitchGifAllowedDomains { get; set; } = new List<string>
        {
            "giphy.com"
        };

        // ── URL filter ───────────────────────────────────────────────
        //  Applies to PASTED links only. Native Twitch GIFs use the
        //  TwitchGifAllowedDomains list above.
        public List<string> AllowedDomains { get; set; } = new List<string>
        {
            "giphy.com",
            "i.giphy.com",
            "media.giphy.com"
        };

        // ── Allowed values (shared by Sanitize and the Settings window) ──
        internal static class Limits
        {
            internal const int StageMin   = 320,  StageMax   = 7680;
            internal const int MaxGifMin  = 50,   MaxGifMax  = 4000;
            internal const int PercentMin = 10,   PercentMax = 200;
            internal const int PadMin     = 0,    PadMax     = 500;
            internal const int OffsetMin  = -2000, OffsetMax = 2000;
            internal const int SecondsMin = 1,    SecondsMax = 3600;
            internal const int GlowMax    = 120;
            internal const int BorderMax  = 20;
            internal const int RadiusMax  = 100;
            internal const int OpacityMin = 10,   OpacityMax = 100;
            internal const int PickMin    = 1,    PickMax    = 10;
            internal const int ApprovalMin = 10,  ApprovalMax = 300;
        }

        internal static readonly string[] PlacementModes =
        {
            "random", "random-corner",
            "top-left", "top-center", "top-right",
            "middle-left", "middle-center", "middle-right",
            "bottom-left", "bottom-center", "bottom-right",
            "fullscreen"
        };
        internal static readonly string[] GlowModes = { "role", "off", "color", "random" };
        internal static readonly string[] Entrances = { "auto", "bounce", "fade", "zoom" };
        internal static readonly string[] Ratings   = { "g", "pg", "pg-13", "r" };

        // ── Load / Save ──────────────────────────────────────────────
        internal const string DataFolder = "GifDisplay";
        internal const string FileName   = "config";

        /// <summary>
        /// Loads config.json, or creates it with defaults on first run.
        /// Never returns null, and every number is forced into a safe range
        /// (a hand-edited "MaxGifWidth": 0 can't break the overlay).
        /// </summary>
        public static GifBattlerConfig Load()
        {
            var cfg = OSWData.Load<GifBattlerConfig>(DataFolder, FileName);
            if (cfg != null)
            {
                // A hand-edited file can null the list out — guard it.
                if (cfg.AllowedDomains == null)
                    cfg.AllowedDomains = new List<string>();
                if (cfg.TwitchGifAllowedDomains == null)
                    cfg.TwitchGifAllowedDomains = new List<string>();
                cfg.Sanitize();
                return cfg;
            }

            cfg = new GifBattlerConfig();
            OSWData.Save(DataFolder, FileName, cfg);
            return cfg;
        }

        public void Save()
        {
            Sanitize();
            OSWData.Save(DataFolder, FileName, this);
        }

        // ── Safety net ───────────────────────────────────────────────
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>
        /// Forces every display value into a range the overlay can handle
        /// and translates old DisplayMode names. Safe to call any time.
        /// </summary>
        public void Sanitize()
        {
            StageWidth   = Clamp(StageWidth,   Limits.StageMin,  Limits.StageMax);
            StageHeight  = Clamp(StageHeight,  Limits.StageMin,  Limits.StageMax);
            MaxGifWidth  = Clamp(MaxGifWidth,  Limits.MaxGifMin, Limits.MaxGifMax);
            MaxGifHeight = Clamp(MaxGifHeight, Limits.MaxGifMin, Limits.MaxGifMax);
            ScaleModPercent    = Clamp(ScaleModPercent,    Limits.PercentMin, Limits.PercentMax);
            ScaleSubPercent    = Clamp(ScaleSubPercent,    Limits.PercentMin, Limits.PercentMax);
            ScaleViewerPercent = Clamp(ScaleViewerPercent, Limits.PercentMin, Limits.PercentMax);
            EdgePadding = Clamp(EdgePadding, Limits.PadMin,    Limits.PadMax);
            OffsetX     = Clamp(OffsetX,     Limits.OffsetMin, Limits.OffsetMax);
            OffsetY     = Clamp(OffsetY,     Limits.OffsetMin, Limits.OffsetMax);
            DurationSeconds = Clamp(DurationSeconds, Limits.SecondsMin, Limits.SecondsMax);
            GlowSize       = Clamp(GlowSize,       0, Limits.GlowMax);
            BorderWidth    = Clamp(BorderWidth,    0, Limits.BorderMax);
            CornerRadius   = Clamp(CornerRadius,   0, Limits.RadiusMax);
            OpacityPercent = Clamp(OpacityPercent, Limits.OpacityMin, Limits.OpacityMax);

            GiphyPickFromTop       = Clamp(GiphyPickFromTop,       Limits.PickMin,     Limits.PickMax);
            ApprovalTimeoutSeconds = Clamp(ApprovalTimeoutSeconds, Limits.ApprovalMin, Limits.ApprovalMax);
            GiphyRating = OneOf(GiphyRating, Ratings, "pg");
            GiphyApiKey = CleanKey(GiphyApiKey);
            SearchBlockedWords = CleanWords(SearchBlockedWords);

            DisplayMode = NormalizeDisplayMode(DisplayMode);
            GlowMode    = OneOf(GlowMode, GlowModes, "role");
            Entrance    = OneOf(Entrance, Entrances, "auto");
            GlowColor   = NormalizeHex(GlowColor, "#DC2828");
        }

        /// <summary>A pasted key often has spaces, quotes or a line break around it.</summary>
        internal static string CleanKey(string key)
        {
            string k = (key ?? "").Trim().Trim('"', '\'', ' ');
            return k.Length > 100 ? k.Substring(0, 100) : k;
        }

        /// <summary>Lower-cased, trimmed, no blanks, no repeats; never null.</summary>
        internal static List<string> CleanWords(List<string> words)
        {
            var clean = new List<string>();
            if (words == null) return clean;
            foreach (string w in words)
            {
                string t = (w ?? "").Trim().ToLowerInvariant();
                if (t.Length > 0 && !clean.Contains(t)) clean.Add(t);
            }
            return clean;
        }

        /// <summary>Old and misspelled names in, a valid mode out.</summary>
        internal static string NormalizeDisplayMode(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            if (m == "fixed")   return "middle-center";   // old name
            if (m == "corners") return "random-corner";   // old name
            return OneOf(m, PlacementModes, "random");
        }

        private static string OneOf(string value, string[] allowed, string fallback)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            foreach (var a in allowed) if (a == v) return a;
            return fallback;
        }

        /// <summary>Accepts "#abc", "#aabbcc" or "aabbcc"; returns "#RRGGBB".</summary>
        internal static string NormalizeHex(string hex, string fallback)
        {
            string h = (hex ?? "").Trim().TrimStart('#');
            if (h.Length == 3)
                h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
            if (h.Length != 6) return fallback;
            foreach (char c in h)
                if (!Uri.IsHexDigit(c)) return fallback;
            return "#" + h.ToUpperInvariant();
        }
    }
}
