// =============================================================================
// OSWTools — Extensions/EmoteExtensions.cs
//
// Twitch emote helpers — shared across tools.
//
// PUBLIC API (added to OSWLib via partial class):
//   HashSet<string> GetChannelEmotes(int ttlHours = 6)
//   HashSet<string> GetGlobalEmotes(int ttlHours = 6)
//   bool            IsTwitchEmoteToken(string token, string msg, string emoteData)
//   bool            RefreshEmoteCache(bool force = true)
//   bool            IsKnownEmote(string token)        // convenience: channel ∪ global
//   string          GetEmoteImageUrl(string name, int ttlHours = 6)   // NEW: picture URL, "" if unknown
//
// CHANGELOG
//   NEW  GetEmoteImageUrl(): name -> https picture URL, built from the Helix
//        emote ID that this cache was already downloading (it just wasn't
//        being remembered in memory). Purely additive: no existing method,
//        signature or file format changed.
//
// PERSISTENCE:
//   Cache is stored once at OSWData/EmoteCache/twitch.json containing both
//   channel + global emote sets and the fetch timestamp. Multiple tools share
//   the same cache file, and an in-memory static cache prevents duplicate
//   fetches within a single SB session.
//
// AUTH:
//   Uses CPH.TwitchClientId + CPH.TwitchOAuthToken which are exposed by
//   Streamer.bot's IInlineInvokeProxy.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OSWTools.Data;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ── Public model ──────────────────────────────────────────────────────
        // Shape mirrors Helix /chat/emotes response so tools can deserialize raw JSON
        // if they ever need to (e.g. to access tier or owner_id).
        public class TwitchEmote
        {
            public string id { get; set; }
            public string name { get; set; }
            public string tier { get; set; }
            public string emote_type { get; set; }
            public string emote_set_id { get; set; }
            public string owner_id { get; set; }
        }

        // ── Cache payload (stored as one JSON file) ───────────────────────────
        private class EmoteCachePayload
        {
            public List<TwitchEmote> Channel { get; set; } = new List<TwitchEmote>();
            public List<TwitchEmote> Global  { get; set; } = new List<TwitchEmote>();
            public DateTime FetchedAtUtc { get; set; } = DateTime.MinValue;
        }

        // Internal Helix response wrapper
        private class TwitchEmoteResponse { public List<TwitchEmote> data { get; set; } }

        // ── Static state shared across tools in the same SB session ───────────
        private static readonly object _emoteCacheLock = new object();
        private static HashSet<string> _channelEmoteCache =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _globalEmoteCache =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // NEW: emote name -> Helix emote id, filled every time the name sets above
        // are filled. Lets us build a picture URL for any cached emote.
        private static Dictionary<string, string> _emoteIdByName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _emoteCacheLoadedAtUtc = DateTime.MinValue;
        private static bool _emoteCacheBootstrapped = false;

        private const int    DefaultEmoteTtlHours = 6;
        private const string EmoteCacheTool       = "EmoteCache";
        private const string EmoteCacheFile       = "twitch";

        // Lazy-init HttpClient so the DLL doesn't open a socket unless asked.
        // Static so multiple tools/threads share the same connection pool.
        private static readonly HttpClient _emoteHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Get the channel's subscriber + bit emote names
        // ─────────────────────────────────────────────────────────────────────
        public HashSet<string> GetChannelEmotes(int ttlHours = DefaultEmoteTtlHours)
        {
            EnsureEmotesLoaded(false, ttlHours);
            // Return a defensive copy — callers shouldn't mutate the shared set
            return new HashSet<string>(_channelEmoteCache, StringComparer.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Get Twitch's global emote names
        // ─────────────────────────────────────────────────────────────────────
        public HashSet<string> GetGlobalEmotes(int ttlHours = DefaultEmoteTtlHours)
        {
            EnsureEmotesLoaded(false, ttlHours);
            return new HashSet<string>(_globalEmoteCache, StringComparer.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Convenience — true if the token matches any known emote
        // ─────────────────────────────────────────────────────────────────────
        public bool IsKnownEmote(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            EnsureEmotesLoaded(false, DefaultEmoteTtlHours);
            return _channelEmoteCache.Contains(token) || _globalEmoteCache.Contains(token);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC (NEW): picture URL for a Twitch emote name, or "" if the name
        // isn't a channel/global Twitch emote we know about.
        //
        // Third-party emotes (BTTV / FFZ / 7TV) are NOT in Helix, so they return
        // "" here — callers should fall back to the image URL Streamer.bot
        // gives them in the chat message's `emotes` argument.
        //
        // URL pattern (Twitch's public emote CDN):
        //   https://static-cdn.jtvnw.net/emoticons/v2/{id}/{format}/{theme}/{scale}
        // ─────────────────────────────────────────────────────────────────────
        public string GetEmoteImageUrl(string name, int ttlHours = DefaultEmoteTtlHours)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            EnsureEmotesLoaded(false, ttlHours);

            lock (_emoteCacheLock)
            {
                string id;
                if (_emoteIdByName.TryGetValue(name.Trim(), out id) && !string.IsNullOrEmpty(id))
                    return "https://static-cdn.jtvnw.net/emoticons/v2/" + id + "/default/dark/3.0";
            }
            return "";
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Force a refresh from Helix (bypasses TTL)
        //   Use case: settings form's manual "Refresh Emotes" button.
        // ─────────────────────────────────────────────────────────────────────
        public bool RefreshEmoteCache(bool force = true)
        {
            return EnsureEmotesLoaded(force, DefaultEmoteTtlHours);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Validate a token against Twitch's IRC `emotes` tag.
        //
        // Twitch IRC tags include positions like "12345:0-3,5-8/67890:10-15"
        // meaning emote ID 12345 occupies char ranges 0-3 and 5-8 in the message.
        // This method confirms a token's exact char range matches one of those.
        //
        // Pure logic — no state, no network, no exceptions thrown.
        // ─────────────────────────────────────────────────────────────────────
        public bool IsTwitchEmoteToken(string token, string msg, string emoteData)
        {
            if (string.IsNullOrWhiteSpace(emoteData)) return false;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(msg)) return false;

            try
            {
                int idx = msg.IndexOf(token, StringComparison.Ordinal);
                if (idx < 0) return false;

                // Format: "<emoteId>:<start>-<end>,<start>-<end>/<emoteId>:..."
                foreach (var entry in emoteData.Split('/'))
                {
                    int colon = entry.IndexOf(':');
                    if (colon < 0) continue;

                    string positions = entry.Substring(colon + 1);
                    foreach (var range in positions.Split(','))
                    {
                        var parts = range.Split('-');
                        if (parts.Length < 2) continue;

                        if (int.TryParse(parts[0], out int start)
                            && int.TryParse(parts[1], out int end)
                            && start == idx
                            && end == idx + token.Length - 1)
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug("[Emotes] IsTwitchEmoteToken parse failed: " + ex.Message);
            }

            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        // INTERNAL: Cache loading / TTL handling
        //
        // Three-tier loading strategy:
        //   1. In-memory static cache (instant, shared across tools)
        //   2. OSWData/EmoteCache/twitch.json (persists across SB restarts)
        //   3. Fresh Helix fetch (only when forced or both above are stale)
        //
        // Returns true if the cache is populated (from any tier), false on
        // total failure (no cache + Helix unreachable).
        // ═════════════════════════════════════════════════════════════════════
        private bool EnsureEmotesLoaded(bool forceRefresh, int ttlHours)
        {
            try
            {
                lock (_emoteCacheLock)
                {
                    // Tier 1: in-memory cache fresh?
                    if (!forceRefresh && _emoteCacheBootstrapped
                        && (_channelEmoteCache.Count > 0 || _globalEmoteCache.Count > 0)
                        && _emoteCacheLoadedAtUtc != DateTime.MinValue
                        && (DateTime.UtcNow - _emoteCacheLoadedAtUtc).TotalHours < ttlHours)
                    {
                        return true;
                    }

                    // Tier 2: try the OSWData file
                    if (!forceRefresh)
                    {
                        var cached = OSWData.LoadOrDefault(
                            EmoteCacheTool, EmoteCacheFile, new EmoteCachePayload());

                        bool fileFresh = cached.FetchedAtUtc != DateTime.MinValue
                            && (DateTime.UtcNow - cached.FetchedAtUtc).TotalHours < ttlHours;

                        if (fileFresh && (cached.Channel.Count > 0 || cached.Global.Count > 0))
                        {
                            _channelEmoteCache = new HashSet<string>(
                                cached.Channel.Select(e => e.name),
                                StringComparer.OrdinalIgnoreCase);
                            _globalEmoteCache = new HashSet<string>(
                                cached.Global.Select(e => e.name),
                                StringComparer.OrdinalIgnoreCase);
                            RebuildEmoteIdMap(cached.Channel, cached.Global);   // NEW
                            _emoteCacheLoadedAtUtc = cached.FetchedAtUtc;
                            _emoteCacheBootstrapped = true;
                            LogInfo($"[Emotes] Loaded from cache file. " +
                                    $"Channel={_channelEmoteCache.Count} " +
                                    $"Global={_globalEmoteCache.Count}");
                            return true;
                        }
                    }

                    // Tier 3: fresh fetch from Helix
                    return FetchAndPersistEmotes();
                }
            }
            catch (Exception ex)
            {
                LogError("[Emotes] EnsureEmotesLoaded failed: " + ex.Message);
                // Return whether we have anything at all in memory (better than nothing)
                return _channelEmoteCache.Count > 0 || _globalEmoteCache.Count > 0;
            }
        }

        // NEW. Remembers name -> id for every emote in both lists. Channel is added
        // last so, if a name appears in both, the channel emote wins.
        // Caller must hold _emoteCacheLock.
        private static void RebuildEmoteIdMap(List<TwitchEmote> channel, List<TwitchEmote> global)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in new[] { global, channel })
            {
                if (list == null) continue;
                foreach (var e in list)
                    if (e != null && !string.IsNullOrEmpty(e.name) && !string.IsNullOrEmpty(e.id))
                        map[e.name] = e.id;
            }
            _emoteIdByName = map;
        }

        // Hits Helix for both channel + global emotes, saves to disk, updates memory.
        // Caller must hold _emoteCacheLock.
        private bool FetchAndPersistEmotes()
        {
            try
            {
                // Get broadcaster ID via CPH (already wrapped — never throws)
                var broadcaster = _CPH.TwitchGetBroadcaster();
                string broadcasterId = broadcaster?.UserId ?? "";

                if (string.IsNullOrWhiteSpace(broadcasterId))
                {
                    LogWarn("[Emotes] No broadcaster ID — keeping existing cache.");
                    return _channelEmoteCache.Count > 0 || _globalEmoteCache.Count > 0;
                }

                // Channel emotes
                var channelList = FetchHelixEmotesAsync(
                    $"https://api.twitch.tv/helix/chat/emotes?broadcaster_id={broadcasterId}")
                    .GetAwaiter().GetResult();

                // Global emotes
                var globalList = FetchHelixEmotesAsync(
                    "https://api.twitch.tv/helix/chat/emotes/global")
                    .GetAwaiter().GetResult();

                // Update memory cache
                _channelEmoteCache = new HashSet<string>(
                    channelList.Select(e => e.name), StringComparer.OrdinalIgnoreCase);
                _globalEmoteCache = new HashSet<string>(
                    globalList.Select(e => e.name), StringComparer.OrdinalIgnoreCase);
                RebuildEmoteIdMap(channelList, globalList);   // NEW
                _emoteCacheLoadedAtUtc = DateTime.UtcNow;
                _emoteCacheBootstrapped = true;

                // Persist
                var payload = new EmoteCachePayload
                {
                    Channel = channelList,
                    Global  = globalList,
                    FetchedAtUtc = _emoteCacheLoadedAtUtc
                };
                OSWData.Save(EmoteCacheTool, EmoteCacheFile, payload);

                LogInfo($"[Emotes] Refreshed from Helix. " +
                        $"Channel={_channelEmoteCache.Count} " +
                        $"Global={_globalEmoteCache.Count}");
                return true;
            }
            catch (Exception ex)
            {
                LogError("[Emotes] Helix fetch failed: " + ex.Message);
                return _channelEmoteCache.Count > 0 || _globalEmoteCache.Count > 0;
            }
        }

        // Single Helix GET → parse → return list. Used for both channel + global endpoints.
        private async Task<List<TwitchEmote>> FetchHelixEmotesAsync(string url)
        {
            using (var req = CreateTwitchHelixRequest(url))
            {
                var resp = await _emoteHttp.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    LogWarn($"[Emotes] Helix returned {resp.StatusCode} for {url}");
                    return new List<TwitchEmote>();
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonConvert.DeserializeObject<TwitchEmoteResponse>(body);
                return parsed?.data ?? new List<TwitchEmote>();
            }
        }

        // Builds an authenticated Helix request using the live CPH proxy's tokens.
        // Marked private but factored out so other Helix endpoints (future:
        // raids, polls, predictions) can reuse the auth pattern.
        private HttpRequestMessage CreateTwitchHelixRequest(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);

            string clientId = _CPH.TwitchClientId;
            string token = _CPH.TwitchOAuthToken;

            if (!string.IsNullOrWhiteSpace(clientId))
                req.Headers.Add("Client-Id", clientId);

            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

            return req;
        }
    }
}
