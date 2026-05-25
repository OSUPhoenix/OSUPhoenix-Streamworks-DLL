// =============================================================================
// OSWTools — Versioning/ProductManifest.cs
//
// Data classes (POCOs) that map to the JSON manifest returned by the product
// registry (Apps Script web app, GitHub raw JSON, website endpoint, etc).
//
// SHAPE (mirrors your Google Sheet columns 1:1 — only the fields we actually
// consume are decoded; extra fields in the JSON are ignored harmlessly):
//
//   {
//     "schema":     1,
//     "updated_at": "2026-05-17T12:00:00Z",
//     "products": [
//       {
//         "code":            "SAS",
//         "name":            "Streamer Achievement System",
//         "type":            "Action Pack",
//         "current_version": "3.2.0",
//         "min_dll_version": "1.1.0",
//         "status":          "active",
//         "tier":            "free, creator",
//         "distribution":    "github",
//         "download_url":    "https://...",
//         "provides":        "SAS",
//         "depends_on":      "OSW",
//         "notes":           "..."
//       }
//     ]
//   }
//
// All string fields are NULLABLE in JSON terms — they may be missing or empty
// in the sheet. The code in ProductRegistry handles null/empty gracefully so a
// sparsely-filled row never crashes the fetch.
//
// WHY POCOs INSTEAD OF JObject (like UpdateChecker.cs uses):
//   This schema is richer and will grow. Strongly-typed classes give us
//   compile-time safety and IntelliSense when calling sites multiply.
//   UpdateChecker stayed on JObject because it only reads two fields.
// =============================================================================

using System.Collections.Generic;
using Newtonsoft.Json;

namespace OSWTools.Versioning
{
    /// <summary>
    /// Top-level manifest container. Holds the schema version, a timestamp,
    /// and the list of products.
    /// </summary>
    public class ProductManifest
    {
        /// <summary>
        /// Schema version. Bump this in the Apps Script when the JSON shape
        /// changes in a breaking way; the DLL can refuse mismatched manifests.
        /// Current expected value: 1.
        /// </summary>
        [JsonProperty("schema")]
        public int Schema { get; set; }

        /// <summary>
        /// ISO-8601 timestamp of when the manifest was last generated.
        /// Informational only — used for logging and a "last checked" UI label.
        /// </summary>
        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }

        /// <summary>
        /// Every product row from the registry sheet. Never null after a
        /// successful parse — empty if the sheet has no data rows.
        /// </summary>
        [JsonProperty("products")]
        public List<ProductInfo> Products { get; set; }

        public ProductManifest()
        {
            Products = new List<ProductInfo>();
        }
    }

    /// <summary>
    /// One row from the registry — one product (DLL, Action Pack, widget, etc).
    /// Field names map 1:1 to your sheet columns via JsonProperty attributes.
    /// </summary>
    public class ProductInfo
    {
        /// <summary>Sheet column: "Product Code". Short, case-insensitive key (e.g. "SAS", "CGGC", "OSW").</summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>Sheet column: "Display Name". Human-readable (e.g. "Streamer Achievement System").</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Sheet column: "Type". Free-text (e.g. "DLL", "Action Pack", "Widget", "Extension").</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Sheet column: "Current Version". Semver string (e.g. "3.2.0"). The version available right now.</summary>
        [JsonProperty("current_version")]
        public string CurrentVersion { get; set; }

        /// <summary>Sheet column: "Min DLL Version". The minimum OSWTools.dll version this product needs.</summary>
        [JsonProperty("min_dll_version")]
        public string MinDllVersion { get; set; }

        /// <summary>Sheet column: "Status". Free-text (e.g. "active", "beta", "deprecated", "pre-dll").</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>Sheet column: "Tier". Comma-separated tier list (e.g. "free, creator"). Informational only — updates do not gate on tier.</summary>
        [JsonProperty("tier")]
        public string Tier { get; set; }

        /// <summary>Sheet column: "Distribution". Free-text channel name (e.g. "github", "patreon", "kofi", "notion").</summary>
        [JsonProperty("distribution")]
        public string Distribution { get; set; }

        /// <summary>
        /// Sheet column: "Distribution" URL — OR an optional separate column you can add.
        /// Direct download URL for free products, or a storefront/landing page for paid ones.
        /// The update prompt will offer to open this in the default browser.
        /// </summary>
        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }

        /// <summary>Sheet column: "Owns / Provides". Comma-separated module codes this product registers. Usually just its own Code.</summary>
        [JsonProperty("provides")]
        public string Provides { get; set; }

        /// <summary>Sheet column: "Depends On". Comma-separated module codes this product requires.</summary>
        [JsonProperty("depends_on")]
        public string DependsOn { get; set; }

        /// <summary>Sheet column: "Notes". Free-text release notes / context.</summary>
        [JsonProperty("notes")]
        public string Notes { get; set; }
    }
}
