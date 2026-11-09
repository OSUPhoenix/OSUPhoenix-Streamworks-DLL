// =============================================================================
// OSWTools — Extensions/OSWLib_Bue.cs                                (NEW)
//
// "Blow Up Emote" (module code: BUE) — the OSWLib methods that Streamer.bot
// actions call. Extends OSWLib (partial class) the same way EmoteExtensions.cs
// and the Tiltify UI do.
//
//   lib.BueMigrateLegacy(id)     one-time import of the old global variables
//   lib.BueBroadcastState(id)    tell the widget "here is how things are now"
//   lib.BueCancelRound(id)       cancel the round: empty + hide, no pop
//   lib.BueStatus(id)            log what the saved settings/state are (debug)
//   lib.BueStartRound(...)       start a round (show the balloon at 0%)
//   lib.BueChangeEmote(...)      change which emote the balloon is (with the
//                                streamer-only lock and chat validation)
//   lib.BueOpenSettings(id)      the settings window       (OSWLib_BueUI.cs)
//
// WHY A FACADE?
//   An action file can't reach OSWLib's private CPH proxy, and we want actions
//   to stay short and speak in simple types (string, bool). Putting the work
//   here means the chat action, the Set Emote action, the Sync action and the
//   settings window all share ONE implementation instead of four copies.
// =============================================================================

using System;
using System.Collections.Generic;
using OSWTools.BlowUpEmote;
using OSWTools.Data;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────────────────────────
        // One-time import of BUE v1.x data from Streamer.bot global variables
        // into the JSON files.
        //
        // Uses your existing GlobalsMigration helper: "if the JSON file does
        // not exist yet, build it from this global". Once the file exists it
        // does nothing, so calling it often is harmless (it's just a file
        // check). On a brand-new install there is no global, so it simply
        // writes a default file you can then edit.
        //
        // The old globals are NOT deleted. After you confirm everything works
        // you can remove BUE_Config_default, BUE_State_default and
        // BUE_Images_default in Streamer.bot -> Variables.
        // ─────────────────────────────────────────────────────────────────
        public void BueMigrateLegacy(string targetId = "default")
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";

            // Let the store report problems in the Streamer.bot log.
            BueStore.Logger = s => LogWarn(s);

            try
            {
                GlobalsMigration.MigrateJsonGlobalIfMissing<BueTargetConfig>(
                    BueStore.Tool, BueStore.ConfigFile(targetId), _CPH,
                    "BUE_Config_" + targetId,
                    new BueTargetConfig { TargetId = targetId });

                GlobalsMigration.MigrateJsonGlobalIfMissing<BueState>(
                    BueStore.Tool, BueStore.StateFile(targetId), _CPH,
                    "BUE_State_" + targetId,
                    new BueState { TargetId = targetId });

                GlobalsMigration.MigrateJsonGlobalIfMissing<Dictionary<string, string>>(
                    BueStore.Tool, BueStore.ImagesFile(targetId), _CPH,
                    "BUE_Images_" + targetId,
                    new Dictionary<string, string>());
            }
            catch (Exception ex)
            {
                LogWarn("[BUE] Migration from globals failed: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Send the widget a BUE_State message: current fill, current emote and
        // its picture, and the widget-facing settings.
        //
        // Called when: the emote changes, settings are saved, and when the
        // widget connects and asks for a sync (BUESync action).
        // ─────────────────────────────────────────────────────────────────
        public void BueBroadcastState(string targetId = "default")
        {
            BueSendState(targetId, false);
        }

        // Same message, but `started` = true tells the widget "a round just began,
        // spring the balloon in" instead of silently appearing.
        private void BueSendState(string targetId, bool started, bool reset = false)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";

            try
            {
                BueMigrateLegacy(targetId);

                BueTargetConfig config = BueStore.LoadConfig(targetId);
                BueState state;
                lock (BueStore.SyncRoot)
                {
                    state = BueStore.LoadState(targetId);
                    // Bring the fill up to date: subtract whatever has drained
                    // since it was last saved, so the widget gets the TRUE number.
                    if (BlowUpEmoteEngine.ApplyDeflation(state, config, DateTime.UtcNow))
                        BueStore.SaveState(targetId, state);
                }

                // Rounds on: the running round's emote. Rounds off: the Settings emote.
                string emote = BlowUpEmoteEngine.ActiveEmote(config, state);
                string image = BlowUpEmoteEngine.ActiveImage(this, config, state, "");

                _CPH.WebsocketBroadcastJson(BuePayloads.State(config, state, emote, image, started, reset));

                LogDebug("[BUE] Sent state to widget: emote='" + emote + "' fill=" + state.FillPercent.ToString("0.#") +
                         "% picture=" + (string.IsNullOrEmpty(image) ? "none" : "yes"));
            }
            catch (Exception ex)
            {
                LogError("[BUE] BueBroadcastState failed: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // CANCEL THE ROUND ("Cancel Round" action - attach any trigger).
        //
        // Ends the game early and puts everything back to zero WITHOUT a pop:
        //   - fill 0%, balloon hidden (when rounds are on)
        //   - this round's leaderboard cleared, per-user cooldowns forgotten
        //   - no pop animation, no achievement events
        // Kept: all-time contributor totals, the pop count, the chosen emote.
        // Works whether or not rounds are switched on.
        // ─────────────────────────────────────────────────────────────────
        public bool BueCancelRound(string targetId, out string message)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";

            try
            {
                BueMigrateLegacy(targetId);

                bool wasActive;
                double wasFill;
                string wasEmote;
                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    wasActive = state.Active;
                    wasFill = state.FillPercent;
                    wasEmote = state.RoundEmote ?? "";
                    BlowUpEmoteEngine.CancelRound(state);
                    BueStore.SaveState(targetId, state);
                }

                // Read it back from the file: proves the reset really was saved.
                BueState after;
                lock (BueStore.SyncRoot) { after = BueStore.LoadState(targetId); }

                BueSendState(targetId, false, true);
                if (wasActive && wasEmote.Length > 0)
                    BueAnnounce(BueStore.LoadConfig(targetId), BlowUpEmoteEngine.MsgCancelled(wasEmote));
                BueLog.Write("CANCEL  was " + (wasActive ? "running" : "not running") + " -> reset to idle");
                message = "[v" + BueStore.BuildTag + "] Round cancelled. Before: " + (wasActive ? "running" : "not running") +
                          ", " + wasFill.ToString("0.#") + "%.  Saved now: " + (after.Active ? "running" : "not running") +
                          ", " + after.FillPercent.ToString("0.#") + "%.  Widget told to reset.";
                return true;
            }
            catch (Exception ex)
            {
                LogError("[BUE] BueCancelRound failed: " + ex.Message);
                message = "[v" + BueStore.BuildTag + "] Error: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Download fresh 7TV/BTTV/FFZ lists now and log the result (counts or the error)
        /// to bue_log.txt. Run from the "Blow Up Emote - Refresh Emotes" action: this is
        /// also the quickest way to check that each service is reachable from your PC.
        /// </summary>
        public string BueRefreshEmotes(string targetId)
        {
            try
            {
                BueTargetConfig c = BueStore.LoadConfig(targetId);
                string report = RefreshThirdPartyEmotes(c.Use7TV, c.UseBTTV, c.UseFFZ);
                BueLog.Write("EMOTES refresh:\n  " + report);
                return "[v" + BueStore.BuildTag + "] Emote lists:\n  " + report;
            }
            catch (Exception ex)
            {
                BueLog.Write("EMOTES refresh error: " + ex.Message);
                return "[v" + BueStore.BuildTag + "] Error: " + ex.Message;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // SHARED: work out which emote a trigger is asking for, and whether that
        // is allowed. Used by Start Round and Set Emote so the rules live once.
        //
        //   1. Settings > "Streamer only": a viewer's trigger is refused.
        //   2. The emote must be a REAL emote (in the message's own emote list or
        //      known to Twitch). The streamer may use any word (7TV/BTTV names).
        //   3. Banned list / allowed list (viewers only; empty allowed = any).
        //   4. Only the streamer may attach a picture link.
        // ─────────────────────────────────────────────────────────────────
        private bool BueResolveRequest(
            BueTargetConfig config, string text, string emotesJson,
            string userId, string broadcasterId, string userName, string broadcasterName,
            out string emote, out string url, out Dictionary<string, string> found, out string message)
        {
            emote = ""; url = ""; message = "";
            found = BlowUpEmoteEngine.ParseEmoteImages(emotesJson);

            bool fromStreamer = BlowUpEmoteEngine.IsBroadcaster(userId, broadcasterId, userName, broadcasterName);
            string who = string.IsNullOrWhiteSpace(userName) ? "unknown" : userName;

            if (config.EmoteControl == EmoteControlMode.StreamerOnly && !fromStreamer)
            {
                message = "Ignored: choosing the emote is locked to the streamer (Settings > 'Streamer only'). Triggered by '" + who + "'. " +
                          "For viewer-triggered rounds choose 'Chat can change it'.";
                return false;
            }

            // Everyone, the streamer included, must name a REAL emote. The one exception: the
            // streamer may use any word (a 7TV/BTTV name no API knows) if the same text also
            // carries an https picture link, because then BUE has a picture for it.
            bool streamerCustom = fromStreamer && BlowUpEmoteEngine.FindHttpsUrl(text).Length > 0;
            emote = BlowUpEmoteEngine.PickEmote(this, config, text, found, streamerCustom);
            if (string.IsNullOrWhiteSpace(emote))
            {
                message = fromStreamer
                    ? "No real emote found in the text (a custom 7TV/BTTV name needs a picture link: 'name https://...')."
                    : "No recognised emote in the message from '" + who + "'.";
                return false;
            }

            string reason;
            if (!BlowUpEmoteEngine.IsEmotePickAllowed(config, emote, out reason))
            {
                message = "Refused: " + reason;
                emote = "";
                return false;
            }

            url = fromStreamer ? BlowUpEmoteEngine.FindHttpsUrl(text) : "";
            return true;
        }

        // ─────────────────────────────────────────────────────────────────
        // START A ROUND ("Start Round" action - attach any trigger).
        //
        // The flow:   Start (with an emote)  ->  chat/rewards/bits grow it
        //             ->  whoever pops it wins  ->  balloon hides until the next Start.
        //
        // THE EMOTE FOR THE ROUND comes from the trigger's text ("!blowup Kappa",
        // a reward's text box, a cheer's message) and lives in the game state - it
        // does NOT change your Settings. The Settings emote is only the DEFAULT,
        // used when a Start carries no emote.
        //
        // WHAT HAPPENS WHEN THE PICK IS REFUSED (banned, not on the allowed list,
        // locked to the streamer, no real emote in the text):
        //   - normal trigger (command, channel point reward...): the round does NOT
        //     start. `outcome` = "refused". The action refunds a channel point
        //     redemption, because the viewer paid and got nothing.
        //   - PAID trigger (bits, power-up, sub, donation... `paid` = true): money
        //     can't be handed back, so the round starts anyway with the DEFAULT emote
        //     from Settings; if there is none (or your own lists refuse it) with the
        //     universal fallback emote  :)   `outcome` = "fallback".
        //
        // outcome: "started", "fallback", "refused", "running", "off", "error".
        // Returns true when a round started (outcomes started / fallback).
        // If a round is already running nothing changes (Set Emote changes the emote
        // mid-game; Cancel Round ends it).
        // ─────────────────────────────────────────────────────────────────
        public bool BueStartRound(
            string targetId, string text, string emotesJson,
            string userId, string broadcasterId, string userName, string broadcasterName,
            out string message)
        {
            string outcome;
            return BueStartRoundEx(targetId, text, emotesJson, userId, broadcasterId, userName, broadcasterName,
                                   false, out outcome, out message);
        }

        /// <summary>Lets an action write a line into BUE's own log file (OSWData/BlowUpEmote/bue_log.txt).</summary>
        public void BueLogLine(string line) { BueLog.Write(line); }

        /// <summary>Full path of BUE's own log file.</summary>
        public string BueLogPath() { return BueLog.FilePath; }

        public bool BueStartRoundEx(
            string targetId,
            string text,
            string emotesJson,
            string userId,
            string broadcasterId,
            string userName,
            string broadcasterName,
            bool paid,
            out string outcome,
            out string message)
        {
            string startedEmote, reason;
            bool pickPending;
            return BueStartRoundFull(targetId, text, emotesJson, userId, broadcasterId, userName, broadcasterName,
                                     paid, out outcome, out message, out startedEmote, out reason, out pickPending);
        }

        /// <summary>
        /// Same as BueStartRoundEx, plus the facts an action needs to word a chat reply:
        ///   startedEmote - the emote the round actually started with ("" if none started)
        ///   reason       - short plain-English reason when the request was refused / not
        ///                  honoured ("'Pepega' is on the banned list.", "a round is already running")
        ///   pickPending  - true when the (paid) starter may now send a message with a
        ///                  different emote to switch the round to it
        /// </summary>
        public bool BueStartRoundFull(
            string targetId,
            string text,
            string emotesJson,
            string userId,
            string broadcasterId,
            string userName,
            string broadcasterName,
            bool paid,
            out string outcome,
            out string message,
            out string startedEmote,
            out string reason,
            out bool pickPending)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";
            string tag = "[v" + BueStore.BuildTag + "] ";
            outcome = "error";
            startedEmote = "";
            reason = "";
            pickPending = false;

            try
            {
                BueMigrateLegacy(targetId);

                BueTargetConfig config = BueStore.LoadConfig(targetId);
                BueLog.Write("START  1/5 request: text='" + (text ?? "") + "' user='" + userName + "' paid=" + paid +
                             " | settings: default='" + BlowUpEmoteEngine.CurrentEmote(config) + "' who='" + config.EmoteControl +
                             "' allowed=[" + string.Join(",", config.AllowedEmotes) + "] banned=[" + string.Join(",", config.BannedEmotes) + "]");
                if (!config.StartHidden)
                {
                    outcome = "off";
                    reason = "rounds are switched off";
                    message = tag + "Not started: rounds are switched off (Settings > 'Hide the balloon between rounds'), so the balloon is always on.";
                    return false;
                }

                // A round that has timed out must not block a new Start.
                string expiredNote;
                BueExpireIfDue(targetId, out expiredNote);

                bool running;
                lock (BueStore.SyncRoot) { running = BueStore.LoadState(targetId).Active; }
                BueLog.Write("START  2/5 round already running? " + running);
                if (running)
                {
                    outcome = "running";
                    reason = "a round is already running";
                    message = tag + "Not started: a round is already running. (Set Emote changes the emote mid-game; Cancel Round ends it.)";
                    return false;
                }

                // 1) Which emote? `why` collects the reason when the request is refused.
                string emote = "", url = "", why = "", source = "";
                Dictionary<string, string> found = new Dictionary<string, string>();
                bool refused = false;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (BueResolveRequest(config, text, emotesJson, userId, broadcasterId, userName, broadcasterName,
                                          out emote, out url, out found, out why))
                        source = "from the trigger";
                    else
                        refused = true;
                }
                else
                {
                    emote = BlowUpEmoteEngine.CurrentEmote(config);
                    url = config.ImageOverrideUrl ?? "";
                    source = "the default from Settings";
                    string listWhy;
                    if (string.IsNullOrWhiteSpace(emote))
                    {
                        refused = true;
                        why = "no emote. Put one in the trigger (!blowup Kappa) or set a default emote in Settings.";
                    }
                    else if (!BlowUpEmoteEngine.IsEmotePickAllowed(config, emote, out listWhy))
                    {
                        refused = true;
                        why = "the default emote from Settings is refused by your own lists. " + listWhy;
                    }
                }

                BueLog.Write("START  3/5 pick result: " + (refused ? "REFUSED - " + why : "OK emote='" + emote + "' (" + source + ")"));
                bool usedFallback = false;
                if (refused)
                {
                    if (!paid)
                    {
                        BueLog.Write("START  4/5 refused and NOT a paid trigger -> no fallback");
                        outcome = "refused";
                        reason = CleanReason(why);
                        message = tag + "Not started: " + why;
                        return false;
                    }

                    // PAID trigger: start anyway. Default emote first, then the universal one.
                    string defEmote = BlowUpEmoteEngine.CurrentEmote(config);
                    string ignoredWhy;
                    if (!string.IsNullOrWhiteSpace(defEmote) && BlowUpEmoteEngine.IsEmotePickAllowed(config, defEmote, out ignoredWhy))
                    {
                        emote = defEmote;
                        url = config.ImageOverrideUrl ?? "";
                        source = "the default from Settings, because the requested emote was refused";
                    }
                    else
                    {
                        emote = BlowUpEmoteEngine.UniversalEmote;
                        url = BlowUpEmoteEngine.UniversalEmoteImage;
                        source = "the universal fallback emote, because the requested emote was refused and there is no usable default";
                    }
                    usedFallback = true;
                    BueLog.Write("START  4/5 PAID FALLBACK -> emote='" + emote + "' (" + source + ")");
                    found = new Dictionary<string, string>();   // don't learn pictures from a refused message
                }

                // 2) Start it (one locked load -> change -> save step).
                bool started;
                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    started = BlowUpEmoteEngine.StartRound(state, config, emote, url);
                    // Paid start that was swapped to a fallback: let the payer pick again,
                    // but only if viewers are allowed to choose emotes at all.
                    if (started && usedFallback && config.EmoteControl == EmoteControlMode.ChatTriggered &&
                        (!string.IsNullOrWhiteSpace(userId) || !string.IsNullOrWhiteSpace(userName)))
                    {
                        BlowUpEmoteEngine.SetPendingPick(state, userId, userName, DateTime.UtcNow);
                        pickPending = true;
                    }
                    if (started) BueStore.SaveState(targetId, state);
                }

                if (!started)
                {
                    outcome = "running";
                    reason = "a round is already running";
                    message = tag + "Not started: a round is already running.";
                    return false;
                }

                if (found.Count > 0) BueStore.MergeImages(targetId, found);

                BueSendState(targetId, true);
                BueAnnounce(config, BlowUpEmoteEngine.MsgStarted(userName, emote));
                outcome = usedFallback ? "fallback" : "started";
                startedEmote = emote;
                reason = usedFallback ? CleanReason(why) : "";
                BueLog.Write("START  5/5 DONE outcome=" + outcome + " emote='" + emote + "' pickPending=" + pickPending);
                message = tag + "Round started with '" + emote + "' (" + source + ")." +
                          (usedFallback ? " Reason: " + why : "");
                return true;
            }
            catch (Exception ex)
            {
                BueLog.Write("START  ERROR " + ex);
                LogError("[BUE] BueStartRound failed: " + ex.Message);
                outcome = "error";
                message = tag + "Error: " + ex.Message;
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // AUTO-END: end the round once the balloon has sat at 0% for AutoEndSeconds.
        //
        // The countdown only runs while the balloon is EMPTY (see BlowUpEmoteEngine.AutoEndAt).
        //
        // WHO CALLS THIS (the server is the only one who may end a round):
        //   - the "Blow Up Emote - Expire Check" action, which the WIDGET runs when
        //     its own countdown reaches zero (and which you can also put on a
        //     Streamer.bot timer as a backstop, e.g. every 15 s)
        //   - the main chat action, before it handles a message
        //   - Start Round, so a timed-out round never blocks the next one
        // It ends the round exactly like Cancel: no pop, no winner, widget hides.
        // Returns true only when it actually ended a round.
        // ─────────────────────────────────────────────────────────────────
        public bool BueExpireIfDue(string targetId, out string message)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";
            message = "";

            try
            {
                BueTargetConfig config = BueStore.LoadConfig(targetId);
                string emote;
                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    if (!BlowUpEmoteEngine.IsAutoEndDue(state, config, DateTime.UtcNow))
                    {
                        double? left = BlowUpEmoteEngine.SecondsUntilAutoEnd(state, config, DateTime.UtcNow);
                        message = !state.Active ? "no round running"
                                : left.HasValue ? "not due yet: " + left.Value.ToString("0") + "s left (fill " + state.FillPercent.ToString("0.#") + "%)"
                                : "no countdown running (balloon is at " + state.FillPercent.ToString("0.#") + "% and " +
                                  (config.DeflatePerSecond > 0 ? "deflating" : "not deflating, so it never empties") + ")";
                        return false;
                    }

                    emote = state.RoundEmote ?? "";
                    BlowUpEmoteEngine.CancelRound(state);
                    BueStore.SaveState(targetId, state);
                }

                BueSendState(targetId, false, true);       // tell the widget: reset + hide
                BueAnnounce(config, BlowUpEmoteEngine.MsgTimedOut(emote));
                message = "Round ended automatically: the balloon for '" + emote + "' sat at 0% for " + config.AutoEndSeconds + "s.";
                BueLog.Write("EXPIRE " + message);
                return true;
            }
            catch (Exception ex)
            {
                BueLog.Write("EXPIRE ERROR " + ex);
                message = "Error: " + ex.Message;
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // ROUND MESSAGES IN CHAT (started / popped / ended).
        // Sent to EVERY platform BUE is enabled for (Settings), because all of those
        // chats are playing. Only while rounds are on, and only if Settings >
        // "Post round messages in chat" is ticked. Replies to ONE viewer (a rejected
        // entry, a refund) are different: the Start action sends those to the viewer's
        // own platform only.
        // Never throws: a chat problem must not break the game.
        // ─────────────────────────────────────────────────────────────────
        private void BueAnnounce(BueTargetConfig config, string text)
        {
            try
            {
                if (config == null || !config.StartHidden || !config.AnnounceInChat) return;
                if (string.IsNullOrWhiteSpace(text)) return;
                foreach (string platform in config.EnabledPlatforms)
                    SendMessageToPlatform(platform.ToLowerInvariant(), text);
                BueLog.Write("CHAT  (all platforms) " + text);
            }
            catch (Exception ex) { BueLog.Write("CHAT  ERROR " + ex.Message); }
        }

        /// <summary>The balloon was popped: say who did it. Called by the main chat action.</summary>
        public void BueAnnouncePop(string targetId, string user, string emote)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(emote)) return;
            BueAnnounce(BueStore.LoadConfig(targetId), BlowUpEmoteEngine.MsgPopped(user, emote));
        }

        // "Refused: 'Pepega' is on the banned list." -> "'Pepega' is on the banned list."
        // so it reads naturally inside a chat sentence.
        private static string CleanReason(string why)
        {
            string r = (why ?? "").Trim();
            if (r.StartsWith("Refused: ", StringComparison.Ordinal)) r = r.Substring("Refused: ".Length);
            // Not-an-emote messages are written for the log; chat gets a plain version.
            if (r.StartsWith("No recognised emote", StringComparison.Ordinal) ||
                r.StartsWith("No real emote found", StringComparison.Ordinal) ||
                r.StartsWith("No emote name found", StringComparison.Ordinal))
                r = "that isn't an emote.";

            // The streamer-only lock message is written for the log (it mentions Settings);
            // chat gets a plain version.
            if (r.StartsWith("Ignored: choosing the emote is locked", StringComparison.Ordinal))
                r = "viewers can't choose the emote right now.";
            return r;
        }

        // ─────────────────────────────────────────────────────────────────
        // CLAIM A PICK  (called by the main chat action for every chat message
        // while somebody is "pending" - see BueState.PendingPick*).
        //
        // A viewer paid (bits, power-up...) with an emote we refused, so the round
        // started with a fallback. If THIS message is from that viewer, inside the
        // time window, and holds an emote that passes the normal rules (real emote,
        // not banned, on the allowed list), the round switches to it.
        //
        // A message that doesn't qualify changes nothing and the viewer keeps waiting
        // until the window ends - so "hello chat" or a banned emote doesn't use up
        // their one chance.
        //
        // Returns true only when the round's emote was switched.
        // ─────────────────────────────────────────────────────────────────
        public bool BueTryClaimPick(
            string targetId, string text, string emotesJson,
            string userId, string broadcasterId, string userName, string broadcasterName,
            out string newEmote, out string message)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";
            newEmote = ""; message = "";

            try
            {
                BueTargetConfig config = BueStore.LoadConfig(targetId);

                // Cheap checks first, under the lock (this runs on every chat message).
                lock (BueStore.SyncRoot)
                {
                    BueState peek = BueStore.LoadState(targetId);
                    if (peek.PendingPickUntilUtc == null) return false;
                    if (DateTime.UtcNow > peek.PendingPickUntilUtc.Value)
                    {
                        BlowUpEmoteEngine.ClearPendingPick(peek);       // window over: tidy up
                        BueStore.SaveState(targetId, peek);
                        BueLog.Write("CLAIM  window expired for '" + userName + "' - fallback emote stays");
                        return false;
                    }
                    if (!BlowUpEmoteEngine.IsPendingPickUser(peek, userId, userName, DateTime.UtcNow)) return false;
                }

                string emote, url, why;
                Dictionary<string, string> found;
                if (!BueResolveRequest(config, text, emotesJson, userId, broadcasterId, userName, broadcasterName,
                                       out emote, out url, out found, out why))
                {
                    message = "still waiting: " + why;
                    BueLog.Write("CLAIM  '" + userName + "' message did not qualify - " + why);
                    return false;
                }

                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    // Re-check inside the same lock that saves (round may have just ended).
                    if (!BlowUpEmoteEngine.IsPendingPickUser(state, userId, userName, DateTime.UtcNow)) return false;

                    state.RoundEmote = emote;
                    state.RoundImageUrl = BlowUpEmoteEngine.IsHttpsUrl(url) ? url : "";
                    BlowUpEmoteEngine.ClearPendingPick(state);
                    BueStore.SaveState(targetId, state);
                }

                if (found.Count > 0) BueStore.MergeImages(targetId, found);
                BueSendState(targetId, false);

                newEmote = emote;
                message = "'" + userName + "' picked '" + emote + "'; the round now uses it.";
                BueLog.Write("CLAIM  " + message);
                return true;
            }
            catch (Exception ex)
            {
                BueLog.Write("CLAIM  ERROR " + ex);
                message = "Error: " + ex.Message;
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // STATUS (debug): writes one block to the Streamer.bot log saying exactly
        // what the saved settings and game state are, then re-sends the state to
        // the widget. Run it when something isn't behaving.
        // ─────────────────────────────────────────────────────────────────
        public void BueStatus(string targetId = "default")
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";
            try
            {
                BueMigrateLegacy(targetId);
                BueTargetConfig c = BueStore.LoadConfig(targetId);
                BueState s;
                lock (BueStore.SyncRoot) { s = BueStore.LoadState(targetId); }

                string dll = "";
                try { dll = typeof(BueStore).Assembly.Location; } catch { }

                LogInfo("[BUE Status] DLL build v" + BueStore.BuildTag + (dll.Length > 0 ? " (" + dll + ")" : "") +
                        "\n  Rounds on (hide between rounds): " + c.StartHidden +
                        "\n  Round running: " + BlowUpEmoteEngine.IsBalloonActive(c, s) + "   fill: " + s.FillPercent.ToString("0.#") + "%" +
                        "\n  Growth: " + (c.RandomGrowth ? "random " + c.GrowthMinPercent + "-" + c.GrowthMaxPercent + "% per entry" : c.GrowthPerUse + "% per entry") +
                        "   deflate: " + (c.DeflatePerSecond > 0 ? c.DeflatePerSecond + "%/s" : "off") +
                        "\n  Default emote (Settings): '" + BlowUpEmoteEngine.CurrentEmote(c) + "'   emote of the running round: '" + (s.RoundEmote ?? "") + "'" +
                        "\n  Who may pick the emote: " + c.EmoteControl + "   allowed list: " + (c.AllowedEmotes.Count == 0 ? "(any)" : string.Join(", ", c.AllowedEmotes)) +
                        "   banned: " + (c.BannedEmotes.Count == 0 ? "(none)" : string.Join(", ", c.BannedEmotes)) +
                        "\n  Auto-end: " + (c.AutoEndEnabled ? "after " + c.AutoEndSeconds + "s at 0% (flash red for the last " + c.AutoEndWarnSeconds + "s)" : "off") +
                        (BlowUpEmoteEngine.SecondsUntilAutoEnd(s, c, DateTime.UtcNow).HasValue
                            ? "   time left: " + BlowUpEmoteEngine.SecondsUntilAutoEnd(s, c, DateTime.UtcNow).Value.ToString("0") + "s" : "") +
                        "\n  Platforms: " + string.Join(", ", c.EnabledPlatforms) +
                        "\n  Pops so far: " + s.TotalPops + "   this round contributors: " + s.RoundContributors.Count +
                        "\n  Config file: " + BueStore.ConfigPath(targetId) +
                        "\n  Emote services: Twitch always on, " + ThirdPartySummary(c.Use7TV, c.UseBTTV, c.UseFFZ) +
                        "\n  BUE log file: " + BueLog.FilePath);
                BueLog.Write("STATUS build " + BueStore.BuildTag + " rounds=" + c.StartHidden + " running=" + s.Active +
                             " default='" + BlowUpEmoteEngine.CurrentEmote(c) + "'");

                BueSendState(targetId, false);
            }
            catch (Exception ex)
            {
                LogError("[BUE] BueStatus failed: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Change WHICH emote the balloon is ("Set Emote" action - any trigger).
        //
        //   Round running  -> changes the RUNNING ROUND's emote (use it to swap
        //                     out an inappropriate or unwanted pick). The fill and
        //                     leaderboard stay as they are.
        //   No round / rounds off -> changes the DEFAULT emote in Settings.
        //
        // Same rules as Start Round (lock, real emote, banned/allowed list).
        // Returns true if the emote was changed; `message` says what happened.
        // ─────────────────────────────────────────────────────────────────
        public bool BueChangeEmote(
            string targetId,
            string text,
            string emotesJson,
            string userId,
            string broadcasterId,
            string userName,
            string broadcasterName,
            out string message)
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";

            try
            {
                BueMigrateLegacy(targetId);

                BueTargetConfig config = BueStore.LoadConfig(targetId);

                string emote, url, why;
                Dictionary<string, string> found;
                if (!BueResolveRequest(config, text, emotesJson, userId, broadcasterId, userName, broadcasterName,
                                       out emote, out url, out found, out why))
                {
                    message = why;
                    return false;
                }

                string where;
                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    if (config.StartHidden && state.Active)
                    {
                        state.RoundEmote = emote;
                        state.RoundImageUrl = BlowUpEmoteEngine.IsHttpsUrl(url) ? url : "";
                        BueStore.SaveState(targetId, state);
                        where = "the running round";
                    }
                    else
                    {
                        BueTargetConfig edit = BueStore.LoadConfigCopy(targetId);
                        BlowUpEmoteEngine.ApplyEmoteSelection(edit, emote, url);
                        BueStore.SaveConfig(targetId, edit);
                        where = config.StartHidden ? "the default emote (no round is running)" : "the balloon";
                    }
                }

                if (found.Count > 0)
                    BueStore.MergeImages(targetId, found);

                BueBroadcastState(targetId);

                BueTargetConfig saved = BueStore.LoadConfig(targetId);
                BueState nowState;
                lock (BueStore.SyncRoot) { nowState = BueStore.LoadState(targetId); }
                string image = BlowUpEmoteEngine.ActiveImage(this, saved, nowState, "");
                message = "Emote for " + where + " set to '" + emote + "'. Picture " +
                          (string.IsNullOrEmpty(image)
                              ? "NOT known yet - it is learned when the emote first appears in chat, or add a link: !bueset " + emote + " https://..."
                              : "found.");
                return true;
            }
            catch (Exception ex)
            {
                LogError("[BUE] BueChangeEmote failed: " + ex.Message);
                message = "Error: " + ex.Message;
                return false;
            }
        }
    }
}
