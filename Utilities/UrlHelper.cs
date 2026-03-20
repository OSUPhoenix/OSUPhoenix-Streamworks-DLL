// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Utilities/UrlHelper.cs                    DLL v1.1.0
//
//  Static helpers for URL validation and domain allowlist checking.
//
//  WHY THIS IS IN THE DLL:
//    Multiple OSW tools need to validate user-submitted URLs — the
//    GIF Display, any future clip/image submission features, link
//    approval queues, etc. Centralising this avoids duplicated logic
//    and subtle differences between implementations.
//
//  PLACEMENT:
//    Utilities/ → static class, no CPH dependency, safe anywhere.
//    No csproj change needed — the existing wildcard glob picks it up.
//
//  USAGE:
//    using OSWTools.Utilities;
//
//    bool ok  = UrlHelper.IsValidUrl("https://tenor.com/abc");
//    bool ok2 = UrlHelper.IsAllowedDomain(url, cfg.AllowedDomains);
//    string h = UrlHelper.GetHost("https://media.tenor.com/abc");  // "media.tenor.com"
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace OSWTools.Utilities
{
    /// <summary>
    /// Static helpers for URL validation and domain allowlist checking.
    /// No Streamer.bot / CPH dependency — safe to use anywhere.
    /// </summary>
    public static class UrlHelper
    {
        // ── IsValidUrl ────────────────────────────────────────────────
        /// <summary>
        /// Returns true if <paramref name="url"/> is a well-formed
        /// absolute HTTP or HTTPS URL.
        /// Returns false for null, empty, or non-HTTP schemes (ftp:, etc.).
        /// Never throws.
        /// </summary>
        /// <example>
        ///   UrlHelper.IsValidUrl("https://tenor.com/abc")  → true
        ///   UrlHelper.IsValidUrl("not a url")              → false
        ///   UrlHelper.IsValidUrl("ftp://files.example.com")→ false
        /// </example>
        public static bool IsValidUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            // Uri.TryCreate handles the heavy lifting of RFC-compliant parsing.
            // We then restrict to http/https only — we never want ftp://, file://,
            // javascript:, data:, etc. coming through from chat.
            Uri uri;
            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        // ── GetHost ───────────────────────────────────────────────────
        /// <summary>
        /// Returns the host portion of a URL, lowercased and with any
        /// leading "www." stripped.
        /// Returns <see cref="string.Empty"/> if the URL is invalid.
        /// Never throws.
        /// </summary>
        /// <example>
        ///   GetHost("https://www.Tenor.com/path")    → "tenor.com"
        ///   GetHost("https://media.tenor.com/path")  → "media.tenor.com"
        ///   GetHost("not a url")                     → ""
        /// </example>
        public static string GetHost(string url)
        {
            if (!IsValidUrl(url)) return string.Empty;

            string host = new Uri(url).Host.ToLowerInvariant();

            // Strip www. so callers can normalise domains simply
            if (host.StartsWith("www.")) host = host.Substring(4);

            return host;
        }

        // ── IsAllowedDomain ───────────────────────────────────────────
        /// <summary>
        /// Returns true if the URL's host is within one of the entries in
        /// <paramref name="allowedDomains"/>.
        ///
        /// Matching rules:
        ///   • Case-insensitive
        ///   • "www." is stripped from both sides before comparison
        ///   • Exact match:    "tenor.com"       matches "https://tenor.com/…"
        ///   • Subdomain match: "tenor.com"      matches "https://media.tenor.com/…"
        ///   • An empty/null collection returns <c>true</c> (no restriction).
        ///
        /// Never throws.
        /// </summary>
        /// <example>
        ///   var allowed = new List&lt;string&gt; { "tenor.com", "giphy.com" };
        ///
        ///   IsAllowedDomain("https://tenor.com/abc",       allowed) → true
        ///   IsAllowedDomain("https://media.tenor.com/abc", allowed) → true  (subdomain)
        ///   IsAllowedDomain("https://evil.com/tenor.com",  allowed) → false (path, not host)
        ///   IsAllowedDomain("https://i.giphy.com/abc",     allowed) → true  (subdomain)
        ///   IsAllowedDomain("https://imgur.com/abc",       allowed) → false
        /// </example>
        public static bool IsAllowedDomain(string url, IEnumerable<string> allowedDomains)
        {
            // Null or empty collection = no restriction applied
            if (allowedDomains == null) return true;

            string host = GetHost(url);
            if (string.IsNullOrEmpty(host)) return false;   // invalid URL

            foreach (string domain in allowedDomains)
            {
                if (string.IsNullOrWhiteSpace(domain)) continue;

                // Normalise the allowlist entry the same way we normalise the URL host
                string d = domain.Trim().ToLowerInvariant();
                if (d.StartsWith("www.")) d = d.Substring(4);

                // Exact match:    host == "tenor.com"
                // Subdomain match: host ends with ".tenor.com"
                //   This handles "media.tenor.com", "c.tenor.com", etc.
                //   The leading dot prevents "badtenor.com" from matching "tenor.com".
                if (host == d || host.EndsWith("." + d)) return true;
            }

            return false;
        }
    }
}
