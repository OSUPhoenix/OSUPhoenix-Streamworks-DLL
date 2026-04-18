// =============================================================================
// OSWTools — Core/GlobalsExtensions.cs
//
// Extra Globals helpers. Adds IncrementInGlobalDict() — atomic-ish increment
// of a value inside a JSON dictionary stored as a single global var.
//
// Common pattern this replaces:
//   string json = CPH.GetGlobalVar<string>("Leaderboard", true) ?? "{}";
//   var dict = JsonConvert.DeserializeObject<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
//   if (!dict.ContainsKey(user)) dict[user] = 0;
//   dict[user] += amount;
//   CPH.SetGlobalVar("Leaderboard", JsonConvert.SerializeObject(dict), true);
//
// Becomes:
//   Lib.IncrementInGlobalDict("Leaderboard", user, amount);
// =============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Increment a key's value inside a JSON dictionary stored at
        // a global var. Creates the dictionary if missing, creates the key
        // entry if missing.
        //
        // Returns the new value at `dictKey`, or 0 on failure.
        //
        // CONCURRENCY: This is read-modify-write, NOT an atomic CAS.
        // Concurrent callers can race and lose increments. Acceptable for
        // chat-driven leaderboards where exact-counting isn't critical;
        // not appropriate for billing or anything safety-relevant.
        // ─────────────────────────────────────────────────────────────────────
        public int IncrementInGlobalDict(
            string globalKey, string dictKey, int amount, bool persisted = true)
        {
            if (string.IsNullOrWhiteSpace(globalKey) || string.IsNullOrWhiteSpace(dictKey))
                return 0;

            try
            {
                string json = _CPH.GetGlobalVar<string>(globalKey, persisted) ?? "{}";

                Dictionary<string, int> dict;
                try
                {
                    dict = JsonConvert.DeserializeObject<Dictionary<string, int>>(json)
                           ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    // Corrupted value — start fresh rather than crash
                    LogWarn($"[GlobalDict] '{globalKey}' was unparseable; resetting to empty.");
                    dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                if (!dict.ContainsKey(dictKey))
                    dict[dictKey] = 0;

                dict[dictKey] += amount;

                _CPH.SetGlobalVar(globalKey, JsonConvert.SerializeObject(dict), persisted);
                return dict[dictKey];
            }
            catch (Exception ex)
            {
                LogError($"[GlobalDict] Increment failed for {globalKey}[{dictKey}]: {ex.Message}");
                return 0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Read the current value at a key inside a JSON dict global.
        // Returns 0 if anything's missing or unparseable.
        // ─────────────────────────────────────────────────────────────────────
        public int ReadFromGlobalDict(string globalKey, string dictKey, bool persisted = true)
        {
            if (string.IsNullOrWhiteSpace(globalKey) || string.IsNullOrWhiteSpace(dictKey))
                return 0;

            try
            {
                string json = _CPH.GetGlobalVar<string>(globalKey, persisted) ?? "{}";
                var dict = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                if (dict != null && dict.TryGetValue(dictKey, out int val))
                    return val;
            }
            catch (Exception ex)
            {
                LogWarn($"[GlobalDict] Read failed for {globalKey}[{dictKey}]: {ex.Message}");
            }

            return 0;
        }
    }
}
