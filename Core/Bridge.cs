// =============================================================================
// PATCH: OSWLib.Bridge.cs — new partial class for OSW cross-tool event routing
//
// Drop this file into the OSWTools.dll project alongside the other OSWLib.*.cs
// partial classes. No code changes elsewhere in the DLL are required — this is
// purely additive.
//
// PURPOSE
//   Provide a centralized bridge between OSW tools and the Achievement System
//   (SAS) — and, eventually, between any pair of OSW tools that need to
//   coordinate. v3.0.1 ships with the SAS-bound path only; future versions can
//   add additional consumers (display tools, leaderboards, Discord webhooks)
//   without changing the bridge API.
//
// ARCHITECTURE
//   ┌─ HTML widget (e.g. GIF Battle overlay) ───────────────────────────┐
//   │  fires a Streamer.bot Custom Code Event via WebSocket             │
//   │  Event names:  OSW.<Tool>.<Verb>   (e.g. OSW.GIFBattle.Win)       │
//   │            or  OSW.AchievementEvent (generic fallback)             │
//   └──────────────┬───────────────────────────────────────────────────┘
//                  ↓
//   ┌─ SB action "OSW Bridge — Receive External Event" ─────────────────┐
//   │  - Reads payload args                                              │
//   │  - Validates sender is a registered OSW integration                │
//   │  - Sets canonical globals (OSW_Bridge_*)                           │
//   │  - Runs SAS action that calls OnExternalAchievementEvent           │
//   └──────────────┬───────────────────────────────────────────────────┘
//                  ↓
//   ┌─ SAS.OnExternalAchievementEvent ──────────────────────────────────┐
//   │  - Re-validates sender (defense in depth)                          │
//   │  - Parses category NAME (reorder-safe) → Category enum             │
//   │  - Applies SAS exclusions, dispatches via ProcessEvent             │
//   └────────────────────────────────────────────────────────────────────┘
//
//   For C# senders (SB inline actions written in C#), the helper
//   Lib.FireAchievementEvent(...) skips the WebSocket and Custom Code Event
//   layers — it sets the canonical globals and runs the bridge action directly.
//   That's the recommended path for any future OSW tool written in C#.
//
// PAYLOAD CONTRACT
//   Field           Type    Required  Notes
//   ─────────────── ─────── ────────  ─────────────────────────────────────
//   sourceTool      string  yes       Registered OSWIntegration code
//                                     (e.g. "GIFB", "CGGC"). Fail-closed.
//   category        string  yes       SAS Category enum NAME (e.g. "GIFs").
//                                     Names are reorder-safe by design.
//   platform        string  yes       "Twitch" / "YouTube" / "Kick" / "Any"
//   user            string  yes       Viewer name (post-relay-extraction)
//   qualifier       string  no        Free-form filter; per-category meaning
//   amount          int     no        triggerAmount; default 1 if 0 or missing
//
// FAIL-CLOSED DESIGN
//   - Unknown sourceTool → drop event, log [Bridge] entry
//   - Missing required field → drop event, log [Bridge] entry
//   - SAS-side validation (category parse, user exclusion) is a second layer
//
// LOG MARKER
//   Bridge-emitted log lines use the [Bridge] prefix to distinguish them from
//   SAS's [Skip]/[Exclude]/[Award] markers. Matches existing OSW conventions.
//
// VERSIONING
//   Initial version: 1.1.0. The bridge contract is additive — new fields can
//   be added to the payload without breaking older senders, but existing
//   field names are permanent.
// =============================================================================

using System;
using System.Collections.Generic;
using Streamer.bot.Plugin.Interface;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ── Bridge constants ────────────────────────────────────────────────
        //
        // Action name that SAS imports into Streamer.bot. The bridge action's
        // C# inline code reads OSW_Bridge_* globals and dispatches to SAS.
        // If a user renames this action they'll break the bridge — but renaming
        // SB actions is uncommon enough that I'm not going to make this
        // configurable. It's a constant by design.
        private const string BridgeActionName = "OSW SAS — Receive External Event";

        // Canonical global-variable names that the bridge sets before running
        // the action. SAS's OnExternalAchievementEvent reads the same names.
        // Centralised here so renaming any of them requires changing exactly
        // one file. They're prefixed OSW_Bridge_ to namespace away from
        // tool-specific globals (OSUP_SAS_*, etc.).
        private const string BridgeArg_SourceTool = "OSW_Bridge_SourceTool";
        private const string BridgeArg_Category   = "OSW_Bridge_Category";
        private const string BridgeArg_Platform   = "OSW_Bridge_Platform";
        private const string BridgeArg_User       = "OSW_Bridge_User";
        private const string BridgeArg_Qualifier  = "OSW_Bridge_Qualifier";
        private const string BridgeArg_Amount     = "OSW_Bridge_Amount";

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Fire an achievement event to SAS. Use this from any OSW tool's
        /// C# code to award a viewer for some external action (winning a
        /// GIF battle, redeeming a custom item, completing a task, etc.).
        ///
        /// The sender must be a registered OSW integration — pass the same
        /// moduleCode you used in DeclareInstalled(). Fail-closed: if the
        /// module isn't registered, the event is dropped with a log entry.
        ///
        /// For HTML/JS senders that can't call this method, fire a Streamer.bot
        /// Custom Code Event named "OSW.AchievementEvent" (or "OSW.&lt;Tool&gt;.&lt;Verb&gt;")
        /// with the same args — the bridge action handles routing.
        /// </summary>
        /// <param name="sourceTool">Registered OSWIntegration code (e.g. "GIFB").</param>
        /// <param name="category">SAS Category enum NAME (e.g. "GIFs"). Reorder-safe.</param>
        /// <param name="platform">"Twitch", "YouTube", "Kick", or "Any".</param>
        /// <param name="user">Viewer who earned the event.</param>
        /// <param name="qualifier">Optional qualifier filter for SAS achievements.</param>
        /// <param name="amount">Trigger amount; default 1.</param>
        /// <returns>True if event was queued. False if it was dropped at the bridge.</returns>
        public bool FireAchievementEvent(
            string sourceTool,
            string category,
            string platform,
            string user,
            string qualifier = "",
            int amount = 1)
        {
            // Validate sender at the bridge boundary. SAS will validate again
            // defensively — defense in depth. Logging at the bridge tells you
            // exactly which sender misbehaved, which is what you want when
            // debugging an integration.
            if (string.IsNullOrWhiteSpace(sourceTool))
            {
                LogInfo("[Bridge] Drop: empty sourceTool.");
                return false;
            }

            if (!IsRegisteredOswModule(sourceTool))
            {
                LogInfo($"[Bridge] Drop: '{sourceTool}' is not a registered OSW integration. " +
                        $"Call Lib.DeclareInstalled before firing events.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(category))
            {
                LogInfo($"[Bridge] Drop from '{sourceTool}': empty category. " +
                        $"Pass a SAS Category enum NAME (e.g. \"GIFs\").");
                return false;
            }

            if (string.IsNullOrWhiteSpace(user))
            {
                LogInfo($"[Bridge] Drop from '{sourceTool}': empty user.");
                return false;
            }

            // Default platform if not specified. We prefer "Twitch" over "Any"
            // because explicit-platform matching is the common case; "Any" is
            // a deliberate broadcast that the sender should request explicitly.
            if (string.IsNullOrWhiteSpace(platform))
                platform = "Twitch";

            // Normalise amount. 0 and negatives have no meaning here; we treat
            // them as "default to 1" rather than rejecting, since most senders
            // will simply omit the field and let the default apply.
            if (amount <= 0)
                amount = 1;

            // Set canonical globals so the bridge action (and SAS) can read them.
            // We use persisted=false because these are per-event scratch values,
            // not durable state — they should be overwritten on every event.
            _CPH.SetGlobalVar(BridgeArg_SourceTool, sourceTool,            false);
            _CPH.SetGlobalVar(BridgeArg_Category,   category,              false);
            _CPH.SetGlobalVar(BridgeArg_Platform,   platform,              false);
            _CPH.SetGlobalVar(BridgeArg_User,       user,                  false);
            _CPH.SetGlobalVar(BridgeArg_Qualifier,  qualifier ?? "",       false);
            _CPH.SetGlobalVar(BridgeArg_Amount,     amount,                false);

            LogDebug($"[Bridge] Fire from '{sourceTool}': category={category}, " +
                     $"platform={platform}, user={user}, qualifier={qualifier}, amount={amount}");

            // Run the SAS-bound bridge action. If it's missing (e.g. user
            // didn't import SAS), RunAction logs a warning and returns false.
            // We surface that to the caller via our own return value so they
            // can decide what to do.
            bool dispatched = RunAction(BridgeActionName);
            if (!dispatched)
                LogInfo($"[Bridge] Drop from '{sourceTool}': bridge action " +
                        $"'{BridgeActionName}' is not present in Streamer.bot. " +
                        $"Is SAS installed and imported?");
            return dispatched;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the given module code has been declared installed
        /// via DeclareInstalled() in the current Streamer.bot session. We use
        /// the same OSUP_&lt;MODULE&gt;_Installed convention that the rest of the
        /// integration registry uses.
        ///
        /// This is a deliberately lightweight check — we only look for the
        /// _Installed flag, not for version matches. Version compatibility is
        /// SAS's concern via its OSUPIntegrations.Rules table.
        /// </summary>
        private bool IsRegisteredOswModule(string moduleCode)
        {
            if (string.IsNullOrWhiteSpace(moduleCode))
                return false;

            // The DLL itself is always "registered" — internal calls from
            // OSWTools shouldn't have to declare themselves. This lets the
            // DLL fire events on its own behalf (e.g. for future internal
            // tools) without an extra DeclareInstalled boilerplate.
            if (string.Equals(moduleCode, "OSW", StringComparison.OrdinalIgnoreCase))
                return true;

            // Reuse the RegistryPrefix convention from the IntegrationRegistry
            // partial class. If that prefix changes, both files need to update —
            // but since both live in the same DLL, the coupling is contained.
            const string registryPrefix = "OSUP_";
            try
            {
                return _CPH.GetGlobalVar<bool>(registryPrefix + moduleCode + "_Installed", true);
            }
            catch
            {
                // Defensive: if the registry read fails for any reason, fail
                // closed. Better to drop a legitimate event than to let an
                // unknown sender through during an SB hiccup.
                return false;
            }
        }
    }
}
