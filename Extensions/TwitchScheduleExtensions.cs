////TwitchScheduleExtensions.cs//
// =============================================================================
// Extensions\TwitchScheduleExtensions.cs  (REPLACES the previous version)
// The complete Twitch Stream Schedule API, built on TwitchHelixExtensions.cs.
//   read    GetTwitchSchedule, GetNextTwitchScheduledStream, GetTwitchScheduleICalendar
//   write   CreateTwitchScheduleSegment, UpdateTwitchScheduleSegment,
//           CancelTwitchScheduleSegment, DeleteTwitchScheduleSegment,
//           SetTwitchScheduleVacation, ClearTwitchScheduleVacation
// Reading works with any login. Writing needs the channel:manage:schedule
// scope — check with TwitchTokenHasScope("channel:manage:schedule"); if
// Streamer.bot's login lacks it, sign in with your own app (Device Code
// sign-in) and set lib.TwitchAuthProfile.
// https://dev.twitch.tv/docs/api/schedule/
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    // ── Result + model types ─────────────────────────────────────────────────

    /// <summary>One broadcast in the schedule (one occurrence, for recurring ones).</summary>
    public class TwitchScheduleSegment
    {
        /// <summary>Occurrence ID — what Update/Cancel/Delete take.</summary>
        public string Id { get; set; } = "";
        public DateTime StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
        public string Title { get; set; } = "";
        public string CategoryId { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public bool IsRecurring { get; set; }
        /// <summary>Set when this occurrence was canceled (Twitch keeps it in the list).</summary>
        public DateTime? CanceledUntilUtc { get; set; }
        public bool IsCanceled => CanceledUntilUtc.HasValue;
        public int DurationMinutes => EndUtc.HasValue ? (int)Math.Round((EndUtc.Value - StartUtc).TotalMinutes) : 0;
    }

    /// <summary>A page of the broadcaster's schedule.</summary>
    public class TwitchSchedule
    {
        public string BroadcasterId { get; set; } = "";
        public string BroadcasterName { get; set; } = "";
        public string BroadcasterLogin { get; set; } = "";
        public List<TwitchScheduleSegment> Segments { get; set; } = new List<TwitchScheduleSegment>();
        public DateTime? VacationStartUtc { get; set; }
        public DateTime? VacationEndUtc { get; set; }
        /// <summary>Pass to GetTwitchSchedule(after:) for the next page; empty when there are no more.</summary>
        public string NextCursor { get; set; } = "";

        public bool IsOnVacationAt(DateTime utc)
            => VacationStartUtc.HasValue && VacationEndUtc.HasValue
               && utc >= VacationStartUtc.Value && utc <= VacationEndUtc.Value;
    }

    public partial class OSWLib
    {
        // =====================================================================
        //  Stream Schedule
        //  https://dev.twitch.tv/docs/api/schedule/
        // =====================================================================

        private const string ScheduleScope = "channel:manage:schedule";

        /// <summary>
        /// Gets one page of a broadcaster's schedule (default: the connected
        /// broadcaster, from now on). Recurring segments come back as one
        /// entry per upcoming occurrence, and can extend for years — use
        /// NextCursor to page, or GetNextTwitchScheduledStream for "what's next".
        ///
        /// No schedule at all comes back from Twitch as 404; that's returned
        /// here as SUCCESS with an empty list, because "nothing scheduled" is
        /// a normal answer, not an error.
        /// </summary>
        public OSWApiResult<TwitchSchedule> GetTwitchSchedule(string broadcasterId = null, DateTime? startUtc = null,
            int first = 25, string after = null, IEnumerable<string> segmentIds = null)
        {
            broadcasterId = string.IsNullOrEmpty(broadcasterId) ? GetTwitchBroadcasterIdFromToken() : broadcasterId;
            if (string.IsNullOrEmpty(broadcasterId))
                return OSWApiResult<TwitchSchedule>.Fail("Couldn't determine the Twitch broadcaster ID.");

            var q = new StringBuilder("schedule?broadcaster_id=").Append(Uri.EscapeDataString(broadcasterId));
            q.Append("&first=").Append(Math.Max(1, Math.Min(25, first)));
            if (startUtc.HasValue) q.Append("&start_time=").Append(Uri.EscapeDataString(ToRfc3339(startUtc.Value)));
            if (!string.IsNullOrEmpty(after)) q.Append("&after=").Append(Uri.EscapeDataString(after));
            if (segmentIds != null)
                foreach (var id in segmentIds.Take(100))
                    q.Append("&id=").Append(Uri.EscapeDataString(id));

            var r = TwitchHelixRequest(HttpMethod.Get, q.ToString());
            if (!r.Success && r.StatusCode == 404)
                return OSWApiResult<TwitchSchedule>.Ok(new TwitchSchedule { BroadcasterId = broadcasterId }, 404);
            if (!r.Success)
                return OSWApiResult<TwitchSchedule>.Fail(r.Error, r.StatusCode);
            try
            {
                return OSWApiResult<TwitchSchedule>.Ok(ParseSchedule(JObject.Parse(r.Data)), r.StatusCode);
            }
            catch (Exception ex)
            {
                return OSWApiResult<TwitchSchedule>.Fail("Couldn't read Twitch's schedule response: " + ex.Message);
            }
        }

        /// <summary>
        /// The next stream that will actually happen: skips canceled
        /// occurrences and (by default) anything inside a scheduled vacation.
        /// Data is null when nothing upcoming is scheduled. Looks at up to
        /// `maxPages` pages (25 per page).
        /// </summary>
        public OSWApiResult<TwitchScheduleSegment> GetNextTwitchScheduledStream(string broadcasterId = null,
            bool skipVacation = true, int maxPages = 2)
        {
            string cursor = null;
            for (int page = 0; page < Math.Max(1, maxPages); page++)
            {
                var r = GetTwitchSchedule(broadcasterId, null, 25, cursor);
                if (!r.Success)
                    return OSWApiResult<TwitchScheduleSegment>.Fail(r.Error, r.StatusCode);
                var s = r.Data;
                var next = s.Segments
                    .Where(x => !x.IsCanceled)
                    .Where(x => (x.EndUtc ?? x.StartUtc) > DateTime.UtcNow) // not already over
                    .Where(x => !skipVacation || !s.IsOnVacationAt(x.StartUtc))
                    .OrderBy(x => x.StartUtc)
                    .FirstOrDefault();
                if (next != null)
                    return OSWApiResult<TwitchScheduleSegment>.Ok(next);
                if (string.IsNullOrEmpty(s.NextCursor))
                    break;
                cursor = s.NextCursor;
            }
            return OSWApiResult<TwitchScheduleSegment>.Ok(null);
        }

        /// <summary>
        /// The schedule as an iCalendar (.ics) file — handy for "add to
        /// calendar" links or exporting. Twitch serves this one without auth.
        /// </summary>
        public OSWApiResult<string> GetTwitchScheduleICalendar(string broadcasterId = null)
        {
            broadcasterId = string.IsNullOrEmpty(broadcasterId) ? GetTwitchBroadcasterIdFromToken() : broadcasterId;
            if (string.IsNullOrEmpty(broadcasterId))
                return OSWApiResult<string>.Fail("Couldn't determine the Twitch broadcaster ID.");
            return TwitchHelixRequest(HttpMethod.Get, "schedule/icalendar?broadcaster_id=" + Uri.EscapeDataString(broadcasterId));
        }

        /// <summary>
        /// Adds a broadcast. Recurring = weekly on the same weekday/time as
        /// startUtc (Twitch starts it from the first such weekday on or after
        /// today). Non-recurring segments are Partner/Affiliate only.
        /// timeZone is IANA ("America/Chicago"); null = this PC's time zone.
        /// Needs channel:manage:schedule.
        /// </summary>
        public OSWApiResult<TwitchScheduleSegment> CreateTwitchScheduleSegment(DateTime startUtc, int durationMinutes,
            bool isRecurring, string title = null, string categoryId = null, string timeZone = null)
        {
            var scopeCheck = RequireScheduleScope<TwitchScheduleSegment>();
            if (scopeCheck != null) return scopeCheck;

            var body = new Dictionary<string, object>
            {
                ["start_time"] = ToRfc3339(startUtc),
                ["timezone"] = string.IsNullOrEmpty(timeZone) ? OSWTimeZones.GetLocalIana() : (OSWTimeZones.ToIana(timeZone) ?? timeZone),
                ["duration"] = durationMinutes.ToString(CultureInfo.InvariantCulture),
                ["is_recurring"] = isRecurring
            };
            if (!string.IsNullOrEmpty(title)) body["title"] = title;
            if (!string.IsNullOrEmpty(categoryId)) body["category_id"] = categoryId;

            return WriteSegment(HttpMethod.Post, "schedule/segment?broadcaster_id=" + Uri.EscapeDataString(GetTwitchBroadcasterIdFromToken()), body);
        }

        /// <summary>
        /// Changes a segment. Only the arguments you pass are changed.
        /// For RECURRING segments every change applies to all occurrences, and
        /// the start time can't be changed (Twitch rule). Needs channel:manage:schedule.
        /// </summary>
        public OSWApiResult<TwitchScheduleSegment> UpdateTwitchScheduleSegment(string segmentId,
            DateTime? startUtc = null, int? durationMinutes = null, string title = null,
            string categoryId = null, bool? isCanceled = null, string timeZone = null)
        {
            var scopeCheck = RequireScheduleScope<TwitchScheduleSegment>();
            if (scopeCheck != null) return scopeCheck;

            var body = new Dictionary<string, object>();
            if (startUtc.HasValue) body["start_time"] = ToRfc3339(startUtc.Value);
            if (durationMinutes.HasValue) body["duration"] = durationMinutes.Value.ToString(CultureInfo.InvariantCulture);
            if (title != null) body["title"] = title;
            if (categoryId != null) body["category_id"] = categoryId;
            if (isCanceled.HasValue) body["is_canceled"] = isCanceled.Value;
            if (timeZone != null) body["timezone"] = OSWTimeZones.ToIana(timeZone) ?? timeZone; // accepts Windows or IANA names
            if (body.Count == 0)
                return OSWApiResult<TwitchScheduleSegment>.Fail("Nothing to update — pass at least one field.");

            return WriteSegment(OSWHttp.Patch, SegmentPath(segmentId), body);
        }

        /// <summary>
        /// Cancels one occurrence (it stays visible as canceled). For a
        /// recurring segment Twitch only allows canceling the NEXT occurrence.
        /// </summary>
        public OSWApiResult<TwitchScheduleSegment> CancelTwitchScheduleSegment(string segmentId)
            => UpdateTwitchScheduleSegment(segmentId, isCanceled: true);

        /// <summary>
        /// Deletes a segment. WARNING: for a recurring segment this deletes
        /// the WHOLE weekly series, not just one occurrence — use
        /// CancelTwitchScheduleSegment to skip a single week.
        /// </summary>
        public OSWApiResult<bool> DeleteTwitchScheduleSegment(string segmentId)
        {
            var scopeCheck = RequireScheduleScope<bool>();
            if (scopeCheck != null) return scopeCheck;
            var r = TwitchHelixRequest(HttpMethod.Delete, SegmentPath(segmentId));
            return r.Success ? OSWApiResult<bool>.Ok(true, r.StatusCode) : OSWApiResult<bool>.Fail(r.Error, r.StatusCode);
        }

        /// <summary>
        /// Shows a vacation on the channel's schedule. Doesn't remove or
        /// cancel the segments inside it (Twitch rule) — it's a notice.
        /// </summary>
        public OSWApiResult<bool> SetTwitchScheduleVacation(DateTime startUtc, DateTime endUtc, string timeZone = null)
        {
            return UpdateScheduleSettings(new Dictionary<string, object>
            {
                ["is_vacation_enabled"] = true,
                ["vacation_start_time"] = ToRfc3339(startUtc),
                ["vacation_end_time"] = ToRfc3339(endUtc),
                ["timezone"] = string.IsNullOrEmpty(timeZone) ? OSWTimeZones.GetLocalIana() : (OSWTimeZones.ToIana(timeZone) ?? timeZone)
            });
        }

        /// <summary>Removes the vacation notice.</summary>
        public OSWApiResult<bool> ClearTwitchScheduleVacation()
            => UpdateScheduleSettings(new Dictionary<string, object> { ["is_vacation_enabled"] = false });

        // ── Schedule internals ────────────────────────────────────────────────

        private OSWApiResult<bool> UpdateScheduleSettings(Dictionary<string, object> body)
        {
            var scopeCheck = RequireScheduleScope<bool>();
            if (scopeCheck != null) return scopeCheck;
            var r = TwitchHelixRequest(OSWHttp.Patch,
                "schedule/settings?broadcaster_id=" + Uri.EscapeDataString(GetTwitchBroadcasterIdFromToken()), body);
            return r.Success ? OSWApiResult<bool>.Ok(true, r.StatusCode) : OSWApiResult<bool>.Fail(r.Error, r.StatusCode);
        }

        // Checks the scope BEFORE calling Twitch, so the caller gets "your
        // login lacks permission X" instead of a vague 401.
        private OSWApiResult<T> RequireScheduleScope<T>()
        {
            var v = ValidateTwitchToken();
            if (!v.Success)
                return OSWApiResult<T>.Fail(v.Error, v.StatusCode);
            if (!v.Data.Scopes.Contains(ScheduleScope))
                return OSWApiResult<T>.Fail("Streamer.bot's Twitch login doesn't include the \"" + ScheduleScope +
                    "\" permission, so the schedule can be read but not changed.", 401);
            return null;
        }

        private string SegmentPath(string segmentId)
            => "schedule/segment?broadcaster_id=" + Uri.EscapeDataString(GetTwitchBroadcasterIdFromToken())
               + "&id=" + Uri.EscapeDataString(segmentId ?? "");

        private OSWApiResult<TwitchScheduleSegment> WriteSegment(HttpMethod method, string path, object body)
        {
            var r = TwitchHelixRequest(method, path, body);
            if (!r.Success)
                return OSWApiResult<TwitchScheduleSegment>.Fail(r.Error, r.StatusCode);
            try
            {
                var s = ParseSchedule(JObject.Parse(r.Data));
                return OSWApiResult<TwitchScheduleSegment>.Ok(s.Segments.FirstOrDefault(), r.StatusCode);
            }
            catch (Exception ex)
            {
                return OSWApiResult<TwitchScheduleSegment>.Fail("Saved, but couldn't read Twitch's reply: " + ex.Message, r.StatusCode);
            }
        }

        private static TwitchSchedule ParseSchedule(JObject root)
        {
            var d = root["data"] as JObject ?? new JObject();
            var s = new TwitchSchedule
            {
                BroadcasterId = (string)d["broadcaster_id"] ?? "",
                BroadcasterName = (string)d["broadcaster_name"] ?? "",
                BroadcasterLogin = (string)d["broadcaster_login"] ?? "",
                NextCursor = (string)root["pagination"]?["cursor"] ?? ""
            };
            if (d["vacation"] is JObject vac)
            {
                s.VacationStartUtc = ParseUtc((string)vac["start_time"]);
                s.VacationEndUtc = ParseUtc((string)vac["end_time"]);
            }
            if (d["segments"] is JArray segs)
            {
                foreach (var t in segs.OfType<JObject>())
                {
                    var seg = new TwitchScheduleSegment
                    {
                        Id = (string)t["id"] ?? "",
                        StartUtc = ParseUtc((string)t["start_time"]) ?? DateTime.MinValue,
                        EndUtc = ParseUtc((string)t["end_time"]),
                        Title = (string)t["title"] ?? "",
                        IsRecurring = (bool?)t["is_recurring"] ?? false,
                        CanceledUntilUtc = ParseUtc((string)t["canceled_until"])
                    };
                    // Twitch's own examples show `category` as an object
                    // {id, name} in some responses and a bare number in
                    // others — accept both rather than trusting one shape.
                    var cat = t["category"];
                    if (cat is JObject co)
                    {
                        seg.CategoryId = (string)co["id"] ?? "";
                        seg.CategoryName = (string)co["name"] ?? "";
                    }
                    else if (cat != null && cat.Type != JTokenType.Null)
                    {
                        seg.CategoryId = cat.ToString();
                    }
                    s.Segments.Add(seg);
                }
            }
            return s;
        }

        private static string ToRfc3339(DateTime utc)
            => DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
                       .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // ── Time zones ────────────────────────────────────────────────────────

        /// <summary>This PC's time zone as an IANA ID. Kept for compatibility —
        /// new code can use OSWTimeZones directly (conversions, picker list).</summary>
        public static string GetIanaTimeZoneId() => OSWTimeZones.GetLocalIana();
    }
}
