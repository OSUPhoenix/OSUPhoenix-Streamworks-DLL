// =============================================================================
// OSWTools — Extensions/externals/ThirdPartyEmotes.cs
//
// 7TV, BetterTTV and FrankerFaceZ emotes - shared across tools.
//
// WHY THIS FILE EXISTS: Twitch only knows its OWN emotes. A BTTV/7TV/FFZ emote
// (KEKW, Pepega...) is plain text to Twitch, so the message's `emotes` argument
// never lists it and Helix doesn't either. To tell  KEKW  from  hello  we ask each
// service for the emotes enabled on the broadcaster's channel (plus its global set)
// and remember them. The same lookup gives us the PICTURE.
//
// PUBLIC API (added to OSWLib via partial class):
//   string GetThirdPartyEmoteImage(string name, bool use7tv, bool useBttv, bool useFfz, int ttlHours = 6)
//        -> the https picture link of that emote, or "" if none of the enabled
//           services has it.  Priority when two services share a name: 7TV, BTTV, FFZ.
//   string RefreshThirdPartyEmotes(bool use7tv, bool useBttv, bool useFfz)
//        -> forces a fresh download; returns a multi-line report (counts / errors).
//   string ThirdPartySummary(bool use7tv, bool useBttv, bool useFfz)
//        -> one line, what is cached right now (never downloads).
//
// HOW IT STAYS FAST AND SAFE
//   - One memory cache + one file (OSWData/EmoteCache/thirdparty.json) for all tools.
//   - Each service is separate: one being down never affects the other two.
//   - A service that fails keeps its last good list, and is not retried for 5 minutes.
//   - A word nobody knows triggers at most one refresh per service per 10 minutes, so
//     an emote the streamer added a minute ago starts working without a restart.
//   - Names are CASE-SENSITIVE (chat only shows the emote for the exact spelling).
//
// LEARNING NOTE: the JSON parsers are small PURE functions (text in, dictionary out)
// in ThirdPartyEmoteParsers, so they can be tested with sample JSON without the
// internet. Everything that touches the network is in the OSWLib part below.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OSWTools.Data;

namespace OSWTools
{
    // ─────────────────────────────────────────────────────────────────────
    // PURE parsers: JSON text -> { emote name -> https picture link }.
    // They never throw: unreadable input just gives an empty dictionary.
    // They read the JSON loosely (JObject) instead of strict classes, so a
    // service adding a new field can't break us.
    // ─────────────────────────────────────────────────────────────────────
    public static class ThirdPartyEmoteParsers
    {
        private static Dictionary<string, string> NewMap()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);   // case-sensitive
        }

        private static string Str(JToken t)
        {
            return t == null || t.Type == JTokenType.Null ? "" : (string)t;
        }

        // "//cdn.x/y.webp" -> "https://cdn.x/y.webp". Anything not https after that -> "".
        public static string ToHttps(string url)
        {
            string u = (url ?? "").Trim();
            if (u.StartsWith("//", StringComparison.Ordinal)) u = "https:" + u;
            return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : "";
        }

        /// <summary>BetterTTV: a list of { id, code, ... } (the global endpoint, or a channel's lists).</summary>
        private static void AddBttvList(Dictionary<string, string> map, JToken list)
        {
            JArray arr = list as JArray;
            if (arr == null) return;
            foreach (JToken e in arr)
            {
                string id = Str(e["id"]), code = Str(e["code"]);
                if (id.Length == 0 || code.Length == 0 || map.ContainsKey(code)) continue;
                map[code] = "https://cdn.betterttv.net/emote/" + id + "/3x.webp";
            }
        }

        /// <summary>BetterTTV global endpoint: a plain JSON array.</summary>
        public static Dictionary<string, string> ParseBttvGlobal(string json)
        {
            var map = NewMap();
            try { AddBttvList(map, JToken.Parse(json)); } catch (Exception) { }
            return map;
        }

        /// <summary>BetterTTV channel endpoint: { channelEmotes: [...], sharedEmotes: [...] }.</summary>
        public static Dictionary<string, string> ParseBttvChannel(string json)
        {
            var map = NewMap();
            try
            {
                JObject o = JObject.Parse(json);
                AddBttvList(map, o["channelEmotes"]);
                AddBttvList(map, o["sharedEmotes"]);
            }
            catch (Exception) { }
            return map;
        }

        /// <summary>
        /// FrankerFaceZ (global set or a room): { sets: { "<id>": { emoticons: [ { name, urls:{"1","2","4"} } ] } } }.
        /// Takes the largest picture available.
        /// </summary>
        public static Dictionary<string, string> ParseFfz(string json)
        {
            var map = NewMap();
            try
            {
                JObject sets = JObject.Parse(json)["sets"] as JObject;
                if (sets == null) return map;
                foreach (JProperty set in sets.Properties())
                {
                    JArray emoticons = set.Value["emoticons"] as JArray;
                    if (emoticons == null) continue;
                    foreach (JToken e in emoticons)
                    {
                        string name = Str(e["name"]);
                        if (name.Length == 0 || map.ContainsKey(name)) continue;
                        JToken urls = e["urls"];
                        string url = "";
                        foreach (string size in new[] { "4", "2", "1" })
                        {
                            url = ToHttps(Str(urls == null ? null : urls[size]));
                            if (url.Length > 0) break;
                        }
                        if (url.Length > 0) map[name] = url;
                    }
                }
            }
            catch (Exception) { }
            return map;
        }

        /// <summary>
        /// 7TV: either an emote set  { emotes: [ { id, name } ] }  (the global endpoint) or a
        /// user  { emote_set: { emotes: [...] } }  (the by-Twitch-id endpoint).
        /// Picture: https://cdn.7tv.app/emote/{id}/4x.webp
        /// </summary>
        public static Dictionary<string, string> ParseSevenTv(string json)
        {
            var map = NewMap();
            try
            {
                JObject root = JObject.Parse(json);
                JArray emotes = (root["emote_set"] != null ? root["emote_set"]["emotes"] : root["emotes"]) as JArray;
                if (emotes == null) return map;
                foreach (JToken e in emotes)
                {
                    string id = Str(e["id"]), name = Str(e["name"]);
                    if (id.Length == 0 || name.Length == 0 || map.ContainsKey(name)) continue;
                    map[name] = "https://cdn.7tv.app/emote/" + id + "/4x.webp";
                }
            }
            catch (Exception) { }
            return map;
        }
    }

    public partial class OSWLib
    {
        // ── Cache model (also what is saved to thirdparty.json) ───────────────
        public class ThirdPartySource
        {
            public DateTime FetchedAtUtc { get; set; } = DateTime.MinValue;
            public Dictionary<string, string> Emotes { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public class ThirdPartyCacheFile
        {
            public Dictionary<string, ThirdPartySource> Sources { get; set; } =
                new Dictionary<string, ThirdPartySource>(StringComparer.OrdinalIgnoreCase);
        }

        private const string TpTool = "EmoteCache";
        private const string TpFile = "thirdparty";
        private static readonly string[] TpOrder = { "7tv", "bttv", "ffz" };   // priority when names clash

        private static readonly object _tpLock = new object();
        private static ThirdPartyCacheFile _tpCache;                                   // loaded once from disk
        private static readonly Dictionary<string, DateTime> _tpAttempt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);        // last download TRY per service
        private static readonly Dictionary<string, string> _tpError =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);          // last problem per service

        private static readonly HttpClient _tpHttp = CreateTpHttp();
        private static HttpClient CreateTpHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("OSWTools-EmoteLookup/1.0");
            return c;
        }

        /// <summary>
        /// Test hook: set this to answer every web request without the internet.
        /// Return the JSON text, or null for "404 not found". Leave null in real use.
        /// </summary>
        public static Func<string, string> ThirdPartyHttpGet;

        // One web request. Returns the body, or null for 404 (e.g. a channel with no 7TV account).
        // Any other problem throws, so the caller can report it.
        private static string TpGet(string url)
        {
            if (ThirdPartyHttpGet != null) return ThirdPartyHttpGet(url);
            using (HttpResponseMessage resp = _tpHttp.GetAsync(url).GetAwaiter().GetResult())
            {
                if ((int)resp.StatusCode == 404) return null;
                if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        private static bool TpEnabled(string source, bool use7tv, bool useBttv, bool useFfz)
        {
            return source == "7tv" ? use7tv : source == "bttv" ? useBttv : useFfz;
        }

        // Load the file once per Streamer.bot session. Caller holds _tpLock.
        private void TpLoad()
        {
            if (_tpCache != null) return;
            try { _tpCache = OSWData.LoadOrDefault(TpTool, TpFile, new ThirdPartyCacheFile()); }
            catch (Exception) { _tpCache = new ThirdPartyCacheFile(); }
            if (_tpCache == null) _tpCache = new ThirdPartyCacheFile();
        }

        private ThirdPartySource TpSource(string source)
        {
            ThirdPartySource s;
            if (!_tpCache.Sources.TryGetValue(source, out s) || s == null)
            {
                s = new ThirdPartySource();
                _tpCache.Sources[source] = s;
            }
            return s;
        }

        // Download one service. Returns the merged { name -> picture } (channel first, then global).
        // Throws only if BOTH requests failed (a missing channel account is fine).
        private Dictionary<string, string> TpDownload(string source, string broadcasterId)
        {
            Func<string, Func<string, Dictionary<string, string>>, Dictionary<string, string>> fetch =
                (url, parse) => { string body = TpGet(url); return body == null ? null : parse(body); };

            string channelUrl, globalUrl;
            Func<string, Dictionary<string, string>> parseChannel, parseGlobal;
            if (source == "7tv")
            {
                channelUrl = "https://7tv.io/v3/users/twitch/" + broadcasterId;
                globalUrl = "https://7tv.io/v3/emote-sets/global";
                parseChannel = ThirdPartyEmoteParsers.ParseSevenTv;
                parseGlobal = ThirdPartyEmoteParsers.ParseSevenTv;
            }
            else if (source == "bttv")
            {
                channelUrl = "https://api.betterttv.net/3/cached/users/twitch/" + broadcasterId;
                globalUrl = "https://api.betterttv.net/3/cached/emotes/global";
                parseChannel = ThirdPartyEmoteParsers.ParseBttvChannel;
                parseGlobal = ThirdPartyEmoteParsers.ParseBttvGlobal;
            }
            else
            {
                channelUrl = "https://api.frankerfacez.com/v1/room/id/" + broadcasterId;
                globalUrl = "https://api.frankerfacez.com/v1/set/global";
                parseChannel = ThirdPartyEmoteParsers.ParseFfz;
                parseGlobal = ThirdPartyEmoteParsers.ParseFfz;
            }

            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            int failures = 0, tries = 0;
            string lastProblem = "";

            if (!string.IsNullOrWhiteSpace(broadcasterId))
            {
                tries++;
                try
                {
                    var ch = fetch(channelUrl, parseChannel);
                    if (ch != null) foreach (var kv in ch) if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
                }
                catch (Exception ex) { failures++; lastProblem = "channel: " + ex.Message; }
            }

            tries++;
            try
            {
                var gl = fetch(globalUrl, parseGlobal);
                if (gl != null) foreach (var kv in gl) if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
            }
            catch (Exception ex) { failures++; lastProblem = "global: " + ex.Message; }

            if (failures >= tries) throw new Exception(lastProblem);
            return merged;
        }

        // Make sure one service's list is loaded and fresh enough. Caller holds _tpLock.
        // force = ignore the TTL and the retry pause (the Refresh action).
        // Returns a short text for the report ("" = nothing to say).
        private string TpEnsure(string source, bool force, int ttlHours, TimeSpan minAge)
        {
            ThirdPartySource s = TpSource(source);
            DateTime now = DateTime.UtcNow;

            bool never = s.FetchedAtUtc == DateTime.MinValue;
            bool stale = (now - s.FetchedAtUtc).TotalHours >= ttlHours;
            bool olderThanMin = (now - s.FetchedAtUtc) >= minAge;
            DateTime lastTry;
            bool pauseOver = !_tpAttempt.TryGetValue(source, out lastTry) || (now - lastTry).TotalMinutes >= 5;

            bool due = force || ((never || stale || olderThanMin) && pauseOver);
            if (!due) return "";

            _tpAttempt[source] = now;
            try
            {
                string broadcasterId = "";
                try
                {
                    var b = _CPH.TwitchGetBroadcaster();
                    broadcasterId = b == null ? "" : (b.UserId ?? "");
                }
                catch (Exception) { /* global set only */ }

                Dictionary<string, string> list = TpDownload(source, broadcasterId);
                s.Emotes = list;
                s.FetchedAtUtc = DateTime.UtcNow;
                _tpError.Remove(source);
                try { OSWData.Save(TpTool, TpFile, _tpCache); } catch (Exception) { /* cache file is a bonus */ }
                return source.ToUpperInvariant() + ": " + list.Count + " emotes" + (broadcasterId.Length == 0 ? " (global only: no broadcaster id)" : "");
            }
            catch (Exception ex)
            {
                _tpError[source] = ex.Message;
                LogWarn("[Emotes] " + source.ToUpperInvariant() + " download failed, keeping the old list (" + s.Emotes.Count + "): " + ex.Message);
                return source.ToUpperInvariant() + ": FAILED - " + ex.Message + " (kept " + s.Emotes.Count + " cached)";
            }
        }

        /// <summary>The picture link of a 7TV/BTTV/FFZ emote, or "" if the enabled services don't have it.</summary>
        public string GetThirdPartyEmoteImage(string name, bool use7tv, bool useBttv, bool useFfz, int ttlHours = 6)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (!use7tv && !useBttv && !useFfz) return "";

            lock (_tpLock)
            {
                TpLoad();
                foreach (string src in TpOrder)
                    if (TpEnabled(src, use7tv, useBttv, useFfz)) TpEnsure(src, false, ttlHours, TimeSpan.MaxValue);

                string url = TpFind(name, use7tv, useBttv, useFfz);
                if (url.Length > 0) return url;

                // Unknown word: maybe the streamer added the emote recently. Allow ONE
                // refresh per service per 10 minutes, then look again.
                bool refreshed = false;
                foreach (string src in TpOrder)
                    if (TpEnabled(src, use7tv, useBttv, useFfz) && TpEnsure(src, false, ttlHours, TimeSpan.FromMinutes(10)).Length > 0)
                        refreshed = true;
                return refreshed ? TpFind(name, use7tv, useBttv, useFfz) : "";
            }
        }

        private string TpFind(string name, bool use7tv, bool useBttv, bool useFfz)
        {
            foreach (string src in TpOrder)
            {
                if (!TpEnabled(src, use7tv, useBttv, useFfz)) continue;
                string url;
                if (TpSource(src).Emotes.TryGetValue(name, out url)) return url;
            }
            return "";
        }

        /// <summary>Download fresh lists now (ignores the timers). Returns a multi-line report.</summary>
        public string RefreshThirdPartyEmotes(bool use7tv, bool useBttv, bool useFfz)
        {
            var lines = new List<string>();
            lock (_tpLock)
            {
                TpLoad();
                foreach (string src in TpOrder)
                {
                    if (!TpEnabled(src, use7tv, useBttv, useFfz)) { lines.Add(src.ToUpperInvariant() + ": off in Settings"); continue; }
                    lines.Add(TpEnsure(src, true, 6, TimeSpan.Zero));
                }
            }
            return string.Join("\n  ", lines);
        }

        /// <summary>One line: what is cached (no download). For the Status action.</summary>
        public string ThirdPartySummary(bool use7tv, bool useBttv, bool useFfz)
        {
            lock (_tpLock)
            {
                TpLoad();
                var parts = new List<string>();
                foreach (string src in TpOrder)
                {
                    string label = src == "7tv" ? "7TV" : src == "bttv" ? "BTTV" : "FFZ";
                    if (!TpEnabled(src, use7tv, useBttv, useFfz)) { parts.Add(label + " off"); continue; }
                    ThirdPartySource s = TpSource(src);
                    string err;
                    parts.Add(label + " " + (s.FetchedAtUtc == DateTime.MinValue ? "not loaded yet" : s.Emotes.Count + " emotes") +
                              (_tpError.TryGetValue(src, out err) ? " (last problem: " + err + ")" : ""));
                }
                return string.Join(", ", parts);
            }
        }
    }
}
