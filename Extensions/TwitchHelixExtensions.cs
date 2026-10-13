////TwitchHelixExtensions.cs//
// =============================================================================
// NEW FILE — Extensions\TwitchHelixExtensions.cs
// A general Twitch API ("Helix") client for every OSW tool.
//
// WHOSE LOGIN IS USED
//   By default: Streamer.bot's own Twitch broadcaster login. Nothing to set
//   up — no developer app, no client secret, no browser pop-up.
//
//   Optionally: a saved "auth profile" — your OWN Twitch app's login,
//   obtained once with Device Code sign-in (the streamer visits
//   twitch.tv/activate and types a code). Use this when a tool needs a
//   permission (scope) Streamer.bot's login doesn't include.
//
//     lib.TwitchAuthProfile = "multischeduler";   // use the saved profile
//     lib.TwitchAuthProfile = null;               // back to Streamer.bot's login
//
// Profiles refresh themselves and are stored ENCRYPTED for the current
// Windows user (see OSWSecretStore), never as readable text.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    // ── Models ───────────────────────────────────────────────────────────────

    /// <summary>What Twitch's /oauth2/validate says about a token.</summary>
    public class TwitchTokenInfo
    {
        public string UserId { get; set; } = "";
        public string Login { get; set; } = "";
        public string ClientId { get; set; } = "";
        public List<string> Scopes { get; set; } = new List<string>();
        public int ExpiresInSeconds { get; set; }
    }

    public class TwitchUser
    {
        public string Id { get; set; } = "";
        public string Login { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ProfileImageUrl { get; set; } = "";
        public string OfflineImageUrl { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>"partner", "affiliate", or "" (normal).</summary>
        public string BroadcasterType { get; set; } = "";
        public string ChannelUrl => "https://twitch.tv/" + Login;
    }

    /// <summary>A Twitch category (game). Same thing Twitch calls a "game" in its API.</summary>
    public class TwitchCategory
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string BoxArtUrl { get; set; } = "";
        public string IgdbId { get; set; } = "";

        /// <summary>
        /// Box art at any size. Twitch returns it either as a template
        /// ("...-{width}x{height}.jpg", from Get Games) or pre-sized
        /// ("...-52x72.jpg", from Search) — this handles both. 285x380 is
        /// the standard full-size box art.
        /// </summary>
        public string GetBoxArtUrl(int width = 285, int height = 380)
        {
            if (string.IsNullOrEmpty(BoxArtUrl)) return "";
            string size = width + "x" + height;
            if (BoxArtUrl.Contains("{width}"))
                return BoxArtUrl.Replace("{width}", width.ToString(CultureInfo.InvariantCulture))
                                .Replace("{height}", height.ToString(CultureInfo.InvariantCulture));
            return Regex.Replace(BoxArtUrl, @"-\d+x\d+(\.\w+)$", "-" + size + "$1");
        }
    }

    /// <summary>Step 1 of Device Code sign-in: show UserCode + VerificationUri to the streamer.</summary>
    public class TwitchDeviceAuthStart
    {
        public string DeviceCode { get; set; } = "";
        /// <summary>The code the streamer types at twitch.tv/activate.</summary>
        public string UserCode { get; set; } = "";
        /// <summary>Open this for them — it already has the code filled in.</summary>
        public string VerificationUri { get; set; } = "";
        public int IntervalSeconds { get; set; } = 5;
        public DateTime ExpiresUtc { get; set; }
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public List<string> Scopes { get; set; } = new List<string>();
    }

    public enum TwitchDeviceAuthStatus { Pending, SlowDown, Success, Denied, Expired, Error }

    public class TwitchDeviceAuthPoll
    {
        public TwitchDeviceAuthStatus Status { get; set; }
        public string Message { get; set; } = "";
        /// <summary>Who signed in (on Success).</summary>
        public string Login { get; set; } = "";
    }

    /// <summary>A saved auth profile, minus the secrets — safe to show in a UI.</summary>
    public class TwitchAuthProfileInfo
    {
        public string Name { get; set; } = "";
        public string Login { get; set; } = "";
        public string UserId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public List<string> Scopes { get; set; } = new List<string>();
        public DateTime LastRefreshedUtc { get; set; }
    }

    // The stored form (encrypted at rest). Internal: tokens never leave the DLL.
    internal class TwitchAuthProfileData
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";   // only for "confidential" apps; public apps leave this empty
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
        public DateTime LastRefreshedUtc { get; set; }
        public List<string> Scopes { get; set; } = new List<string>();
        public string UserId { get; set; } = "";
        public string Login { get; set; } = "";
    }

    public partial class OSWLib
    {
        /// <summary>
        /// Whose Twitch login API calls use. null/empty = Streamer.bot's own
        /// login (the default). A name = a profile saved with Device Code
        /// sign-in (StartTwitchDeviceAuth / PollTwitchDeviceAuth).
        /// </summary>
        public string TwitchAuthProfile { get; set; }

        private const string HelixBase = "https://api.twitch.tv/helix/";
        private const string TwitchIdBase = "https://id.twitch.tv/oauth2/";

        // ── Credentials ───────────────────────────────────────────────────────

        private bool TryGetTwitchCredentials(out string token, out string clientId, bool forceRefresh = false)
        {
            if (!string.IsNullOrEmpty(TwitchAuthProfile))
            {
                var p = GetFreshTwitchProfile(TwitchAuthProfile, forceRefresh);
                token = p?.AccessToken ?? "";
                clientId = p?.ClientId ?? "";
                return token.Length > 0 && clientId.Length > 0;
            }

            // Streamer.bot's login, read by REFLECTION on purpose:
            // CPH.TwitchOAuthToken / TwitchClientId are widely used in community
            // code but we couldn't verify them on an official docs page.
            // Writing _CPH.TwitchOAuthToken directly would make the WHOLE DLL
            // fail to build if a name were ever wrong; reflection just returns
            // empty, and callers get a clear error instead.
            token = ReadProxyProperty("TwitchOAuthToken");
            clientId = ReadProxyProperty("TwitchClientId");
            if (token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(6);
            return token.Length > 0 && clientId.Length > 0;
        }

        private string ReadProxyProperty(string name)
        {
            try
            {
                // Check the object's own type, then each interface: a property
                // implemented "explicitly" is only visible through its interface.
                var types = new List<Type> { _CPH.GetType() };
                types.AddRange(_CPH.GetType().GetInterfaces());
                foreach (var t in types)
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null)
                        return p.GetValue(_CPH)?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                LogWarn("Couldn't read CPH." + name + ": " + ex.Message);
            }
            return "";
        }

        private string NoLoginMessage()
            => string.IsNullOrEmpty(TwitchAuthProfile)
                ? "Couldn't get Streamer.bot's Twitch login. Is the Twitch broadcaster account connected?"
                : "Twitch sign-in \"" + TwitchAuthProfile + "\" is missing or expired. Sign in again (Device Code sign-in).";

        // ── Validate ──────────────────────────────────────────────────────────

        private static readonly Dictionary<string, Tuple<TwitchTokenInfo, DateTime>> TokenInfoCache
            = new Dictionary<string, Tuple<TwitchTokenInfo, DateTime>>();
        private static readonly object TokenInfoLock = new object();
        private static readonly TimeSpan TokenInfoTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Asks Twitch who the current login belongs to and what it may do.
        /// Cached for 10 minutes per token (a refreshed token is a new token,
        /// so it's re-checked automatically).
        /// </summary>
        public OSWApiResult<TwitchTokenInfo> ValidateTwitchToken(bool forceRefresh = false)
        {
            if (!TryGetTwitchCredentials(out string token, out _))
                return OSWApiResult<TwitchTokenInfo>.Fail(NoLoginMessage());

            lock (TokenInfoLock)
            {
                if (!forceRefresh && TokenInfoCache.TryGetValue(token, out var hit)
                    && DateTime.UtcNow - hit.Item2 < TokenInfoTtl)
                    return OSWApiResult<TwitchTokenInfo>.Ok(hit.Item1);
            }
            var r = ValidateRawToken(token);
            if (r.Success)
                lock (TokenInfoLock) { TokenInfoCache[token] = Tuple.Create(r.Data, DateTime.UtcNow); }
            return r;
        }

        private static OSWApiResult<TwitchTokenInfo> ValidateRawToken(string token)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, TwitchIdBase + "validate");
                req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
                var resp = OSWHttp.Send(req);
                string body = OSWHttp.ReadBody(resp);
                if (!resp.IsSuccessStatusCode)
                    return OSWApiResult<TwitchTokenInfo>.Fail("Twitch rejected the login (" + (int)resp.StatusCode + ").", (int)resp.StatusCode);
                var j = JObject.Parse(body);
                return OSWApiResult<TwitchTokenInfo>.Ok(new TwitchTokenInfo
                {
                    UserId = (string)j["user_id"] ?? "",
                    Login = (string)j["login"] ?? "",
                    ClientId = (string)j["client_id"] ?? "",
                    Scopes = j["scopes"]?.ToObject<List<string>>() ?? new List<string>(),
                    ExpiresInSeconds = (int?)j["expires_in"] ?? 0
                });
            }
            catch (Exception ex)
            {
                return OSWApiResult<TwitchTokenInfo>.Fail("Token check failed: " + ex.Message);
            }
        }

        /// <summary>True if the current login includes the given scope.</summary>
        public bool TwitchTokenHasScope(string scope)
        {
            var v = ValidateTwitchToken();
            return v.Success && v.Data.Scopes.Contains(scope);
        }

        /// <summary>The Twitch user ID of whoever the current login belongs to.</summary>
        public string GetTwitchBroadcasterIdFromToken()
        {
            var v = ValidateTwitchToken();
            return v.Success ? v.Data.UserId : "";
        }

        // ── Raw Helix request ─────────────────────────────────────────────────

        /// <summary>
        /// Any Twitch API call. pathAndQuery is relative to
        /// https://api.twitch.tv/helix/ (e.g. "schedule?broadcaster_id=123").
        /// jsonBody is serialized to JSON. Returns the raw response body.
        /// On a 401 it gets a fresh token (refreshing a profile if needed)
        /// and retries once.
        /// </summary>
        public OSWApiResult<string> TwitchHelixRequest(HttpMethod method, string pathAndQuery, object jsonBody = null)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (!TryGetTwitchCredentials(out string token, out string clientId, forceRefresh: attempt == 2))
                    return OSWApiResult<string>.Fail(NoLoginMessage());
                try
                {
                    var req = new HttpRequestMessage(method, HelixBase + pathAndQuery.TrimStart('/'));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Add("Client-Id", clientId);
                    if (jsonBody != null)
                        req.Content = new StringContent(JsonConvert.SerializeObject(jsonBody), Encoding.UTF8, "application/json");

                    var resp = OSWHttp.Send(req);
                    int code = (int)resp.StatusCode;
                    string body = OSWHttp.ReadBody(resp);
                    if (code == 401 && attempt == 1)
                        continue; // stale token — try once more with a fresh one
                    return resp.IsSuccessStatusCode
                        ? OSWApiResult<string>.Ok(body, code)
                        : OSWApiResult<string>.Fail(DescribeHelixError(code, body), code);
                }
                catch (Exception ex)
                {
                    return OSWApiResult<string>.Fail("Request to Twitch failed: " + ex.Message);
                }
            }
            return OSWApiResult<string>.Fail("Twitch rejected the login twice in a row. " + NoLoginMessage(), 401);
        }

        private static string DescribeHelixError(int code, string body)
        {
            string twitchMsg = "";
            try { twitchMsg = (string)JObject.Parse(body)["message"] ?? ""; } catch { }
            string hint;
            switch (code)
            {
                case 400: hint = "Twitch didn't accept the request"; break;
                case 401: hint = "Not authorized — the login may be missing a permission (scope)"; break;
                case 403: hint = "Not allowed — some features are Partner/Affiliate only"; break;
                case 404: hint = "Not found"; break;
                case 409: hint = "Conflicts with something that already exists"; break;
                case 429: hint = "Too many requests — Twitch is rate-limiting, try again shortly"; break;
                default: hint = "Twitch returned an error"; break;
            }
            return hint + " (" + code + ")" + (twitchMsg.Length > 0 ? ": " + twitchMsg : ".");
        }

        // ── Users ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Looks up Twitch users by login name and/or ID (up to 100 total).
        /// Pass nothing to get the logged-in user. Handy for profile pictures
        /// and display names in announcements.
        /// </summary>
        public OSWApiResult<List<TwitchUser>> GetTwitchUsers(IEnumerable<string> logins = null, IEnumerable<string> ids = null)
        {
            var q = new List<string>();
            if (logins != null) q.AddRange(logins.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => "login=" + Uri.EscapeDataString(s.Trim())));
            if (ids != null) q.AddRange(ids.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => "id=" + Uri.EscapeDataString(s.Trim())));
            var r = TwitchHelixRequest(HttpMethod.Get, "users" + (q.Count > 0 ? "?" + string.Join("&", q.Take(100)) : ""));
            if (!r.Success) return r.As<List<TwitchUser>>();
            try
            {
                var list = ((JArray)JObject.Parse(r.Data)["data"] ?? new JArray()).OfType<JObject>().Select(u => new TwitchUser
                {
                    Id = (string)u["id"] ?? "",
                    Login = (string)u["login"] ?? "",
                    DisplayName = (string)u["display_name"] ?? "",
                    ProfileImageUrl = (string)u["profile_image_url"] ?? "",
                    OfflineImageUrl = (string)u["offline_image_url"] ?? "",
                    Description = (string)u["description"] ?? "",
                    BroadcasterType = (string)u["broadcaster_type"] ?? ""
                }).ToList();
                return OSWApiResult<List<TwitchUser>>.Ok(list, r.StatusCode);
            }
            catch (Exception ex) { return OSWApiResult<List<TwitchUser>>.Fail("Couldn't read Twitch's user data: " + ex.Message); }
        }

        // ── Categories ────────────────────────────────────────────────────────

        /// <summary>
        /// Searches categories/games by name ("blender" → Art, Blender…),
        /// e.g. for a scheduler's category picker. Works with any login.
        /// </summary>
        public OSWApiResult<List<TwitchCategory>> SearchTwitchCategories(string query, int first = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return OSWApiResult<List<TwitchCategory>>.Ok(new List<TwitchCategory>());
            var r = TwitchHelixRequest(HttpMethod.Get, "search/categories?query=" + Uri.EscapeDataString(query.Trim())
                + "&first=" + Math.Max(1, Math.Min(100, first)));
            return r.Success ? ParseCategories(r) : r.As<List<TwitchCategory>>();
        }

        /// <summary>Exact category lookup by ID and/or exact name (up to 100 total).</summary>
        public OSWApiResult<List<TwitchCategory>> GetTwitchCategories(IEnumerable<string> ids = null, IEnumerable<string> names = null)
        {
            var q = new List<string>();
            if (ids != null) q.AddRange(ids.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => "id=" + Uri.EscapeDataString(s.Trim())));
            if (names != null) q.AddRange(names.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => "name=" + Uri.EscapeDataString(s.Trim())));
            if (q.Count == 0)
                return OSWApiResult<List<TwitchCategory>>.Ok(new List<TwitchCategory>());
            var r = TwitchHelixRequest(HttpMethod.Get, "games?" + string.Join("&", q.Take(100)));
            return r.Success ? ParseCategories(r) : r.As<List<TwitchCategory>>();
        }

        private static OSWApiResult<List<TwitchCategory>> ParseCategories(OSWApiResult<string> r)
        {
            try
            {
                var list = ((JArray)JObject.Parse(r.Data)["data"] ?? new JArray()).OfType<JObject>().Select(c => new TwitchCategory
                {
                    Id = (string)c["id"] ?? "",
                    Name = (string)c["name"] ?? "",
                    BoxArtUrl = (string)c["box_art_url"] ?? "",
                    IgdbId = (string)c["igdb_id"] ?? ""
                }).ToList();
                return OSWApiResult<List<TwitchCategory>>.Ok(list, r.StatusCode);
            }
            catch (Exception ex) { return OSWApiResult<List<TwitchCategory>>.Fail("Couldn't read Twitch's category data: " + ex.Message); }
        }

        // =====================================================================
        //  Device Code sign-in (for auth profiles)
        //  https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#device-code-grant-flow
        //
        //  Replaces the old "start a web server on localhost:6969 and catch the
        //  redirect" approach. Nothing listens on a port; the streamer just
        //  visits twitch.tv/activate and confirms a code. Register the Twitch
        //  app as a PUBLIC client and no client secret is needed at all.
        //
        //  UI pattern (keeps every call short — nothing blocks for minutes):
        //    var s = lib.StartTwitchDeviceAuth(clientId, new[] { "channel:manage:schedule" });
        //    show s.Data.UserCode, open s.Data.VerificationUri in the browser
        //    every s.Data.IntervalSeconds (e.g. a WinForms Timer):
        //        var p = lib.PollTwitchDeviceAuth("multischeduler", s.Data);
        //        stop when p.Status is Success, Denied, Expired or Error
        // =====================================================================

        /// <summary>Step 1: ask Twitch for a code to show the streamer.</summary>
        public OSWApiResult<TwitchDeviceAuthStart> StartTwitchDeviceAuth(string clientId, IEnumerable<string> scopes, string clientSecret = null)
        {
            var scopeList = (scopes ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            try
            {
                // Note the parameter really is "scopes" (plural) for this endpoint.
                var resp = OSWHttp.PostForm(TwitchIdBase + "device", new Dictionary<string, string>
                {
                    ["client_id"] = clientId ?? "",
                    ["scopes"] = string.Join(" ", scopeList)
                });
                string body = OSWHttp.ReadBody(resp);
                if (!resp.IsSuccessStatusCode)
                    return OSWApiResult<TwitchDeviceAuthStart>.Fail("Twitch wouldn't start sign-in: " + TwitchMessage(body)
                        + " Check the Client ID, and that the app is registered for Device Code sign-in.", (int)resp.StatusCode);
                var j = JObject.Parse(body);
                return OSWApiResult<TwitchDeviceAuthStart>.Ok(new TwitchDeviceAuthStart
                {
                    DeviceCode = (string)j["device_code"] ?? "",
                    UserCode = (string)j["user_code"] ?? "",
                    VerificationUri = (string)j["verification_uri"] ?? "https://www.twitch.tv/activate",
                    IntervalSeconds = Math.Max(1, (int?)j["interval"] ?? 5),
                    ExpiresUtc = DateTime.UtcNow.AddSeconds((int?)j["expires_in"] ?? 1800),
                    ClientId = clientId,
                    ClientSecret = clientSecret ?? "",
                    Scopes = scopeList
                });
            }
            catch (Exception ex)
            {
                return OSWApiResult<TwitchDeviceAuthStart>.Fail("Couldn't reach Twitch to start sign-in: " + ex.Message);
            }
        }

        /// <summary>
        /// Step 2: ONE check for whether the streamer has finished. Call it every
        /// IntervalSeconds. On Success the login is saved as `profileName`.
        /// </summary>
        public TwitchDeviceAuthPoll PollTwitchDeviceAuth(string profileName, TwitchDeviceAuthStart start)
        {
            if (start == null || string.IsNullOrEmpty(start.DeviceCode))
                return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Error, Message = "Sign-in wasn't started." };
            if (DateTime.UtcNow > start.ExpiresUtc)
                return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Expired, Message = "The code expired. Start again." };
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["client_id"] = start.ClientId,
                    ["scopes"] = string.Join(" ", start.Scopes),
                    ["device_code"] = start.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                };
                if (!string.IsNullOrEmpty(start.ClientSecret)) form["client_secret"] = start.ClientSecret;
                var resp = OSWHttp.PostForm(TwitchIdBase + "token", form);
                string body = OSWHttp.ReadBody(resp);

                if (!resp.IsSuccessStatusCode)
                {
                    string msg = TwitchMessage(body).ToLowerInvariant();
                    if (msg.Contains("pending")) return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Pending, Message = "Waiting for you to approve on Twitch…" };
                    if (msg.Contains("slow")) return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.SlowDown, Message = "Checking too often — slowing down." };
                    if (msg.Contains("denied")) return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Denied, Message = "Sign-in was declined on Twitch." };
                    if (msg.Contains("invalid device code") || msg.Contains("expired"))
                        return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Expired, Message = "The code expired or was already used. Start again." };
                    return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Error, Message = "Twitch said: " + TwitchMessage(body) };
                }

                var t = TokenResponseToProfile(JObject.Parse(body), start.ClientId, start.ClientSecret);
                var who = ValidateRawToken(t.AccessToken);
                if (who.Success) { t.UserId = who.Data.UserId; t.Login = who.Data.Login; }
                SaveTwitchProfile(profileName, t);
                return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Success, Login = t.Login, Message = "Signed in as " + t.Login + "." };
            }
            catch (Exception ex)
            {
                return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Error, Message = "Couldn't reach Twitch: " + ex.Message };
            }
        }

        /// <summary>
        /// Convenience: polls until done or `timeout`. BLOCKS the calling
        /// thread — never call it from a UI thread or a Streamer.bot action
        /// you need back quickly. In a form, prefer PollTwitchDeviceAuth on a Timer.
        /// </summary>
        public TwitchDeviceAuthPoll WaitForTwitchDeviceAuth(string profileName, TwitchDeviceAuthStart start, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            int interval = start?.IntervalSeconds ?? 5;
            while (true)
            {
                var p = PollTwitchDeviceAuth(profileName, start);
                if (p.Status == TwitchDeviceAuthStatus.SlowDown) interval += 5; // the OAuth device-flow standard says: back off 5s
                else if (p.Status != TwitchDeviceAuthStatus.Pending) return p;
                if (DateTime.UtcNow.AddSeconds(interval) > until)
                    return new TwitchDeviceAuthPoll { Status = TwitchDeviceAuthStatus.Expired, Message = "Timed out waiting for sign-in." };
                Thread.Sleep(TimeSpan.FromSeconds(interval));
            }
        }

        // ── Profiles ──────────────────────────────────────────────────────────

        public bool HasTwitchAuthProfile(string profileName) => LoadTwitchProfile(profileName) != null;

        /// <summary>Who a saved profile belongs to and what it may do (no secrets).</summary>
        public TwitchAuthProfileInfo GetTwitchAuthProfileInfo(string profileName)
        {
            var p = LoadTwitchProfile(profileName);
            return p == null ? null : new TwitchAuthProfileInfo
            {
                Name = profileName, Login = p.Login, UserId = p.UserId, ClientId = p.ClientId,
                Scopes = p.Scopes, LastRefreshedUtc = p.LastRefreshedUtc
            };
        }

        /// <summary>Signs out: tells Twitch to cancel the token (by default), then deletes the profile.</summary>
        public void RemoveTwitchAuthProfile(string profileName, bool revokeOnTwitch = true)
        {
            var p = LoadTwitchProfile(profileName);
            if (p != null && revokeOnTwitch && !string.IsNullOrEmpty(p.AccessToken))
            {
                try
                {
                    OSWHttp.PostForm(TwitchIdBase + "revoke", new Dictionary<string, string>
                        { ["client_id"] = p.ClientId, ["token"] = p.AccessToken });
                }
                catch (Exception ex) { LogWarn("Twitch token revoke failed (profile removed anyway): " + ex.Message); }
            }
            OSWSecretStore.Delete(this, TwitchProfileKey(profileName));
        }

        private static string TwitchProfileKey(string name) => "osw_TwitchAuth_" + SafeKeyPart(name);
        private TwitchAuthProfileData LoadTwitchProfile(string name)
            => string.IsNullOrEmpty(name) ? null : OSWSecretStore.Load<TwitchAuthProfileData>(this, TwitchProfileKey(name));
        private void SaveTwitchProfile(string name, TwitchAuthProfileData p)
            => OSWSecretStore.Save(this, TwitchProfileKey(name), p);

        // One lock for all profile refreshes. This matters MORE than usual:
        // Twitch refresh tokens are ONE-TIME USE. If two actions refreshed
        // at the same moment, the second would spend an already-spent
        // token, fail, and leave the profile signed out.
        private static readonly object TwitchRefreshLock = new object();

        private TwitchAuthProfileData GetFreshTwitchProfile(string name, bool force)
        {
            var p = LoadTwitchProfile(name);
            if (p == null) return null;
            if (!force && p.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return p;

            lock (TwitchRefreshLock)
            {
                // Re-read inside the lock: another action may have refreshed
                // while we waited, in which case we use its fresh token.
                p = LoadTwitchProfile(name);
                if (p == null) return null;
                if (!force && p.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return p;
                if (string.IsNullOrEmpty(p.RefreshToken)) return null;
                try
                {
                    var form = new Dictionary<string, string>
                    {
                        ["client_id"] = p.ClientId,
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = p.RefreshToken
                    };
                    if (!string.IsNullOrEmpty(p.ClientSecret)) form["client_secret"] = p.ClientSecret;
                    var resp = OSWHttp.PostForm(TwitchIdBase + "token", form);
                    string body = OSWHttp.ReadBody(resp);
                    if (!resp.IsSuccessStatusCode)
                    {
                        LogWarn("Twitch sign-in \"" + name + "\" couldn't refresh (" + TwitchMessage(body) + "). Sign in again.");
                        return null;
                    }
                    var fresh = TokenResponseToProfile(JObject.Parse(body), p.ClientId, p.ClientSecret);
                    fresh.UserId = p.UserId;
                    fresh.Login = p.Login;
                    if (fresh.Scopes.Count == 0) fresh.Scopes = p.Scopes;
                    SaveTwitchProfile(name, fresh); // save the NEW refresh token — the old one is now dead
                    return fresh;
                }
                catch (Exception ex)
                {
                    LogWarn("Twitch sign-in \"" + name + "\" refresh failed: " + ex.Message);
                    return p.ExpiresUtc > DateTime.UtcNow ? p : null; // network blip: use the old token while it lasts
                }
            }
        }

        private static TwitchAuthProfileData TokenResponseToProfile(JObject j, string clientId, string clientSecret)
        {
            var scopesTok = j["scope"];
            var scopes = scopesTok is JArray a ? a.ToObject<List<string>>()
                       : scopesTok != null ? ((string)scopesTok ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                       : new List<string>();
            return new TwitchAuthProfileData
            {
                ClientId = clientId ?? "",
                ClientSecret = clientSecret ?? "",
                AccessToken = (string)j["access_token"] ?? "",
                RefreshToken = (string)j["refresh_token"] ?? "",
                ExpiresUtc = DateTime.UtcNow.AddSeconds((int?)j["expires_in"] ?? 3600),
                LastRefreshedUtc = DateTime.UtcNow,
                Scopes = scopes
            };
        }

        private static string TwitchMessage(string body)
        {
            try { return (string)JObject.Parse(body)["message"] ?? body; } catch { return body ?? ""; }
        }
    }
}
