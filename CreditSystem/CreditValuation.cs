// =============================================================================
// OSWTools — CreditSystem/CreditValuation.cs
//
// NEW FILE (partial class OSWLib). Two jobs:
//
//   1. READ the payment out of the current Streamer.bot event, whatever the
//      platform, into one OSWCreditPayment.          → ReadPaymentFromCurrentEvent()
//
//   2. VALUE that payment in the streamer's base currency.   → ValuePayment()
//
// ─────────────────────────────────────────────────────────────────────────────
// EVENT ARGS THIS READS (from docs.streamer.bot trigger pages)
//
//   Twitch  Cheer           bits, messageId, userId, user
//   YouTube Super Chat      microAmount, currencyCode, messageId, userId, user
//   YouTube Super Sticker   same as Super Chat + stickerId
//   Kick    Kicks Gifted    kicks.amount, userId, user
//   Donations (any service) via the EXISTING DonationExtensions helpers:
//                           GetDonationAmount / GetDonationCurrency / GetDonorName
//
//   LEARNING NOTE — why microAmount instead of amount on YouTube?
//     YouTube's "amount" arg is display text like "$1.00" or "¥500" — great
//     for showing, awful for math. "microAmount" is the exact number in
//     millionths (1000000 = 1.00), so we divide by 1,000,000 and get a
//     precise decimal with no text parsing.
//
// REUSED FROM THE DLL
//   GetFlexibleArg / GetFlexibleDecimal  (Extensions/DonationExtensions.cs)
//   GetDonationAmount / Currency / DonorName (Extensions/DonationExtensions.cs)
//   ConvertCurrency + OSWCurrencies      (Extensions/CurrencyConversionExtensions.cs)
//   ProfileHelper.DetectPlatform         (Utilities/ProfileHelper.cs)
// =============================================================================

using System;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ═════════════════════════════════════════════════════════════════════
        //  1. READ
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Looks at the current event's args and describes the payment.
        /// Returns null if this event isn't a recognised paid event.
        ///
        /// Order matters: we check the most specific signals first. "bits" only
        /// exists on cheers, "microAmount" only on YouTube paid messages, etc.
        /// Donations go last because their arg names ("amount", "userName")
        /// are generic and could appear on other events too.
        /// </summary>
        public OSWCreditPayment ReadPaymentFromCurrentEvent()
        {
            // ── Twitch Cheer ────────────────────────────────────────────────
            decimal bits = GetFlexibleDecimal(new[] { "bits" }, 0m);
            if (bits > 0)
            {
                return new OSWCreditPayment
                {
                    Platform      = "twitch",
                    UserId        = GetFlexibleArg(new[] { "userId" }),
                    UserName      = GetFlexibleArg(new[] { "user", "userName" }, "Anonymous"),
                    Kind          = OSWPaymentKind.Bits,
                    NativeAmount  = bits,
                    NativeUnit    = "bits",
                    SourceEventId = GetEventIdForDedupe()
                };
            }

            // ── YouTube Super Chat / Super Sticker ──────────────────────────
            decimal micros = GetFlexibleDecimal(new[] { "microAmount" }, 0m);
            if (micros > 0)
            {
                bool isSticker = !string.IsNullOrWhiteSpace(GetFlexibleArg(new[] { "stickerId" }));
                return new OSWCreditPayment
                {
                    Platform      = "youtube",
                    UserId        = GetFlexibleArg(new[] { "userId" }),
                    UserName      = GetFlexibleArg(new[] { "user", "userName" }, "Anonymous"),
                    Kind          = isSticker ? OSWPaymentKind.SuperSticker : OSWPaymentKind.SuperChat,
                    // OSWCurrencies.FromMicros already exists for exactly this.
                    NativeAmount  = OSWCurrencies.FromMicros((long)micros),
                    NativeUnit    = GetFlexibleArg(new[] { "currencyCode" }, "USD").ToUpperInvariant(),
                    SourceEventId = GetEventIdForDedupe()
                };
            }

            // ── Kick Kicks Gifted ───────────────────────────────────────────
            decimal kicks = GetFlexibleDecimal(new[] { "kicks.amount" }, 0m);
            if (kicks > 0)
            {
                return new OSWCreditPayment
                {
                    Platform      = "kick",
                    UserId        = GetFlexibleArg(new[] { "userId" }),
                    UserName      = GetFlexibleArg(new[] { "user", "userName" }, "Anonymous"),
                    Kind          = OSWPaymentKind.KicksGift,
                    NativeAmount  = kicks,
                    NativeUnit    = "kicks",
                    SourceEventId = GetEventIdForDedupe()
                };
            }

            // ── Donation services (reuses DonationExtensions) ───────────────
            decimal donation = GetDonationAmount();
            if (donation > 0)
            {
                string donor = GetDonorName();
                return new OSWCreditPayment
                {
                    // Donations aren't tied to a chat account, so they live in
                    // their own "donation" platform, keyed by lowercase name.
                    // (Linking a donor to a Twitch/YouTube/Kick account is a
                    // planned Phase 2 feature — see README.)
                    Platform      = "donation",
                    UserId        = donor.Trim().ToLowerInvariant(),
                    UserName      = donor,
                    Kind          = OSWPaymentKind.Donation,
                    NativeAmount  = donation,
                    NativeUnit    = GetDonationCurrency(),
                    // Passing null as the fallback makes GetFlexibleArg return
                    // null (not "") when nothing is found, so ?? can kick in.
                    SourceEventId = GetFlexibleArg(new[] { "donationId", "tipId", "fw.id", "messageId" }, null)
                                    ?? GetEventIdForDedupe()
                };
            }

            return null;
        }

        /// <summary>
        /// The ID we use to make sure ONE payment can only ever produce ONE credit.
        ///
        /// 1st choice: "messageId" — the platform's own ID for the cheer/super chat.
        /// 2nd choice: "runningActionId" — Streamer.bot's ID for this particular
        ///             run of the action. Not as strong (it can't catch the same
        ///             event running two different actions), but it does stop
        ///             one action from crediting twice if two failure branches
        ///             both call CreditFailedTrigger.
        /// </summary>
        private string GetEventIdForDedupe()
        {
            string id = GetFlexibleArg(new[] { "messageId" });
            if (!string.IsNullOrWhiteSpace(id)) return id;

            id = GetFlexibleArg(new[] { "runningActionId" });
            if (!string.IsNullOrWhiteSpace(id)) return "run:" + id;

            return "";
        }

        /// <summary>
        /// Which platform the CURRENT event/viewer is on: "twitch", "youtube" or "kick".
        /// Thin wrapper over the existing Utilities.ProfileHelper.DetectPlatform.
        /// </summary>
        public string GetCurrentPlatform()
        {
            return Utilities.ProfileHelper.DetectPlatform(name => GetFlexibleArg(new[] { name }, null));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  2. VALUE
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a payment to the base currency.
        /// Returns true + value on success; false if a currency conversion was
        /// needed and the rate service couldn't be reached.
        ///
        /// Step 1: native unit → a real currency
        ///           bits  × UsdPerBit  → USD
        ///           kicks × UsdPerKick → USD
        ///           anything else is already a currency
        /// Step 2: that currency → BaseCurrency via the existing ConvertCurrency()
        ///         (no network call when they already match)
        /// Step 3: round to the base currency's normal decimals (0 for JPY, 2 for USD)
        /// </summary>
        internal bool TryValuePayment(string nativeUnit, decimal nativeAmount,
                                      OSWCreditConfig cfg, out decimal baseValue)
        {
            baseValue = 0m;

            // Step 1
            string currency;
            decimal amount;
            switch ((nativeUnit ?? "").Trim().ToLowerInvariant())
            {
                case "bits":
                    currency = "USD";
                    amount   = nativeAmount * cfg.UsdPerBit;
                    break;
                case "kicks":
                    currency = "USD";
                    amount   = nativeAmount * cfg.UsdPerKick;
                    break;
                default:
                    currency = (nativeUnit ?? "").Trim().ToUpperInvariant();
                    if (currency.Length == 0) currency = cfg.BaseCurrency;
                    amount   = nativeAmount;
                    break;
            }

            // Step 2
            OSWApiResult<decimal> converted = ConvertCurrency(amount, currency, cfg.BaseCurrency);
            if (!converted.Success)
            {
                LogWarn("[Credits] Couldn't convert " + amount + " " + currency + " → " +
                        cfg.BaseCurrency + ": " + converted.Error + " (will retry later)");
                return false;
            }

            // Step 3
            OSWCurrencyInfo info = OSWCurrencies.Get(cfg.BaseCurrency);
            int digits = info != null ? info.DecimalDigits : 2;
            // MidpointRounding.AwayFromZero = normal "school" rounding (2.5 → 3).
            // .NET's default is "banker's rounding" (2.5 → 2), which would
            // occasionally round a viewer's credit DOWN. We round in their favor.
            baseValue = Math.Round(converted.Data, digits, MidpointRounding.AwayFromZero);
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  FORMATTING (for chat messages)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>"$5.00" in the configured base currency. Uses OSWCurrencies.Format.</summary>
        public string FormatCreditValue(decimal value)
        {
            return OSWCurrencies.Format(value, OSWCreditStore.GetConfig().BaseCurrency);
        }

        /// <summary>
        /// Describes what was originally paid, e.g. "500 bits", "¥1,000",
        /// "250 Kicks". Shown so viewers recognise their own payment.
        /// </summary>
        public string DescribeNativeAmount(string nativeUnit, decimal nativeAmount)
        {
            string unit = (nativeUnit ?? "").Trim().ToLowerInvariant();
            if (unit == "bits")  return nativeAmount.ToString("0") + " bits";
            if (unit == "kicks") return nativeAmount.ToString("0") + " Kicks";
            return OSWCurrencies.Format(nativeAmount, nativeUnit);
        }
    }
}
