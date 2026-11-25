// =============================================================================
// OSWTools — CreditSystem/CreditTriggers.cs
//
// NEW FILE (partial class OSWLib). Three jobs:
//
//   1. TRIGGER REGISTRY — the menu of things credit can buy.
//        lib.RegisterCreditTrigger("tts", "Text to Speech", "TTS - Speak", 5.00m, requiresInput: true);
//
//   2. REPLAY — spend credit and actually run the action.
//        lib.RedeemCreditForTrigger(platform, userId, userName, "tts", "hello chat");
//
//   3. CUSTOM TRIGGERS — so any Streamer.bot action can react to credit events
//      (alerts, overlays, Discord logs) without touching C#.
//        Triggers menu → Custom → OSWTools → Credits → "Credit Issued" etc.
//
// ─────────────────────────────────────────────────────────────────────────────
// HOW A REPLAY TELLS THE ACTION "YOU'RE BEING PAID BY CREDIT"
//
//   Streamer.bot docs: RunAction(name, runImmediately: true) runs the target
//   "as part of the current action, like a sub-action". That means arguments
//   we set with CPH.SetArgument() BEFORE calling it are visible inside it.
//   So we set:
//
//     oswCredit.isReplay       true
//     oswCredit.redemptionId   RD-91BC02
//     oswCredit.triggerKey     tts
//     oswCredit.platform       twitch / youtube / kick
//     oswCredit.userId         viewer's platform ID
//     oswCredit.user           viewer's display name
//     oswCredit.input          text after "!credits use tts ..."
//
//   …and the action reads them with lib.GetCreditReplay(). Plain sub-actions
//   (no C#) can use them too: %oswCredit.user%, %oswCredit.input%.
//
// "CODE PROPOSES, STREAMER DECIDES"
//   Tools call RegisterCreditTrigger from their Init() every startup. If the
//   trigger already exists, we update the technical bits (action name,
//   display name) but KEEP the streamer's price and on/off choice, so a tool
//   update never silently overrides what the streamer set.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ── Custom event names (internal C# names) ──────────────────────────
        // Display names in SB's menu are set in RegisterCreditCustomTriggers().
        private const string CreditEvt_Issued   = "osw_credit_issued";
        private const string CreditEvt_Redeemed = "osw_credit_redeemed";
        private const string CreditEvt_Released = "osw_credit_released";

        // ── Replay arg names (one place, so renaming is a one-line change) ──
        private const string ReplayArg_IsReplay     = "oswCredit.isReplay";
        private const string ReplayArg_RedemptionId = "oswCredit.redemptionId";
        private const string ReplayArg_TriggerKey   = "oswCredit.triggerKey";
        private const string ReplayArg_Platform     = "oswCredit.platform";
        private const string ReplayArg_UserId       = "oswCredit.userId";
        private const string ReplayArg_User         = "oswCredit.user";
        private const string ReplayArg_Input        = "oswCredit.input";

        // ═════════════════════════════════════════════════════════════════════
        //  1. TRIGGER REGISTRY
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Adds a trigger to the credit menu (or refreshes it). Safe to call on
        /// every startup from a tool's Init().
        /// </summary>
        /// <param name="key">Short stable key: "tts". Lowercase, no spaces. Never rename later.</param>
        /// <param name="displayName">What viewers see: "Text to Speech".</param>
        /// <param name="actionName">Exact Streamer.bot action name to run on replay.</param>
        /// <param name="price">Price in the config BaseCurrency. Used only when first created
        ///   (or when overwritePrice is true) — after that the streamer owns it.</param>
        /// <param name="requiresInput">True if the action needs viewer text (TTS line, media link).</param>
        /// <param name="overwritePrice">Force <paramref name="price"/> even if the trigger already exists.</param>
        public bool RegisterCreditTrigger(string key, string displayName, string actionName, decimal price,
                                          bool requiresInput = false, bool overwritePrice = false)
        {
            string k = NormalizeKey(key);
            if (k.Length == 0 || k.Contains(" "))
            {
                LogError("[Credits] RegisterCreditTrigger: key must be non-empty with no spaces (got '" + key + "').");
                return false;
            }
            if (string.IsNullOrWhiteSpace(actionName))
            {
                LogError("[Credits] RegisterCreditTrigger('" + k + "'): actionName is required.");
                return false;
            }
            if (price < 0) price = 0;

            OSWCreditTrigger existing = OSWCreditStore.GetTrigger(k);
            var t = new OSWCreditTrigger
            {
                Key           = k,
                DisplayName   = string.IsNullOrWhiteSpace(displayName) ? k : displayName.Trim(),
                ActionName    = actionName.Trim(),
                Price         = (existing != null && !overwritePrice) ? existing.Price : price,
                Enabled       = existing == null || existing.Enabled,
                RequiresInput = requiresInput,
                RegisteredBy  = _ToolName
            };
            OSWCreditStore.UpsertTrigger(t);

            LogDebug("[Credits] Trigger '" + k + "' " + (existing == null ? "registered" : "refreshed") +
                     " → action '" + t.ActionName + "', price " + FormatCreditValue(t.Price));
            return true;
        }

        /// <summary>Every trigger, optionally only the enabled ones, cheapest first.</summary>
        public List<OSWCreditTrigger> GetCreditTriggers(bool enabledOnly = true)
        {
            return OSWCreditStore.GetTriggers()
                                 .Where(t => !enabledOnly || t.Enabled)
                                 .OrderBy(t => t.Price).ThenBy(t => t.DisplayName)
                                 .ToList();
        }

        /// <summary>Change a trigger's price (base currency).</summary>
        public bool SetCreditTriggerPrice(string key, decimal price)
        {
            OSWCreditTrigger t = OSWCreditStore.GetTrigger(key);
            if (t == null || price < 0) return false;
            t.Price = price;
            OSWCreditStore.UpsertTrigger(t);
            return true;
        }

        /// <summary>Turn a trigger on/off for credit redemptions without deleting it.</summary>
        public bool SetCreditTriggerEnabled(string key, bool enabled)
        {
            OSWCreditTrigger t = OSWCreditStore.GetTrigger(key);
            if (t == null) return false;
            t.Enabled = enabled;
            OSWCreditStore.UpsertTrigger(t);
            return true;
        }

        /// <summary>Removes a trigger. Existing credits from it keep their value.</summary>
        public bool RemoveCreditTrigger(string key)
        {
            return OSWCreditStore.RemoveTrigger(NormalizeKey(key));
        }

        private string TriggerDisplayName(string key)
        {
            OSWCreditTrigger t = OSWCreditStore.GetTrigger(key);
            return t != null ? t.DisplayName : (string.IsNullOrWhiteSpace(key) ? "credit" : key);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  2. REPLAY
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// All-in-one: hold the viewer's credit, run the trigger's action, then
        /// commit (it worked) or leave it released (the action called
        /// CreditFailedTrigger). Announces the result in the viewer's chat.
        ///
        /// Result codes:
        ///   Success + Redeemed        → ran and committed (credit spent)
        ///   Success + ReturnedToViewer→ ran, failed again, credit returned
        ///   Fail    + InsufficientCredit / NotFound / Disabled → nothing ran
        /// </summary>
        public OSWApiResult<OSWCreditRedemption> RedeemCreditForTrigger(string platform, string userId,
                                                                       string userName, string triggerKey,
                                                                       string input = "")
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            OSWCreditTrigger trigger = OSWCreditStore.GetTrigger(triggerKey);

            if (trigger != null && trigger.RequiresInput && string.IsNullOrWhiteSpace(input))
                return OSWApiResult<OSWCreditRedemption>.Fail(
                    trigger.DisplayName + " needs text: " + cfg.CommandName + " use " + trigger.Key + " <text>",
                    OSWCreditCodes.BadRequest);

            // ── Step 1: hold ────────────────────────────────────────────────
            OSWApiResult<OSWCreditRedemption> hold = ReserveCredit(platform, userId, userName, triggerKey);
            if (!hold.Success) return hold;
            string rdId = hold.Data.Id;

            // ── Step 2: tell the action it's a replay, then run it ──────────
            SetReplayArgs(true, rdId, trigger.Key, platform, userId, userName, input);
            bool found;
            try
            {
                found = RunAction(trigger.ActionName, true);
            }
            finally
            {
                // LEARNING NOTE — try/finally: the finally block runs even if
                // RunAction throws. We MUST clear the replay args, or any later
                // sub-action in this same command action would wrongly think
                // it's also a replay.
                SetReplayArgs(false, "", "", "", "", "", "");
            }

            if (!found)
            {
                ReleaseCreditRedemption(rdId, "action '" + trigger.ActionName + "' not found");
                LogError("[Credits] Trigger '" + trigger.Key + "' points at action '" + trigger.ActionName +
                         "', which doesn't exist. Credit returned to " + userName + ".");
                return OSWApiResult<OSWCreditRedemption>.Fail("Action not found.", OSWCreditCodes.NotFound);
            }

            // ── Step 3: did the action report a failure? ────────────────────
            // If it called CreditFailedTrigger, the redemption is already
            // Released. If it's still Held, the action finished without
            // complaining → commit.
            OSWRedemptionStatus status = OSWCreditStore.Read(ledger =>
            {
                OSWCreditRedemption r = ledger.Redemptions.FirstOrDefault(x => x.Id == rdId);
                return r != null ? r.Status : OSWRedemptionStatus.Released;
            });

            decimal balance = GetCreditBalance(platform, userId);
            if (status == OSWRedemptionStatus.Held)
            {
                OSWApiResult<OSWCreditRedemption> done = CommitCreditRedemption(rdId);
                AnnounceToViewer(platform, cfg.MsgRedeemed,
                    "user", userName, "trigger", trigger.DisplayName,
                    "price", FormatCreditValue(trigger.Price), "balance", FormatCreditValue(balance));
                done.StatusCode = OSWCreditCodes.Redeemed;
                return done;
            }

            AnnounceToViewer(platform, cfg.MsgReleased,
                "user", userName, "trigger", trigger.DisplayName, "balance", FormatCreditValue(balance));
            return new OSWApiResult<OSWCreditRedemption>
            {
                Success = true, StatusCode = OSWCreditCodes.ReturnedToViewer, Data = hold.Data
            };
        }

        /// <summary>
        /// Inside an action: returns replay info if this run is being paid for
        /// with credit, otherwise null.
        ///
        ///   var replay = lib.GetCreditReplay();
        ///   string text = replay != null ? replay.Input : args["rawInput"].ToString();
        /// </summary>
        public OSWCreditReplay GetCreditReplay()
        {
            bool isReplay = false;
            try { _CPH.TryGetArg(ReplayArg_IsReplay, out isReplay); } catch { }
            string rdId = GetFlexibleArg(new[] { ReplayArg_RedemptionId });
            if (!isReplay || string.IsNullOrWhiteSpace(rdId)) return null;

            return new OSWCreditReplay
            {
                RedemptionId = rdId,
                TriggerKey   = GetFlexibleArg(new[] { ReplayArg_TriggerKey }),
                Platform     = GetFlexibleArg(new[] { ReplayArg_Platform }),
                UserId       = GetFlexibleArg(new[] { ReplayArg_UserId }),
                UserName     = GetFlexibleArg(new[] { ReplayArg_User }),
                Input        = GetFlexibleArg(new[] { ReplayArg_Input })
            };
        }

        /// <summary>Shortcut: true if this run is paid for with credit.</summary>
        public bool IsCreditReplay()
        {
            return GetCreditReplay() != null;
        }

        private void SetReplayArgs(bool isReplay, string rdId, string key, string platform,
                                   string userId, string user, string input)
        {
            try
            {
                _CPH.SetArgument(ReplayArg_IsReplay,     isReplay);
                _CPH.SetArgument(ReplayArg_RedemptionId, rdId ?? "");
                _CPH.SetArgument(ReplayArg_TriggerKey,   key ?? "");
                _CPH.SetArgument(ReplayArg_Platform,     platform ?? "");
                _CPH.SetArgument(ReplayArg_UserId,       userId ?? "");
                _CPH.SetArgument(ReplayArg_User,         user ?? "");
                _CPH.SetArgument(ReplayArg_Input,        input ?? "");
            }
            catch (Exception ex)
            {
                LogError("[Credits] Couldn't set replay args: " + ex.Message);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  3. CUSTOM TRIGGERS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Adds "Credit Issued / Redeemed / Released" to Streamer.bot's trigger
        /// menu under Custom → OSWTools → Credits. Call ONCE from Init() of
        /// your credit command action (with "Precompile on Application Start").
        /// Same pattern as TiltifyRegisterTriggers().
        /// </summary>
        public bool RegisterCreditCustomTriggers()
        {
            string[] categories = { "OSWTools", "Credits" };
            try
            {
                bool ok = _CPH.RegisterCustomTrigger("Credit Issued",   CreditEvt_Issued,   categories);
                ok &= _CPH.RegisterCustomTrigger("Credit Redeemed", CreditEvt_Redeemed, categories);
                ok &= _CPH.RegisterCustomTrigger("Credit Released", CreditEvt_Released, categories);
                return ok;
            }
            catch (Exception ex)
            {
                LogError("[Credits] RegisterCustomTrigger failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Fires one of the custom triggers with event details as args.
        ///
        /// These use a SEPARATE prefix ("oswCreditEvent.") from the replay
        /// args ("oswCredit.") so firing an event from inside a replay can't
        /// overwrite the replay's own args.
        ///
        /// Args available to listening actions:
        ///   oswCreditEvent.type        issued / redeemed / released
        ///   oswCreditEvent.platform    twitch / youtube / kick / donation
        ///   oswCreditEvent.user        display name
        ///   oswCreditEvent.userId
        ///   oswCreditEvent.id          CR-xxxxxx or RD-xxxxxx
        ///   oswCreditEvent.triggerKey
        ///   oswCreditEvent.value       number, base currency
        ///   oswCreditEvent.valueText   "$5.00"
        ///   oswCreditEvent.paid        "500 bits" (issued only)
        ///   oswCreditEvent.reason      note / reason text
        /// </summary>
        private void FireCreditEvent(string eventName, OSWCredit credit, OSWCreditRedemption redemption)
        {
            try
            {
                string type = eventName == CreditEvt_Issued ? "issued"
                            : eventName == CreditEvt_Redeemed ? "redeemed" : "released";
                decimal value = credit != null ? credit.BaseValue
                              : redemption != null ? redemption.Allocations.Sum(a => a.Value) : 0m;

                _CPH.SetArgument("oswCreditEvent.type",       type);
                _CPH.SetArgument("oswCreditEvent.platform",   credit != null ? credit.Platform   : redemption.Platform);
                _CPH.SetArgument("oswCreditEvent.user",       credit != null ? credit.UserName   : redemption.UserName);
                _CPH.SetArgument("oswCreditEvent.userId",     credit != null ? credit.UserId     : redemption.UserId);
                _CPH.SetArgument("oswCreditEvent.id",         credit != null ? credit.Id         : redemption.Id);
                _CPH.SetArgument("oswCreditEvent.triggerKey", credit != null ? credit.OriginTriggerKey : redemption.TriggerKey);
                _CPH.SetArgument("oswCreditEvent.value",      value);
                _CPH.SetArgument("oswCreditEvent.valueText",  FormatCreditValue(value));
                _CPH.SetArgument("oswCreditEvent.paid",       credit != null
                                                               ? DescribeNativeAmount(credit.NativeUnit, credit.NativeAmount) : "");
                _CPH.SetArgument("oswCreditEvent.reason",     credit != null ? credit.Note : (redemption.Note ?? ""));

                // useArgs: true → the listening action receives the args above.
                _CPH.TriggerCodeEvent(eventName, true);
            }
            catch (Exception ex)
            {
                // Never let an alert problem break the money bookkeeping,
                // which has already been saved by this point.
                LogWarn("[Credits] Couldn't fire '" + eventName + "': " + ex.Message);
            }
        }
    }
}
