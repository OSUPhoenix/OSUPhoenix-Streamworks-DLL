// =============================================================================
// Bridge.cs — OSWLib partial class for cross-tool event routing
//
// File path: Core/Bridge.cs
// Drops into OSWTools.dll alongside other OSWLib partial class files.
//
// PURPOSE
//   Provide a centralized C# bridge between OSW tools and the Achievement
//   System (SAS). Any tool (current or future) that needs to fire an
//   achievement event calls Lib.FireAchievementEvent(...). The bridge
//   validates the sender, sets canonical globals, and dispatches into
//   SAS via the standard SB action chain.
//
// ARCHITECTURE
//   ┌─ C# sender (router, action, any OSW tool's SB code) ────────────┐
//   │  lib.FireAchievementEvent("GIFB", "GIFs", "Twitch", user,       │
//   │                           "win", 1)                              │
//   └──────────────┬───────────────────────────────────────────────────┘
//                  ↓
//   ┌─ DLL bridge (this file) ─────────────────────────────────────────┐
//   │  - Validates sender is registered (OSUP_<MODULE>_Installed)      │
//   │  - Sets OSW_Bridge_* globals                                     │
//   │  - TriggerEvent("OSW.ExternalAchievementEvent")              │
//   └──────────────┬───────────────────────────────────────────────────┘
//                  ↓
//   ┌─ Achievement System action — OnExternalAchievementEvent ─────────┐
//   │  - Re-validates everything (defense in depth)                    │
//   │  - Reads OSW_Bridge_* globals                                    │
//   │  - Dispatches via SAS's ProcessEvent                             │
//   └──────────────────────────────────────────────────────────────────┘
//
// HTML WIDGETS
//   HTML widgets do NOT speak to SAS or this bridge directly. A widget
//   that needs to fire an achievement event sends a WebSocket message
//   to its OWN widget action in Streamer.bot (e.g. "GIF Display - Router A"),
//   and that C# action calls Lib.FireAchievementEvent like any other
//   C# sender. SAS doesn't need to know about HTML at all.
//
// PAYLOAD CONTRACT (the canonical globals set before dispatch)
//   Field              Type    Required  Notes
//   ────────────────── ─────── ────────  ─────────────────────────────────
//   OSW_Bridge_SourceTool string yes     Registered OSWIntegration code
//   OSW_Bridge_Category   string yes     SAS Category enum NAME (reorder-safe)
//   OSW_Bridge_Platform   string yes     "Twitch" / "YouTube" / "Kick" / "Any"
//   OSW_Bridge_User       string yes     Viewer name (post-relay-extraction)
//   OSW_Bridge_Qualifier  string no      Free-form filter; per-category meaning
//   OSW_Bridge_Amount     int    no      triggerAmount; default 1 if 0 or missing
//
// FAIL-CLOSED DESIGN
//   - Unknown sourceTool → drop event, log [Bridge] entry
//   - Missing required field → drop event, log [Bridge] entry
//   - SAS-side validation runs a second time as defense in depth
//
// LOG MARKER
//   Bridge-emitted log lines use the [Bridge] prefix to distinguish them
//   from SAS's [Skip] / [Exclude] / [Award] markers.
//
// VERSIONING
//   First shipped in DLL 1.0.2. The bridge contract is additive — new
//   payload fields can be added without breaking older senders, but
//   existing field names are permanent.
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
        // SB Custom Code Event name that SAS listens for. The Achievement
        // System action has a sub-action with a Custom Code Event trigger
        // matching this name and method-bound to OnExternalAchievementEvent.
        //
        // CPH.TriggerEvent is the correct dispatch mechanism here (not
        // RunAction) because:
        //   - RunAction would fire ALL sub-actions on the target action,
        //     which would re-run Execute and break things
        //   - TriggerEvent invokes only the specific method-bound
        //     sub-action whose trigger matches the event name
        //
        // The event name is namespaced "OSW." to avoid colliding with
        // any other tool's custom events. Future bridge consumers can
        // share this same event name.
        private const string BridgeCodeEventName = "OSW.ExternalAchievementEvent";

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

            // Dispatch via Custom Code Event. SAS's sub-action with a matching
            // Custom Code Event trigger picks this up and runs its bound
            // OnExternalAchievementEvent method. We pass useArgs=false because
            // we communicate via globals (OSW_Bridge_*), not args — globals
            // survive the SB action boundary; args are scoped per-execution.
            //
            // TriggerEvent has no return value, so we can't surface dispatch
            // failures back to the caller. If SAS isn't installed or doesn't
            // have the trigger configured, the event silently goes nowhere.
            // That's a setup-time problem detectable via the SAS startup log,
            // not a runtime problem worth complicating the return signature for.
            try
            {
                _CPH.TriggerEvent(BridgeCodeEventName, useArgs: false);
                return true;
            }
            catch (Exception ex)
            {
                // Defensive: if TriggerEvent itself throws (e.g. SB is in
                // an unusual state), don't take down the calling tool.
                LogInfo($"[Bridge] Drop from '{sourceTool}': TriggerEvent " +
                        $"threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
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
