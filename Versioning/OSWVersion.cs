// =============================================================================
// OSWTools — Versioning/OSWVersion.cs
//
// Single source of truth for:
//   1. The current DLL version (used by Register() and the update prompt)
//   2. The GitHub repo coordinates (used by UpdateChecker for self-update)
//   3. The product registry URL list (used by ProductRegistryClient)
//
// CHANGED in this revision (master version checker work, May 2026):
//   ★ Current bumped from "1.0.0" → "1.0.1" (matches the master sheet)
//   ★ GitHubRepo typo fixed: "OSUPhoneix" → "OSUPhoenix" (per sheet)
//   ★ ProductRegistryUrls replaced with the published Google Sheet CSV URL
//   ★ Comments updated to reflect CSV (not JSON) registry format
// =============================================================================

using System.Collections.Generic;

namespace OSWTools.Versioning
{
    /// <summary>
    /// Compile-time constants identifying this build of OSWTools.dll and its
    /// remote update sources.
    /// </summary>
    public static class OSWVersion
    {
        // ─────────────────────────────────────────────────────────────────────
        // DLL IDENTITY
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Semantic version of this DLL build. Bump on every public release.
        /// Must match the "Current Version" cell of the OSWTOOLS row in the
        /// master product registry sheet — otherwise the master update checker
        /// will tell users their DLL is out of date when it isn't.
        /// </summary>
        public const string Current     = "1.0.1";  // ★ bumped from 1.0.0

        // ─────────────────────────────────────────────────────────────────────
        // GITHUB SELF-UPDATE COORDINATES (used by UpdateChecker.cs)
        // ─────────────────────────────────────────────────────────────────────

        public const string GitHubOwner = "OSUPhoenix";
        public const string GitHubRepo  = "OSUPhoenix-Streamworks-DLL";  // ★ typo fixed

        /// <summary>API URL for the "latest release" GitHub endpoint.</summary>
        public static string GitHubApiLatest
        {
            get { return "https://api.github.com/repos/" + GitHubOwner + "/" + GitHubRepo + "/releases/latest"; }
        }

        /// <summary>Direct download URL for the latest OSWTools.dll asset.</summary>
        public static string GitHubDllDownload
        {
            get { return "https://github.com/" + GitHubOwner + "/" + GitHubRepo + "/releases/latest/download/OSWTools.dll"; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // PRODUCT REGISTRY (master version checker, May 2026 — CSV-based)
        //
        // Ordered list of URLs that return the registry as CSV. The
        // ProductRegistryClient tries them in order and uses the first one
        // that returns a parseable CSV manifest. Adding fallback mirrors is
        // a matter of pushing more URLs into this list — no other code
        // changes needed.
        //
        // CURRENT SOURCE:
        //   Google Sheet "OSW_Product_Registry", Products tab, published to
        //   web as CSV. The URL is the standard Google Sheets publish-to-web
        //   format with output=csv. Anyone-with-link access; no auth needed
        //   from the DLL side.
        //
        // TO ADD A MIRROR:
        //   The CSV parser is column-name-driven, so any source that returns
        //   CSV with matching header names (case-insensitive) will work. A
        //   plain CSV uploaded to GitHub raw would be a natural fallback if
        //   Google Sheets ever becomes unreachable.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ordered list of registry CSV endpoints. First success wins.
        /// </summary>
        public static readonly IList<string> ProductRegistryUrls = new List<string>
        {
            // 1) Primary — Products tab of the OSW Product Registry sheet,
            //    published as CSV. Transformation pattern: take the pubhtml
            //    URL from Google's Publish-to-Web dialog, swap "pubhtml" for
            //    "pub", and add "&output=csv".
            "https://docs.google.com/spreadsheets/d/e/2PACX-1vRYeE7GVYQbbCt8vr9IkLi00ibPiy-IgZAUx1SwvzmeBckaHKozZtXnZVqGAd2nyOZoS3DlMqEpEX0F/pub?gid=730684055&single=true&output=csv",

            // 2) Mirror — reserved for future GitHub raw CSV snapshot.
            //    Uncomment and fill in once a snapshot path exists.
            // "https://raw.githubusercontent.com/OSUPhoenix/OSUPhoenix-Streamworks-DLL/main/registry/products.csv",

            // 3) Mirror — reserved for future osuphoenix.tv hosted snapshot.
            // "https://osuphoenix.tv/registry/products.csv"
        };
    }
}
