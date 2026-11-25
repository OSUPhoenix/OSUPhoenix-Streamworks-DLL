// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerEngine.cs     DLL v1.3.0
//
//  The GIF Display & Battler "engine". Every rule the widget follows
//  lives here; the Streamer.bot side is now a thin set of one-line
//  calls (see GifBattler_StreamerBot.cs).
//
//  ── HOW IT FITS TOGETHER ────────────────────────────────────────────
//
//    Streamer.bot action ──► CPHInline.RouterA()  (1 line)
//                               │
//                               ▼
//                    new GifBattlerEngine(CPH).RouterA()
//                               │
//          ┌────────────────────┼─────────────────────┐
//          ▼                    ▼                     ▼
//   GifBattlerConfig     GifBattlerState      GifBattlerOverlay
//   (config.json)        (session globals)    (WebSocket → HTML)
//
//  Only THIS class is public. Config is public too (step 3's settings
//  UI will need it); State and Overlay are internal.
//
//  ── PUBLIC ENTRY POINTS (one per Streamer.bot action) ───────────────
//    RouterA / RouterB      chat message with a GIF link
//    ChallengeStart         channel point / !gifchallenge
//    ChallengeExpired       "GIF - Challenge Expiry" timer
//    BattleWindowExpired    "GIF - Battle Window Reset" timer
//    AutoBattleExpired      old "GIF - Auto Battle Expiry" timer (alias)
//    Cancel                 !gifcancel
//    Clear                  !cleargif
//    PollCreated / PollUpdated / PollCompleted   Twitch poll triggers
//
//  Every entry point returns bool exactly like the old Execute() did,
//  so Streamer.bot's "continue / stop" behaviour is unchanged.
//
//  ── ★ BEHAVIOUR FIXES MADE DURING THE MOVE ─────────────────────────
//   1. Router A and Router B now run the SAME decision logic
//      (ProcessGifPost). Which router fired no longer matters — the
//      engine reads the state and does the right thing. The A/B split
//      still exists in Streamer.bot to dodge SB's duplicate-queue drop,
//      but a router firing at the "wrong" time can't break anything.
//   2. Same user posting again during their own battle window: the GIF
//      now SHOWS (cooldown permitting) and the window stays armed.
//      Previously it was silently dropped. (Known pending bug.)
//   3. Queue expiry is checked against the stored time, not just the
//      timer — fixes the "resets to Router A too quickly / stale
//      queue" class of bugs.
//   4. Cooldowns stored as UTC ticks (see GifBattlerState).
//   5. Poll winner matched against the EXACT stored choice text.
//      Old code used Contains(userA), so "bob" vs "bobby" could pick
//      the wrong winner.
//   6. Poll title / choices are trimmed to Twitch's length limits
//      (60 / 25 characters), so two long usernames no longer make
//      poll creation fail.
//   7. !gifcancel during an open battle window now also stops the
//      "Battle Window Reset" timer and flips back to Router A. The old
//      code disabled a different timer and left Router B enabled.
//   8. Forfeit wins fire the achievement bridge ("win") instead of the
//      old "SAS - Award Achievement" action, matching every other path.
//   9. The integration registration (DeclareInstalled) is one shared
//      static now. Before, each action needed its own copy because
//      static fields don't share between CPHInline classes.
//
//  ── ★ STEP 2: TWITCH NATIVE CHAT GIFS (engine v2.2.0) ──────────────
//   Tier 2 / Tier 3 subs can send GIFs from Twitch's GIPHY-powered GIF
//   Keyboard. Those messages carry NO link in their text — the chat
//   text is just a caption like "[Y A Y Yes GIF by Someone]". The real
//   URL travels separately as a message fragment / part.
//
//   ★ STEP 3 (v2.3.0): OpenSettings() shows the Settings window
//   (placement, size, time on screen, glow, rules) and TestDisplay()
//   puts a built-in test card on screen. Both are run from manual
//   actions. Every setting rides along in each gif_show message.
//
//   ★ STEP 4 (v2.4.0): viewers can paste a Giphy PAGE link (what the
//   Giphy app's "Copy Link" gives, e.g. giphy.com/gifs/funny-dance-ID).
//   GifBattlerLinks.Resolve() pulls the ID out and builds the direct
//   picture link, for normal posts AND challenges. Direct links work
//   exactly as before. See GifBattlerLinks.cs for the rules.
//
//   ★ STEP 5 (v2.5.0): GiphySearch() runs both the optional !gif command
//   and the optional channel points reward ("type words, get a GIF").
//   It searches Giphy (GifBattlerSearch), optionally asks YOU to approve
//   it in a small always-on-top window (GifBattlerApproval), then sends
//   the GIF through the SAME flow as a pasted link — so cooldowns, battle
//   windows, challenges and achievements all still apply.
//
//   The engine accepts a native GIF from THREE places, checked in order:
//
//   1. OVERLAY BRIDGE  (works on every Streamer.bot version — main path)
//      The overlay already listens to Streamer.bot's WebSocket. It also
//      subscribes to Twitch → ChatMessage, whose data.parts contains
//      { type: "gif", url, gifId } — the same data chat widgets use.
//      When it sees one, it sends a DoAction request to the action
//      "GIF Battler - Native GIF" with args the engine already knows
//      (userName, userId, isSubscribed...) plus nativeGifUrl and
//      nativeGifMsgId. → entry point NativeGif().
//
//   2. %gif.N.url% ARGS  (Streamer.bot 1.1.0+)
//      Streamer.bot's own documented chat args, read by Router A / B.
//
//   3. ARG SCANNER  (any version that stores parts somewhere in args)
//      Router A / B hand over the full args dictionary and
//      GifBattlerGifScanner searches every value for a GIF-shaped
//      object, whatever the arg is called.
//
//   The same GIF can arrive by more than one path (or from two copies
//   of the overlay), so native GIFs are de-duplicated by Twitch's
//   message ID. First arrival wins; repeats are dropped silently.
//
//   Twitch's rule: "Applications rendering the GIF must use the full
//   URL provided; it must not be modified." — the URL is passed to the
//   overlay exactly as received (no trimming of query params).
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OSWTools.BotEliminator;
using OSWTools.Data;
using OSWTools.Utilities;
using Streamer.bot.Plugin.Interface;

namespace OSWTools.Functions.GifBattler
{
    public sealed class GifBattlerEngine
    {
        // ── Identity ──────────────────────────────────────────────────
        public const string ToolName      = "GIF Display";
        public const string ToolVersion   = "2.0.0";   // 2.0.0 DLL move, 2.1.0 native GIFs, 2.2.0 overlay bridge + scanner, 2.3.0 Settings window, 2.4.0 Giphy page links, 2.5.0 Giphy search + approval
        public const string MinDllVersion = "1.2.0";

        /// <summary>Must match SAS's OSUPIntegrations.GIFB constant.</summary>
        internal const string IntegrationCode = "GIFB";

        // ── Streamer.bot names — must match your SB setup EXACTLY ────
        //  (plain hyphens only — em dashes break Enable/DisableAction)
        internal const string ACTION_A             = "GIF Display - Router A";
        internal const string ACTION_B             = "GIF Display - Router B";
        internal const string TIMER_WINDOW_RESET   = "GIF - Battle Window Reset";
        internal const string TIMER_AUTO_EXPIRY    = "GIF - Auto Battle Expiry";
        internal const string TIMER_CHALLENGE      = "GIF - Challenge Expiry";

        // ── Twitch poll limits ────────────────────────────────────────
        private const int POLL_TITLE_MAX  = 60;
        private const int POLL_CHOICE_MAX = 25;
        private const string POLL_TAG     = "GIF Battle"; // PollCreated filters on this

        // ── One registration per SB session, shared by every action ──
        private static bool _integrationDeclared;
        private static readonly object _declareLock = new object();

        private readonly IInlineInvokeProxy _cph;
        private readonly OSWLib             _lib;
        private GifBattlerConfig            _cfg;   // not readonly: reloaded after the Settings window saves
        private readonly GifBattlerState    _state;
        private readonly GifBattlerOverlay  _overlay;

        /// <summary>
        /// Build a fresh engine per action run. Cheap: it only wraps CPH,
        /// reads config.json, and registers the tool version once.
        /// </summary>
        public GifBattlerEngine(IInlineInvokeProxy cph)
        {
            if (cph == null) throw new ArgumentNullException("cph");

            _cph     = cph;
            _lib     = new OSWLib(cph, ToolName);
            _cfg     = GifBattlerConfig.Load();
            _state   = new GifBattlerState(_lib);
            _overlay = new GifBattlerOverlay(cph);

            var reg = _lib.Register(ToolVersion, MinDllVersion);
            if (!reg.IsCompatible)
                _lib.LogWarn(reg.Message);
        }

        // ═════════════════════════════════════════════════════════════
        //  GIF POSTS  (chat message triggers)
        // ═════════════════════════════════════════════════════════════

        public bool RouterA() => ProcessGifPost("A", null);
        public bool RouterB() => ProcessGifPost("B", null);

        /// <summary>
        /// ★ step 2b. Same as RouterA()/RouterB(), but with the trigger's full
        /// args dictionary so the scanner can look for a native GIF part in
        /// args whose names we don't know. The Streamer.bot script calls
        /// these: Engine().RouterA(args).
        /// </summary>
        public bool RouterA(IDictionary<string, object> args) => ProcessGifPost("A", args);
        public bool RouterB(IDictionary<string, object> args) => ProcessGifPost("B", args);

        /// <summary>
        /// ★ step 2b. Runs when the overlay's WebSocket bridge spots a native
        /// GIF and sends DoAction → "GIF Battler - Native GIF". The overlay
        /// supplies the args (nativeGifUrl, nativeGifMsgId, userName, ...),
        /// so this is just the normal post flow with a different source.
        /// It deliberately does NOT care whether Router A or B is enabled —
        /// the engine decides everything from state.
        /// </summary>
        public bool NativeGif() => ProcessGifPost("Overlay", null);

        /// <summary>
        /// The single decision tree for "a viewer posted a GIF link".
        /// Order matters — read it top to bottom like a flowchart.
        /// </summary>
        private bool ProcessGifPost(string routerName, IDictionary<string, object> args, string forcedUrl = null)
        {
            var v = Viewer.Read(_cph, args);

            // 1) Where did the GIF come from?  ★ STEP 2
            //    Native Twitch GIF first (it's unambiguous — Twitch told us
            //    it's a GIF), then a pasted link in the message text.
            //    ★ STEP 5: forcedUrl = a link GiphySearch already found and
            //    (if enabled) got approved. It is trusted, so it skips the
            //    "is this chat text a link?" filter and goes straight to the
            //    same cooldown / battle logic as any other GIF.
            string gifUrl;
            bool   isNative;
            if (forcedUrl != null)
            {
                gifUrl   = forcedUrl;
                isNative = false;
            }
            else if (_cfg.TwitchChatGifsEnabled && !string.IsNullOrEmpty(v.NativeGifUrl))
            {
                gifUrl   = v.NativeGifUrl;   // used exactly as Twitch sent it
                isNative = true;
            }
            else
            {
                // Silent pre-filter. Most chat messages aren't GIF links;
                // say nothing so normal chat isn't spammed.
                // ★ STEP 4: Resolve() turns a Giphy PAGE link (what the Giphy
                //   app's "Copy Link" gives) into the direct picture link; a
                //   direct link passes through unchanged. null = not usable.
                gifUrl   = GifBattlerLinks.Resolve(v.RawInput);
                isNative = false;
                if (gifUrl == null || !IsAllowedGifUrl(gifUrl))
                    return false;
            }

            string source = isNative ? "twitch" : (forcedUrl != null ? "search" : "link");

            if (isNative)
            {
                // Native GIFs may come through the WebSocket bridge, so the
                // host is checked against TwitchGifAllowedDomains.
                if (!IsAllowedNativeGifUrl(gifUrl))
                {
                    _lib.LogWarn("Native GIF rejected (host not in TwitchGifAllowedDomains): " + gifUrl);
                    return false;
                }

                // Same GIF arriving twice (router + overlay, or two overlays)?
                if (!_state.MarkNativeMessageSeen(v.MessageId))
                {
                    _lib.LogDebug("Native GIF already handled (msg " + v.MessageId + ") — skipped duplicate from " + routerName + ".");
                    return false;
                }

                _lib.LogInfo("Native Twitch GIF via " + v.NativeGifSource + " | " + v.UserName);
            }

            // Pasted links always play the battle game. Native GIFs play
            // only if the streamer allows it (config switch).
            bool canBattle = !isNative || _cfg.TwitchChatGifsCanBattle;

            _lib.LogDebug("Router " + routerName + " | " + v.UserName + " | " + source + " | " + gifUrl);

            // 2) Optional bot filter.
            if (_cfg.IgnoreBotsOnGifPosts && IsBot(v.UserName))
                return false;

            // 3) A battle is on screen — nothing else can start.
            if (_state.BattleActive)
                return false;

            // 4) Is this the person who was challenged? Checked BEFORE
            //    cooldowns on purpose: a challenged viewer should always
            //    be able to answer, even if they just posted a GIF.
            string target = _state.ChallengeTarget;
            if (canBattle && !string.IsNullOrEmpty(target) && SameUser(target, v.UserName))
                return AcceptChallenge(v, gifUrl);

            // 5) Cooldowns.
            if (!PassesCooldowns(v))
                return false;

            // 6) Permission (one switch for everyone — see config).
            if (!HasPermission(v))
            {
                _lib.SendMessage("@" + v.UserName + " — GIF display is for subscribers and VIPs only!");
                return false;
            }

            // 7) Display-only native GIF (battles off for native GIFs).
            //    Shows on screen and leaves any open window / challenge
            //    exactly as it was.
            if (!canBattle)
            {
                ShowSingleGif(v, gifUrl, source);
                return true;
            }

            // 8) Someone is waiting for an opponent.
            if (_state.QueueIsOpen)
            {
                if (SameUser(_state.QueueUser, v.UserName))
                {
                    // ★ FIX #2 — show it, keep the window armed.
                    ShowSingleGif(v, gifUrl, source);
                    _lib.LogInfo("Same user re-posted inside their window — shown, window kept: " + v.UserName);
                    return true;
                }

                return LaunchFromQueue(v, gifUrl);
            }

            // 9) Nobody waiting. If a stale queue is hanging around
            //    (timer misfired), clean it before starting fresh.
            if (!string.IsNullOrEmpty(_state.QueueUser))
                _state.ClearQueue();

            ShowSingleGif(v, gifUrl, source);

            if (!_cfg.AutoBattleEnabled)
                return true;

            // 10) Become Challenger A and open the window.
            string avatar = _lib.GetProfilePictureIfMissing("twitch", v.UserName);
            _state.OpenQueue(gifUrl, v.UserName, v.UserId, avatar, _cfg.AutoBattleWindowSeconds);
            SwitchToRouterB();
            _cph.EnableTimer(TIMER_WINDOW_RESET);
            _lib.LogInfo("Queue opened: " + v.UserName + " (" + _cfg.AutoBattleWindowSeconds + "s window)");
            return true;
        }

        /// <summary>Show one GIF + cooldowns + 'post' achievement.</summary>
        private void ShowSingleGif(Viewer v, string gifUrl, string source)
        {
            _overlay.ShowGif(_cfg, gifUrl, v.UserName, DetermineTier(v), source);
            _state.StartUserCooldown(v.UserId, _cfg.UserCooldownSeconds);
            _state.StartGlobalCooldown(_cfg.GlobalCooldownSeconds);
            FireAchievement(v.UserName, "post");
        }

        /// <summary>Challenger B arrived inside the window.</summary>
        private bool LaunchFromQueue(Viewer v, string gifUrl)
        {
            string userA   = _state.QueueUser;
            string urlA    = _state.QueueUrl;
            string avatarA = _state.QueueAvatar;

            _cph.DisableTimer(TIMER_WINDOW_RESET);
            _state.ClearQueue();
            SwitchToRouterA();

            string avatarB = _lib.GetProfilePictureIfMissing("twitch", v.UserName);
            LaunchBattle(urlA, userA, avatarA, gifUrl, v.UserName, avatarB);
            _state.StartUserCooldown(v.UserId, _cfg.UserCooldownSeconds);
            _lib.LogInfo("Auto-battle: " + userA + " vs " + v.UserName);
            return true;
        }

        // ═════════════════════════════════════════════════════════════
        //  CHALLENGES
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Channel point reward or !gifchallenge.
        /// Input format: "@TargetUser https://media.giphy.com/..."
        /// </summary>
        public bool ChallengeStart()
        {
            var v = Viewer.Read(_cph);

            // Challenges always skip bots (unchanged).
            if (IsBot(v.UserName))
                return false;

            if (!HasPermission(v))
            {
                _lib.SendMessage("@" + v.UserName + " — GIF challenges are for subscribers and VIPs only!");
                return false;
            }

            if (_state.BattleActive)
            {
                _lib.SendMessage("@" + v.UserName + " — A GIF battle is already running! Wait for it to finish.");
                return false;
            }

            string pending = _state.ChallengeTarget;
            if (!string.IsNullOrEmpty(pending))
            {
                _lib.SendMessage("@" + v.UserName + " — There's already a pending challenge for @" + pending + "!");
                return false;
            }

            // ── Parse "@target url" ───────────────────────────────────
            string[] parts = (v.RawInput ?? "").Trim()
                .Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                _lib.SendMessage("@" + v.UserName + " — Submit format is name then link like this: @username [giphy url]");
                return false;
            }

            string targetUser = parts[0].TrimStart('@').Trim().ToLowerInvariant();
            // ★ STEP 4: page links work in challenges too (null = unusable).
            string gifUrl     = GifBattlerLinks.Resolve(parts[1]);

            if (string.IsNullOrEmpty(targetUser))
            {
                _lib.SendMessage("@" + v.UserName + " — Please specify who you're challenging.");
                return false;
            }

            if (SameUser(targetUser, v.UserName))
            {
                _lib.SendMessage("@" + v.UserName + " — What are you trying to do? You can't challenge yourself!");
                return false;
            }

            if (gifUrl == null || !IsAllowedGifUrl(gifUrl))
            {
                _lib.SendMessage("@" + v.UserName + " — Please include a valid Giphy link with your challenge (use the GIF's Copy Link button).");
                return false;
            }

            string avatar = _lib.GetProfilePictureIfMissing("twitch", v.UserName);
            _state.OpenChallenge(gifUrl, v.UserName, v.UserId, avatar, targetUser, _cfg.ChallengeWindowSeconds);
            _cph.EnableTimer(TIMER_CHALLENGE);

            string minutes = FormatWindow(_cfg.ChallengeWindowSeconds);
            _lib.SendMessage("⚔️ @" + targetUser + " — you've been challenged to a GIF battle by @" + v.UserName +
                             "! Post a Giphy link within " + minutes + " to accept, or forfeit. 🎭");
            _lib.LogInfo("Challenge issued | " + v.UserName + " → " + targetUser);

            FireAchievement(v.UserName, "challenge_initiated");
            return true;
        }

        /// <summary>The challenged viewer answered with a GIF.</summary>
        private bool AcceptChallenge(Viewer v, string gifUrl)
        {
            string urlA    = _state.ChallengeUrl;
            string userA   = _state.ChallengeUser;
            string avatarA = _state.ChallengeAvatar;

            _cph.DisableTimer(TIMER_CHALLENGE);
            _cph.DisableTimer(TIMER_WINDOW_RESET);
            _state.ClearChallenge();
            _state.ClearQueue();          // a challenge beats any open auto-window
            SwitchToRouterA();

            string avatarB = _lib.GetProfilePictureIfMissing("twitch", v.UserName);
            LaunchBattle(urlA, userA, avatarA, gifUrl, v.UserName, avatarB);
            _state.StartUserCooldown(v.UserId, _cfg.UserCooldownSeconds);

            _lib.LogInfo("Challenge accepted | " + userA + " vs " + v.UserName);
            FireAchievement(v.UserName, "challenge_accepted");
            return true;
        }

        /// <summary>Timer: the challenged viewer never answered → forfeit.</summary>
        public bool ChallengeExpired()
        {
            string target = _state.ChallengeTarget;
            string user   = _state.ChallengeUser;
            string url    = _state.ChallengeUrl;

            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(user))
            {
                _lib.LogDebug("Challenge expiry fired but nothing pending — already resolved.");
                return true;
            }

            _state.ClearChallenge();
            _lib.SendMessage("⏰ @" + target + " ran out of time! @" + user + " wins by forfeit! 🏆");

            // Winner gets the "mod" tier display as a victory perk.
            _overlay.ShowGif(_cfg, url, user, "mod");

            FireAchievement(user, "win");   // ★ FIX #8
            _lib.LogInfo("Forfeit win → " + user);
            return true;
        }

        // ═════════════════════════════════════════════════════════════
        //  WINDOW TIMERS
        // ═════════════════════════════════════════════════════════════

        /// <summary>Timer: nobody joined Challenger A in time.</summary>
        public bool BattleWindowExpired()
        {
            string queued = _state.QueueUser;
            if (!string.IsNullOrEmpty(queued))
                _lib.LogInfo("Battle window closed with no opponent for " + queued + ".");

            _state.ClearQueue();
            SwitchToRouterA();
            return true;
        }

        /// <summary>
        /// Old "GIF - Auto Battle Expiry" timer. Nothing enables that
        /// timer anymore — BattleWindowExpired does its job — but this
        /// alias keeps an existing SB setup from erroring. Safe to delete
        /// the timer + action once you've switched over.
        /// </summary>
        public bool AutoBattleExpired() => BattleWindowExpired();

        // ═════════════════════════════════════════════════════════════
        //  MOD COMMANDS
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// !cleargif — dismiss a single GIF.
        ///
        /// ★ STEP 3: also works from anything that is NOT a chat message —
        /// a Stream Deck button, a hotkey, "Test Trigger", a timer. Those
        /// have no chat user (userName is empty), and the streamer is the
        /// only one who can press them. This matters for "keep the GIF up
        /// until I clear it": you want a button, not just a chat command.
        /// A chat message ALWAYS carries a userName, so viewers are still
        /// blocked unless they are a mod or the broadcaster.
        /// </summary>
        public bool Clear()
        {
            var v = Viewer.Read(_cph);
            bool fromChat = v.UserName.Length > 0;
            if (fromChat && !v.IsMod && !v.IsBroadcaster)
            {
                _lib.LogDebug("!cleargif denied — " + v.UserName);
                return false;
            }

            _overlay.Dismiss();
            _lib.LogInfo("Overlay cleared by " + (fromChat ? v.UserName : "a non-chat trigger"));
            return true;
        }

        // ═════════════════════════════════════════════════════════════
        //  GIPHY SEARCH — !gif command + channel points reward  (★ STEP 5)
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// "Type some words, get a GIF." One method serves BOTH triggers:
        ///   • Command  "!gif dancing cat"   (args: rawInput = "dancing cat")
        ///   • Reward   with a text box      (args: rawInput = the text, plus
        ///                                    rewardId + redemptionId)
        /// Both are optional — disable the Streamer.bot action to turn one off.
        ///
        /// THE ORDER (cheap checks first, so a mod is never asked to approve a
        /// GIF that would be refused anyway):
        ///   1 permission  2 words typed  3 battle running  4 cooldown
        ///   5 search Giphy  6 (optional) approval window  7 normal post flow
        ///
        /// Anything that ends WITHOUT a GIF on screen refunds a reward
        /// (when RefundFailedRewards is on); success completes it.
        /// Returns true when a GIF was shown.
        /// </summary>
        public bool GiphySearch()
        {
            var v = Viewer.Read(_cph, null);
            bool ok = false;
            try
            {
                ok = RunGiphySearch(v);
            }
            catch (Exception ex)
            {
                // Never let a surprise leave a viewer's points stuck.
                _lib.LogError("GIF search failed: " + GifBattlerSearch.Scrub(ex.Message, _cfg.GiphyApiKey));
            }

            FinishRedemption(ok);
            return ok;
        }

        private bool RunGiphySearch(Viewer v)
        {
            string typed = ReadText("rawInput") ?? ReadText("input");

            // 1) Permission — the same single switch the pasted-link flow uses.
            if (!HasPermission(v))
            {
                _lib.SendMessage("@" + v.UserName + " — GIF display is for subscribers and VIPs only!");
                return false;
            }

            // 2) Did they type something usable, and is search set up?
            //    (Find re-checks all of this; we do it here too so a bad request
            //    never reaches the battle / cooldown checks below.)
            SearchOutcome early = GifBattlerSearch.Find(_cfg, typed, preflightOnly: true);
            if (early.Status != SearchStatus.Ok)
                return ReplyFailure(v, early);

            // 3) A battle is on screen — nothing new can start.
            if (_state.BattleActive)
            {
                _lib.SendMessage("@" + v.UserName + " — a GIF battle is on right now. Try again when it's over!");
                return false;
            }

            // 4) Cooldowns (PassesCooldowns replies by itself when it matters).
            //    A viewer who was CHALLENGED is exempt, exactly like in the
            //    pasted-link flow: they can always answer the challenge.
            string target = _state.ChallengeTarget;
            bool answeringChallenge = !string.IsNullOrEmpty(target) && SameUser(target, v.UserName);
            if (!answeringChallenge && !PassesCooldowns(v))
                return false;

            // 5) Ask Giphy.
            SearchOutcome found = GifBattlerSearch.Find(_cfg, typed);
            if (found.Status != SearchStatus.Ok)
            {
                if (found.Status == SearchStatus.Error)
                    _lib.LogWarn("GIF search error: " + found.Detail);
                return ReplyFailure(v, found);
            }
            _lib.LogInfo("GIF search | " + v.UserName + " | \"" + found.Term + "\" -> " + found.Id);

            // 6) Optional: you approve it before the stream sees it.
            if (_cfg.ApprovalEnabled)
            {
                byte[] bytes = GifBattlerSearch.HttpGetBytes(found.Url, GifBattlerSearch.MaxPreviewBytes);
                if (bytes == null)
                {
                    _lib.LogWarn("GIF search: could not download a preview, so it was not shown.");
                    _lib.SendMessage("@" + v.UserName + " — I couldn't load that GIF. Try different words!");
                    return false;
                }

                ApprovalResult verdict = GifBattlerApproval.Show(new ApprovalRequest
                {
                    UserName       = v.UserName,
                    SearchText     = found.Term,
                    GifBytes       = bytes,
                    TimeoutSeconds = _cfg.ApprovalTimeoutSeconds
                });

                if (verdict == ApprovalResult.Failed)
                {
                    _lib.LogWarn("GIF approval window could not open, so the GIF was not shown.");
                    _lib.SendMessage("@" + v.UserName + " — I couldn't load that GIF. Try different words!");
                    return false;
                }
                if (verdict != ApprovalResult.Approved)
                {
                    _lib.LogInfo("GIF not approved (" + verdict + ") | " + v.UserName + " | \"" + found.Term + "\"");
                    _lib.SendMessage("@" + v.UserName + " — that GIF wasn't approved.");
                    return false;
                }
            }

            // 7) From here it is exactly a pasted link: queue, battle window,
            //    challenge answers, cooldowns and achievements all apply.
            return ProcessGifPost("Search", null, found.Url);
        }

        private bool ReplyFailure(Viewer v, SearchOutcome outcome)
        {
            if (outcome.Status == SearchStatus.NoKey)
                _lib.LogWarn("GIF search is not set up: add your Giphy API key in the GIF Battler settings.");

            string why = GifBattlerSearch.DescribeFailure(outcome);
            if (why != null) _lib.SendMessage("@" + v.UserName + " — " + why);
            return false;
        }

        /// <summary>
        /// Completes the reward when a GIF was shown, or refunds it when not.
        /// Does nothing for the !gif command (no redemption ids).
        /// NOTE: Twitch only lets Streamer.bot change redemptions of rewards
        /// that Streamer.bot itself created, and not ones set to skip the queue.
        /// </summary>
        private void FinishRedemption(bool gifShown)
        {
            string rewardId     = ReadText("rewardId");
            string redemptionId = ReadText("redemptionId");
            if (rewardId == null || redemptionId == null) return;

            try
            {
                if (gifShown)
                {
                    _cph.TwitchRedemptionFulfill(rewardId, redemptionId);
                }
                else if (_cfg.RefundFailedRewards)
                {
                    if (!_cph.TwitchRedemptionCancel(rewardId, redemptionId))
                        _lib.LogWarn("Could not refund the reward. It must be created in Streamer.bot (Platforms > Twitch > Channel Point Rewards) with 'Skip redemption requests queue' off.");
                }
            }
            catch (Exception ex)
            {
                _lib.LogWarn("Reward update failed: " + ex.Message);
            }
        }

        /// <summary>Reads any trigger arg as trimmed text (null when missing or blank).</summary>
        private string ReadText(string name)
        {
            object o;
            if (!_cph.TryGetArg(name, out o) || o == null) return null;
            string s = Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture).Trim();
            return s.Length == 0 ? null : s;
        }

        // ═════════════════════════════════════════════════════════════
        //  SETTINGS WINDOW + TEST  (★ STEP 3)
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the GIF Battler Settings window and blocks until it closes.
        /// Run it from a MANUAL action ("GIF Battler - Settings").
        /// Returns true if the user pressed Save.
        ///
        /// HOW IT WORKS (same recipe as the Blow Up Emote window):
        ///   1. Load a private COPY of config.json. Cancel just throws the
        ///      copy away, so nothing can ever be half-saved.
        ///   2. Show the window on its own STA thread — WinForms needs one,
        ///      and Streamer.bot runs scripts on MTA threads.
        ///   3. On Save: write config.json, then (if the box was ticked)
        ///      send a test GIF so you see the result on stream right away.
        /// </summary>
        public bool OpenSettings()
        {
            GifBattlerConfig copy = GifBattlerConfig.Load();
            string path   = OSWData.GetFilePath(GifBattlerConfig.DataFolder, GifBattlerConfig.FileName);
            bool   saved  = false;
            bool   test   = false;
            string failed = null;

            DpiHelper.EnsureDpiAware();

            StaThread.Run(() =>
            {
                Application.EnableVisualStyles();
                try { Application.SetCompatibleTextRenderingDefault(false); }
                catch (InvalidOperationException) { /* already set earlier in this process - fine */ }

                using (var form = new GifBattlerSettingsForm(copy, path))
                {
                    form.StartPosition = FormStartPosition.CenterScreen;
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        // Plain file I/O — safe on the STA thread.
                        try
                        {
                            form.Result.Save();
                            saved = true;
                            test  = form.ShowTestAfterSave;
                        }
                        catch (Exception ex) { failed = ex.Message; }
                    }
                }
            });

            if (failed != null)
                _lib.LogError("GIF settings could not be saved: " + failed);

            if (saved)
            {
                _cfg = GifBattlerConfig.Load();      // pick up the new values now
                _lib.LogInfo("GIF settings saved to " + path);
                if (test) TestDisplay();
            }
            else if (failed == null)
            {
                _lib.LogInfo("GIF settings window closed without saving.");
            }

            return saved;
        }

        /// <summary>
        /// Puts a built-in test card on the overlay using the CURRENT saved
        /// settings. The overlay draws the card itself (no internet needed)
        /// in a random shape — wide, tall or square — so you can see the
        /// size limit and placement working. Shown as a "mod" so you see
        /// the biggest size and the mod glow.
        /// </summary>
        public bool TestDisplay()
        {
            _overlay.ShowGif(_cfg, "osw:test", "Preview", "mod", "test");
            _lib.LogInfo("GIF test card sent to the overlay.");
            return true;
        }

        /// <summary>!gifcancel — stop a battle, challenge, or open window.</summary>
        public bool Cancel()
        {
            var v = Viewer.Read(_cph);
            if (!v.IsMod && !v.IsBroadcaster)
            {
                _lib.LogDebug("!gifcancel denied — " + v.UserName);
                return false;
            }

            // 1) Live battle → end the poll, clear the screen.
            if (_state.BattleActive)
            {
                string pollId = _state.PollId;
                if (!string.IsNullOrEmpty(pollId))
                {
                    try { _cph.TwitchPollArchive(pollId); }
                    catch (Exception ex) { _lib.LogWarn("Could not archive poll: " + ex.Message); }
                }

                _overlay.Dismiss();
                _state.ClearBattle();
                _lib.SendMessage("🛑 GIF Battle cancelled by @" + v.UserName + ".");
                return true;
            }

            // 2) Pending challenge.
            string target = _state.ChallengeTarget;
            if (!string.IsNullOrEmpty(target))
            {
                string challenger = _state.ChallengeUser;
                _cph.DisableTimer(TIMER_CHALLENGE);
                _state.ClearChallenge();
                _lib.SendMessage("🛑 Challenge between @" + challenger + " and @" + target + " cancelled by @" + v.UserName + ".");
                return true;
            }

            // 3) Open auto-battle window. ★ FIX #7
            if (!string.IsNullOrEmpty(_state.QueueUser))
            {
                _cph.DisableTimer(TIMER_WINDOW_RESET);
                _cph.DisableTimer(TIMER_AUTO_EXPIRY);
                _state.ClearQueue();
                SwitchToRouterA();
                _lib.SendMessage("🛑 Battle window cancelled by @" + v.UserName + ".");
                return true;
            }

            _lib.SendMessage("@" + v.UserName + " — No active GIF battle or challenge to cancel.");
            return true;
        }

        // ═════════════════════════════════════════════════════════════
        //  BATTLE + TWITCH POLLS
        // ═════════════════════════════════════════════════════════════

        private void LaunchBattle(string urlA, string userA, string avatarA,
                                  string urlB, string userB, string avatarB)
        {
            string pollTitle = BuildPollTitle(userA, userB);
            string choiceA   = BuildChoice(userA, "🩷");
            string choiceB   = BuildChoice(userB, "💙");

            _state.StartBattle(userA, userB, pollTitle, choiceA, choiceB);

            // Choice order matters: A first = pink/left on the overlay.
            var choices = new List<string> { choiceA, choiceB };
            bool created = _cph.TwitchPollCreate(pollTitle, choices, _cfg.BattleVotingSeconds);
            if (created) _lib.LogInfo("Poll created | " + pollTitle);
            else         _lib.LogWarn("Poll creation failed — battle continues without voting.");

            _overlay.BattleStart(_cfg, urlA, userA, avatarA, urlB, userB, avatarB);
        }

        /// <summary>Trigger: Twitch → Polls → Poll Created. Stores the poll ID for !gifcancel.</summary>
        public bool PollCreated()
        {
            string title;
            _cph.TryGetArg("poll.Title", out title);
            if ((title ?? "").IndexOf(POLL_TAG, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string pollId;
            _cph.TryGetArg("poll.Id", out pollId);
            if (string.IsNullOrEmpty(pollId))
            {
                _lib.LogWarn("PollCreated fired but poll.Id was empty.");
                return true;
            }

            _state.SetPollId(pollId);
            _lib.LogDebug("Poll ID stored: " + pollId);
            return true;
        }

        /// <summary>
        /// Trigger: Twitch → Polls → Poll Updated. Sends live vote bars.
        /// Reads poll._json (Twitch's own field names: "votes" and
        /// "channel_points_votes") — the fix for the 50/50 stuck bars.
        /// </summary>
        public bool PollUpdated()
        {
            if (!_state.BattleActive)
                return true;

            string json;
            _cph.TryGetArg("poll._json", out json);
            if (string.IsNullOrEmpty(json))
                return true;

            JArray choices;
            try
            {
                choices = JObject.Parse(json)["choices"] as JArray;
            }
            catch (Exception ex)
            {
                _lib.LogWarn("PollUpdated: could not parse poll._json — " + ex.Message);
                return true;
            }

            if (choices == null || choices.Count < 2)
                return true;

            // choices[0] = first created = A (pink, left)
            int pink = SumVotes(choices[0]);
            int blue = SumVotes(choices[1]);
            int total = pink + blue;

            double pinkPct = 50.0, bluePct = 50.0;
            if (total > 0)
            {
                const double FLOOR = 5.0; // keep both bars visible
                pinkPct = Math.Round((double)pink / total * 100, 1);
                pinkPct = Math.Max(FLOOR, Math.Min(100.0 - FLOOR, pinkPct));
                bluePct = Math.Round(100.0 - pinkPct, 1);
            }

            _overlay.VoteUpdate(pinkPct, bluePct);
            return true;
        }

        /// <summary>Trigger: Twitch → Polls → Poll Completed. Declares the winner.</summary>
        public bool PollCompleted()
        {
            if (!_state.BattleActive)
                return false;

            string userA = _state.BattleUserA;
            string userB = _state.BattleUserB;

            string winningTitle;
            _cph.TryGetArg("poll.winningChoice.title", out winningTitle);
            winningTitle = (winningTitle ?? "").Trim();

            string side = PickWinnerSide(winningTitle, userA, userB);
            if (side == null)
            {
                _lib.LogWarn("PollCompleted: could not determine winner — dismissing.");
                _overlay.Dismiss();
                _state.ClearBattle();
                return false;
            }

            string winner = side == "A" ? userA : userB;
            string loser  = side == "A" ? userB : userA;

            _overlay.BattleResult(side, winner);
            _lib.SendMessage("🏆 @" + winner + "'s GIF wins the battle! Better luck next time @" + loser + " 🎭");
            FireAchievement(winner, "win");

            _state.ClearBattle();
            _lib.LogInfo("Battle winner: " + winner + " (side " + side + ")");
            return true;
        }

        /// <summary>
        /// ★ FIX #5. Three layers, most reliable first:
        ///   1. exact match against the choice text we stored at launch
        ///   2. username Contains — LONGER name checked first so
        ///      "bob" can't steal a win from "bobby"
        ///   3. poll.winningIndex (1 = A, 2 = B)
        /// </summary>
        private string PickWinnerSide(string winningTitle, string userA, string userB)
        {
            if (winningTitle.Length > 0)
            {
                if (string.Equals(winningTitle, _state.BattleChoiceA, StringComparison.Ordinal)) return "A";
                if (string.Equals(winningTitle, _state.BattleChoiceB, StringComparison.Ordinal)) return "B";

                bool aFirst = (userA ?? "").Length >= (userB ?? "").Length;
                string first  = aFirst ? userA : userB;
                string second = aFirst ? userB : userA;

                if (!string.IsNullOrEmpty(first) && winningTitle.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0)
                    return aFirst ? "A" : "B";
                if (!string.IsNullOrEmpty(second) && winningTitle.IndexOf(second, StringComparison.OrdinalIgnoreCase) >= 0)
                    return aFirst ? "B" : "A";
            }

            int index;
            _cph.TryGetArg("poll.winningIndex", out index);
            _lib.LogWarn("PollCompleted: title match failed ('" + winningTitle + "'), using index " + index);
            if (index == 1) return "A";
            if (index == 2) return "B";
            return null;
        }

        // ═════════════════════════════════════════════════════════════
        //  DIAGNOSTIC  ★ STEP 2
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ step 2b. Logs EVERY trigger arg — name, C# type, and value. Complex
        /// values (lists, Streamer.bot objects) are printed as JSON so you can
        /// see what's inside them. Then it runs the GIF scanner and reports
        /// whether (and where) it found a native GIF.
        ///
        /// Use: send one native GIF in chat with the diagnostic action
        /// enabled, then read Streamer.bot's log. Same "dump the args" trick
        /// that cracked the poll._json vote-bar bug.
        ///
        /// WHY IT TAKES args AS A PARAMETER:
        ///   The engine only holds CPH, and CPH has no "list every arg"
        ///   method. The full dictionary lives on the CPHInline class itself
        ///   (its built-in `args` field), so the script hands it over.
        /// </summary>
        public bool LogAllArgs(IDictionary<string, object> args)
        {
            if (args == null)
            {
                _lib.LogWarn("[GIF diag] args was null.");
                return true;
            }

            _lib.LogInfo("[GIF diag] ───── " + args.Count + " args ─────");
            foreach (var kv in args)
            {
                string type = kv.Value == null ? "null" : kv.Value.GetType().Name;
                string text;

                var tree = (kv.Value is string) ? null : GifBattlerGifScanner.ToTree(kv.Value);
                text = tree != null ? tree.ToString(Formatting.None) : Convert.ToString(kv.Value);

                if (text != null && text.Length > 1500)
                    text = text.Substring(0, 1500) + "…(" + text.Length + " chars)";

                _lib.LogInfo("[GIF diag] " + kv.Key + " (" + type + ") = " + text);
            }

            var hit = GifBattlerGifScanner.Find(args);
            if (hit != null)
                _lib.LogInfo("[GIF diag] ✔ Native GIF FOUND in arg '" + hit.ArgKey + "' → " + hit.Url);
            else
                _lib.LogInfo("[GIF diag] ✘ No native GIF in this trigger's args. (Normal for a text message; " +
                             "for a native GIF it means only the overlay bridge can see it.)");
            return true;
        }

        /// <summary>Old name from step 2 — kept so an existing setup still runs.</summary>
        public bool LogGifArgs(IDictionary<string, object> args) => LogAllArgs(args);

        // ═════════════════════════════════════════════════════════════
        //  HELPERS
        // ═════════════════════════════════════════════════════════════

        private bool IsAllowedGifUrl(string url)
        {
            if (!UrlHelper.IsValidUrl(url)) return false;
            if (_cfg.AllowedDomains.Count == 0) return true;
            return UrlHelper.IsAllowedDomain(url, _cfg.AllowedDomains);
        }

        private bool IsAllowedNativeGifUrl(string url)
        {
            if (!UrlHelper.IsValidUrl(url)) return false;
            if (_cfg.TwitchGifAllowedDomains.Count == 0) return true;
            return UrlHelper.IsAllowedDomain(url, _cfg.TwitchGifAllowedDomains);
        }

        private bool PassesCooldowns(Viewer v)
        {
            int global = _state.GlobalCooldownRemaining();
            if (global > 0)
            {
                // Quiet on purpose: a global cooldown hits everyone, and
                // replying to each viewer would flood chat.
                _lib.LogDebug("Global cooldown: " + global + "s left.");
                return false;
            }

            int mine = _state.UserCooldownRemaining(v.UserId);
            if (mine > 0)
            {
                _lib.SendMessage("@" + v.UserName + " — GIF cooldown: " + mine + "s remaining.");
                return false;
            }

            return true;
        }

        private bool HasPermission(Viewer v)
        {
            if (!_cfg.RestrictToSubsAndVips) return true;
            return v.IsSub || v.IsVip || v.IsMod || v.IsBroadcaster;
        }

        private bool IsBot(string userName)
        {
            try { return new BotEliminatorManager(_cph).IsExcluded(userName); }
            catch (Exception ex)
            {
                _lib.LogWarn("Bot check failed (" + ex.Message + ") — allowing.");
                return false;
            }
        }

        private static bool SameUser(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string DetermineTier(Viewer v)
        {
            if (v.IsBroadcaster || v.IsMod) return "mod";
            if (v.IsSub) return "sub";
            return "vip";
        }

        private void SwitchToRouterA()
        {
            _cph.DisableAction(ACTION_B);
            _cph.EnableAction(ACTION_A);
        }

        private void SwitchToRouterB()
        {
            _cph.DisableAction(ACTION_A);
            _cph.EnableAction(ACTION_B);
        }

        /// <summary>★ FIX #6 — stay within Twitch's 60-char title limit.</summary>
        private static string BuildPollTitle(string userA, string userB)
        {
            string full = userA + " vs " + userB + " 🎭 " + POLL_TAG;
            if (full.Length <= POLL_TITLE_MAX) return full;

            // Fallback keeps the "GIF Battle" tag (PollCreated needs it)
            // and shares the remaining space between both names.
            string prefix = POLL_TAG + ": ";
            int each = (POLL_TITLE_MAX - prefix.Length - " vs ".Length) / 2;
            return prefix + Trim(userA, each) + " vs " + Trim(userB, each);
        }

        /// <summary>★ FIX #6 — stay within Twitch's 25-char choice limit.</summary>
        private static string BuildChoice(string user, string heart)
        {
            string full = user + "'s GIF " + heart;
            if (full.Length <= POLL_CHOICE_MAX) return full;

            string shortForm = user + " " + heart;
            if (shortForm.Length <= POLL_CHOICE_MAX) return shortForm;

            return Trim(user, POLL_CHOICE_MAX);
        }

        private static string Trim(string s, int max)
        {
            s = s ?? "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static string FormatWindow(int seconds)
        {
            if (seconds % 60 == 0)
            {
                int m = seconds / 60;
                return m + (m == 1 ? " minute" : " minutes");
            }
            return seconds + " seconds";
        }

        private static int SumVotes(JToken choice)
        {
            if (choice == null) return 0;
            int votes  = choice["votes"] != null ? choice["votes"].ToObject<int>() : 0;
            int points = choice["channel_points_votes"] != null ? choice["channel_points_votes"].ToObject<int>() : 0;
            return votes + points;
        }

        // ── Achievement bridge ────────────────────────────────────────
        private void FireAchievement(string user, string qualifier)
        {
            EnsureIntegrationDeclared();
            _lib.FireAchievementEvent(
                sourceTool: IntegrationCode,
                category:   "GIFs",
                platform:   "Twitch",
                user:       user,
                qualifier:  qualifier,
                amount:     1);
        }

        /// <summary>
        /// The bridge drops events from tools that haven't declared
        /// themselves installed. Locked + static so it runs once per SB
        /// session no matter how many actions fire at the same moment.
        /// </summary>
        private void EnsureIntegrationDeclared()
        {
            if (_integrationDeclared) return;
            lock (_declareLock)
            {
                if (_integrationDeclared) return;
                _lib.DeclareInstalled(IntegrationCode, ToolVersion);
                _integrationDeclared = true;
                _lib.LogInfo("Registered with OSW bridge as '" + IntegrationCode + "' v" + ToolVersion + ".");
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Viewer — the trigger args every entry point reads, in one go
        // ═════════════════════════════════════════════════════════════
        private sealed class Viewer
        {
            internal string RawInput;
            internal string UserName;
            internal string UserId;
            internal bool   IsSub;
            internal bool   IsVip;
            internal bool   IsMod;
            internal bool   IsBroadcaster;

            /// <summary>★ STEP 2 — URL of the first native Twitch GIF, or null.</summary>
            internal string NativeGifUrl;

            /// <summary>Where the native GIF was found (for the log).</summary>
            internal string NativeGifSource;

            /// <summary>Twitch message ID — used to de-duplicate native GIFs.</summary>
            internal string MessageId;

            internal static Viewer Read(IInlineInvokeProxy cph) => Read(cph, null);

            internal static Viewer Read(IInlineInvokeProxy cph, IDictionary<string, object> args)
            {
                var v = new Viewer();
                cph.TryGetArg("rawInput",      out v.RawInput);
                cph.TryGetArg("userName",      out v.UserName);
                cph.TryGetArg("userId",        out v.UserId);
                cph.TryGetArg("isSubscribed",  out v.IsSub);
                cph.TryGetArg("isVip",         out v.IsVip);
                cph.TryGetArg("isModerator",   out v.IsMod);
                cph.TryGetArg("isBroadcaster", out v.IsBroadcaster);
                v.UserName = v.UserName ?? "";
                v.UserId   = v.UserId   ?? "";
                // Message ID: the overlay sends nativeGifMsgId; Streamer.bot's
                // own chat trigger calls it msgId.
                v.MessageId = ArgString(cph, "nativeGifMsgId") ?? ArgString(cph, "msgId") ?? "";

                // 1) Overlay bridge — explicit arg, no guessing.
                string bridged = ArgString(cph, "nativeGifUrl");
                if (!string.IsNullOrEmpty(bridged) && UrlHelper.IsValidUrl(bridged))
                {
                    v.NativeGifUrl    = bridged;
                    v.NativeGifSource = "overlay bridge";
                    return v;
                }

                // 2) Streamer.bot 1.1.0+ documented args.
                string documented = ReadNativeGifUrl(cph);
                if (documented != null)
                {
                    v.NativeGifUrl    = documented;
                    v.NativeGifSource = "%gif.N.url% args";
                    return v;
                }

                // 3) Scan every arg for a GIF-shaped part.
                var hit = GifBattlerGifScanner.Find(args);
                if (hit != null && UrlHelper.IsValidUrl(hit.Url))
                {
                    v.NativeGifUrl    = hit.Url;
                    v.NativeGifSource = "arg scan ('" + hit.ArgKey + "')";
                }

                return v;
            }

            /// <summary>Reads any arg as text, whatever type SB stored it as.</summary>
            private static string ArgString(IInlineInvokeProxy cph, string name)
            {
                object o;
                if (!cph.TryGetArg(name, out o) || o == null) return null;
                string s = Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture).Trim();
                return s.Length == 0 ? null : s;
            }

            /// <summary>
            /// Finds the first usable native GIF URL in the trigger args.
            ///
            /// WHY READ AS object INSTEAD OF int / string:
            ///   CPH.TryGetArg&lt;T&gt; returns false if the stored value's type
            ///   isn't exactly T. Streamer.bot might store gif.count as int,
            ///   long, or even string depending on version. Reading as
            ///   object and converting ourselves works for all of them.
            ///
            /// WHY LOOP:
            ///   A message could hold more than one GIF fragment. We show the
            ///   first valid one (one pop-up per message keeps the screen sane).
            /// </summary>
            private static string ReadNativeGifUrl(IInlineInvokeProxy cph)
            {
                object countObj;
                int count = 0;
                if (cph.TryGetArg("gif.count", out countObj) && countObj != null)
                    int.TryParse(Convert.ToString(countObj, System.Globalization.CultureInfo.InvariantCulture), out count);

                // No count arg at all? Still peek at gif.0.url — costs nothing
                // and covers a version that sends fragments without a count.
                int checkUpTo = Math.Min(Math.Max(count, 1), 10);

                for (int i = 0; i < checkUpTo; i++)
                {
                    object urlObj;
                    if (!cph.TryGetArg("gif." + i + ".url", out urlObj) || urlObj == null)
                        continue;

                    string url = Convert.ToString(urlObj).Trim();
                    // Must be a real http(s) URL so nothing odd
                    // (javascript:, data:, file:) reaches OBS. The host is
                    // checked later against TwitchGifAllowedDomains.
                    if (UrlHelper.IsValidUrl(url))
                        return url;
                }

                return null;
            }
        }
    }
}
