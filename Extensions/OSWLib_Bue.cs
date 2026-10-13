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
                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    wasActive = state.Active;
                    wasFill = state.FillPercent;
                    BlowUpEmoteEngine.CancelRound(state);
                    BueStore.SaveState(targetId, state);
                }

                // Read it back from the file: proves the reset really was saved.
                BueState after;
                lock (BueStore.SyncRoot) { after = BueStore.LoadState(targetId); }

                BueSendState(targetId, false, true);
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

            emote = BlowUpEmoteEngine.PickEmote(this, text, found, fromStreamer);
            if (string.IsNullOrWhiteSpace(emote))
            {
                message = fromStreamer
                    ? "No emote name found in the text."
                    : "No recognised emote in the message from '" + who + "'.";
                return false;
            }

            string reason;
            if (!BlowUpEmoteEngine.IsEmotePickAllowed(config, emote, fromStreamer, out reason))
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
        // or a reward's text box) and lives in the game state - it does NOT change
        // your Settings. The Settings emote is only the DEFAULT, used when a Start
        // carries no emote. A viewer's pick must pass the rules in
        // BueResolveRequest (lock, real emote, banned/allowed).
        //
        // If the pick is refused the round does NOT start (a round quietly using
        // some other emote would waste what the viewer paid).
        // If a round is already running nothing changes (Set Emote changes the
        // emote mid-game; Cancel Round ends it).
        // ─────────────────────────────────────────────────────────────────
        public bool BueStartRound(
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
            string tag = "[v" + BueStore.BuildTag + "] ";

            try
            {
                BueMigrateLegacy(targetId);

                BueTargetConfig config = BueStore.LoadConfig(targetId);
                if (!config.StartHidden)
                {
                    message = tag + "Not started: rounds are switched off (Settings > 'Hide the balloon between rounds'), so the balloon is always on.";
                    return false;
                }

                bool running;
                lock (BueStore.SyncRoot) { running = BueStore.LoadState(targetId).Active; }
                if (running)
                {
                    message = tag + "Not started: a round is already running. (Set Emote changes the emote mid-game; Cancel Round ends it.)";
                    return false;
                }

                // 1) Which emote?
                string emote, url, why;
                Dictionary<string, string> found = new Dictionary<string, string>();
                string source;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!BueResolveRequest(config, text, emotesJson, userId, broadcasterId, userName, broadcasterName,
                                           out emote, out url, out found, out why))
                    {
                        message = tag + "Not started: " + why;
                        return false;
                    }
                    source = "from the trigger";
                }
                else
                {
                    emote = BlowUpEmoteEngine.CurrentEmote(config);
                    url = config.ImageOverrideUrl ?? "";
                    source = "the default from Settings";
                    if (string.IsNullOrWhiteSpace(emote))
                    {
                        message = tag + "Not started: no emote. Put one in the trigger (!blowup Kappa) or set a default emote in Settings.";
                        return false;
                    }
                }

                // 2) Start it (one locked load -> change -> save step).
                bool started;
                lock (BueStore.SyncRoot)
                {
                    BueState state = BueStore.LoadState(targetId);
                    started = BlowUpEmoteEngine.StartRound(state, config, emote, url);
                    if (started) BueStore.SaveState(targetId, state);
                }

                if (!started)
                {
                    message = tag + "Not started: a round is already running.";
                    return false;
                }

                if (found.Count > 0) BueStore.MergeImages(targetId, found);

                BueSendState(targetId, true);
                message = tag + "Round started with '" + emote + "' (" + source + ").";
                return true;
            }
            catch (Exception ex)
            {
                LogError("[BUE] BueStartRound failed: " + ex.Message);
                message = tag + "Error: " + ex.Message;
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
                        "\n  Platforms: " + string.Join(", ", c.EnabledPlatforms) +
                        "\n  Pops so far: " + s.TotalPops + "   this round contributors: " + s.RoundContributors.Count +
                        "\n  Config file: " + BueStore.ConfigPath(targetId));

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
