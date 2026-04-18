// =============================================================================
// OSWTools — Data/SafeGlobalReader.cs
//
// Typed safe wrapper around CPH.GetGlobalVar. The problem it solves:
//
//   CPH.GetGlobalVar<bool>("someKey", true) THROWS if "someKey" was set as a
//   string ("true" instead of a real bool), or if the var doesn't exist on
//   some SB builds. SafeGetBool wraps it so callers get a fallback instead.
//
// This class is used:
//   - Directly by tools that want typed safe reads: `new SafeGlobalReader(CPH).GetBool(...)`
//   - Internally by GlobalsMigration.MigrateIfMissing as the builder's input
//
// All methods return the fallback on ANY failure (wrong type, missing, etc.)
// and never throw.
// =============================================================================

using System;
using System.Globalization;
using Streamer.bot.Plugin.Interface;

namespace OSWTools.Data
{
    public class SafeGlobalReader
    {
        private readonly IInlineInvokeProxy _cph;
        private readonly bool _persisted;

        /// <summary>
        /// Wrap a CPH proxy for safe typed reads. Pass persisted=false only
        /// when reading session globals — default matches the old SAS pattern
        /// (all reads against persisted globals).
        /// </summary>
        public SafeGlobalReader(IInlineInvokeProxy cph, bool persisted = true)
        {
            if (cph == null) throw new ArgumentNullException("cph");
            _cph = cph;
            _persisted = persisted;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: String reader. Never returns null — empty string if missing.
        // ─────────────────────────────────────────────────────────────────────
        public string GetString(string key, string fallback = "")
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            try
            {
                string raw = _cph.GetGlobalVar<string>(key, _persisted);
                return raw ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Bool reader. Handles several legacy storage formats.
        //
        // Accepts: native bool, "true"/"false" strings, "1"/"0" strings.
        // Anything else → fallback.
        // ─────────────────────────────────────────────────────────────────────
        public bool GetBool(string key, bool fallback = false)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;

            // Try native bool first — fastest path when type is already right
            try
            {
                return _cph.GetGlobalVar<bool>(key, _persisted);
            }
            catch
            {
                // Fall through to string-based coercion
            }

            // Fall back to string parsing
            try
            {
                string raw = _cph.GetGlobalVar<string>(key, _persisted);
                if (string.IsNullOrWhiteSpace(raw)) return fallback;

                raw = raw.Trim().ToLowerInvariant();
                if (raw == "true"  || raw == "1" || raw == "yes" || raw == "on")  return true;
                if (raw == "false" || raw == "0" || raw == "no"  || raw == "off") return false;
            }
            catch { }

            return fallback;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Int reader.
        // ─────────────────────────────────────────────────────────────────────
        public int GetInt(string key, int fallback = 0)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            try
            {
                return _cph.GetGlobalVar<int>(key, _persisted);
            }
            catch { }

            // Fallback: parse from string
            try
            {
                string raw = _cph.GetGlobalVar<string>(key, _persisted);
                int val;
                if (!string.IsNullOrWhiteSpace(raw)
                    && int.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out val))
                    return val;
            }
            catch { }

            return fallback;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Double reader.
        // ─────────────────────────────────────────────────────────────────────
        public double GetDouble(string key, double fallback = 0.0)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            try
            {
                return _cph.GetGlobalVar<double>(key, _persisted);
            }
            catch { }

            try
            {
                string raw = _cph.GetGlobalVar<string>(key, _persisted);
                double val;
                if (!string.IsNullOrWhiteSpace(raw)
                    && double.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out val))
                    return val;
            }
            catch { }

            return fallback;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Check whether a global exists. Useful for conditional migrations.
        //
        // Returns true if GetGlobalVar<string> returns a non-null non-empty value.
        // (SB doesn't expose a true "exists" method, so this is a proxy.)
        // ─────────────────────────────────────────────────────────────────────
        public bool Exists(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            try
            {
                string raw = _cph.GetGlobalVar<string>(key, _persisted);
                return !string.IsNullOrEmpty(raw);
            }
            catch { return false; }
        }
    }
}
