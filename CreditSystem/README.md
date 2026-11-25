# OSW Credit System

**Folder:** `CreditSystem/` in OSWTools.dll · **Data:** `<Streamer.bot>\OSWData\Credits\` · **Status:** Phase 1 groundwork

When a viewer spends real money — bits, a Super Chat or Super Sticker, Kicks, or a donation — on a Streamer.bot trigger that **fails** or gets **rejected**, they get a credit instead of losing their money. They can re-run the same trigger or spend the credit on a different one. Twitch, YouTube and Kick all work from the same code.

---

## Files

| File | What it does |
|---|---|
| `CreditModels.cs` | Data shapes: credits, redemptions, receipts, journal, config, triggers. Read this first; it's the map. |
| `CreditStore.cs` | Loads/saves the JSON through the existing `OSWData` → `SafeWriter`. Owns the lock that makes parallel actions safe. |
| `CreditValuation.cs` | Reads the payment from any platform's event args and values it in your base currency (reuses `ConvertCurrency`). |
| `CreditLedger.cs` | The main API: issue, check balance, hold → commit/release, void, receipts, housekeeping. |
| `CreditTriggers.cs` | The menu of things credit can buy, the replay engine, and the custom Streamer.bot triggers. |
| `CreditCommands.cs` | A ready-made `!credits` command for viewers and mods on all three platforms. |

### Existing DLL code this reuses

`OSWData` / `SafeWriter` (atomic JSON + .bak), `OSWApiResult<T>` (result type), `ConvertCurrency` / `OSWCurrencies` (exchange rates, formatting, `FromMicros`), `GetFlexibleArg` / `GetFlexibleDecimal` / `GetDonation*` (arg reading), `ProfileHelper.DetectPlatform`, `ApplyTemplate` / `SendMessageToPlatform` (chat), `RunAction`, `GetTwitchUserInfo`, and the dual-channel `Log*` methods. Nothing existing was modified.

---

## How it works

### 1. Issuing a credit

```
Cheer 500 bits → your TTS action → OBS offline → lib.CreditFailedTrigger("tts", "OBS offline")
                                                         │
          reads bits/messageId/userId from args  ◄───────┘
          values it: 500 × $0.01 = $5.00 (locked in)
          saves credit CR-7F3A9C, tells the viewer in THEIR chat, fires "Credit Issued"
```

Safe to call twice for the same event: the platform's `messageId` makes it **idempotent**, so one payment can only ever produce one credit.

### 2. Spending a credit: hold → commit / release

```
!credits use tts hello
   └─ ReserveCredit  → $5 moved into a HOLD (can't be double-spent)
   └─ RunAction("TTS - Speak") with oswCredit.* args set
         ├─ works                         → Commit  (spent)
         └─ calls CreditFailedTrigger     → Release (viewer keeps it)
```

`CreditFailedTrigger` is **replay-aware**. In a normal paid run it issues a credit. In a credit replay it releases the hold instead. The same line of code is correct both ways.

A hold stuck longer than `HoldTimeoutMinutes` (for example, Streamer.bot crashed mid-replay) is released automatically. When in doubt, the viewer keeps the money.

### 3. Payment rules

1. **Same-trigger guarantee.** An untouched credit from the exact trigger it was paid for always covers that trigger, even if you've raised the price or exchange rates moved. This is the "you get what you paid for" promise.
2. **By value.** Otherwise, credits are spent toward any trigger, using credits from the same trigger first, then the soonest to expire, then the oldest. Leftover value stays as credit.

### 4. Identity and multistreaming

A viewer is identified by **platform + user ID**, not by name, because names can change. Credits earned on Kick are spent on Kick. Every chat message goes back to the viewer's own platform. Donations live under their own `donation` platform, keyed by donor name (see Phase 2).

---

## Setup in Streamer.bot

### Step 0: add the folder to the project

In `OSWTools.csproj`, add one line next to the other folder includes (already done in the provided csproj):

```xml
<Compile Include="CreditSystem\**\*.cs" />
```

### Step 1: the `!credits` command (one action for all platforms)

1. **Commands:** create `!credits`. Turn on the **Twitch, YouTube and Kick** sources.
2. **Action** `OSW Credits - Command`, triggered by that command, with an *Execute C# Code* sub-action. In its settings, turn on **Precompile on Application Start** so `Init()` runs at launch.

```csharp
using OSWTools;

public class CPHInline
{
    public void Init()
    {
        // Adds Custom → OSWTools → Credits → Credit Issued / Redeemed / Released
        new OSWLib(CPH, "Credits").RegisterCreditCustomTriggers();
    }

    public bool Execute()
    {
        new OSWLib(CPH, "Credits").HandleCreditCommand();
        return true;
    }
}
```

### Step 2: make a paid action credit-aware

Use a TTS action triggered by a **Twitch Cheer**, a **YouTube Super Chat** and **Kick Kicks Gifted** as the example:

```csharp
using OSWTools;

public class CPHInline
{
    public void Init()
    {
        // Put it on the credit menu. The price is used only the FIRST time;
        // after that, triggers.json is yours to edit.
        new OSWLib(CPH, "TTS").RegisterCreditTrigger(
            "tts", "Text to Speech", "TTS - Speak", 5.00m, requiresInput: true);
    }

    public bool Execute()
    {
        var lib = new OSWLib(CPH, "TTS");

        // Where's the text? A credit replay passes it in oswCredit.input;
        // a normal cheer/super chat has it in "message".
        var replay = lib.GetCreditReplay();
        string text = replay != null ? replay.Input : lib.GetFlexibleArg(new[] { "message", "rawInput" });

        if (!CPH.ObsIsConnected())
        {
            lib.CreditFailedTrigger("tts", "OBS not connected");   // ← the one line
            return true;
        }

        // ... speak `text` ...
        return true;
    }
}
```

### Step 3 (optional): let mods reject after the fact

For things a mod reviews *after* the event (media share, song request), record a receipt at the start of the paid action:

```csharp
new OSWLib(CPH, "Media Share").RecordPaidTrigger("media");
```

Later, a mod types `!credits reject @viewer NSFW link`, and the viewer's most recent un-credited receipt becomes a credit. Receipts are kept for `ReceiptRetentionHours` (48 by default).

### Step 4 (optional): react to credit events without C#

Any action can use **Custom → OSWTools → Credits → Credit Issued** (or Redeemed/Released) as a trigger. Available args: `%oswCreditEvent.user%`, `%oswCreditEvent.platform%`, `%oswCreditEvent.valueText%`, `%oswCreditEvent.paid%`, `%oswCreditEvent.reason%`, `%oswCreditEvent.id%`, `%oswCreditEvent.triggerKey%`. Use these for overlays, sounds or a Discord log.

---

## Chat commands

| Who | Command | Does |
|---|---|---|
| Viewer | `!credits` | Your balance |
| Viewer | `!credits list` | Triggers you can use it on (✓ = affordable) |
| Viewer | `!credits use tts hello` or `!credits tts hello` | Spend credit |
| Mod | `!credits check @name` | Someone's balance |
| Mod | `!credits reject @name [why]` | Credit their last paid trigger |
| Mod | `!credits grant @name 5.00 [why]` | Give credit by hand |
| Mod | `!credits void CR-XXXXXX [why]` | Cancel a credit |

---

## Settings: `OSWData\Credits\config.json`

Created on first run with your PC's regional currency. Edit it while Streamer.bot is closed (or the next save will overwrite your edit).

| Setting | Default | Notes |
|---|---|---|
| `BaseCurrency` | your region | The currency everything is valued and priced in |
| `UsdPerBit` | 0.01 | 100 bits = $1 |
| `UsdPerKick` | 0.01 | **Check this** against Kick's current creator payout |
| `ExpireAfterDays` | 0 | 0 = never expire (recommended for paid credit) |
| `SameTriggerGuarantee` | true | See payment rule 1 |
| `AllowCrossTrigger` | true | false = credit only re-runs its original trigger |
| `HoldTimeoutMinutes` | 10 | Stuck holds are released back to the viewer |
| `AnnounceInChat` | true | Turn off to make the system silent |
| `Msg*` | — | Chat templates. Tokens: `{user} {value} {balance} {trigger} {price} {command} {reason} {id}` |

---

## Using it from your own code

| Call | Returns |
|---|---|
| `lib.CreditFailedTrigger(key, reason)` | `OSWApiResult<OSWCredit>` |
| `lib.CreditRejectedTrigger(key, reason, by)` | same |
| `lib.RecordPaidTrigger(key)` | receipt ID or `""` |
| `lib.CreditRejectedLastReceipt(platform, name, reason, mod)` | `OSWApiResult<OSWCredit>` |
| `lib.GrantCredit(platform, userId, name, value, note, by)` | `OSWApiResult<OSWCredit>` |
| `lib.GetCreditBalance(platform, userId)` | `decimal` |
| `lib.GetOpenCredits(platform, userId)` | `List<OSWCredit>` (copies) |
| `lib.RedeemCreditForTrigger(platform, userId, name, key, input)` | `OSWApiResult<OSWCreditRedemption>` |
| `lib.ReserveCredit` / `CommitCreditRedemption` / `ReleaseCreditRedemption` | manual hold control |
| `lib.GetCreditReplay()` / `lib.IsCreditReplay()` | replay info, or null / bool |
| `lib.RegisterCreditTrigger(...)`, `SetCreditTriggerPrice`, `SetCreditTriggerEnabled` | trigger menu |
| `lib.VoidCredit(id, note, by)` | `OSWApiResult<OSWCredit>` |

Compare `result.StatusCode` against the named constants in `OSWCreditCodes`: `Created`, `AlreadyCredited`, `ReturnedToViewer`, `Redeemed`, `Held`, `InsufficientCredit`, `NotFound`, `Disabled`, `Conflict`, `BadRequest`.

---

## Verify in live Streamer.bot before shipping

The code compiles against the real DLL sources and passed 12 simulated scenarios: idempotency, replay success and failure, the price-rise guarantee, cross-trigger spending, insufficient credit, a Kick mod reject, a non-mod blocked from mod commands, a JPY Super Chat while offline, 50 simultaneous failures, and a missing action. These assumptions about **live** Streamer.bot still need a real test:

1. **Replay args reach the target action.** The docs say `RunAction(name, true)` runs "like a sub-action," so `SetArgument` values carry over. Test: do a credit replay and confirm the target action logs `%oswCredit.isReplay%` = True.
2. **Event arg names.** Twitch Cheer gives `bits` and `messageId`. Kick Kicks Gifted gives `kicks.amount` and `userId`. Check one real event of each in the action history.
3. **Mod flags per platform.** The command checks `isModerator`, `isBroadcaster`, `isOwner`, and Twitch `role ≥ 3`. Test a mod command from YouTube and from Kick.
4. **Kick has no `messageId` on Kicks Gifted**, so dedupe falls back to `runningActionId`. That stops double credits within one run, but not if two separate actions both credit the same Kicks event. Credit from one action per event.

---

## Phase 2 ideas (not built)

- **Account linking.** Let a viewer prove a YouTube and Twitch account are the same person (or claim a donation) so credit can move between them.
- **WinForms settings and ledger viewer** in the OSWTheme style, like the Tiltify UI: edit prices, search viewers, view the journal, grant or void.
- **Overlay widget** for "Credit Issued" alerts, plus a Discord audit-log hook.
- **More payment types** as Streamer.bot adds them (for example, Twitch bits-based Power-ups). Add a branch in `ReadPaymentFromCurrentEvent()` and a value in `OSWPaymentKind`.
- **Timed housekeeping action** for streamers who turn expiry on (housekeeping already runs on its own whenever credits are used).
