////OSWTimeZones.cs//
// =============================================================================
// NEW FILE — Extensions\OSWTimeZones.cs
// Time-zone helpers for anything that schedules things.
//
// THE PROBLEM: Windows and the web name time zones differently.
//   Windows (TimeZoneInfo.Id): "Central Standard Time"
//   IANA (Twitch, Google, everything web): "America/Chicago"
// .NET Framework can't convert between them (that arrived in .NET 6), and on
// Windows TimeZoneInfo only understands the Windows names. So this file
// carries a map, and every method here accepts EITHER kind of name.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace OSWTools
{
    /// <summary>One entry for a time-zone dropdown.</summary>
    public class OSWTimeZoneOption
    {
        public string WindowsId { get; set; }
        public string IanaId { get; set; }
        /// <summary>Windows' friendly name, e.g. "(UTC-06:00) Central Time (US &amp; Canada)".</summary>
        public string DisplayName { get; set; }
        public TimeSpan BaseUtcOffset { get; set; }
        public override string ToString() => DisplayName; // so a ComboBox shows the friendly name
    }

    public static class OSWTimeZones
    {
        /// <summary>This PC's time zone as an IANA ID, e.g. "America/Chicago".</summary>
        public static string GetLocalIana() => ToIana(TimeZoneInfo.Local.Id) ?? "Etc/UTC";

        /// <summary>Any time-zone name → IANA. Already-IANA names pass through. Null if unknown.</summary>
        public static string ToIana(string windowsOrIana)
        {
            if (string.IsNullOrWhiteSpace(windowsOrIana)) return null;
            if (WindowsToIanaMap.TryGetValue(windowsOrIana, out string iana)) return iana;
            return windowsOrIana.Contains("/") ? windowsOrIana : null; // IANA names always contain "/"
        }

        /// <summary>Any time-zone name → Windows ID. Already-Windows names pass through. Null if unknown.</summary>
        public static string ToWindows(string windowsOrIana)
        {
            if (string.IsNullOrWhiteSpace(windowsOrIana)) return null;
            if (WindowsToIanaMap.ContainsKey(windowsOrIana)) return windowsOrIana;
            return IanaToWindowsMap.TryGetValue(windowsOrIana, out string win) ? win : null;
        }

        /// <summary>
        /// The TimeZoneInfo for any name, or null. Tries the name as given
        /// first (works for Windows IDs on Windows, IANA IDs elsewhere),
        /// then the converted form.
        /// </summary>
        public static TimeZoneInfo Find(string windowsOrIana)
        {
            foreach (var id in new[] { windowsOrIana, ToWindows(windowsOrIana), ToIana(windowsOrIana) })
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return null;
        }

        /// <summary>
        /// "Friday 7:00 PM in Chicago" → the exact UTC moment. wallClock's
        /// Kind is ignored — it's treated as a reading on that zone's clocks.
        /// Daylight saving is handled by Windows' own rules for that zone.
        /// </summary>
        public static DateTime ToUtc(DateTime wallClock, string windowsOrIana)
        {
            var tz = Find(windowsOrIana) ?? throw new ArgumentException("Unknown time zone: " + windowsOrIana);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), tz);
        }

        /// <summary>A UTC moment → what the clock reads in that zone.</summary>
        public static DateTime FromUtc(DateTime utc, string windowsOrIana)
        {
            var tz = Find(windowsOrIana) ?? throw new ArgumentException("Unknown time zone: " + windowsOrIana);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        }

        /// <summary>
        /// Every zone this PC knows that also has an IANA name — sorted by
        /// offset, ready for a dropdown. Shows Windows' friendly names
        /// instead of raw IDs (which is all the old settings form could show).
        /// </summary>
        public static List<OSWTimeZoneOption> GetPickerList()
        {
            return TimeZoneInfo.GetSystemTimeZones()
                .Select(tz => new { tz, iana = ToIana(tz.Id) })
                .Where(x => x.iana != null)
                .Select(x => new OSWTimeZoneOption
                {
                    WindowsId = ToWindows(x.tz.Id) ?? x.tz.Id,
                    IanaId = x.iana,
                    DisplayName = x.tz.DisplayName,
                    BaseUtcOffset = x.tz.BaseUtcOffset
                })
                .OrderBy(o => o.BaseUtcOffset).ThenBy(o => o.DisplayName)
                .ToList();
        }

        // Windows ID → IANA. Merged from OSUPhoenix's original multi-scheduler
        // map plus a few additions; each Windows zone maps to its "primary"
        // IANA city (the same choice Unicode CLDR makes).
        private static readonly Dictionary<string, string> WindowsToIanaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dateline Standard Time"] = "Etc/GMT+12",
            ["UTC-11"] = "Etc/GMT+11",
            ["Aleutian Standard Time"] = "America/Adak",
            ["Hawaiian Standard Time"] = "Pacific/Honolulu",
            ["Marquesas Standard Time"] = "Pacific/Marquesas",
            ["Alaskan Standard Time"] = "America/Anchorage",
            ["Pacific Standard Time (Mexico)"] = "America/Tijuana",
            ["Pacific Standard Time"] = "America/Los_Angeles",
            ["US Mountain Standard Time"] = "America/Phoenix",
            ["Mountain Standard Time (Mexico)"] = "America/Chihuahua",
            ["Mountain Standard Time"] = "America/Denver",
            ["Central America Standard Time"] = "America/Guatemala",
            ["Central Standard Time"] = "America/Chicago",
            ["Central Standard Time (Mexico)"] = "America/Mexico_City",
            ["Canada Central Standard Time"] = "America/Regina",
            ["SA Pacific Standard Time"] = "America/Bogota",
            ["Eastern Standard Time (Mexico)"] = "America/Cancun",
            ["Eastern Standard Time"] = "America/New_York",
            ["US Eastern Standard Time"] = "America/Indianapolis",
            ["Atlantic Standard Time"] = "America/Halifax",
            ["Venezuela Standard Time"] = "America/Caracas",
            ["Paraguay Standard Time"] = "America/Asuncion",
            ["Central Brazilian Standard Time"] = "America/Cuiaba",
            ["SA Western Standard Time"] = "America/La_Paz",
            ["Pacific SA Standard Time"] = "America/Santiago",
            ["Newfoundland Standard Time"] = "America/St_Johns",
            ["E. South America Standard Time"] = "America/Sao_Paulo",
            ["Argentina Standard Time"] = "America/Buenos_Aires",
            ["Greenland Standard Time"] = "America/Godthab",
            ["Montevideo Standard Time"] = "America/Montevideo",
            ["Magallanes Standard Time"] = "America/Punta_Arenas",
            ["Bahia Standard Time"] = "America/Bahia",
            ["UTC-02"] = "Etc/GMT+2",
            ["Azores Standard Time"] = "Atlantic/Azores",
            ["Cape Verde Standard Time"] = "Atlantic/Cape_Verde",
            ["UTC"] = "Etc/UTC",
            ["GMT Standard Time"] = "Europe/London",
            ["Greenwich Standard Time"] = "Atlantic/Reykjavik",
            ["Morocco Standard Time"] = "Africa/Casablanca",
            ["W. Europe Standard Time"] = "Europe/Berlin",
            ["Central Europe Standard Time"] = "Europe/Budapest",
            ["Romance Standard Time"] = "Europe/Paris",
            ["Central European Standard Time"] = "Europe/Warsaw",
            ["W. Central Africa Standard Time"] = "Africa/Lagos",
            ["Jordan Standard Time"] = "Asia/Amman",
            ["GTB Standard Time"] = "Europe/Bucharest",
            ["Middle East Standard Time"] = "Asia/Beirut",
            ["Egypt Standard Time"] = "Africa/Cairo",
            ["E. Europe Standard Time"] = "Europe/Chisinau",
            ["Syria Standard Time"] = "Asia/Damascus",
            ["West Bank Standard Time"] = "Asia/Hebron",
            ["South Africa Standard Time"] = "Africa/Johannesburg",
            ["FLE Standard Time"] = "Europe/Kiev",
            ["Israel Standard Time"] = "Asia/Jerusalem",
            ["Libya Standard Time"] = "Africa/Tripoli",
            ["Turkey Standard Time"] = "Europe/Istanbul",
            ["Arab Standard Time"] = "Asia/Riyadh",
            ["Russian Standard Time"] = "Europe/Moscow",
            ["E. Africa Standard Time"] = "Africa/Nairobi",
            ["Iran Standard Time"] = "Asia/Tehran",
            ["Arabian Standard Time"] = "Asia/Dubai",
            ["Astrakhan Standard Time"] = "Europe/Astrakhan",
            ["Mauritius Standard Time"] = "Indian/Mauritius",
            ["Georgian Standard Time"] = "Asia/Tbilisi",
            ["Afghanistan Standard Time"] = "Asia/Kabul",
            ["West Asia Standard Time"] = "Asia/Tashkent",
            ["Pakistan Standard Time"] = "Asia/Karachi",
            ["India Standard Time"] = "Asia/Kolkata",
            ["Bangladesh Standard Time"] = "Asia/Dhaka",
            ["Myanmar Standard Time"] = "Asia/Yangon",
            ["SE Asia Standard Time"] = "Asia/Bangkok",
            ["China Standard Time"] = "Asia/Shanghai",
            ["Singapore Standard Time"] = "Asia/Singapore",
            ["Taipei Standard Time"] = "Asia/Taipei",
            ["W. Australia Standard Time"] = "Australia/Perth",
            ["Tokyo Standard Time"] = "Asia/Tokyo",
            ["Korea Standard Time"] = "Asia/Seoul",
            ["Cen. Australia Standard Time"] = "Australia/Adelaide",
            ["AUS Central Standard Time"] = "Australia/Darwin",
            ["AUS Eastern Standard Time"] = "Australia/Sydney",
            ["E. Australia Standard Time"] = "Australia/Brisbane",
            ["Tasmania Standard Time"] = "Australia/Hobart",
            ["New Zealand Standard Time"] = "Pacific/Auckland",
            ["UTC+12"] = "Pacific/Tarawa",
            ["Fiji Standard Time"] = "Pacific/Fiji",
            ["Samoa Standard Time"] = "Pacific/Apia",
        };

        // Built once from the map above, so the two directions can never disagree.
        private static readonly Dictionary<string, string> IanaToWindowsMap = BuildReverse();

        private static Dictionary<string, string> BuildReverse()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in WindowsToIanaMap)
                if (!d.ContainsKey(kv.Value)) d[kv.Value] = kv.Key;
            // Common alternate IANA spellings that should still resolve.
            d["Asia/Calcutta"] = "India Standard Time";
            d["America/Nuuk"] = "Greenland Standard Time";
            d["Europe/Kyiv"] = "FLE Standard Time";
            d["America/Argentina/Buenos_Aires"] = "Argentina Standard Time";
            d["America/Indiana/Indianapolis"] = "US Eastern Standard Time";
            d["UTC"] = "UTC";
            return d;
        }
    }
}
