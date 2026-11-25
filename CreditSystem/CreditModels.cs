// =============================================================================
// OSWTools — CreditSystem/CreditModels.cs
//
// NEW FILE. The "shapes" of every piece of data the Credit System stores.
//
// WHAT THE CREDIT SYSTEM IS FOR
//   A viewer spends real money (bits, a Super Chat, Kicks, a donation) on a
//   Streamer.bot trigger — and the trigger fails (OBS offline, TTS crashed)
//   or a moderator rejects it (bad media share). Instead of the money just
//   vanishing, the viewer gets a CREDIT they can use to re-run that trigger
//   or spend toward a different one.
//
// HOW THE DATA FITS TOGETHER
//
//   OSWCreditLedger  (one JSON file: OSWData\Credits\ledger.json)
//    ├─ Credits      — money owed to viewers. One per failed/rejected payment.
//    ├─ Redemptions  — a viewer spending credit on a trigger. Starts "Held",
//    │                 then becomes "Committed" (it worked) or "Released"
//    │                 (it failed again → the credit goes back to them).
//    ├─ Receipts     — short-term log of recent paid triggers, so a mod can
//    │                 reject something AFTER the event has already finished.
//    └─ Journal      — append-only audit trail. Every change to money is
//                      written here, so you can always answer "what happened
//                      to this viewer's $5?"
//
//   OSWCreditConfig  (OSWData\Credits\config.json)   — streamer's settings
//   OSWCreditTrigger (OSWData\Credits\triggers.json) — what credit can buy
//
// LEARNING NOTE — why classes with only properties?
//   These are "data models" (sometimes called POCOs: Plain Old CLR Objects).
//   They hold data and nothing else. The LOGIC lives in other files. Keeping
//   them separate means Newtonsoft.Json can turn them into JSON and back
//   without surprises, and you can read this file as a "map" of the system.
//
// LEARNING NOTE — [JsonConverter(typeof(StringEnumConverter))]
//   By default, enums save to JSON as numbers ("Status": 2). That's
//   unreadable when you open the file, and it BREAKS if anyone reorders the
//   enum later. StringEnumConverter saves the name instead ("Status":
//   "Spent"), which is readable and reorder-safe. Same reason the Bridge
//   passes SAS category NAMES instead of numbers.
// =============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace OSWTools
{
    // ─────────────────────────────────────────────────────────────────────────
    // ENUMS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>What kind of real-money payment created a credit.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OSWPaymentKind
    {
        Unknown,
        /// <summary>Twitch Cheer. Native unit = bits.</summary>
        Bits,
        /// <summary>YouTube Super Chat. Native unit = a real currency (any).</summary>
        SuperChat,
        /// <summary>YouTube Super Sticker. Native unit = a real currency (any).</summary>
        SuperSticker,
        /// <summary>Kick "Kicks Gifted". Native unit = kicks.</summary>
        KicksGift,
        /// <summary>Tip/donation service (Streamlabs, StreamElements, Ko-fi...).</summary>
        Donation,
        /// <summary>Granted by the streamer/mod by hand (goodwill, fixing a mistake).</summary>
        Manual
    }

    /// <summary>Why the credit exists.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OSWCreditReason
    {
        /// <summary>The trigger broke (OBS offline, API error, timeout...).</summary>
        Failed,
        /// <summary>A human said no (moderator rejected the request).</summary>
        Rejected,
        /// <summary>Given by hand, not tied to a specific failure.</summary>
        Manual
    }

    /// <summary>Lifecycle of a single credit.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OSWCreditStatus
    {
        /// <summary>Has value left to spend.</summary>
        Open,
        /// <summary>Fully used up.</summary>
        Spent,
        /// <summary>Passed its expiry date with value left (only if expiry is turned on).</summary>
        Expired,
        /// <summary>Cancelled by the streamer/mod (e.g. issued by mistake).</summary>
        Voided
    }

    /// <summary>Lifecycle of a redemption (a viewer spending credit).</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OSWRedemptionStatus
    {
        /// <summary>Credit is set aside while the trigger runs. Not spent yet.</summary>
        Held,
        /// <summary>The trigger worked — the credit is now really spent.</summary>
        Committed,
        /// <summary>The trigger failed again — credit returned to the viewer.</summary>
        Released
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PAYMENT — a normalized description of "what the viewer paid"
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every platform describes money differently: Twitch says "bits: 500",
    /// YouTube says "microAmount: 5000000, currencyCode: USD", Kick says
    /// "kicks.amount: 500". This class is the ONE shape they all get turned
    /// into, so the rest of the system never has to care which platform
    /// the money came from.
    ///
    /// LEARNING NOTE — this is called "normalization": convert many input
    /// formats into one internal format at the edge of your system, then
    /// write all the real logic against that one format.
    /// </summary>
    public class OSWCreditPayment
    {
        /// <summary>"twitch", "youtube", "kick", or "donation". Always lowercase.</summary>
        public string Platform { get; set; } = "";

        /// <summary>
        /// Platform user ID. This — not the username — is the viewer's
        /// identity, because usernames can change but IDs never do.
        /// For donations (no platform account), the lowercased donor name.
        /// </summary>
        public string UserId { get; set; } = "";

        /// <summary>Display name at the time of payment (for messages/UI only).</summary>
        public string UserName { get; set; } = "";

        public OSWPaymentKind Kind { get; set; } = OSWPaymentKind.Unknown;

        /// <summary>Amount in the payment's OWN unit: 500 (bits), 5.00 (USD), 500 (kicks).</summary>
        public decimal NativeAmount { get; set; }

        /// <summary>"bits", "kicks", or an ISO currency code like "USD" / "JPY".</summary>
        public string NativeUnit { get; set; } = "";

        /// <summary>
        /// The platform's ID for the event (e.g. Twitch/YouTube messageId).
        /// Used to make crediting IDEMPOTENT — see CreditLedger.cs.
        /// </summary>
        public string SourceEventId { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CREDIT — money owed to a viewer
    // ─────────────────────────────────────────────────────────────────────────

    public class OSWCredit
    {
        /// <summary>Short readable ID, e.g. "CR-7F3A9C". Mods can type it in chat.</summary>
        public string Id { get; set; } = "";

        // ── Who ─────────────────────────────────────────────────────────────
        public string Platform { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";

        // ── What they originally paid ───────────────────────────────────────
        public OSWPaymentKind Kind { get; set; } = OSWPaymentKind.Unknown;
        public decimal NativeAmount { get; set; }
        public string NativeUnit { get; set; } = "";
        public string SourceEventId { get; set; } = "";

        // ── What it's worth in the streamer's base currency ─────────────────
        //
        // LEARNING NOTE — why store BOTH native and base value?
        //   Native (500 bits) is the truth of what was paid.
        //   Base   ($5.00)    is a common yardstick so a Super Chat in yen can
        //                     be compared to a trigger priced in dollars.
        //   The base value is LOCKED IN when the credit is created, so exchange
        //   rate swings next week can't shrink what the viewer is owed.

        /// <summary>ISO code of the base currency used when this was valued.</summary>
        public string BaseCurrency { get; set; } = "USD";

        /// <summary>Full value at creation, in BaseCurrency.</summary>
        public decimal BaseValue { get; set; }

        /// <summary>Value not yet spent or held. Starts equal to BaseValue.</summary>
        public decimal RemainingValue { get; set; }

        /// <summary>
        /// False if currency conversion failed at creation (e.g. rate service
        /// offline). Housekeeping retries later. Unvalued credits can still be
        /// used for their ORIGINAL trigger (same-trigger guarantee) but don't
        /// count toward the spendable balance until valued.
        /// </summary>
        public bool IsValued { get; set; } = true;

        // ── Why it exists ───────────────────────────────────────────────────
        public OSWCreditReason Reason { get; set; } = OSWCreditReason.Failed;

        /// <summary>The trigger the viewer originally paid for (e.g. "tts").</summary>
        public string OriginTriggerKey { get; set; } = "";

        /// <summary>Free text: "OBS not connected", "rejected by mod_bob: NSFW link"...</summary>
        public string Note { get; set; } = "";

        // ── Lifecycle ───────────────────────────────────────────────────────
        public OSWCreditStatus Status { get; set; } = OSWCreditStatus.Open;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Null = never expires (the default).</summary>
        public DateTime? ExpiresUtc { get; set; }

        /// <summary>
        /// True while the credit has never been touched. Only untouched credits
        /// get the "same-trigger guarantee" (see CreditLedger.ReserveCredit).
        /// </summary>
        [JsonIgnore]
        public bool IsUntouched
        {
            get { return RemainingValue >= BaseValue; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REDEMPTION — a viewer spending credit (a "hold" until we know it worked)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One slice of one credit used by a redemption.</summary>
    public class OSWCreditAllocation
    {
        public string CreditId { get; set; } = "";
        public decimal Value { get; set; }
    }

    public class OSWCreditRedemption
    {
        /// <summary>e.g. "RD-91BC02".</summary>
        public string Id { get; set; } = "";

        public string Platform { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";

        public string TriggerKey { get; set; } = "";

        /// <summary>Trigger price at the moment of redemption, in base currency.</summary>
        public decimal Price { get; set; }

        /// <summary>Which credits paid for this, and how much from each.</summary>
        public List<OSWCreditAllocation> Allocations { get; set; } = new List<OSWCreditAllocation>();

        /// <summary>True if paid by the same-trigger guarantee rather than by value.</summary>
        public bool UsedSameTriggerGuarantee { get; set; }

        public OSWRedemptionStatus Status { get; set; } = OSWRedemptionStatus.Held;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedUtc { get; set; }
        public string Note { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RECEIPT — a recent paid trigger, kept briefly for after-the-fact rejects
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why receipts exist: a moderator usually rejects something AFTER the
    /// cheer event is over — by then the event args (bits, messageId...) are
    /// gone. A paid action records a receipt when it starts, so later a mod
    /// can type "!credits reject @viewer" and we still know what they paid.
    /// </summary>
    public class OSWCreditReceipt
    {
        /// <summary>e.g. "RC-44D1E0".</summary>
        public string Id { get; set; } = "";
        public OSWCreditPayment Payment { get; set; } = new OSWCreditPayment();
        public string TriggerKey { get; set; } = "";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Set once this receipt has been turned into a credit (prevents double-crediting).</summary>
        public string CreditId { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JOURNAL — audit trail
    // ─────────────────────────────────────────────────────────────────────────

    public class OSWCreditJournalEntry
    {
        public DateTime Utc { get; set; } = DateTime.UtcNow;

        /// <summary>"Issued", "Held", "Committed", "Released", "Expired", "Voided", "Valued".</summary>
        public string Action { get; set; } = "";

        public string CreditId { get; set; } = "";
        public string RedemptionId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string UserName { get; set; } = "";
        public decimal Value { get; set; }

        /// <summary>Who did it: "system", a tool name, or a moderator's username.</summary>
        public string By { get; set; } = "system";
        public string Note { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LEDGER — the root object saved to ledger.json
    // ─────────────────────────────────────────────────────────────────────────

    public class OSWCreditLedger
    {
        /// <summary>
        /// Schema version. If a future update changes the file layout, this
        /// number tells the loader which migration to run (same idea as
        /// Data/DataMigration.cs).
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        public List<OSWCredit> Credits { get; set; } = new List<OSWCredit>();
        public List<OSWCreditRedemption> Redemptions { get; set; } = new List<OSWCreditRedemption>();
        public List<OSWCreditReceipt> Receipts { get; set; } = new List<OSWCreditReceipt>();
        public List<OSWCreditJournalEntry> Journal { get; set; } = new List<OSWCreditJournalEntry>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CONFIG — the streamer's settings (config.json)
    // ─────────────────────────────────────────────────────────────────────────

    public class OSWCreditConfig
    {
        /// <summary>Master switch. When false, nothing is credited or redeemed.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The yardstick currency every credit is valued in. Defaults to this
        /// PC's regional currency the first time the config is created.
        /// </summary>
        public string BaseCurrency { get; set; } = "USD";

        /// <summary>
        /// USD value of ONE bit. Twitch pays creators $0.01 per bit, and
        /// streamers price bit triggers on that basis (100 bits = $1), so
        /// 0.01 is the "face value" default.
        /// </summary>
        public decimal UsdPerBit { get; set; } = 0.01m;

        /// <summary>
        /// USD value of ONE Kick. Default 0.01 (100 Kicks = $1). Verify against
        /// Kick's current creator payout and change here if needed.
        /// </summary>
        public decimal UsdPerKick { get; set; } = 0.01m;

        /// <summary>Days until an unused credit expires. 0 = never (recommended).</summary>
        public int ExpireAfterDays { get; set; } = 0;

        /// <summary>
        /// If a redemption is still "Held" after this many minutes, something
        /// crashed mid-replay. Housekeeping RELEASES it back to the viewer —
        /// when in doubt, the viewer keeps their money.
        /// </summary>
        public int HoldTimeoutMinutes { get; set; } = 10;

        /// <summary>How long receipts are kept for after-the-fact rejections.</summary>
        public int ReceiptRetentionHours { get; set; } = 48;

        /// <summary>Max journal entries kept (oldest trimmed). Keeps the file small.</summary>
        public int JournalMaxEntries { get; set; } = 2000;

        /// <summary>
        /// If true, an untouched credit can ALWAYS re-run the exact trigger it
        /// came from — even if the price went up or exchange rates moved.
        /// This is the core "you get what you paid for" promise.
        /// </summary>
        public bool SameTriggerGuarantee { get; set; } = true;

        /// <summary>If true, credit value can be spent on a DIFFERENT trigger.</summary>
        public bool AllowCrossTrigger { get; set; } = true;

        /// <summary>Send chat messages to the viewer when credits change.</summary>
        public bool AnnounceInChat { get; set; } = true;

        /// <summary>The chat command viewers type. Used in message templates.</summary>
        public string CommandName { get; set; } = "!credits";

        // ── Chat templates ──────────────────────────────────────────────────
        // Tokens: {user} {value} {balance} {trigger} {price} {command} {reason} {id}
        public string MsgIssued { get; set; } =
            "@{user} that one didn't go through ({reason}) — you've been credited {value}. Type {command} to use it.";
        public string MsgBalance { get; set; } =
            "@{user} you have {balance} in credit. {command} list to see what you can use it on.";
        public string MsgNoBalance { get; set; } =
            "@{user} you don't have any credit right now.";
        public string MsgRedeemed { get; set; } =
            "@{user} used credit for {trigger}! Remaining: {balance}";
        public string MsgReleased { get; set; } =
            "@{user} {trigger} failed again — your credit is safe. Balance: {balance}";
        public string MsgInsufficient { get; set; } =
            "@{user} {trigger} costs {price} but you have {balance} in credit.";
        public string MsgUnknownTrigger { get; set; } =
            "@{user} I don't know that one. Try {command} list";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TRIGGER — something credit can be spent on (triggers.json)
    // ─────────────────────────────────────────────────────────────────────────

    public class OSWCreditTrigger
    {
        /// <summary>
        /// Short, stable key viewers type and code refers to, e.g. "tts".
        /// Lowercase, no spaces. NEVER rename once credits reference it.
        /// </summary>
        public string Key { get; set; } = "";

        /// <summary>Friendly name for chat: "Text to Speech".</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>The Streamer.bot action to run on a credit replay.</summary>
        public string ActionName { get; set; } = "";

        /// <summary>Price in the config's BaseCurrency.</summary>
        public decimal Price { get; set; }

        /// <summary>Hide from "!credits list" and block redemptions without deleting.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// True if the replayed action needs viewer text (TTS message, media
        /// link). The text after "!credits use tts ..." is passed through.
        /// </summary>
        public bool RequiresInput { get; set; }

        /// <summary>Which tool registered this (for troubleshooting).</summary>
        public string RegisteredBy { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REPLAY — what a re-run action is told about itself
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handed to an action that is being re-run with credit, so it knows
    /// (a) it's a replay, (b) who it's for, and (c) any text the viewer
    /// typed. Get it with lib.GetCreditReplay().
    /// </summary>
    public class OSWCreditReplay
    {
        public string RedemptionId { get; set; } = "";
        public string TriggerKey { get; set; } = "";
        public string Platform { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        /// <summary>Viewer text from "!credits use tts hello world" → "hello world".</summary>
        public string Input { get; set; } = "";
    }
}
