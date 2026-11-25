// =============================================================================
// OSWTools — CreditSystem/CreditCommands.cs
//
// NEW FILE (partial class OSWLib). A ready-made "!credits" chat command that
// works on Twitch, YouTube and Kick from ONE Streamer.bot action.
//
// SETUP (one action, one line of code):
//   1. Create a Command "!credits" — enable Twitch, YouTube AND Kick sources.
//   2. Create an action "OSW Credits - Command" triggered by that command.
//   3. Add an Execute C# Code sub-action:
//
//        using OSWTools;
//        public class CPHInline
//        {
//            public void Init()
//            {
//                new OSWLib(CPH, "Credits").RegisterCreditCustomTriggers();
//            }
//            public bool Execute()
//            {
//                new OSWLib(CPH, "Credits").HandleCreditCommand();
//                return true;
//            }
//        }
//
// WHAT VIEWERS CAN TYPE
//   !credits                     → your balance
//   !credits list                → what you can spend it on
//   !credits use <key> [text]    → spend credit (text is for TTS etc.)
//
// WHAT MODS / THE BROADCASTER CAN ALSO TYPE
//   !credits check @user         → someone else's balance
//   !credits reject @user [why]  → credit their most recent paid trigger
//   !credits grant @user <amt> [why] → give credit by hand (base currency)
//   !credits void <CR-id> [why]  → cancel a credit
//
// LEARNING NOTE — why parse rawInput ourselves instead of input0/input1?
//   For "use tts hello there chat", we need everything AFTER the key as one
//   string. Splitting rawInput into at most 3 pieces gives us
//   ["use", "tts", "hello there chat"] in one step.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OSWTools
{
    public partial class OSWLib
    {
        /// <summary>
        /// Handles a "!credits ..." chat command from any platform.
        /// Returns true if the command was understood.
        /// </summary>
        public bool HandleCreditCommand()
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            string platform = GetCurrentPlatform();
            string userId   = GetFlexibleArg(new[] { "userId" });
            string user     = GetFlexibleArg(new[] { "user", "userName" }, "viewer");
            string raw      = GetFlexibleArg(new[] { "rawInput" }, "");

            if (!cfg.Enabled)
            {
                Reply(platform, "@" + user + " credits are turned off right now.");
                return true;
            }
            if (string.IsNullOrWhiteSpace(userId))
            {
                LogWarn("[Credits] Command had no userId arg — is the trigger a chat Command?");
                return false;
            }

            // Split into at most 3 parts: sub-command, first word, everything else.
            string[] parts = raw.Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string sub  = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string arg1 = parts.Length > 1 ? parts[1] : "";
            string rest = parts.Length > 2 ? parts[2] : "";

            switch (sub)
            {
                // ── Viewer commands ────────────────────────────────────────
                case "":
                case "balance":
                case "bal":
                    ShowBalance(platform, userId, user);
                    return true;

                case "list":
                case "menu":
                    ShowMenu(platform, userId, user);
                    return true;

                case "use":
                case "redeem":
                    UseCredit(platform, userId, user, arg1, rest);
                    return true;

                // ── Mod commands ───────────────────────────────────────────
                case "check":
                case "reject":
                case "grant":
                case "void":
                    if (!IsCallerModerator())
                    {
                        Reply(platform, "@" + user + " that one's for mods.");
                        return true;
                    }
                    return HandleModCommand(sub, platform, user, arg1, rest);

                default:
                    // "!credits tts hi" — treat an unknown first word as a trigger key.
                    if (OSWCreditStore.GetTrigger(sub) != null)
                    {
                        string text = (arg1 + " " + rest).Trim();
                        UseCredit(platform, userId, user, sub, text);
                        return true;
                    }
                    Reply(platform, ApplyTemplate(cfg.MsgUnknownTrigger, "user", user, "command", cfg.CommandName));
                    return false;
            }
        }

        // ── Viewer handlers ─────────────────────────────────────────────────

        private void ShowBalance(string platform, string userId, string user)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            decimal balance = GetCreditBalance(platform, userId);

            // Unvalued credits (rate service was down) still count for their
            // own trigger, so mention them rather than say "no credit".
            int pending = GetOpenCredits(platform, userId).Count(c => !c.IsValued);

            if (balance <= 0 && pending == 0)
            {
                Reply(platform, ApplyTemplate(cfg.MsgNoBalance, "user", user, "command", cfg.CommandName));
                return;
            }

            string msg = ApplyTemplate(cfg.MsgBalance,
                "user", user, "balance", FormatCreditValue(balance), "command", cfg.CommandName);
            if (pending > 0)
                msg += " (+" + pending + " credit" + (pending == 1 ? "" : "s") + " still being valued)";
            Reply(platform, msg);
        }

        private void ShowMenu(string platform, string userId, string user)
        {
            decimal balance = GetCreditBalance(platform, userId);
            List<OSWCreditTrigger> triggers = GetCreditTriggers(enabledOnly: true);
            if (triggers.Count == 0)
            {
                Reply(platform, "@" + user + " nothing is set up for credits yet.");
                return;
            }

            // Build "tts $5.00 ✓, sfx $1.00 ✓, media $10.00" and stop before
            // chat's length limit (YouTube is the strictest at 200 chars).
            var sb = new StringBuilder("@" + user + " (" + FormatCreditValue(balance) + "): ");
            int added = 0;
            foreach (OSWCreditTrigger t in triggers)
            {
                string item = t.Key + " " + FormatCreditValue(t.Price) + (t.Price <= balance ? " ✓" : "");
                if (sb.Length + item.Length + 2 > 190) { sb.Append(" …"); break; }
                if (added > 0) sb.Append(", ");
                sb.Append(item);
                added++;
            }
            Reply(platform, sb.ToString());
        }

        private void UseCredit(string platform, string userId, string user, string key, string text)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            if (string.IsNullOrWhiteSpace(key))
            {
                Reply(platform, "@" + user + " usage: " + cfg.CommandName + " use <name> — see " + cfg.CommandName + " list");
                return;
            }

            OSWApiResult<OSWCreditRedemption> r = RedeemCreditForTrigger(platform, userId, user, key, text);
            if (r.Success) return; // RedeemCreditForTrigger already announced it

            switch (r.StatusCode)
            {
                case OSWCreditCodes.InsufficientCredit:
                    OSWCreditTrigger t = OSWCreditStore.GetTrigger(key);
                    Reply(platform, ApplyTemplate(cfg.MsgInsufficient,
                        "user", user,
                        "trigger", t != null ? t.DisplayName : key,
                        "price", FormatCreditValue(t != null ? t.Price : 0m),
                        "balance", FormatCreditValue(GetCreditBalance(platform, userId))));
                    break;
                case OSWCreditCodes.NotFound:
                    Reply(platform, ApplyTemplate(cfg.MsgUnknownTrigger, "user", user, "command", cfg.CommandName));
                    break;
                default:
                    Reply(platform, "@" + user + " " + r.Error);
                    break;
            }
        }

        // ── Mod handlers ────────────────────────────────────────────────────

        private bool HandleModCommand(string sub, string platform, string mod, string target, string rest)
        {
            string who = (target ?? "").Trim().TrimStart('@');

            if (sub == "void")
            {
                var v = VoidCredit(who, rest, mod);
                Reply(platform, v.Success ? "Voided " + v.Data.Id + " (" + v.Data.UserName + ")." : v.Error);
                return v.Success;
            }

            if (who.Length == 0)
            {
                Reply(platform, "@" + mod + " who? e.g. " + OSWCreditStore.GetConfig().CommandName + " " + sub + " @viewer");
                return false;
            }

            if (sub == "reject")
            {
                var r = CreditRejectedLastReceipt(platform, who, rest, mod);
                if (!r.Success) Reply(platform, "@" + mod + " " + r.Error);
                // On success the viewer was already told by IssueCredit.
                return r.Success;
            }

            string targetId = ResolveTargetUserId(platform, who);
            if (targetId.Length == 0)
            {
                Reply(platform, "@" + mod + " I haven't seen " + who + " on " + platform + " yet.");
                return false;
            }

            if (sub == "check")
            {
                Reply(platform, "@" + mod + " " + who + " has " +
                                FormatCreditValue(GetCreditBalance(platform, targetId)) + " in credit.");
                return true;
            }

            // sub == "grant": "<amount> [note...]" — rest holds both.
            string[] amtAndNote = (rest ?? "").Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            decimal amount;
            // Strip a leading currency symbol so "$5" and "5" both work.
            string amtText = amtAndNote.Length > 0 ? amtAndNote[0].TrimStart('$', '€', '£', '¥') : "";
            if (!decimal.TryParse(amtText, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount <= 0)
            {
                Reply(platform, "@" + mod + " usage: grant @viewer 5.00 [reason]");
                return false;
            }
            string note = amtAndNote.Length > 1 ? amtAndNote[1] : "granted by " + mod;
            var g = GrantCredit(platform, targetId, who, amount, note, mod);
            if (!g.Success) Reply(platform, "@" + mod + " " + g.Error);
            return g.Success;
        }

        /// <summary>
        /// Name → user ID. Checks the ledger first (anyone who's ever paid),
        /// then for Twitch asks the API via the existing GetTwitchUserInfo().
        /// YouTube and Kick have no name lookup in Streamer.bot, so for those
        /// the viewer must have paid at least once before.
        /// </summary>
        private string ResolveTargetUserId(string platform, string name)
        {
            string id = FindCreditUserId(platform, name);
            if (id.Length > 0) return id;

            if (string.Equals(platform, "twitch", StringComparison.OrdinalIgnoreCase))
            {
                var info = GetTwitchUserInfo(name);
                if (info != null && !string.IsNullOrWhiteSpace(info.UserId)) return info.UserId;
            }
            return "";
        }

        /// <summary>
        /// True if whoever typed the command is a moderator or the broadcaster.
        /// Checks the arg names each platform uses, plus Twitch's numeric
        /// "role" (1 viewer, 2 VIP, 3 moderator, 4 broadcaster).
        /// </summary>
        private bool IsCallerModerator()
        {
            foreach (string name in new[] { "isModerator", "isBroadcaster", "isOwner" })
            {
                try
                {
                    bool flag;
                    if (_CPH.TryGetArg(name, out flag) && flag) return true;
                }
                catch { }
            }
            try
            {
                int role;
                if (_CPH.TryGetArg("role", out role) && role >= 3) return true;
            }
            catch { }
            return false;
        }

        private void Reply(string platform, string message)
        {
            SendMessageToPlatform(platform, message);
        }
    }
}
