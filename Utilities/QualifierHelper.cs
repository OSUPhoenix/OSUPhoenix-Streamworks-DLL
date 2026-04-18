// =============================================================================
// OSWTools — Utilities/QualifierHelper.cs
//
// Helpers for parsing qualifier strings used in achievement/redemption configs.
//
// Threshold qualifier syntax:
//   ">100"          → value must be ≥ 100
//   "<500"          → value must be ≤ 500
//   ">100,<500"     → value must be ≥ 100 AND ≤ 500    (comma = AND)
//
// Notes:
//   - The `>` operator means "greater-or-equal", `<` means "less-or-equal".
//     This matches existing user-facing semantics in the SAS achievement system.
//   - Whitespace around commas/operators is tolerated.
//   - Any malformed comparator causes the WHOLE qualifier to fail.
//   - Empty / null qualifiers return false (no match) — callers should special-
//     case "no qualifier means match anything" upstream.
//
// Also includes MakeSafeFileName() — strips filesystem-illegal characters from
// a filename candidate. Useful when building per-user/per-event filenames.
// =============================================================================

using System;
using System.Globalization;
using System.IO;

namespace OSWTools.Utilities
{
    public static class QualifierHelper
    {
        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Test whether `value` satisfies all comparators in `qualifier`.
        // Both qualifier numbers and value must be in the same unit.
        //
        // Examples:
        //   MatchesThreshold(">100",       250)  → true   (250 ≥ 100)
        //   MatchesThreshold(">100,<500",  250)  → true   (250 ≥ 100 AND 250 ≤ 500)
        //   MatchesThreshold(">100,<500",  600)  → false  (600 > 500)
        //   MatchesThreshold("",           250)  → false  (no comparators)
        //   MatchesThreshold("100",        250)  → false  (missing operator)
        // ─────────────────────────────────────────────────────────────────────
        public static bool MatchesThreshold(string qualifier, int value)
        {
            if (string.IsNullOrWhiteSpace(qualifier))
                return false;

            var parts = qualifier.Split(',');
            bool sawAtLeastOne = false;

            foreach (var rawPart in parts)
            {
                string part = (rawPart ?? "").Trim();
                if (part.Length < 2) // need at least "<N" or ">N"
                    return false;

                char op = part[0];
                if (op != '>' && op != '<')
                    return false;

                if (!double.TryParse(part.Substring(1).Trim(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double thresh))
                    return false;

                bool ok = (op == '>') ? value >= (int)thresh : value <= (int)thresh;
                if (!ok)
                    return false;

                sawAtLeastOne = true;
            }

            return sawAtLeastOne;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Currency variant — qualifier is in dollars, value is in cents.
        //
        // The qualifier ">5" means "$5 or more", which gets compared against
        // the value (in cents). Internally multiplies the qualifier by 100 with
        // ceiling rounding (so ">5.99" → 599 cents, not 598).
        //
        // Example:
        //   MatchesCurrencyThreshold(">5",          500)  → true   ($5.00 ≥ $5)
        //   MatchesCurrencyThreshold(">5",          499)  → false  ($4.99 < $5)
        //   MatchesCurrencyThreshold(">1,<10",     750)  → true   ($7.50 in [$1,$10])
        // ─────────────────────────────────────────────────────────────────────
        public static bool MatchesCurrencyThreshold(string qualifier, int valueInCents)
        {
            if (string.IsNullOrWhiteSpace(qualifier))
                return false;

            var parts = qualifier.Split(',');
            bool sawAtLeastOne = false;

            foreach (var rawPart in parts)
            {
                string part = (rawPart ?? "").Trim();
                if (part.Length < 2)
                    return false;

                char op = part[0];
                if (op != '>' && op != '<')
                    return false;

                if (!double.TryParse(part.Substring(1).Trim(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double dollars))
                    return false;

                int threshCents = (int)Math.Ceiling(dollars * 100);
                bool ok = (op == '>') ? valueInCents >= threshCents : valueInCents <= threshCents;
                if (!ok)
                    return false;

                sawAtLeastOne = true;
            }

            return sawAtLeastOne;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Strip filesystem-illegal characters from a filename candidate.
        //
        // Replaces every character in Path.GetInvalidFileNameChars() with '_'.
        // Returns "Unknown" for null/whitespace input so callers always get a
        // usable string. Trims leading/trailing whitespace.
        //
        // NOTE: Does NOT enforce length limits or reserved-name avoidance
        // (CON, NUL, PRN, etc. on Windows). Callers building paths under
        // user control should still validate the final path.
        // ─────────────────────────────────────────────────────────────────────
        public static string MakeSafeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "Unknown";

            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            return s.Trim();
        }
    }
}
