// =============================================================================
// OSWTools — Versioning/ProductRegistry.cs
//
// Public registry API on OSWLib (partial class).
//
// THIS IS WHAT TOOLS CALL. Everything user-facing — lookups, update checks,
// per-product tier read/write — lives here. The fetch + cache mechanics are in
// ProductRegistryClient.cs.
//
// RELATIONSHIP TO EXISTING SYSTEMS:
//
//   OSWVersion.cs ─────► tells us where the registry lives (URL list).
//   ProductRegistryClient ──► fetches + caches the manifest JSON.
//   ProductRegistry (THIS FILE) ──► high-level methods on OSWLib for tools.
//   UpdateChecker.cs ──► UNCHANGED for now. Still hits GitHub for the DLL.
//                       Later we can rewrite CheckAsync() to call
//                       CheckProductUpdate("OSW") instead — purely additive
//                       migration.
//   IntegrationRegistry.cs ──► UNCHANGED. Still tracks LOCAL install state
//                       via OSUP_<Code>_Installed globals. The product
//                       registry tracks WHAT EXISTS IN THE WILD. Two
//                       different questions, both useful.
//
// USAGE (from a tool's Execute()):
//
//   var lib = new OSWLib(CPH, "Streamer Achievement System");
//   lib.Register("3.2.0", minDllVersion: "1.1.0");
//
//   // Optional one-time tier write (e.g. from a settings dropdown)
//   lib.SetProductTier("SAS", "creator");
//
//   // Check for updates — synchronous wrapper for ease of CPH use
//   var result = lib.CheckProductUpdate("SAS", "3.2.0");
//   if (result.UpdateAvailable)
//       CPH.LogInfo("SAS update available: " + result.LatestVersion);
//
// =============================================================================

using System;
using System.Collections.Generic;
using OSWTools.Versioning;   // ProductManifest, ProductInfo, UpdateCheckResult, ProductRegistryClient

namespace OSWTools
{
    public partial class OSWLib
    {
        // ── Global key conventions ───────────────────────────────────────────
        // Per-product entitlement tier lives in OSUP_<Code>_Tier so it sits
        // alongside the existing IntegrationRegistry globals (_Installed, _Version).
        private const string TierGlobalSuffix = "_Tier";

        // ─────────────────────────────────────────────────────────────────────
        // FETCH — refresh the manifest (sync wrappers around the async client,
        // since CPH actions are synchronous-friendly).
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetches the manifest (using the cache if fresh). Returns null on
        /// total failure — always check before dereferencing.
        ///
        /// This is the synchronous wrapper. Use it from CPH actions.
        /// </summary>
        public ProductManifest FetchProductRegistry()
        {
            try
            {
                RegistryFetchResult result =
                    ProductRegistryClient.FetchAsync(
                        OSWVersion.ProductRegistryUrls,
                        msg => LogDebug(msg)
                    ).GetAwaiter().GetResult();

                if (!result.Succeeded)
                {
                    LogWarn("[Registry] " + (result.ErrorDetail ?? "Unknown fetch failure."));
                    return null;
                }
                if (result.FromCache)
                    LogDebug("[Registry] Using cached manifest.");
                else
                    LogDebug("[Registry] Fetched fresh manifest from " + result.SourceUrl);

                return result.Manifest;
            }
            catch (Exception ex)
            {
                LogWarn("[Registry] FetchProductRegistry exception: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Forces the next FetchProductRegistry call to hit the network.
        /// Useful behind a "Refresh" button in a settings form.
        /// </summary>
        public void InvalidateProductRegistryCache()
        {
            ProductRegistryClient.InvalidateCache();
            LogDebug("[Registry] Cache invalidated; next fetch will hit the network.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // LOOKUP — find one product in the manifest
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the ProductInfo row for the given code, or null if absent.
        /// Lookup is case-insensitive on Code.
        /// </summary>
        public ProductInfo GetProductInfo(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            ProductManifest manifest = FetchProductRegistry();
            if (manifest == null || manifest.Products == null) return null;

            // Linear scan is fine — registry will be at most a few dozen rows.
            foreach (ProductInfo p in manifest.Products)
            {
                if (p != null && !string.IsNullOrWhiteSpace(p.Code)
                    && string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // UPDATE CHECK — the headline feature
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Compares the caller's currently-installed version against the registry's
        /// current_version for that product. Returns an UpdateCheckResult shaped
        /// identically to the one UpdatePromptForm already accepts.
        ///
        /// Returns a result with CheckSucceeded = false if the product can't be
        /// found or the registry can't be reached. UpdateAvailable is only ever
        /// true when both versions parse cleanly and registry > installed.
        ///
        /// NOTE ON TIER: per your spec, updates do NOT gate on tier. Free and
        /// paid users get prompted alike. The tier field is informational.
        /// </summary>
        public UpdateCheckResult CheckProductUpdate(string code, string installedVersion)
        {
            UpdateCheckResult r = new UpdateCheckResult
            {
                InstalledVersion = installedVersion ?? "",
                CheckSucceeded   = false
            };

            ProductInfo product = GetProductInfo(code);
            if (product == null)
            {
                r.ErrorMessage = "Product '" + code + "' not found in registry.";
                LogDebug("[Registry] " + r.ErrorMessage);
                return r;
            }

            r.LatestVersion = product.CurrentVersion ?? "";
            r.ReleaseNotes  = product.Notes ?? "";

            // Use the same version-comparison rules the existing code already uses.
            // Both versions must parse as System.Version; otherwise we can't compare.
            System.Version installed;
            System.Version latest;
            string installedRaw = (installedVersion ?? "").TrimStart('v', 'V').Trim();
            string latestRaw    = (product.CurrentVersion ?? "").TrimStart('v', 'V').Trim();

            if (!System.Version.TryParse(installedRaw, out installed)
                || !System.Version.TryParse(latestRaw, out latest))
            {
                r.ErrorMessage = "Could not parse version strings ('"
                                 + installedVersion + "' vs '" + product.CurrentVersion + "').";
                LogDebug("[Registry] " + r.ErrorMessage);
                return r;
            }

            r.CheckSucceeded  = true;
            r.UpdateAvailable = latest > installed;

            if (r.UpdateAvailable)
                LogInfo("[Registry] " + product.Name + " update available: "
                        + installedVersion + " -> " + product.CurrentVersion);
            else
                LogDebug("[Registry] " + product.Name + " is up to date (v"
                         + installedVersion + ").");

            return r;
        }

        // ─────────────────────────────────────────────────────────────────────
        // TIER — per-product entitlement (informational; doesn't gate updates)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the user's entitlement tier for one product (e.g. "free", "creator").
        /// Returns "" if not set. Convention: stored in OSUP_&lt;Code&gt;_Tier global.
        /// </summary>
        public string GetProductTier(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            try
            {
                string key = "OSUP_" + code + TierGlobalSuffix;
                return _CPH.GetGlobalVar<string>(key, true) ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Writes the user's entitlement tier for one product. Persisted global.
        /// Pass empty string to clear. Tier values are free-text but conventionally:
        ///   "free", "ea" (early access), "creator", "patreon", etc.
        /// </summary>
        public void SetProductTier(string code, string tier)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try
            {
                string key = "OSUP_" + code + TierGlobalSuffix;
                _CPH.SetGlobalVar(key, tier ?? "", true);
                LogDebug("[Registry] Set tier for " + code + " = '" + (tier ?? "") + "'");
            }
            catch (Exception ex)
            {
                LogWarn("[Registry] SetProductTier(" + code + ") failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns the registry's full list of tiers offered for one product,
        /// already split on commas and trimmed. Empty if the field is blank
        /// or the product is unknown.
        /// </summary>
        public List<string> GetProductAvailableTiers(string code)
        {
            List<string> result = new List<string>();
            ProductInfo p = GetProductInfo(code);
            if (p == null || string.IsNullOrWhiteSpace(p.Tier)) return result;

            string[] parts = p.Tier.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (!string.IsNullOrEmpty(t)) result.Add(t);
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // CONVENIENCE — small helpers for common UI questions
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>True if the product's Status column is set to a deprecated-like value.</summary>
        public bool IsProductDeprecated(string code)
        {
            ProductInfo p = GetProductInfo(code);
            if (p == null || string.IsNullOrWhiteSpace(p.Status)) return false;
            string s = p.Status.Trim().ToLowerInvariant();
            return s == "deprecated" || s == "retired" || s == "eol" || s == "end-of-life";
        }

        /// <summary>
        /// Returns the download URL for a product, or "" if blank.
        /// May be a direct download link OR a storefront / landing page —
        /// the prompt UI just opens it in the default browser.
        /// </summary>
        public string GetProductDownloadUrl(string code)
        {
            ProductInfo p = GetProductInfo(code);
            return (p != null && !string.IsNullOrWhiteSpace(p.DownloadUrl))
                ? p.DownloadUrl : "";
        }

        /// <summary>
        /// Parses the "Depends On" column for one product and returns the list
        /// of module codes. Empty list if the product has no dependencies or
        /// isn't in the registry.
        /// </summary>
        public List<string> GetProductDependencies(string code)
        {
            List<string> result = new List<string>();
            ProductInfo p = GetProductInfo(code);
            if (p == null || string.IsNullOrWhiteSpace(p.DependsOn)) return result;

            string[] parts = p.DependsOn.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string d = parts[i].Trim();
                if (!string.IsNullOrEmpty(d)) result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Returns true if every dependency declared in the registry for this
        /// product is currently installed (per the IntegrationRegistry globals).
        /// Useful for "ready to use?" status panels.
        /// </summary>
        public bool AreProductDependenciesSatisfied(string code)
        {
            List<string> deps = GetProductDependencies(code);
            for (int i = 0; i < deps.Count; i++)
            {
                if (!IsModuleInstalled(deps[i])) return false;
            }
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // MASTER UPDATE CHECK (Phase 2 — May 2026)
        //
        // The headline feature: a single call that walks every product in the
        // registry, checks which ones the user has installed (via the
        // OSUP_<Code>_Installed globals), and returns a list of those that
        // are out of date.
        //
        // WHY THIS API SHAPE:
        //   - Returns a typed list (not a dialog call) so the caller can
        //     decide what to do with the results — show a dialog, log them,
        //     write them to a settings panel, whatever.
        //   - Skips products whose Status starts with "pre-DLL" or "retired".
        //     Pre-DLL widgets don't integrate with the DLL by definition,
        //     so they can't be detected at runtime. Retired products are
        //     historical and shouldn't push updates.
        //   - Each outdated entry records WHY it's outdated. Three possible
        //     reasons today:
        //         "widget"     — installed widget version < sheet version
        //         "dll"        — installed DLL version < sheet's MinDllVersion
        //         "widget+dll" — both
        //     This lets the dialog show appropriately-distinct messages.
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// One entry in the result of <see cref="CheckAllInstalledProducts"/>.
        /// Represents a product that is installed locally but out of date
        /// according to the master registry.
        /// </summary>
        public class OutdatedProduct
        {
            /// <summary>Product code (e.g. "GIFB", "SAS"). Matches the sheet's Product Code column.</summary>
            public string Code { get; set; }

            /// <summary>Human-readable name from the sheet (e.g. "GIF Display and Battler").</summary>
            public string DisplayName { get; set; }

            /// <summary>What the user currently has installed (from the OSUP_&lt;Code&gt;_Version global).</summary>
            public string InstalledVersion { get; set; }

            /// <summary>The latest version listed in the master sheet.</summary>
            public string LatestVersion { get; set; }

            /// <summary>Minimum DLL version required by the latest widget version. Empty if none.</summary>
            public string RequiredDllVersion { get; set; }

            /// <summary>The DLL version currently installed (OSWVersion.Current at check time).</summary>
            public string CurrentDllVersion { get; set; }

            /// <summary>URL the dialog opens when the user clicks the link. From the sheet's "Product webpage" column.</summary>
            public string DownloadUrl { get; set; }

            /// <summary>
            /// Why this product is flagged. One of: "widget", "dll", "widget+dll".
            /// "widget" means installed version &lt; sheet version.
            /// "dll" means installed DLL &lt; sheet's MinDllVersion (widget can't run on this DLL).
            /// "widget+dll" means both — widget needs updating AND so does the DLL.
            /// </summary>
            public string Reason { get; set; }
        }

        /// <summary>
        /// Walks the master registry, checks every product for "is it installed
        /// and out of date?", and returns the list of those needing attention.
        ///
        /// HOW IT DETECTS "INSTALLED":
        ///   The bridge's IntegrationRegistry sets OSUP_&lt;Code&gt;_Installed = true
        ///   when a tool calls Lib.DeclareInstalled(code, version). We invert
        ///   the lookup — for each product code from the sheet, ask whether
        ///   that flag is set. This works without needing an enumerable list
        ///   of installed modules.
        ///
        /// HOW IT DETECTS "OUT OF DATE":
        ///   1. Skip if Status starts with "pre-DLL" or "retired".
        ///   2. Compare installed version (OSUP_&lt;Code&gt;_Version) vs sheet's
        ///      Current Version. If lower → flagged as widget-outdated.
        ///   3. Compare OSWVersion.Current vs sheet's Min DLL Version for this
        ///      product. If DLL is lower → flagged as DLL-outdated. A widget
        ///      can be DLL-outdated even when its own version is current
        ///      (e.g. user has widget v1.0.0, sheet has v1.0.0, but the
        ///      widget now needs DLL v1.0.5 to function correctly).
        ///   4. Both can apply simultaneously, in which case Reason = "widget+dll".
        ///
        /// RETURNS:
        ///   - null if the manifest couldn't be fetched (network down, sheet
        ///     unreachable, parse failure). Caller should not show a dialog.
        ///   - Empty list if everything's up to date. Caller stays silent.
        ///   - Non-empty list if there are updates available.
        /// </summary>
        public List<OutdatedProduct> CheckAllInstalledProducts()
        {
            ProductManifest manifest = FetchProductRegistry();
            if (manifest == null || manifest.Products == null)
            {
                LogWarn("[Registry] Master check skipped — manifest unavailable.");
                return null;
            }

            var outdated = new List<OutdatedProduct>();
            string currentDll = OSWVersion.Current ?? "";

            foreach (ProductInfo p in manifest.Products)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Code)) continue;

                // ── Skip non-eligible statuses ──────────────────────────────
                // pre-DLL: doesn't integrate with the DLL, can't be detected.
                // retired: historical row, shouldn't push updates.
                // Everything else (shipped, beta — pre-public, future "shipped"):
                // check it.
                string status = (p.Status ?? "").Trim().ToLowerInvariant();
                if (status.StartsWith("pre-dll") || status.StartsWith("retired"))
                    continue;

                // ── Is this product actually installed locally? ─────────────
                if (!IsModuleInstalled(p.Code)) continue;

                string installedVer = GetModuleVersion(p.Code) ?? "";
                string sheetVer     = (p.CurrentVersion ?? "").Trim();
                string minDll       = (p.MinDllVersion  ?? "").Trim();

                // ── Compare widget version: installed vs sheet ──────────────
                // Fail-soft: empty/unparseable values short-circuit to
                // "not outdated" so we don't false-positive on a malformed
                // sheet cell. The diagnostic log line makes that visible.
                bool widgetNeedsUpdate = false;
                if (TryCompareVersions(installedVer, sheetVer, out int widgetCmp, out string widgetParseError))
                    widgetNeedsUpdate = widgetCmp < 0;
                else if (!string.IsNullOrEmpty(widgetParseError))
                    LogDebug("[Registry] " + p.Code + ": widget version compare skipped — " + widgetParseError);

                // ── Compare DLL version: current vs widget's MinDllVersion ──
                // Same fail-soft pattern. If the sheet's MinDllVersion is empty
                // or malformed, treat as "no DLL requirement."
                bool dllNeedsUpdate = false;
                if (!string.IsNullOrEmpty(minDll))
                {
                    if (TryCompareVersions(currentDll, minDll, out int dllCmp, out string dllParseError))
                        dllNeedsUpdate = dllCmp < 0;
                    else if (!string.IsNullOrEmpty(dllParseError))
                        LogDebug("[Registry] " + p.Code + ": DLL version compare skipped — " + dllParseError);
                }

                if (!widgetNeedsUpdate && !dllNeedsUpdate) continue;

                // ── Build the outdated entry ────────────────────────────────
                string reason;
                if (widgetNeedsUpdate && dllNeedsUpdate) reason = "widget+dll";
                else if (widgetNeedsUpdate)              reason = "widget";
                else                                      reason = "dll";

                outdated.Add(new OutdatedProduct
                {
                    Code               = p.Code,
                    DisplayName        = string.IsNullOrWhiteSpace(p.Name) ? p.Code : p.Name,
                    InstalledVersion   = installedVer,
                    LatestVersion      = sheetVer,
                    RequiredDllVersion = minDll,
                    CurrentDllVersion  = currentDll,
                    DownloadUrl        = (p.DownloadUrl ?? "").Trim(),
                    Reason             = reason
                });

                LogInfo("[Registry] Outdated: " + p.Code
                    + " (" + reason + ") installed=" + installedVer
                    + " latest=" + sheetVer
                    + (string.IsNullOrEmpty(minDll) ? "" : " minDll=" + minDll));
            }

            return outdated;
        }
        // ─────────────────────────────────────────────────────────────────────
        // Internal helper — version compare with graceful failure
        //
        // We use System.Version because it's the same comparator the existing
        // UpdateChecker uses. Returns true if BOTH versions parsed cleanly and
        // cmp is set to -1/0/+1 (left < right, equal, greater). On any parse
        // failure, returns false and writes a reason to parseError.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryCompareVersions(string left, string right,
            out int cmp, out string parseError)
        {
            cmp = 0;
            parseError = null;

            if (string.IsNullOrWhiteSpace(left))  { parseError = "left version was empty";  return false; }
            if (string.IsNullOrWhiteSpace(right)) { parseError = "right version was empty"; return false; }

            string leftRaw  = left.TrimStart('v', 'V').Trim();
            string rightRaw = right.TrimStart('v', 'V').Trim();

            System.Version lv, rv;
            if (!System.Version.TryParse(leftRaw, out lv))
            {
                parseError = "left version '" + left + "' did not parse as System.Version";
                return false;
            }
            if (!System.Version.TryParse(rightRaw, out rv))
            {
                parseError = "right version '" + right + "' did not parse as System.Version";
                return false;
            }

            cmp = lv.CompareTo(rv);
            return true;
        }
    }
}
