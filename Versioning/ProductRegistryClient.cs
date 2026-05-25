// =============================================================================
// OSWTools — Versioning/ProductRegistryClient.cs
//
// Static client for fetching the product registry manifest.
//
// REWRITTEN (May 2026) — switched from JSON (Apps Script) to CSV (Google
// Sheets publish-to-web). The data model (ProductManifest / ProductInfo)
// is unchanged; only the parser is new.
//
// RESPONSIBILITIES:
//   1. Fetch CSV text from the URLs in OSWVersion.ProductRegistryUrls.
//   2. Parse CSV into a ProductManifest, mapping columns to fields by
//      HEADER NAME (so column reorders in the sheet don't break us).
//   3. Cache the parsed manifest in memory for a configurable TTL so we
//      don't hammer the sheet on every CPH call.
//   4. Never throw — all errors return a failure RegistryFetchResult and
//      log via the callback if provided.
//
// CSV PARSING — WHY HAND-ROLLED:
//   The .NET 4.8.1 BCL has no CSV reader. We could pull in CsvHelper, but
//   the sheet's CSV is well-formed (Google produces it) and our parser
//   needs are modest: quoted fields, embedded commas, embedded newlines,
//   doubled-quote escapes. A small hand-rolled parser is more honest about
//   what we depend on than adding a NuGet for ~80 lines of code.
//
// HEADER-NAME MAPPING:
//   The sheet's column order may change over time without breaking the
//   integration. We read the first row as headers, normalise them, and map
//   each subsequent row's cells to ProductInfo fields by header name. If a
//   column is missing, the corresponding field stays null/empty.
//
// THREADING:
//   Cache is guarded by a lock. FetchAsync is safe to call from any thread.
//
// .NET 4.8.1 / C# 7.3 NOTES:
//   - WebClient (not HttpClient), same as UpdateChecker.cs.
//   - No System.Text.Json, no nullable annotations, no switch expressions.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace OSWTools.Versioning
{
    /// <summary>
    /// Result of a registry fetch attempt. Always non-null; check Succeeded.
    /// </summary>
    public class RegistryFetchResult
    {
        public bool            Succeeded   { get; set; }
        public ProductManifest Manifest    { get; set; }
        public string          SourceUrl   { get; set; }   // which URL we got data from
        public string          ErrorDetail { get; set; }
        public bool            FromCache   { get; set; }
    }

    /// <summary>
    /// Fetch + cache for the product registry manifest. All members static —
    /// the cache is shared across every OSWLib instance in the SB session.
    /// </summary>
    public static class ProductRegistryClient
    {
        // ── Cache ────────────────────────────────────────────────────────────
        // In-memory only. Cleared on SB restart (because the DLL reloads).
        // TTL is generous because the registry rarely changes within a session.
        private static ProductManifest _cachedManifest;
        private static DateTime        _cachedAtUtc = DateTime.MinValue;
        private static readonly object _cacheLock   = new object();

        /// <summary>How long a cached manifest is considered fresh. Default: 1 hour.</summary>
        public static TimeSpan CacheLifetime { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Network timeout for a single URL attempt. Default: 10 seconds.</summary>
        public static int RequestTimeoutMs { get; set; } = 10000;

        // ─────────────────────────────────────────────────────────────────────
        // HEADER → FIELD MAPPING
        //
        // The keys here are the lowercased, whitespace-normalised header names
        // expected in the sheet. The values are ProductInfo property setters.
        //
        // Adding a new column to the sheet:
        //   1. Add the property to ProductInfo.cs
        //   2. Add an entry here mapping the header name to the property
        //
        // The parser is case-insensitive on header names. Extra columns in the
        // sheet that aren't in this dictionary are ignored harmlessly. Missing
        // columns leave the corresponding property at its default value.
        // ─────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, Action<ProductInfo, string>> ColumnMap
            = new Dictionary<string, Action<ProductInfo, string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "product code",    (p, v) => p.Code           = v },
            { "display name",    (p, v) => p.Name           = v },
            { "type",            (p, v) => p.Type           = v },
            { "current version", (p, v) => p.CurrentVersion = v },
            { "min dll version", (p, v) => p.MinDllVersion  = v },
            { "status",          (p, v) => p.Status         = v },
            { "tier",            (p, v) => p.Tier           = v },
            { "product webpage", (p, v) => p.DownloadUrl    = v },  // used by the dialog's clickable link
            { "distribution",    (p, v) => p.Distribution   = v },
            { "owns / provides", (p, v) => p.Provides       = v },
            { "depends on",      (p, v) => p.DependsOn      = v },
            { "notes",           (p, v) => p.Notes          = v }
        };

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Fetch the manifest, using the cache if it's still fresh.
        /// Tries each URL in order and returns the first success.
        /// </summary>
        /// <param name="urls">Ordered list of manifest URLs to try.</param>
        /// <param name="log">Optional callback for verbose logging. Pass null to suppress.</param>
        public static async Task<RegistryFetchResult> FetchAsync(
            IList<string> urls,
            Action<string> log = null)
        {
            // ── Cache hit? — bail out fast before touching the network ─────
            lock (_cacheLock)
            {
                if (_cachedManifest != null
                    && DateTime.UtcNow - _cachedAtUtc < CacheLifetime)
                {
                    if (log != null) log("[Registry] Cache hit (age "
                        + (int)(DateTime.UtcNow - _cachedAtUtc).TotalSeconds + "s).");
                    return new RegistryFetchResult
                    {
                        Succeeded = true,
                        Manifest  = _cachedManifest,
                        FromCache = true
                    };
                }
            }

            // ── Try each URL in order ──────────────────────────────────────
            if (urls == null || urls.Count == 0)
            {
                return new RegistryFetchResult
                {
                    Succeeded   = false,
                    ErrorDetail = "No registry URLs configured."
                };
            }

            string lastError = "";
            for (int i = 0; i < urls.Count; i++)
            {
                string url = urls[i];
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (log != null) log("[Registry] Trying source " + (i + 1) + "/" + urls.Count
                                     + ": " + url);

                // ★ FetchAttemptResult bundles manifest + error reason so we can
                //   surface the actual failure cause (network error, timeout,
                //   parse failure) in the final RegistryFetchResult instead of
                //   losing it to the LogDebug-suppressed verbose log.
                FetchAttemptResult attempt = await TryFetchSingleAsync(url, log);
                if (attempt.Manifest != null)
                {
                    // Success — update cache and return.
                    lock (_cacheLock)
                    {
                        _cachedManifest = attempt.Manifest;
                        _cachedAtUtc    = DateTime.UtcNow;
                    }
                    return new RegistryFetchResult
                    {
                        Succeeded = true,
                        Manifest  = attempt.Manifest,
                        SourceUrl = url,
                        FromCache = false
                    };
                }
                // Capture the per-URL failure reason so the final error
                // includes what actually went wrong, not just which URL.
                lastError = "Last source (" + url + ") failed: "
                          + (attempt.ErrorReason ?? "unknown reason");
            }

            return new RegistryFetchResult
            {
                Succeeded   = false,
                ErrorDetail = "All registry sources failed. " + lastError
            };
        }

        /// <summary>
        /// Force a re-fetch on the next call by invalidating the cache.
        /// Call from a "Refresh" button in a settings UI.
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedManifest = null;
                _cachedAtUtc    = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Read the currently-cached manifest without triggering a fetch.
        /// Returns null if nothing has been fetched yet (or the cache was cleared).
        /// </summary>
        public static ProductManifest PeekCache()
        {
            lock (_cacheLock) { return _cachedManifest; }
        }

        // ── Internals: network ───────────────────────────────────────────────

        /// <summary>
        /// Internal helper bundling the outcome of a single-URL fetch attempt.
        /// Either Manifest is non-null (success) OR ErrorReason describes why
        /// it failed. We bundle them so the per-URL failure reason can bubble
        /// up to the caller's RegistryFetchResult.ErrorDetail — otherwise the
        /// reason is only visible via the verbose log callback (LogDebug),
        /// which is suppressed unless osw_DebugMode is true.
        /// </summary>
        private class FetchAttemptResult
        {
            public ProductManifest Manifest    { get; set; }
            public string          ErrorReason { get; set; }
        }

        /// <summary>
        /// Fetch one URL and parse it as CSV. On success returns Manifest set
        /// and ErrorReason null. On failure returns Manifest null and a
        /// short human-readable ErrorReason describing what went wrong.
        ///
        /// Never throws — always returns a non-null FetchAttemptResult.
        /// </summary>
        private static async Task<FetchAttemptResult> TryFetchSingleAsync(
            string url, Action<string> log)
        {
            FetchAttemptResult result = new FetchAttemptResult();
            try
            {
                string csv;
                using (WebClient wc = new WebClient())
                {
                    // Same UA pattern as UpdateChecker — Google's publish-to-web
                    // endpoints don't care, but a recognizable UA helps if we
                    // ever add a GitHub-raw fallback (which DOES care).
                    wc.Headers.Add("User-Agent", "OSWTools/" + OSWVersion.Current);

                    // Google publishes the CSV as UTF-8.
                    wc.Encoding = Encoding.UTF8;

                    // WebClient has no native request timeout — race the download
                    // against a Task.Delay and cancel if the timer wins.
                    Task<string> download = wc.DownloadStringTaskAsync(url);
                    Task         timeout  = Task.Delay(RequestTimeoutMs);
                    Task         winner   = await Task.WhenAny(download, timeout);

                    if (winner == timeout)
                    {
                        wc.CancelAsync();
                        result.ErrorReason = "Timeout after " + RequestTimeoutMs + "ms";
                        if (log != null) log("[Registry] " + result.ErrorReason + ": " + url);
                        return result;
                    }

                    csv = await download;
                }

                if (string.IsNullOrWhiteSpace(csv))
                {
                    result.ErrorReason = "Empty response body";
                    if (log != null) log("[Registry] " + result.ErrorReason + " from " + url);
                    return result;
                }

                // Detect the HTML-instead-of-CSV failure mode early. Google
                // returns an HTML "please sign in" page when the sheet isn't
                // actually published-to-web (vs. just shared with link). The
                // CSV parser would silently produce a manifest with 0 rows —
                // we'd rather flag it as a publishing problem.
                string trimmed = csv.TrimStart();
                if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                {
                    result.ErrorReason = "Server returned HTML instead of CSV (sheet may not be published to web)";
                    if (log != null) log("[Registry] " + result.ErrorReason + ": " + url);
                    return result;
                }

                ProductManifest manifest = ParseCsv(csv, log, out string parseError);
                if (manifest == null)
                {
                    // ★ Use the specific parse error (e.g. "no header row found")
                    //   instead of the generic "CSV could not be parsed".
                    result.ErrorReason = parseError ?? "CSV could not be parsed";
                    if (log != null) log("[Registry] " + result.ErrorReason + " from " + url);
                    return result;
                }
                if (manifest.Products == null || manifest.Products.Count == 0)
                {
                    result.ErrorReason = "CSV parsed but contained no product rows";
                    if (log != null) log("[Registry] " + result.ErrorReason + " from " + url);
                    return result;
                }

                if (log != null) log("[Registry] Loaded " + manifest.Products.Count
                                     + " product(s) from " + url);
                result.Manifest = manifest;
                return result;
            }
            catch (Exception ex)
            {
                // Surface the exception type AND message so we can tell apart
                // WebException (network/HTTP), TaskCanceledException (timeout
                // edge case), and unexpected exception classes.
                result.ErrorReason = ex.GetType().Name + ": " + ex.Message;
                if (log != null) log("[Registry] Fetch failed for " + url + ": " + result.ErrorReason);
                return result;
            }
        }

        // ── Internals: CSV parsing ───────────────────────────────────────────

        /// <summary>
        /// Parse a CSV string into a ProductManifest.
        ///
        /// HEADER AUTO-DETECTION:
        ///   The parser scans rows from the top and treats the first row
        ///   containing at least MIN_HEADER_MATCHES recognised column names
        ///   as the header. Everything above is treated as preamble and
        ///   skipped. This makes the parser tolerant of title rows,
        ///   description blurbs, snapshot-date rows, blank rows, etc. that
        ///   might sit above the actual headers.
        ///
        /// FAILURE REPORTING:
        ///   On failure, returns null and writes a specific reason to the
        ///   failureReason out parameter — e.g. "No header row found in 24
        ///   data rows; first non-empty cell was: 'OSW Product Registry...'"
        ///   This reason bubbles up to the caller's UI/log instead of the
        ///   generic "CSV could not be parsed".
        ///
        /// SUCCESS:
        ///   Returns a non-null ProductManifest with failureReason = null.
        ///   Products.Count may legitimately be 0 if the CSV has a header
        ///   row but no data rows (treated as success, not failure).
        /// </summary>
        private static ProductManifest ParseCsv(string csv, Action<string> log, out string failureReason)
        {
            failureReason = null;

            if (string.IsNullOrWhiteSpace(csv))
            {
                failureReason = "CSV input was empty";
                return null;
            }

            List<List<string>> rows = TokenizeCsv(csv);
            if (rows.Count == 0)
            {
                failureReason = "CSV tokenizer returned zero rows";
                return null;
            }

            // ── Auto-detect the header row ───────────────────────────────────
            // Scan from the top for the first row with at least MIN_HEADER_MATCHES
            // recognised column names. Lower this threshold and you risk
            // matching a data row that happens to contain one column-like cell.
            // 2 is a good balance — sheets typically have 5+ headers, so 2
            // is a safe minimum.
            const int MIN_HEADER_MATCHES = 2;

            int headerRowIndex = -1;
            int bestMatchCount = 0;
            string firstNonEmptyCellSample = "";

            for (int r = 0; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row == null || row.Count == 0) continue;

                int matches = 0;
                for (int c = 0; c < row.Count; c++)
                {
                    string cell = (row[c] ?? "").Trim();
                    if (string.IsNullOrEmpty(cell)) continue;
                    if (string.IsNullOrEmpty(firstNonEmptyCellSample))
                        firstNonEmptyCellSample = cell;  // keep the first non-empty cell anywhere for diagnostics
                    if (ColumnMap.ContainsKey(cell)) matches++;
                }

                if (matches > bestMatchCount) bestMatchCount = matches;

                if (matches >= MIN_HEADER_MATCHES)
                {
                    headerRowIndex = r;
                    break;
                }
            }

            if (headerRowIndex < 0)
            {
                // Build a diagnostic that tells the user (and future us) what
                // we actually saw vs what we needed. Cap the sample at 80
                // chars so the log line stays readable.
                string sample = firstNonEmptyCellSample.Length > 80
                              ? firstNonEmptyCellSample.Substring(0, 80) + "..."
                              : firstNonEmptyCellSample;
                failureReason = "No header row found in " + rows.Count
                              + " rows (best match had " + bestMatchCount
                              + " recognised column(s); expected at least "
                              + MIN_HEADER_MATCHES + "). "
                              + "First non-empty cell was: '" + sample + "'. "
                              + "Expected column names include 'Product Code', 'Current Version', 'Display Name'.";
                return null;
            }

            if (log != null && headerRowIndex > 0)
                log("[Registry] Skipped " + headerRowIndex + " preamble row(s) before the header.");

            // ── Build header→setter map for the detected header row ─────────
            List<string> header = rows[headerRowIndex];
            Action<ProductInfo, string>[] settersByIndex = new Action<ProductInfo, string>[header.Count];
            for (int i = 0; i < header.Count; i++)
            {
                string key = (header[i] ?? "").Trim();
                Action<ProductInfo, string> setter;
                if (ColumnMap.TryGetValue(key, out setter))
                    settersByIndex[i] = setter;
            }

            ProductManifest manifest = new ProductManifest
            {
                Schema    = 1,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };

            // ── Walk data rows (everything after the header) ────────────────
            int skipped = 0;
            for (int r = headerRowIndex + 1; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row == null) { skipped++; continue; }

                // Skip rows where every cell is empty/whitespace.
                bool allEmpty = true;
                for (int c = 0; c < row.Count; c++)
                {
                    if (!string.IsNullOrWhiteSpace(row[c])) { allEmpty = false; break; }
                }
                if (allEmpty) { skipped++; continue; }

                ProductInfo p = new ProductInfo();
                int cellLimit = Math.Min(row.Count, settersByIndex.Length);
                for (int c = 0; c < cellLimit; c++)
                {
                    if (settersByIndex[c] == null) continue;
                    string cell = (row[c] ?? "").Trim();
                    settersByIndex[c](p, cell);
                }

                // Require at minimum a non-empty Code, otherwise the row is
                // garbage from our perspective (couldn't look it up later).
                if (string.IsNullOrWhiteSpace(p.Code)) { skipped++; continue; }

                manifest.Products.Add(p);
            }

            if (log != null && skipped > 0)
                log("[Registry] Skipped " + skipped + " empty/invalid row(s) after the header.");

            return manifest;
        }

        /// <summary>
        /// Tokenise a CSV string into a list of rows, where each row is a list
        /// of cells. Implements the common CSV grammar:
        ///   - Fields separated by ','
        ///   - Rows separated by '\n' or '\r\n'
        ///   - Fields may be wrapped in double-quotes
        ///   - Inside a quoted field: ',' and newlines are literal
        ///   - Inside a quoted field: '""' represents a literal '"'
        ///
        /// This is intentionally a minimal implementation. It does NOT support
        /// custom delimiters, single-quote strings, or fancy escape characters
        /// — Google Sheets emits standard CSV and that's all we need.
        /// </summary>
        private static List<List<string>> TokenizeCsv(string csv)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> current = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inQuotes = false;

            // Normalise '\r\n' to '\n' so we only have to look at one line ending
            // throughout the state machine. Standalone '\r' (rare) becomes '\n' too.
            csv = csv.Replace("\r\n", "\n").Replace('\r', '\n');

            for (int i = 0; i < csv.Length; i++)
            {
                char ch = csv[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        // Doubled quote inside a quoted field = literal quote.
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;  // skip the second quote
                        }
                        else
                        {
                            inQuotes = false;  // end of quoted field
                        }
                    }
                    else
                    {
                        cell.Append(ch);
                    }
                }
                else
                {
                    if (ch == '"')
                    {
                        // Quote at the start of a cell opens a quoted field.
                        // Quotes mid-cell (rare/malformed) are treated literally,
                        // which is forgiving — Google never emits this.
                        if (cell.Length == 0) inQuotes = true;
                        else                  cell.Append(ch);
                    }
                    else if (ch == ',')
                    {
                        current.Add(cell.ToString());
                        cell.Length = 0;
                    }
                    else if (ch == '\n')
                    {
                        current.Add(cell.ToString());
                        cell.Length = 0;
                        rows.Add(current);
                        current = new List<string>();
                    }
                    else
                    {
                        cell.Append(ch);
                    }
                }
            }

            // Flush whatever's left — handles files that don't end with a newline.
            if (cell.Length > 0 || current.Count > 0)
            {
                current.Add(cell.ToString());
                rows.Add(current);
            }

            return rows;
        }
    }
}
