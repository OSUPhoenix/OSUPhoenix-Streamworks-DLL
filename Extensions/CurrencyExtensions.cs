// =============================================================================
// OSWTools — Extensions/CurrencyExtensions.cs
//
// Currency-aware event arg parsing. Different donation/tip services name their
// amount fields differently and sometimes include currency symbols or commas
// inside the value (e.g. "$5.00", "5,00"). This helper strips non-numeric
// characters (keeping the decimal point) and parses to a double.
//
// USAGE:
//   double amount = Lib.ParseCurrencyArg("donationAmount");        // Streamlabs
//   double amount = Lib.ParseCurrencyArg("tipAmount", fallback: 0); // StreamElements
//
// For service-specific arg name conventions, see the existing
// DonationExtensions.GetDonationAmount() helper which tries several common names.
// ParseCurrencyArg is for cases where you already know the exact arg name.
// =============================================================================

using System.Globalization;
using System.Linq;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Read a currency amount from an event arg.
        //
        // Reads the named arg as a string, strips everything that isn't a digit
        // or a decimal point (so "$5.00", "5,00 €", "USD 5.00" all work), then
        // parses the result as a double.
        //
        // Returns `fallback` if the arg is missing, empty, or unparseable.
        //
        // CAVEAT: This treats "5,00" (European decimal) as "500" because the
        // comma is stripped. If you need locale-aware parsing for European-
        // formatted amounts, parse the raw arg yourself.
        // ─────────────────────────────────────────────────────────────────────
        public double ParseCurrencyArg(string argName, double fallback = 0)
        {
            if (string.IsNullOrWhiteSpace(argName))
                return fallback;

            try
            {
                string raw;
                if (!_CPH.TryGetArg(argName, out raw) || string.IsNullOrWhiteSpace(raw))
                    return fallback;

                // Keep only digits and decimal points
                string filtered = new string(raw.Where(c => char.IsDigit(c) || c == '.').ToArray());

                if (string.IsNullOrEmpty(filtered))
                    return fallback;

                return double.TryParse(filtered, NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double val) ? val : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
