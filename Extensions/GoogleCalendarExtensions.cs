////GoogleCalendarExtensions.cs//
// =============================================================================
// NEW FILE — Extensions\GoogleCalendarExtensions.cs
// Google sign-in + Google Calendar for any OSW tool.
//
// SETUP (one time, by whoever ships the tool): in Google Cloud Console,
// enable the Google Calendar API and create an OAuth client of type
// "Desktop app". That gives a client ID + client secret. Google itself says a
// desktop app's "secret" can't really be kept secret, so shipping it inside a
// tool is normal — the protection comes from PKCE (below).
//
// SIGN-IN (once per streamer):
//   var r = lib.AuthorizeGoogle("multischeduler", clientId, clientSecret);
//   then:  lib.GoogleAuthProfile = "multischeduler";
// The profile refreshes itself after that. NOTE: while the Google Cloud
// project's consent screen is in "Testing" status, Google expires the saved
// sign-in after 7 days. Publish the consent screen to avoid that.
//
// FIXES vs. the old multi-scheduler:
//   • asked for permission on EVERY launch (prompt=consent each run) and
//     never used its refresh token → now: sign in once, auto-refresh.
//   • left its port-1313 listener running → "address already in use" on the
//     next run → now: listener always closed, and Windows picks a free port.
//   • recurring events were sent in UTC → a weekly 7 PM stream would shift to
//     6 or 8 PM when daylight saving changes → now: the streamer's time zone
//     is sent, so Google keeps it at 7 PM local all year.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    public class GoogleCalendarInfo
    {
        public string Id { get; set; } = "";
        public string Summary { get; set; } = "";
        public bool IsPrimary { get; set; }
        /// <summary>"owner", "writer", "reader" or "freeBusyReader".</summary>
        public string AccessRole { get; set; } = "";
        public string TimeZone { get; set; } = "";
        public bool CanWrite => AccessRole == "owner" || AccessRole == "writer";
        public override string ToString() => Summary; // ComboBox-friendly
    }

    public class GoogleCalendarEvent
    {
        public string Id { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
        public string Location { get; set; } = "";
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        /// <summary>Windows or IANA name. Empty = this PC's zone. Recurring events expand in this zone.</summary>
        public string TimeZone { get; set; } = "";
        /// <summary>RRULE lines, e.g. GoogleRecurrence.Weekly(). Empty = one-time event.</summary>
        public List<string> Recurrence { get; set; } = new List<string>();
        /// <summary>Link to the event in Google Calendar (read-only).</summary>
        public string HtmlLink { get; set; } = "";
        /// <summary>"confirmed", "tentative" or "cancelled" (read-only).</summary>
        public string Status { get; set; } = "";
        /// <summary>For one occurrence of a recurring event: the series' ID (read-only).</summary>
        public string RecurringEventId { get; set; } = "";
    }

    /// <summary>Builds RRULE strings for recurring Google Calendar events.</summary>
    public static class GoogleRecurrence
    {
        /// <summary>
        /// Weekly. With no days given it repeats on the start date's weekday.
        /// Optionally stop after `count` occurrences or at `untilUtc`.
        /// </summary>
        public static string Weekly(int? count = null, DateTime? untilUtc = null, params DayOfWeek[] days)
        {
            var sb = new StringBuilder("RRULE:FREQ=WEEKLY");
            if (days != null && days.Length > 0)
                sb.Append(";BYDAY=").Append(string.Join(",", days.Distinct().Select(d => d.ToString().Substring(0, 2).ToUpperInvariant())));
            if (count.HasValue) sb.Append(";COUNT=").Append(count.Value);
            else if (untilUtc.HasValue) sb.Append(";UNTIL=").Append(untilUtc.Value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    internal class GoogleAuthProfileData
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
        public string Scopes { get; set; } = "";
    }

    public partial class OSWLib
    {
        /// <summary>Which saved Google sign-in Google calls use (required — there's no default).</summary>
        public string GoogleAuthProfile { get; set; }

        /// <summary>Full calendar access: needed to list calendars AND manage events.</summary>
        public const string GoogleCalendarScope = "https://www.googleapis.com/auth/calendar";

        private const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
        private const string CalendarBase = "https://www.googleapis.com/calendar/v3/";

        // =====================================================================
        //  Sign-in: "loopback" redirect + PKCE (Google's method for desktop apps)
        //  https://developers.google.com/identity/protocols/oauth2/native-app
        //
        //  1. Listen on 127.0.0.1 at a port Windows picks (port 0 = "any free one").
        //  2. Open Google's sign-in page in the browser, telling it to send
        //     the result to http://127.0.0.1:<that port>.
        //  3. The browser lands there with a one-time code; we answer with a
        //     "you can close this tab" page and stop listening.
        //  4. Trade the code for tokens.
        //
        //  PKCE: we invent a random secret (the "verifier"), send only its
        //  SHA-256 fingerprint (the "challenge") with step 2, and reveal the
        //  verifier in step 4. A program that intercepted the code in step 3
        //  can't use it — it never saw the verifier.
        //
        //  A raw TcpListener rather than HttpListener on purpose: HttpListener
        //  can need Windows URL permissions (admin) for some addresses; a
        //  plain socket on 127.0.0.1 never does.
        // =====================================================================

        /// <summary>
        /// Signs in to Google and saves the result as `profileName`. Opens the
        /// browser and BLOCKS until the streamer finishes or `timeoutSeconds`
        /// passes — call it from a button handler on a background thread,
        /// never from a Streamer.bot action you need back quickly.
        /// If the browser can't be opened, the sign-in link is in the log.
        /// </summary>
        public OSWApiResult<bool> AuthorizeGoogle(string profileName, string clientId, string clientSecret,
            IEnumerable<string> scopes = null, int timeoutSeconds = 180)
        {
            string scope = string.Join(" ", (scopes ?? new[] { GoogleCalendarScope }).Distinct());
            string verifier = OSWHttp.RandomUrlSafe(32); // 43 chars — PKCE's minimum length
            string challenge;
            using (var sha = SHA256.Create())
                challenge = OSWHttp.Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = OSWHttp.RandomUrlSafe(16);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port;

                string url = GoogleAuthUrl
                    + "?client_id=" + Uri.EscapeDataString(clientId ?? "")
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&response_type=code"
                    + "&scope=" + Uri.EscapeDataString(scope)
                    + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                    + "&state=" + state
                    + "&access_type=offline"   // ask for a refresh token…
                    + "&prompt=consent";       // …and make sure Google actually sends one
                LogInfo("Google sign-in page: " + url);
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                catch (Exception ex) { LogWarn("Couldn't open the browser (" + ex.Message + "). Open the Google sign-in link from the log manually."); }

                var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, timeoutSeconds));
                while (DateTime.UtcNow < deadline)
                {
                    // Pending() instead of a blocking Accept, so we can give up
                    // at the deadline instead of waiting forever.
                    if (!listener.Pending()) { Thread.Sleep(200); continue; }

                    using (var client = listener.AcceptTcpClient())
                    {
                        var q = ReadLoopbackRequest(client); // null = not the redirect (e.g. a favicon request)
                        if (q == null) { WriteLoopbackPage(client, 404, "Not found"); continue; }

                        if (q.TryGetValue("error", out string err))
                        {
                            WriteLoopbackPage(client, 200, "Sign-in was cancelled. You can close this tab.");
                            return OSWApiResult<bool>.Fail("Google sign-in was cancelled (" + err + ").");
                        }
                        if (!q.TryGetValue("state", out string gotState) || gotState != state)
                        {
                            // A mismatched state means this request didn't come from
                            // OUR sign-in — exactly the forgery "state" exists to stop.
                            WriteLoopbackPage(client, 400, "This sign-in link didn't match. Please try again.");
                            return OSWApiResult<bool>.Fail("Google sign-in response didn't match the request (state mismatch). Try again.");
                        }
                        q.TryGetValue("code", out string code);
                        WriteLoopbackPage(client, 200, "Signed in to Google. You can close this tab and return to Streamer.bot.");
                        return ExchangeGoogleCode(profileName, clientId, clientSecret, code, verifier, redirect, scope);
                    }
                }
                return OSWApiResult<bool>.Fail("Timed out waiting for Google sign-in.");
            }
            catch (Exception ex)
            {
                return OSWApiResult<bool>.Fail("Google sign-in failed: " + ex.Message);
            }
            finally
            {
                listener.Stop(); // ALWAYS — the old tool's missing Stop() is what left port 1313 stuck
            }
        }

        private static Dictionary<string, string> ReadLoopbackRequest(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n"))
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length > 65536) break;
            }
            // First line looks like: GET /?code=abc&state=xyz HTTP/1.1
            string firstLine = sb.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            if (parts.Length < 2) return null;
            int qi = parts[1].IndexOf('?');
            if (qi < 0) return null;
            var result = new Dictionary<string, string>();
            foreach (var pair in parts[1].Substring(qi + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    result[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return result.ContainsKey("code") || result.ContainsKey("error") ? result : null;
        }

        private static void WriteLoopbackPage(TcpClient client, int status, string message)
        {
            string html = "<!doctype html><html><head><meta charset='utf-8'><title>OSW Tools</title></head>"
                + "<body style='font-family:sans-serif;background:#16161d;color:#eee;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'>"
                + "<p style='font-size:18px'>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") + "\r\nContent-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            var stream = client.GetStream();
            byte[] h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private OSWApiResult<bool> ExchangeGoogleCode(string profileName, string clientId, string clientSecret,
            string code, string verifier, string redirect, string scope)
        {
            var resp = OSWHttp.PostForm(GoogleTokenUrl, new Dictionary<string, string>
            {
                ["code"] = code ?? "",
                ["client_id"] = clientId ?? "",
                ["client_secret"] = clientSecret ?? "",
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirect, // must match step 2 exactly
                ["grant_type"] = "authorization_code"
            });
            string body = OSWHttp.ReadBody(resp);
            if (!resp.IsSuccessStatusCode)
                return OSWApiResult<bool>.Fail("Google wouldn't finish sign-in: " + GoogleError(body), (int)resp.StatusCode);
            var j = JObject.Parse(body);
            var p = new GoogleAuthProfileData
            {
                ClientId = clientId, ClientSecret = clientSecret,
                AccessToken = (string)j["access_token"] ?? "",
                RefreshToken = (string)j["refresh_token"] ?? "",
                ExpiresUtc = DateTime.UtcNow.AddSeconds((int?)j["expires_in"] ?? 3600),
                Scopes = (string)j["scope"] ?? scope
            };
            if (string.IsNullOrEmpty(p.RefreshToken))
                LogWarn("Google didn't return a refresh token — the sign-in will stop working in about an hour.");
            OSWSecretStore.Save(this, GoogleProfileKey(profileName), p);
            return OSWApiResult<bool>.Ok(true);
        }

        public bool HasGoogleAuthProfile(string profileName)
            => OSWSecretStore.Load<GoogleAuthProfileData>(this, GoogleProfileKey(profileName)) != null;

        /// <summary>Signs out: tells Google to cancel access (by default), then deletes the profile.</summary>
        public void RemoveGoogleAuthProfile(string profileName, bool revokeOnGoogle = true)
        {
            var p = OSWSecretStore.Load<GoogleAuthProfileData>(this, GoogleProfileKey(profileName));
            if (p != null && revokeOnGoogle)
            {
                try { OSWHttp.PostForm("https://oauth2.googleapis.com/revoke", new Dictionary<string, string> { ["token"] = p.RefreshToken.Length > 0 ? p.RefreshToken : p.AccessToken }); }
                catch (Exception ex) { LogWarn("Google revoke failed (profile removed anyway): " + ex.Message); }
            }
            OSWSecretStore.Delete(this, GoogleProfileKey(profileName));
        }

        private static string GoogleProfileKey(string name) => "osw_GoogleAuth_" + SafeKeyPart(name);
        private static readonly object GoogleRefreshLock = new object();

        private string GetGoogleAccessToken(bool force)
        {
            if (string.IsNullOrEmpty(GoogleAuthProfile)) return "";
            string key = GoogleProfileKey(GoogleAuthProfile);
            var p = OSWSecretStore.Load<GoogleAuthProfileData>(this, key);
            if (p == null) return "";
            if (!force && p.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return p.AccessToken;

            lock (GoogleRefreshLock)
            {
                p = OSWSecretStore.Load<GoogleAuthProfileData>(this, key); // another action may have refreshed already
                if (p == null) return "";
                if (!force && p.ExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return p.AccessToken;
                if (string.IsNullOrEmpty(p.RefreshToken)) return "";
                try
                {
                    var resp = OSWHttp.PostForm(GoogleTokenUrl, new Dictionary<string, string>
                    {
                        ["client_id"] = p.ClientId,
                        ["client_secret"] = p.ClientSecret,
                        ["refresh_token"] = p.RefreshToken,
                        ["grant_type"] = "refresh_token"
                    });
                    string body = OSWHttp.ReadBody(resp);
                    if (!resp.IsSuccessStatusCode)
                    {
                        LogWarn("Google sign-in \"" + GoogleAuthProfile + "\" couldn't refresh (" + GoogleError(body)
                            + "). Sign in again. (If the Cloud project's consent screen is in Testing, Google expires sign-ins after 7 days.)");
                        return "";
                    }
                    var j = JObject.Parse(body);
                    p.AccessToken = (string)j["access_token"] ?? "";
                    p.ExpiresUtc = DateTime.UtcNow.AddSeconds((int?)j["expires_in"] ?? 3600);
                    if (j["refresh_token"] != null) p.RefreshToken = (string)j["refresh_token"]; // Google usually keeps the old one valid
                    OSWSecretStore.Save(this, key, p);
                    return p.AccessToken;
                }
                catch (Exception ex)
                {
                    LogWarn("Google refresh failed: " + ex.Message);
                    return p.ExpiresUtc > DateTime.UtcNow ? p.AccessToken : "";
                }
            }
        }

        /// <summary>
        /// Any Google API call with the saved sign-in (absolute URL). Returns
        /// the raw body; on a 401 it refreshes once and retries.
        /// </summary>
        public OSWApiResult<string> GoogleApiRequest(HttpMethod method, string url, object jsonBody = null)
        {
            if (string.IsNullOrEmpty(GoogleAuthProfile))
                return OSWApiResult<string>.Fail("No Google sign-in selected — set lib.GoogleAuthProfile first.");
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                string token = GetGoogleAccessToken(force: attempt == 2);
                if (token.Length == 0)
                    return OSWApiResult<string>.Fail("Google sign-in \"" + GoogleAuthProfile + "\" is missing or expired. Sign in again.");
                try
                {
                    var req = new HttpRequestMessage(method, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (jsonBody != null)
                        req.Content = new StringContent(JsonConvert.SerializeObject(jsonBody), Encoding.UTF8, "application/json");
                    var resp = OSWHttp.Send(req);
                    int code = (int)resp.StatusCode;
                    string body = OSWHttp.ReadBody(resp);
                    if (code == 401 && attempt == 1) continue;
                    return resp.IsSuccessStatusCode
                        ? OSWApiResult<string>.Ok(body, code)
                        : OSWApiResult<string>.Fail("Google returned an error (" + code + "): " + GoogleError(body), code);
                }
                catch (Exception ex)
                {
                    return OSWApiResult<string>.Fail("Request to Google failed: " + ex.Message);
                }
            }
            return OSWApiResult<string>.Fail("Google rejected the sign-in twice. Sign in again.", 401);
        }

        private static string GoogleError(string body)
        {
            try
            {
                var j = JObject.Parse(body);
                if (j["error"] is JObject e) return (string)e["message"] ?? body;           // API errors
                return ((string)j["error_description"] ?? (string)j["error"]) ?? body;    // OAuth errors
            }
            catch { return body ?? ""; }
        }

        // =====================================================================
        //  Google Calendar
        // =====================================================================

        /// <summary>The streamer's calendars (for a picker). writableOnly hides ones they can't add to.</summary>
        public OSWApiResult<List<GoogleCalendarInfo>> ListGoogleCalendars(bool writableOnly = true)
        {
            var r = GoogleApiRequest(HttpMethod.Get, CalendarBase + "users/me/calendarList?maxResults=250");
            if (!r.Success) return r.As<List<GoogleCalendarInfo>>();
            try
            {
                var list = ((JArray)JObject.Parse(r.Data)["items"] ?? new JArray()).OfType<JObject>().Select(c => new GoogleCalendarInfo
                {
                    Id = (string)c["id"] ?? "",
                    Summary = (string)c["summaryOverride"] ?? (string)c["summary"] ?? "",
                    IsPrimary = (bool?)c["primary"] ?? false,
                    AccessRole = (string)c["accessRole"] ?? "",
                    TimeZone = (string)c["timeZone"] ?? ""
                }).Where(c => !writableOnly || c.CanWrite)
                  .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Summary).ToList();
                return OSWApiResult<List<GoogleCalendarInfo>>.Ok(list);
            }
            catch (Exception ex) { return OSWApiResult<List<GoogleCalendarInfo>>.Fail("Couldn't read Google's calendar list: " + ex.Message); }
        }

        /// <summary>
        /// Events between two times (default: from now), with recurring events
        /// expanded into their individual occurrences, in start-time order.
        /// calendarId "primary" = the streamer's main calendar.
        /// </summary>
        public OSWApiResult<List<GoogleCalendarEvent>> ListGoogleCalendarEvents(string calendarId = "primary",
            DateTime? fromUtc = null, DateTime? toUtc = null, int maxResults = 50)
        {
            string url = CalendarBase + "calendars/" + Uri.EscapeDataString(calendarId) + "/events"
                + "?singleEvents=true&orderBy=startTime&maxResults=" + Math.Max(1, Math.Min(2500, maxResults))
                + "&timeMin=" + Uri.EscapeDataString(Rfc3339(fromUtc ?? DateTime.UtcNow));
            if (toUtc.HasValue) url += "&timeMax=" + Uri.EscapeDataString(Rfc3339(toUtc.Value));
            var r = GoogleApiRequest(HttpMethod.Get, url);
            if (!r.Success) return r.As<List<GoogleCalendarEvent>>();
            try
            {
                var list = ((JArray)JObject.Parse(r.Data)["items"] ?? new JArray()).OfType<JObject>().Select(ParseGoogleEvent).ToList();
                return OSWApiResult<List<GoogleCalendarEvent>>.Ok(list);
            }
            catch (Exception ex) { return OSWApiResult<List<GoogleCalendarEvent>>.Fail("Couldn't read Google's events: " + ex.Message); }
        }

        /// <summary>Adds an event. Returns it as Google saved it (with Id and HtmlLink).</summary>
        public OSWApiResult<GoogleCalendarEvent> CreateGoogleCalendarEvent(string calendarId, GoogleCalendarEvent ev)
        {
            if (ev == null) return OSWApiResult<GoogleCalendarEvent>.Fail("No event given.");
            if (ev.EndUtc <= ev.StartUtc) return OSWApiResult<GoogleCalendarEvent>.Fail("The event must end after it starts.");
            string tz = GoogleTz(ev.TimeZone);
            var body = new Dictionary<string, object>
            {
                ["summary"] = ev.Summary ?? "",
                ["description"] = ev.Description ?? "",
                ["location"] = ev.Location ?? "",
                // The exact moment in UTC PLUS the zone: Google expands a
                // recurrence in that zone, so "weekly at 7 PM" stays 7 PM
                // local across daylight-saving changes.
                ["start"] = new Dictionary<string, string> { ["dateTime"] = Rfc3339(ev.StartUtc), ["timeZone"] = tz },
                ["end"] = new Dictionary<string, string> { ["dateTime"] = Rfc3339(ev.EndUtc), ["timeZone"] = tz }
            };
            if (ev.Recurrence != null && ev.Recurrence.Count > 0) body["recurrence"] = ev.Recurrence;
            return WriteGoogleEvent(HttpMethod.Post, CalendarBase + "calendars/" + Uri.EscapeDataString(calendarId) + "/events", body);
        }

        /// <summary>
        /// Changes an event — only what you pass. For a recurring SERIES pass the
        /// series ID; to change one occurrence pass that occurrence's ID (from
        /// ListGoogleCalendarEvents). Start and end must be given together.
        /// </summary>
        public OSWApiResult<GoogleCalendarEvent> UpdateGoogleCalendarEvent(string calendarId, string eventId,
            string summary = null, string description = null, string location = null,
            DateTime? startUtc = null, DateTime? endUtc = null, string timeZone = null, List<string> recurrence = null)
        {
            var body = new Dictionary<string, object>();
            if (summary != null) body["summary"] = summary;
            if (description != null) body["description"] = description;
            if (location != null) body["location"] = location;
            if (startUtc.HasValue != endUtc.HasValue)
                return OSWApiResult<GoogleCalendarEvent>.Fail("Pass both a new start and a new end, or neither.");
            if (startUtc.HasValue)
            {
                string tz = GoogleTz(timeZone);
                body["start"] = new Dictionary<string, string> { ["dateTime"] = Rfc3339(startUtc.Value), ["timeZone"] = tz };
                body["end"] = new Dictionary<string, string> { ["dateTime"] = Rfc3339(endUtc.Value), ["timeZone"] = tz };
            }
            if (recurrence != null) body["recurrence"] = recurrence;
            if (body.Count == 0) return OSWApiResult<GoogleCalendarEvent>.Fail("Nothing to update — pass at least one field.");
            return WriteGoogleEvent(OSWHttp.Patch, EventUrl(calendarId, eventId), body);
        }

        /// <summary>
        /// Cancels one event, or ONE occurrence of a recurring event (pass the
        /// occurrence's ID) — the rest of the series stays.
        /// </summary>
        public OSWApiResult<GoogleCalendarEvent> CancelGoogleCalendarEvent(string calendarId, string eventId)
            => WriteGoogleEvent(OSWHttp.Patch, EventUrl(calendarId, eventId), new Dictionary<string, object> { ["status"] = "cancelled" });

        /// <summary>Deletes an event. Passing a SERIES ID deletes every occurrence.</summary>
        public OSWApiResult<bool> DeleteGoogleCalendarEvent(string calendarId, string eventId)
        {
            var r = GoogleApiRequest(HttpMethod.Delete, EventUrl(calendarId, eventId));
            // 410 Gone = it was already deleted. Same end result, so not an error.
            if (r.Success || r.StatusCode == 410) return OSWApiResult<bool>.Ok(true, r.StatusCode);
            return r.As<bool>();
        }

        private string EventUrl(string calendarId, string eventId)
            => CalendarBase + "calendars/" + Uri.EscapeDataString(calendarId ?? "primary") + "/events/" + Uri.EscapeDataString(eventId ?? "");

        private OSWApiResult<GoogleCalendarEvent> WriteGoogleEvent(HttpMethod method, string url, object body)
        {
            var r = GoogleApiRequest(method, url, body);
            if (!r.Success) return r.As<GoogleCalendarEvent>();
            try { return OSWApiResult<GoogleCalendarEvent>.Ok(ParseGoogleEvent(JObject.Parse(r.Data)), r.StatusCode); }
            catch (Exception ex) { return OSWApiResult<GoogleCalendarEvent>.Fail("Saved, but couldn't read Google's reply: " + ex.Message, r.StatusCode); }
        }

        private static GoogleCalendarEvent ParseGoogleEvent(JObject e)
        {
            return new GoogleCalendarEvent
            {
                Id = (string)e["id"] ?? "",
                Summary = (string)e["summary"] ?? "",
                Description = (string)e["description"] ?? "",
                Location = (string)e["location"] ?? "",
                StartUtc = ParseGoogleTime(e["start"] as JObject),
                EndUtc = ParseGoogleTime(e["end"] as JObject),
                TimeZone = (string)e["start"]?["timeZone"] ?? "",
                Recurrence = e["recurrence"]?.ToObject<List<string>>() ?? new List<string>(),
                HtmlLink = (string)e["htmlLink"] ?? "",
                Status = (string)e["status"] ?? "",
                RecurringEventId = (string)e["recurringEventId"] ?? ""
            };
        }

        // Timed events have start.dateTime; all-day events have start.date.
        // Read as a string token so Json.NET doesn't reinterpret the offset.
        private static DateTime ParseGoogleTime(JObject t)
        {
            if (t == null) return DateTime.MinValue;
            string s = t["dateTime"]?.Type == JTokenType.Date
                ? ((DateTime)t["dateTime"]).ToUniversalTime().ToString("o")
                : (string)t["dateTime"];
            if (!string.IsNullOrEmpty(s))
                return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).UtcDateTime;
            string d = t["date"]?.Type == JTokenType.Date ? ((DateTime)t["date"]).ToString("yyyy-MM-dd") : (string)t["date"];
            return string.IsNullOrEmpty(d) ? DateTime.MinValue
                : DateTime.SpecifyKind(DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        }

        private static string GoogleTz(string tz)
            => string.IsNullOrWhiteSpace(tz) ? OSWTimeZones.GetLocalIana() : (OSWTimeZones.ToIana(tz) ?? tz);

        private static string Rfc3339(DateTime utc)
            => DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
                       .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
