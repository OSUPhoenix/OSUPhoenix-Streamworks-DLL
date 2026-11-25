// =============================================================================
// OSWTools — CreditSystem/CreditLedger.cs
//
// NEW FILE (partial class OSWLib). The public API your actions call.
//
// ─────────────────────────────────────────────────────────────────────────────
// THE TWO CALLS 95% OF ACTIONS NEED
//
//   // Inside any paid action, where it would otherwise fail silently:
//   if (!obsConnected)
//   {
//       lib.CreditFailedTrigger("tts", "OBS not connected");
//       return true;
//   }
//
//   // In a mod-only command, to reject the viewer's last paid trigger:
//   lib.CreditRejectedLastReceipt("twitch", "SomeViewer", "NSFW link", "mod_bob");
//
// CreditFailedTrigger is "replay-aware": if this run is ALREADY a credit
// replay that failed again, it hands the credit BACK instead of issuing a
// second one. So the same line of code is correct in both situations — your
// action doesn't need to know or care whether it was paid by bits or credit.
//
// ─────────────────────────────────────────────────────────────────────────────
// THE HOLD → COMMIT / RELEASE FLOW  (spending credit)
//
//   viewer: "!credits use tts hi"
//        │
//        ▼
//   ReserveCredit ──► credit value moved into a HOLD (RemainingValue drops)
//        │            The viewer can't double-spend it while the action runs.
//        ▼
//   action runs ─┬─ works ──────────► CommitCreditRedemption  → spent for real
//                └─ calls CreditFailedTrigger ─► ReleaseCreditRedemption
//                                                → value returned to viewer
//
//   LEARNING NOTE: this is the same idea a card terminal uses at a gas pump
//   ("pre-authorization" → "capture" or "void"). Money is reserved first and
//   only truly taken once the thing actually happens.
//
// ─────────────────────────────────────────────────────────────────────────────
// RESULT CODES
//   Every method returns the DLL's existing OSWApiResult<T> (Extensions/OSWHttp.cs).
//   .Success tells you if it worked; .StatusCode tells you WHAT happened, using
//   the named constants in OSWCreditCodes below so scripts stay readable.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace OSWTools
{
    /// <summary>
    /// Named result codes for OSWApiResult.StatusCode from Credit System calls.
    /// They borrow HTTP numbers because OSWApiResult already uses StatusCode
    /// that way elsewhere in the DLL — but always compare against these names.
    /// </summary>
    public static class OSWCreditCodes
    {
        /// <summary>A new credit was issued.</summary>
        public const int Created = 201;
        /// <summary>Credit was spent and the trigger ran successfully.</summary>
        public const int Redeemed = 200;
        /// <summary>This payment was already credited — the existing credit is returned. Not an error.
        /// (208 is HTTP's "Already Reported" — fitting.)</summary>
        public const int AlreadyCredited = 208;
        /// <summary>This run was a credit replay that failed; the held credit went back to the viewer.</summary>
        public const int ReturnedToViewer = 204;
        /// <summary>Redemption is held and ready; run the trigger now.</summary>
        public const int Held = 202;
        /// <summary>Bad or missing input (empty key, negative value...).</summary>
        public const int BadRequest = 400;
        /// <summary>Viewer doesn't have enough credit.</summary>
        public const int InsufficientCredit = 402;
        /// <summary>Credit System is turned off, or trigger is disabled.</summary>
        public const int Disabled = 403;
        /// <summary>Not a paid event / unknown ID / unknown trigger.</summary>
        public const int NotFound = 404;
        /// <summary>Wrong state, e.g. receipt already credited, redemption already resolved.</summary>
        public const int Conflict = 409;
    }

    public partial class OSWLib
    {
        // ═════════════════════════════════════════════════════════════════════
        //  ISSUING CREDIT
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Call when a PAID trigger FAILED. Reads the payment from the current
        /// event and credits the viewer. Safe to call more than once for the
        /// same event (only one credit is ever made).
        ///
        /// If this run is a credit replay, the held credit is returned to the
        /// viewer instead (StatusCode = ReturnedToViewer).
        /// </summary>
        /// <param name="triggerKey">Your trigger's key, e.g. "tts". Should match a registered trigger.</param>
        /// <param name="reason">Short human reason, shown to the viewer: "OBS not connected".</param>
        public OSWApiResult<OSWCredit> CreditFailedTrigger(string triggerKey, string reason)
        {
            return CreditCurrentEvent(triggerKey, OSWCreditReason.Failed, reason, _ToolName);
        }

        /// <summary>
        /// Call when a PAID trigger was REJECTED while the event is still
        /// running (e.g. an automatic filter caught a banned word). For a
        /// moderator rejecting LATER, use CreditRejectedLastReceipt instead.
        /// </summary>
        public OSWApiResult<OSWCredit> CreditRejectedTrigger(string triggerKey, string reason, string rejectedBy = "")
        {
            string by = string.IsNullOrWhiteSpace(rejectedBy) ? _ToolName : rejectedBy;
            return CreditCurrentEvent(triggerKey, OSWCreditReason.Rejected, reason, by);
        }

        /// <summary>Shared logic behind CreditFailedTrigger / CreditRejectedTrigger.</summary>
        private OSWApiResult<OSWCredit> CreditCurrentEvent(string triggerKey, OSWCreditReason reason,
                                                           string note, string by)
        {
            // Replay-aware: is this run already being paid for with credit?
            OSWCreditReplay replay = GetCreditReplay();
            if (replay != null)
            {
                var released = ReleaseCreditRedemption(replay.RedemptionId, note);
                if (!released.Success) return released.As<OSWCredit>();
                return new OSWApiResult<OSWCredit>
                {
                    Success = true, StatusCode = OSWCreditCodes.ReturnedToViewer, Data = null
                };
            }

            OSWCreditPayment payment = ReadPaymentFromCurrentEvent();
            if (payment == null)
            {
                LogWarn("[Credits] " + reason + " on '" + triggerKey + "' but this event has no payment " +
                        "(no bits/microAmount/kicks.amount/donation amount). Nothing to credit.");
                return OSWApiResult<OSWCredit>.Fail("Current event is not a paid event.", OSWCreditCodes.NotFound);
            }

            return IssueCredit(payment, triggerKey, reason, note, by);
        }

        /// <summary>
        /// The core "make a credit" method. Everything else funnels into here.
        /// Call it directly if you build the OSWCreditPayment yourself (e.g.
        /// from a webhook or a custom donation integration).
        /// </summary>
        public OSWApiResult<OSWCredit> IssueCredit(OSWCreditPayment payment, string triggerKey,
                                                   OSWCreditReason reason, string note, string by)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            if (!cfg.Enabled)
                return OSWApiResult<OSWCredit>.Fail("Credit System is disabled.", OSWCreditCodes.Disabled);
            if (payment == null || string.IsNullOrWhiteSpace(payment.Platform) ||
                string.IsNullOrWhiteSpace(payment.UserId) || payment.NativeAmount <= 0)
                return OSWApiResult<OSWCredit>.Fail("Payment is missing platform, user ID or amount.",
                                                     OSWCreditCodes.BadRequest);

            RunCreditHousekeeping();

            // ── Value it BEFORE taking the lock ─────────────────────────────
            // LEARNING NOTE: currency conversion may hit the internet. If we
            // did that inside Mutate(), EVERY other credit action would sit
            // waiting on the network. Rule of thumb: never do slow I/O while
            // holding a lock — prepare outside, then lock only for the change.
            decimal baseValue;
            bool valued = TryValuePayment(payment.NativeUnit, payment.NativeAmount, cfg, out baseValue);
            if (!valued)
            {
                // Provisional value: the viewer paid for THIS trigger, so treat
                // the credit as worth its price until the real rate is known.
                // Housekeeping replaces this with the real value later.
                OSWCreditTrigger t = OSWCreditStore.GetTrigger(triggerKey);
                baseValue = t != null ? t.Price : 0m;
            }

            string key = NormalizeKey(triggerKey);

            OSWApiResult<OSWCredit> result = OSWCreditStore.Mutate(ledger =>
            {
                // ── Idempotency check ───────────────────────────────────────
                // LEARNING NOTE: "idempotent" = doing it twice has the same
                // effect as doing it once. If an action errors and retries, or
                // two failure branches both call us, the viewer still gets ONE
                // credit — never two for one payment.
                if (!string.IsNullOrWhiteSpace(payment.SourceEventId))
                {
                    OSWCredit existing = ledger.Credits.FirstOrDefault(c =>
                        Same(c.Platform, payment.Platform) && c.SourceEventId == payment.SourceEventId);
                    if (existing != null)
                        return new OSWApiResult<OSWCredit>
                        {
                            Success = true, StatusCode = OSWCreditCodes.AlreadyCredited, Data = Clone(existing)
                        };
                }

                var credit = new OSWCredit
                {
                    Id               = OSWCreditStore.NewId("CR", id => ledger.Credits.Any(c => c.Id == id)),
                    Platform         = payment.Platform.ToLowerInvariant(),
                    UserId           = payment.UserId,
                    UserName         = payment.UserName,
                    Kind             = payment.Kind,
                    NativeAmount     = payment.NativeAmount,
                    NativeUnit       = payment.NativeUnit,
                    SourceEventId    = payment.SourceEventId,
                    BaseCurrency     = cfg.BaseCurrency,
                    BaseValue        = baseValue,
                    RemainingValue   = baseValue,
                    IsValued         = valued,
                    Reason           = reason,
                    OriginTriggerKey = key,
                    Note             = note ?? "",
                    CreatedUtc       = DateTime.UtcNow,
                    ExpiresUtc       = cfg.ExpireAfterDays > 0
                                       ? DateTime.UtcNow.AddDays(cfg.ExpireAfterDays)
                                       : (DateTime?)null
                };
                ledger.Credits.Add(credit);

                // If this payment was recorded as a receipt, link them so a
                // mod can't credit the same receipt again later.
                foreach (OSWCreditReceipt r in ledger.Receipts)
                    if (Same(r.Payment.Platform, credit.Platform) &&
                        !string.IsNullOrEmpty(credit.SourceEventId) &&
                        r.Payment.SourceEventId == credit.SourceEventId)
                        r.CreditId = credit.Id;

                OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                {
                    Action = "Issued", CreditId = credit.Id, Platform = credit.Platform,
                    UserName = credit.UserName, Value = credit.BaseValue, By = by ?? "system",
                    Note = reason + ": " + credit.Note +
                           " (" + DescribeNativeAmount(credit.NativeUnit, credit.NativeAmount) + ")" +
                           (valued ? "" : " [provisional value]")
                }, cfg.JournalMaxEntries);

                return new OSWApiResult<OSWCredit>
                {
                    Success = true, StatusCode = OSWCreditCodes.Created, Data = Clone(credit)
                };
            });

            if (result.StatusCode == OSWCreditCodes.Created)
            {
                OSWCredit c = result.Data;
                LogInfo("[Credits] Issued " + c.Id + " to " + c.UserName + " (" + c.Platform + "): " +
                        FormatCreditValue(c.BaseValue) + " for '" + c.OriginTriggerKey + "' — " + c.Note);

                AnnounceToViewer(c.Platform, cfg.MsgIssued,
                    "user", c.UserName,
                    "value", FormatCreditValue(c.BaseValue),
                    "reason", c.Note,
                    "trigger", TriggerDisplayName(c.OriginTriggerKey),
                    "id", c.Id);

                FireCreditEvent(CreditEvt_Issued, c, null);
            }
            else if (result.StatusCode == OSWCreditCodes.AlreadyCredited)
            {
                LogDebug("[Credits] Payment " + payment.SourceEventId + " already credited as " + result.Data.Id);
            }
            return result;
        }

        /// <summary>
        /// Gives a viewer credit by hand (goodwill, fixing a mistake). No
        /// payment event needed. Value is in the base currency.
        ///
        ///   lib.GrantCredit("twitch", userId, "SomeViewer", 5.00m, "Sorry about the outage", "OSUPhoenix");
        /// </summary>
        public OSWApiResult<OSWCredit> GrantCredit(string platform, string userId, string userName,
                                                   decimal baseValue, string note, string grantedBy)
        {
            if (baseValue <= 0)
                return OSWApiResult<OSWCredit>.Fail("Value must be above zero.", OSWCreditCodes.BadRequest);

            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            var payment = new OSWCreditPayment
            {
                Platform      = (platform ?? "").ToLowerInvariant(),
                UserId        = userId,
                UserName      = userName,
                Kind          = OSWPaymentKind.Manual,
                // Already in base currency → TryValuePayment is a no-op conversion.
                NativeAmount  = baseValue,
                NativeUnit    = cfg.BaseCurrency,
                // Unique per grant, so manual grants never "dedupe" each other.
                SourceEventId = "manual:" + Guid.NewGuid().ToString("N")
            };
            return IssueCredit(payment, "", OSWCreditReason.Manual, note, grantedBy);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  RECEIPTS  (for rejecting AFTER the event is over)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Call at the START of a paid action that a moderator might reject
        /// later (media share, song request...). Saves what was paid so it can
        /// be credited after the event's args are gone.
        ///
        /// Returns the receipt ID ("" if this isn't a paid event). Also sets
        /// the arg "oswCredit.receiptId" so later sub-actions can read it.
        /// </summary>
        public string RecordPaidTrigger(string triggerKey)
        {
            if (GetCreditReplay() != null) return ""; // replays were already paid for
            OSWCreditPayment payment = ReadPaymentFromCurrentEvent();
            if (payment == null) return "";

            string id = OSWCreditStore.Mutate(ledger =>
            {
                // Same event recorded twice → reuse the first receipt.
                OSWCreditReceipt dup = ledger.Receipts.FirstOrDefault(r =>
                    !string.IsNullOrEmpty(payment.SourceEventId) &&
                    Same(r.Payment.Platform, payment.Platform) &&
                    r.Payment.SourceEventId == payment.SourceEventId);
                if (dup != null) return dup.Id;

                var receipt = new OSWCreditReceipt
                {
                    Id         = OSWCreditStore.NewId("RC", x => ledger.Receipts.Any(r => r.Id == x)),
                    Payment    = payment,
                    TriggerKey = NormalizeKey(triggerKey),
                    CreatedUtc = DateTime.UtcNow
                };
                ledger.Receipts.Add(receipt);
                return receipt.Id;
            });

            try { _CPH.SetArgument("oswCredit.receiptId", id); } catch { }
            LogDebug("[Credits] Receipt " + id + " recorded for '" + triggerKey + "' from " + payment.UserName);
            return id;
        }

        /// <summary>Turns a specific receipt into a credit.</summary>
        public OSWApiResult<OSWCredit> CreditReceipt(string receiptId, OSWCreditReason reason,
                                                     string note, string by)
        {
            OSWCreditReceipt receipt = OSWCreditStore.Read(ledger =>
                Clone(ledger.Receipts.FirstOrDefault(r => Same(r.Id, receiptId))));

            if (receipt == null)
                return OSWApiResult<OSWCredit>.Fail("Receipt " + receiptId + " not found (it may have aged out).",
                                                     OSWCreditCodes.NotFound);
            if (!string.IsNullOrEmpty(receipt.CreditId))
                return OSWApiResult<OSWCredit>.Fail("Receipt " + receiptId + " was already credited as " +
                                                     receipt.CreditId + ".", OSWCreditCodes.Conflict);

            return IssueCredit(receipt.Payment, receipt.TriggerKey, reason, note, by);
        }

        /// <summary>
        /// Moderator shortcut: credit the most recent un-credited receipt for
        /// this viewer. Matches by user ID or by name (case-insensitive), so a
        /// mod can just type the name they see in chat.
        /// </summary>
        public OSWApiResult<OSWCredit> CreditRejectedLastReceipt(string platform, string userNameOrId,
                                                                 string reason, string moderator)
        {
            string who = (userNameOrId ?? "").Trim().TrimStart('@');
            OSWCreditReceipt latest = OSWCreditStore.Read(ledger => Clone(
                ledger.Receipts
                      .Where(r => string.IsNullOrEmpty(r.CreditId) &&
                                  Same(r.Payment.Platform, platform) &&
                                  (Same(r.Payment.UserId, who) || Same(r.Payment.UserName, who)))
                      .OrderByDescending(r => r.CreatedUtc)
                      .FirstOrDefault()));

            if (latest == null)
                return OSWApiResult<OSWCredit>.Fail("No recent paid trigger found for " + who + " on " + platform + ".",
                                                     OSWCreditCodes.NotFound);

            string note = string.IsNullOrWhiteSpace(reason) ? "rejected" : reason;
            if (!string.IsNullOrWhiteSpace(moderator)) note += " — rejected by " + moderator;
            return CreditReceipt(latest.Id, OSWCreditReason.Rejected, note, moderator);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  READING BALANCES
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Spendable credit for a viewer, in base currency. Excludes value
        /// that's currently held, expired, voided, or not yet valued.
        /// </summary>
        public decimal GetCreditBalance(string platform, string userId)
        {
            RunCreditHousekeeping();
            return OSWCreditStore.Read(ledger => ledger.Credits
                .Where(c => IsSpendable(c) && c.IsValued && Same(c.Platform, platform) && Same(c.UserId, userId))
                .Sum(c => c.RemainingValue));
        }

        /// <summary>A viewer's open credits (copies — safe to keep and inspect).</summary>
        public List<OSWCredit> GetOpenCredits(string platform, string userId)
        {
            return OSWCreditStore.Read(ledger => ledger.Credits
                .Where(c => IsSpendable(c) && Same(c.Platform, platform) && Same(c.UserId, userId))
                .Select(c => Clone(c))
                .ToList());
        }

        /// <summary>
        /// Finds a viewer's user ID from their name, using anything the ledger
        /// has seen (credits and receipts). Handy for mod commands where you
        /// only have "@name". Returns "" if unknown.
        /// </summary>
        public string FindCreditUserId(string platform, string userName)
        {
            string who = (userName ?? "").Trim().TrimStart('@');
            return OSWCreditStore.Read(ledger =>
            {
                OSWCredit c = ledger.Credits.Where(x => Same(x.Platform, platform) && Same(x.UserName, who))
                                            .OrderByDescending(x => x.CreatedUtc).FirstOrDefault();
                if (c != null) return c.UserId;
                OSWCreditReceipt r = ledger.Receipts.Where(x => Same(x.Payment.Platform, platform) &&
                                                                Same(x.Payment.UserName, who))
                                                    .OrderByDescending(x => x.CreatedUtc).FirstOrDefault();
                return r != null ? r.Payment.UserId : "";
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SPENDING CREDIT  (hold → commit / release)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sets credit aside for a trigger. Does NOT run anything — see
        /// RedeemCreditForTrigger (CreditTriggers.cs) for the all-in-one version.
        ///
        /// Payment rules, in order:
        ///   1. SAME-TRIGGER GUARANTEE — an untouched credit from this exact
        ///      trigger covers it in full, whatever the price is today.
        ///   2. VALUE — otherwise, spendable credit is used oldest/soonest-
        ///      expiring first until the price is covered.
        /// </summary>
        public OSWApiResult<OSWCreditRedemption> ReserveCredit(string platform, string userId,
                                                               string userName, string triggerKey)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            if (!cfg.Enabled)
                return OSWApiResult<OSWCreditRedemption>.Fail("Credit System is disabled.", OSWCreditCodes.Disabled);

            OSWCreditTrigger trigger = OSWCreditStore.GetTrigger(triggerKey);
            if (trigger == null)
                return OSWApiResult<OSWCreditRedemption>.Fail("Unknown trigger '" + triggerKey + "'.", OSWCreditCodes.NotFound);
            if (!trigger.Enabled)
                return OSWApiResult<OSWCreditRedemption>.Fail(trigger.DisplayName + " is turned off.", OSWCreditCodes.Disabled);

            RunCreditHousekeeping();
            string key = trigger.Key;
            decimal price = trigger.Price;

            return OSWCreditStore.Mutate(ledger =>
            {
                // Spendable credits for this viewer, in the order we want to use
                // them: same trigger first → soonest expiring → oldest.
                List<OSWCredit> mine = ledger.Credits
                    .Where(c => IsSpendable(c) && Same(c.Platform, platform) && Same(c.UserId, userId))
                    .OrderByDescending(c => Same(c.OriginTriggerKey, key))
                    .ThenBy(c => c.ExpiresUtc ?? DateTime.MaxValue)
                    .ThenBy(c => c.CreatedUtc)
                    .ToList();

                var redemption = new OSWCreditRedemption
                {
                    Id         = OSWCreditStore.NewId("RD", x => ledger.Redemptions.Any(r => r.Id == x)),
                    Platform   = (platform ?? "").ToLowerInvariant(),
                    UserId     = userId,
                    UserName   = userName,
                    TriggerKey = key,
                    Price      = price,
                    CreatedUtc = DateTime.UtcNow
                };

                // ── Rule 1: same-trigger guarantee ──────────────────────────
                OSWCredit guaranteed = cfg.SameTriggerGuarantee
                    ? mine.FirstOrDefault(c => Same(c.OriginTriggerKey, key) && c.IsUntouched && c.RemainingValue > 0)
                    : null;

                if (guaranteed != null)
                {
                    // Take the price, or everything if the credit is worth less
                    // than today's price (price went up / rate moved). Any
                    // surplus stays on the credit for later.
                    // Math.Min does both cases in one line:
                    //   credit $5, price $3 → take $3 (keep $2)
                    //   credit $5, price $8 → take $5 (still covers it!)
                    decimal take = Math.Min(guaranteed.RemainingValue, price);
                    Allocate(redemption, guaranteed, take);
                    redemption.UsedSameTriggerGuarantee = true;
                }
                else
                {
                    // ── Rule 2: by value ────────────────────────────────────
                    IEnumerable<OSWCredit> pool = mine.Where(c => c.IsValued);
                    if (!cfg.AllowCrossTrigger)
                        pool = pool.Where(c => Same(c.OriginTriggerKey, key));
                    List<OSWCredit> usable = pool.ToList();

                    decimal available = usable.Sum(c => c.RemainingValue);
                    if (available < price)
                        return OSWApiResult<OSWCreditRedemption>.Fail(
                            "Not enough credit: need " + price + ", have " + available + ".",
                            OSWCreditCodes.InsufficientCredit);

                    decimal stillNeeded = price;
                    foreach (OSWCredit c in usable)
                    {
                        if (stillNeeded <= 0) break;
                        decimal take = Math.Min(c.RemainingValue, stillNeeded);
                        Allocate(redemption, c, take);
                        stillNeeded -= take;
                    }
                }

                ledger.Redemptions.Add(redemption);
                OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                {
                    Action = "Held", RedemptionId = redemption.Id, Platform = redemption.Platform,
                    UserName = userName, Value = redemption.Allocations.Sum(a => a.Value), By = _ToolName,
                    Note = "for '" + key + "' from " +
                           string.Join(", ", redemption.Allocations.Select(a => a.CreditId)) +
                           (redemption.UsedSameTriggerGuarantee ? " [same-trigger guarantee]" : "")
                }, cfg.JournalMaxEntries);

                return new OSWApiResult<OSWCreditRedemption>
                {
                    Success = true, StatusCode = OSWCreditCodes.Held, Data = Clone(redemption)
                };
            });
        }

        /// <summary>The trigger worked: the held credit is now really spent.</summary>
        public OSWApiResult<OSWCreditRedemption> CommitCreditRedemption(string redemptionId)
        {
            return ResolveRedemption(redemptionId, OSWRedemptionStatus.Committed, "");
        }

        /// <summary>The trigger failed: give the held credit back to the viewer.</summary>
        public OSWApiResult<OSWCreditRedemption> ReleaseCreditRedemption(string redemptionId, string note)
        {
            return ResolveRedemption(redemptionId, OSWRedemptionStatus.Released, note);
        }

        private OSWApiResult<OSWCreditRedemption> ResolveRedemption(string redemptionId,
                                                                    OSWRedemptionStatus outcome, string note)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();

            OSWApiResult<OSWCreditRedemption> result = OSWCreditStore.Mutate(ledger =>
            {
                OSWCreditRedemption rd = ledger.Redemptions.FirstOrDefault(r => Same(r.Id, redemptionId));
                if (rd == null)
                    return OSWApiResult<OSWCreditRedemption>.Fail("Redemption " + redemptionId + " not found.",
                                                                  OSWCreditCodes.NotFound);
                if (rd.Status != OSWRedemptionStatus.Held)
                    // Already resolved (e.g. action called fail AND the command
                    // tried to commit). The first answer wins; not an error.
                    return new OSWApiResult<OSWCreditRedemption>
                    {
                        Success = true, StatusCode = OSWCreditCodes.Conflict, Data = Clone(rd)
                    };

                ApplyResolution(ledger, rd, outcome, note);
                OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                {
                    Action = outcome.ToString(), RedemptionId = rd.Id, Platform = rd.Platform,
                    UserName = rd.UserName, Value = rd.Allocations.Sum(a => a.Value), By = _ToolName, Note = note ?? ""
                }, cfg.JournalMaxEntries);

                return OSWApiResult<OSWCreditRedemption>.Ok(Clone(rd));
            });

            if (result.Success && result.StatusCode != OSWCreditCodes.Conflict)
            {
                OSWCreditRedemption rd = result.Data;
                LogInfo("[Credits] " + rd.Id + " " + outcome + " for " + rd.UserName + " ('" + rd.TriggerKey + "')" +
                        (string.IsNullOrWhiteSpace(note) ? "" : " — " + note));

                string evt = outcome == OSWRedemptionStatus.Committed ? CreditEvt_Redeemed : CreditEvt_Released;
                FireCreditEvent(evt, null, rd);
            }
            return result;
        }

        /// <summary>
        /// The actual bookkeeping for commit/release. Separate from
        /// ResolveRedemption so housekeeping can release stale holds while it
        /// already holds the lock.
        /// </summary>
        private static void ApplyResolution(OSWCreditLedger ledger, OSWCreditRedemption rd,
                                            OSWRedemptionStatus outcome, string note)
        {
            foreach (OSWCreditAllocation a in rd.Allocations)
            {
                OSWCredit c = ledger.Credits.FirstOrDefault(x => x.Id == a.CreditId);
                if (c == null) continue;

                if (outcome == OSWRedemptionStatus.Released)
                {
                    c.RemainingValue += a.Value;               // give it back
                    if (c.Status == OSWCreditStatus.Spent) c.Status = OSWCreditStatus.Open;
                }
                else if (c.RemainingValue <= 0 && c.Status == OSWCreditStatus.Open)
                {
                    // Only mark Spent if no OTHER hold is still using this credit.
                    bool otherHold = ledger.Redemptions.Any(r => r != rd && r.Status == OSWRedemptionStatus.Held &&
                                                                 r.Allocations.Any(x => x.CreditId == c.Id));
                    if (!otherHold) c.Status = OSWCreditStatus.Spent;
                }
            }
            rd.Status      = outcome;
            rd.ResolvedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(note)) rd.Note = note;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ADMIN
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Cancels a credit (e.g. issued by mistake). Its remaining value is removed.</summary>
        public OSWApiResult<OSWCredit> VoidCredit(string creditId, string note, string by)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            return OSWCreditStore.Mutate(ledger =>
            {
                OSWCredit c = ledger.Credits.FirstOrDefault(x => Same(x.Id, creditId));
                if (c == null)
                    return OSWApiResult<OSWCredit>.Fail("Credit " + creditId + " not found.", OSWCreditCodes.NotFound);
                if (ledger.Redemptions.Any(r => r.Status == OSWRedemptionStatus.Held &&
                                                r.Allocations.Any(a => a.CreditId == c.Id)))
                    return OSWApiResult<OSWCredit>.Fail("Credit " + creditId + " is in use right now; try again in a moment.",
                                                         OSWCreditCodes.Conflict);

                decimal removed = c.RemainingValue;
                c.RemainingValue = 0;
                c.Status = OSWCreditStatus.Voided;
                OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                {
                    Action = "Voided", CreditId = c.Id, Platform = c.Platform, UserName = c.UserName,
                    Value = removed, By = by ?? "system", Note = note ?? ""
                }, cfg.JournalMaxEntries);
                return OSWApiResult<OSWCredit>.Ok(Clone(c));
            });
        }

        // ═════════════════════════════════════════════════════════════════════
        //  HOUSEKEEPING
        // ═════════════════════════════════════════════════════════════════════

        private static DateTime _lastHousekeepingUtc = DateTime.MinValue;
        private static readonly object _housekeepingGate = new object();

        /// <summary>
        /// Tidies the ledger. Runs automatically (at most once a minute) from
        /// the other methods, so you never need to call it — but you can, e.g.
        /// from a timed action.
        ///   • Releases holds stuck longer than HoldTimeoutMinutes (viewer keeps the money)
        ///   • Expires credits past ExpiresUtc (only if expiry is turned on)
        ///   • Drops receipts older than ReceiptRetentionHours
        ///   • Retries valuation of credits whose currency conversion failed
        /// </summary>
        public void RunCreditHousekeeping(bool force = false)
        {
            lock (_housekeepingGate)
            {
                if (!force && (DateTime.UtcNow - _lastHousekeepingUtc).TotalSeconds < 60) return;
                _lastHousekeepingUtc = DateTime.UtcNow;
            }

            // Log a one-time warning if the ledger file had to be reset.
            string warn = OSWCreditStore.LastLoadWarning;
            if (!string.IsNullOrEmpty(warn))
            {
                LogError("[Credits] " + warn);
                OSWCreditStore.LastLoadWarning = null;
            }

            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            DateTime now = DateTime.UtcNow;

            int released = 0, expired = 0, pruned = 0;
            List<OSWCredit> needValue = OSWCreditStore.Mutate(ledger =>
            {
                // Stale holds → release
                foreach (OSWCreditRedemption rd in ledger.Redemptions
                         .Where(r => r.Status == OSWRedemptionStatus.Held &&
                                     (now - r.CreatedUtc).TotalMinutes > cfg.HoldTimeoutMinutes).ToList())
                {
                    ApplyResolution(ledger, rd, OSWRedemptionStatus.Released, "hold timed out");
                    OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                    {
                        Action = "Released", RedemptionId = rd.Id, Platform = rd.Platform, UserName = rd.UserName,
                        Value = rd.Allocations.Sum(a => a.Value), Note = "hold timed out"
                    }, cfg.JournalMaxEntries);
                    released++;
                }

                // Expiry
                foreach (OSWCredit c in ledger.Credits.Where(c => c.Status == OSWCreditStatus.Open &&
                                                                  c.ExpiresUtc.HasValue && c.ExpiresUtc < now))
                {
                    OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                    {
                        Action = "Expired", CreditId = c.Id, Platform = c.Platform, UserName = c.UserName,
                        Value = c.RemainingValue
                    }, cfg.JournalMaxEntries);
                    c.Status = OSWCreditStatus.Expired;
                    expired++;
                }

                // Old receipts
                pruned = ledger.Receipts.RemoveAll(r => (now - r.CreatedUtc).TotalHours > cfg.ReceiptRetentionHours);

                // Hand back copies of credits that still need a real value.
                return ledger.Credits.Where(c => !c.IsValued && c.Status == OSWCreditStatus.Open)
                                     .Select(c => Clone(c)).ToList();
            });

            if (released > 0) LogWarn("[Credits] Released " + released + " stuck hold(s) back to viewers.");
            if (expired  > 0) LogInfo("[Credits] Expired " + expired + " credit(s).");
            if (pruned   > 0) LogDebug("[Credits] Pruned " + pruned + " old receipt(s).");

            // Re-value OUTSIDE the lock (network), then apply inside it.
            foreach (OSWCredit pending in needValue)
            {
                decimal value;
                if (!TryValuePayment(pending.NativeUnit, pending.NativeAmount, cfg, out value)) continue;

                OSWCreditStore.Mutate(ledger =>
                {
                    OSWCredit c = ledger.Credits.FirstOrDefault(x => x.Id == pending.Id);
                    if (c == null || c.IsValued) return 0;
                    // Keep whatever has already been spent from the provisional value.
                    decimal spent = c.BaseValue - c.RemainingValue;
                    c.BaseValue      = value;
                    c.RemainingValue = Math.Max(0m, value - spent);
                    c.IsValued       = true;
                    OSWCreditStore.Journal(ledger, new OSWCreditJournalEntry
                    {
                        Action = "Valued", CreditId = c.Id, Platform = c.Platform, UserName = c.UserName,
                        Value = value, Note = "real exchange rate applied"
                    }, cfg.JournalMaxEntries);
                    return 0;
                });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  INTERNAL HELPERS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Open, has value, not expired.</summary>
        private static bool IsSpendable(OSWCredit c)
        {
            return c.Status == OSWCreditStatus.Open && c.RemainingValue > 0 &&
                   (!c.ExpiresUtc.HasValue || c.ExpiresUtc.Value > DateTime.UtcNow);
        }

        private static void Allocate(OSWCreditRedemption rd, OSWCredit c, decimal value)
        {
            c.RemainingValue -= value;
            rd.Allocations.Add(new OSWCreditAllocation { CreditId = c.Id, Value = value });
        }

        /// <summary>Case-insensitive, null-safe string compare.</summary>
        private static bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeKey(string key)
        {
            return (key ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Deep copy via JSON round-trip.
        ///
        /// LEARNING NOTE — why copy? Objects in C# are passed by REFERENCE.
        /// If we handed a script the live OSWCredit from the ledger, and the
        /// script changed .RemainingValue, it would silently change the real
        /// ledger without going through the lock. Copies make the ledger
        /// read-only from the outside. JSON round-trip is the simplest
        /// reliable deep copy for data-only classes like these.
        /// </summary>
        private static T Clone<T>(T obj) where T : class
        {
            if (obj == null) return null;
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(obj));
        }

        /// <summary>
        /// Sends a templated chat message to the viewer's platform, if
        /// announcements are on. Donations have no chat → skipped.
        /// Reuses the existing ApplyTemplate + SendMessageToPlatform (Core/Chat.cs).
        /// </summary>
        private void AnnounceToViewer(string platform, string template, params string[] tokenValuePairs)
        {
            OSWCreditConfig cfg = OSWCreditStore.GetConfig();
            if (!cfg.AnnounceInChat || string.IsNullOrWhiteSpace(template)) return;
            string p = (platform ?? "").ToLowerInvariant();
            if (p != "twitch" && p != "youtube" && p != "kick") return;

            var pairs = new List<string>(tokenValuePairs ?? new string[0]) { "command", cfg.CommandName };
            SendMessageToPlatform(p, ApplyTemplate(template, pairs.ToArray()));
        }
    }
}
