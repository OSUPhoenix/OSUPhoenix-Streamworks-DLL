// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerSearch.cs       DLL v1.5.0
//
//  "Type some words, get a GIF."  Powers the optional !gif command and
//  the optional channel points reward.
//
//  THE JOB, STEP BY STEP (Find):
//    1. Clean the words the viewer typed (extra spaces, length limit).
//    2. Refuse if they contain a blocked word — BEFORE asking Giphy.
//    3. Ask Giphy's search endpoint, with your key and your rating filter.
//    4. Choose one of the top few matches at random.
//    5. Hand back a link we know is safe to show.
//
//  WHAT KEEPS IT SAFE:
//    • The rating filter is sent to Giphy itself ("g", "pg", ...).
//    • We only use a result whose link is on giphy.com. If Giphy ever
//      sent something odd, we fall back to the standard link built from
//      the GIF's ID (see GifBattlerLinks.MediaUrl).
//    • The API key is never logged and never put in a chat message — any
//      error text is scrubbed of it first.
//
//  TESTING WITHOUT THE INTERNET:
//    HttpGet / HttpGetBytes / Pick are plain fields that tests swap for
//    fakes. In the real DLL they do the real work.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OSWTools.Functions.GifBattler
{
    internal enum SearchStatus
    {
        Ok,
        NoKey,          // no API key saved yet
        EmptyTerm,      // viewer typed nothing
        LooksLikeLink,  // viewer pasted a link instead of words
        Blocked,        // contains a blocked word
        NoResults,      // Giphy found nothing
        Error           // Giphy / network trouble (Detail says what)
    }

    internal sealed class SearchOutcome
    {
        internal SearchStatus Status;
        internal string Term;      // the cleaned words
        internal string Id;        // Giphy's ID for the chosen GIF
        internal string Url;       // link to show (safe)
        internal string Title;
        internal string Detail;    // short reason for Error, already scrubbed of the key
    }

    internal static class GifBattlerSearch
    {
        /// <summary>Giphy accepts search text up to 50 characters.</summary>
        internal const int MaxTermLength = 50;

        /// <summary>Biggest GIF we will download for the approval preview.</summary>
        internal const int MaxPreviewBytes = 12 * 1024 * 1024;

        // ── things tests replace ─────────────────────────────────────
        internal static Func<string, string> HttpGet = DefaultHttpGet;
        internal static Func<string, int, byte[]> HttpGetBytes = DefaultHttpGetBytes;
        private static readonly Random Rng = new Random();
        /// <summary>Returns a whole number from lo to hi INCLUSIVE.</summary>
        internal static Func<int, int, int> Pick = (lo, hi) => { lock (Rng) return Rng.Next(lo, hi + 1); };

        // ═════════════════════════════════════════════════════════════
        //  The main entry point
        // ═════════════════════════════════════════════════════════════
        internal static SearchOutcome Find(GifBattlerConfig cfg, string typed, bool preflightOnly = false)
        {
            string term = CleanTerm(typed);
            var outcome = new SearchOutcome { Term = term };

            if (string.IsNullOrEmpty(cfg.GiphyApiKey)) { outcome.Status = SearchStatus.NoKey;     return outcome; }
            if (term.Length == 0)                      { outcome.Status = SearchStatus.EmptyTerm; return outcome; }
            if (term.IndexOf("://", StringComparison.Ordinal) >= 0)
                                                       { outcome.Status = SearchStatus.LooksLikeLink; return outcome; }
            if (IsBlocked(term, cfg.SearchBlockedWords)) { outcome.Status = SearchStatus.Blocked; return outcome; }

            // preflightOnly: everything above passed, so the words are usable.
            // Callers use this to reject a bad request before doing the
            // slower checks and the network call. Status Ok, no GIF yet.
            if (preflightOnly) { outcome.Status = SearchStatus.Ok; return outcome; }

            string json;
            try
            {
                json = HttpGet(BuildUrl(cfg, term));
            }
            catch (Exception ex)
            {
                outcome.Status = SearchStatus.Error;
                outcome.Detail = Scrub(ex.GetType().Name + ": " + ex.Message, cfg.GiphyApiKey);
                return outcome;
            }

            SearchOutcome parsed = Parse(json, cfg.GiphyPickFromTop);
            parsed.Term = term;
            if (parsed.Detail != null) parsed.Detail = Scrub(parsed.Detail, cfg.GiphyApiKey);
            return parsed;
        }

        // ═════════════════════════════════════════════════════════════
        //  Pieces (internal so the tests can check each one)
        // ═════════════════════════════════════════════════════════════

        /// <summary>Trim, squash spaces, drop control characters, cap the length.</summary>
        internal static string CleanTerm(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            var sb = new StringBuilder();
            foreach (char c in raw)
                sb.Append(char.IsControl(c) ? ' ' : c);

            string t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            if (t.Length > MaxTermLength) t = t.Substring(0, MaxTermLength).Trim();
            return t;
        }

        /// <summary>
        /// True if the text contains a blocked word or phrase as WHOLE words,
        /// so blocking "ass" does not block "class" or "glass".
        /// </summary>
        internal static bool IsBlocked(string term, IList<string> blocked)
        {
            if (blocked == null || string.IsNullOrEmpty(term)) return false;
            foreach (string b in blocked)
            {
                if (string.IsNullOrWhiteSpace(b)) continue;
                string pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(b.Trim()) + @"(?![\p{L}\p{N}])";
                if (Regex.IsMatch(term, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }
            return false;
        }

        internal static string BuildUrl(GifBattlerConfig cfg, string term)
        {
            int limit = Math.Max(1, Math.Min(GifBattlerConfig.Limits.PickMax, cfg.GiphyPickFromTop));
            return "https://api.giphy.com/v1/gifs/search"
                 + "?api_key=" + Uri.EscapeDataString(cfg.GiphyApiKey)
                 + "&q="       + Uri.EscapeDataString(term)
                 + "&limit="   + limit
                 + "&offset=0"
                 + "&rating="  + Uri.EscapeDataString(cfg.GiphyRating)
                 + "&lang=en";
        }

        /// <summary>
        /// Reads Giphy's answer and chooses one of the first <paramref name="pickFromTop"/>
        /// results. Never throws: anything unexpected becomes an Error outcome.
        /// </summary>
        internal static SearchOutcome Parse(string json, int pickFromTop)
        {
            var o = new SearchOutcome();
            JObject root;
            try { root = JObject.Parse(json ?? ""); }
            catch (Exception) { o.Status = SearchStatus.Error; o.Detail = "Giphy sent an answer we could not read"; return o; }

            // Giphy reports trouble inside "meta" (401 bad key, 429 too many calls...).
            int status = 200;
            JToken st = root.SelectToken("meta.status");
            if (st != null) int.TryParse((string)st, out status);
            if (status != 200)
            {
                o.Status = SearchStatus.Error;
                string msg = (string)root.SelectToken("meta.msg");
                o.Detail = status == 401 || status == 403 ? "Giphy did not accept the API key"
                         : status == 429                   ? "Giphy says too many searches this hour"
                         : "Giphy error " + status + (string.IsNullOrEmpty(msg) ? "" : " (" + msg + ")");
                return o;
            }

            var candidates = new List<SearchOutcome>();
            JArray data = root["data"] as JArray;
            if (data != null)
            {
                foreach (JToken item in data)
                {
                    string id = (string)item["id"];
                    if (!IsPlainId(id)) continue;

                    // Prefer the lighter "downsized" picture (faster to load, and
                    // to preview); fall back to the original, then to the standard link.
                    string url = SafeGiphyUrl((string)item.SelectToken("images.downsized.url"))
                              ?? SafeGiphyUrl((string)item.SelectToken("images.original.url"))
                              ?? GifBattlerLinks.MediaUrl(id);

                    candidates.Add(new SearchOutcome { Status = SearchStatus.Ok, Id = id, Url = url, Title = (string)item["title"] });
                }
            }

            if (candidates.Count == 0) { o.Status = SearchStatus.NoResults; return o; }

            int top = Math.Max(1, Math.Min(pickFromTop, candidates.Count));
            return candidates[top == 1 ? 0 : Pick(0, top - 1)];
        }

        private static bool IsPlainId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > GifBattlerLinks.MaxIdLength) return false;
            foreach (char c in id)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')) return false;
            return true;
        }

        /// <summary>The link only if it is https and on giphy.com; otherwise null.</summary>
        private static string SafeGiphyUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps) return null;
            string host = uri.Host.ToLowerInvariant();
            return (host == "giphy.com" || host.EndsWith(".giphy.com")) ? uri.AbsoluteUri : null;
        }

        /// <summary>Removes the API key from any text that might be shown or logged.</summary>
        internal static string Scrub(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return text;
            return text.Replace(key, "***").Replace(Uri.EscapeDataString(key), "***");
        }

        /// <summary>Plain-English reply for a failed search (null for Ok).</summary>
        internal static string DescribeFailure(SearchOutcome o)
        {
            switch (o.Status)
            {
                case SearchStatus.NoKey:         return "GIF search isn't set up yet.";
                case SearchStatus.EmptyTerm:     return "type what to search for, like: !gif dancing cat";
                case SearchStatus.LooksLikeLink: return "type words to search for, or paste the GIF link straight into chat.";
                case SearchStatus.Blocked:       return "that search isn't allowed here.";
                case SearchStatus.NoResults:     return "I couldn't find a GIF for \"" + o.Term + "\". Try different words!";
                case SearchStatus.Error:         return "GIF search isn't working right now. Try again in a bit.";
                default:                         return null;
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  The real network calls (tests replace the fields above)
        // ═════════════════════════════════════════════════════════════
        private static string DefaultHttpGet(string url)
        {
            EnableModernTls();
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
            {
                // Giphy explains problems in the JSON body (even on 401/429),
                // so read the body whatever the status code was.
                HttpResponseMessage resp = http.GetAsync(url).GetAwaiter().GetResult();
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>Downloads a picture for the approval preview; null if it fails or is too big.</summary>
        private static byte[] DefaultHttpGetBytes(string url, int maxBytes)
        {
            try
            {
                EnableModernTls();
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                using (HttpResponseMessage resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    long? len = resp.Content.Headers.ContentLength;
                    if (len.HasValue && len.Value > maxBytes) return null;

                    using (Stream s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > maxBytes) return null;
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Older .NET Framework versions default to TLS settings Giphy no longer accepts.
        private static void EnableModernTls()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }   // 3072 = TLS 1.2
            catch (Exception) { /* some hosts forbid changing it; the default may still work */ }
        }
    }
}
