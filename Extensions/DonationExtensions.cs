using System;

namespace OSWTools
{
    /// <summary>
    /// Donation event helpers — flexible arg reading and platform detection.
    ///
    /// Donation events in Streamer.bot come from many different services
    /// (Streamlabs, StreamElements, Ko-fi, DonorDrive, FourthWall, TipeeeStream,
    /// Pally.gg) each with their own arg naming conventions. These helpers
    /// normalise all of that behind a single consistent API.
    ///
    /// TYPICAL USAGE in a donation handler action:
    ///
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   string donor    = lib.GetDonorName();
    ///   decimal amount  = lib.GetDonationAmount();
    ///   string currency = lib.GetDonationCurrency();
    ///   string platform = lib.GetDonationServiceName();
    ///   string pic      = lib.GetDonationProfilePicture(donor);
    ///
    ///   // Resolve which streaming platform the donor belongs to
    ///   string streamPlatform = lib.ResolveUserPlatform(donor, "OSUP_SAS_Progress");
    /// </summary>
    public partial class OSWLib
    {
        // ── Flexible arg reading ──────────────────────────────────────────────────

        /// <summary>
        /// Tries a list of arg names in order and returns the first non-empty string
        /// value found. Returns fallback if none are present.
        ///
        /// USAGE:
        ///   string name = lib.GetFlexibleArg(
        ///       new[] { "donorName", "userName", "displayName", "from" }, "Anonymous");
        /// </summary>
        public string GetFlexibleArg(string[] possibleNames, string fallback = "")
        {
            if (possibleNames == null) return fallback;
            foreach (string name in possibleNames)
            {
                try
                {
                    string value;
                    if (_CPH.TryGetArg(name, out value) && !string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
                catch { }
            }
            return fallback;
        }

        /// <summary>
        /// Tries a list of arg names in order and returns the first valid decimal
        /// value found. Falls back to string parsing if the typed read fails.
        /// Returns fallback if nothing parses.
        ///
        /// USAGE:
        ///   decimal amount = lib.GetFlexibleDecimal(
        ///       new[] { "amount", "donorAmount", "tipAmount" }, 0m);
        /// </summary>
        public decimal GetFlexibleDecimal(string[] possibleNames, decimal fallback = 0m)
        {
            if (possibleNames == null) return fallback;
            foreach (string name in possibleNames)
            {
                try
                {
                    decimal value;
                    if (_CPH.TryGetArg(name, out value))
                        return value;

                    string strValue;
                    if (_CPH.TryGetArg(name, out strValue) && !string.IsNullOrWhiteSpace(strValue))
                    {
                        decimal parsed;
                        if (decimal.TryParse(strValue,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out parsed))
                            return parsed;
                    }
                }
                catch { }
            }
            return fallback;
        }

        // ── Donor info ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the donor's name from the current event args.
        /// Tries: donorName, userName, displayName, from, donationFrom, name, user.
        /// Returns "Anonymous" if none are present.
        /// </summary>
        public string GetDonorName()
        {
            return GetFlexibleArg(
                new[] { "donorName", "userName", "displayName", "from",
                        "donationFrom", "tipUsername", "tipDisplayName",
                        "fw.username", "charityDonationFrom", "name", "user" },
                "Anonymous");
        }

        /// <summary>
        /// Returns the donation amount from the current event args.
        /// Tries: amount, donorAmount, donationAmount, tipAmount,
        ///        charityDonationAmount, tipNetAmount, fw.amount.
        /// Returns 0 if not found.
        /// </summary>
        public decimal GetDonationAmount()
        {
            return GetFlexibleDecimal(
                new[] { "amount", "donorAmount", "donationAmount", "tipAmount",
                        "charityDonationAmount", "tipNetAmount" });
        }

        /// <summary>
        /// Returns the donation currency code (e.g. "USD", "GBP").
        /// Tries: currency, donationCurrency, currencyCode.
        /// Defaults to "USD" if not found.
        /// </summary>
        public string GetDonationCurrency()
        {
            string code = GetFlexibleArg(
                new[] { "currency", "donationCurrency", "currencyCode" }, "");
            return string.IsNullOrWhiteSpace(code) ? "USD" : code.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Returns the name of the donation service that triggered this event
        /// (e.g. "Streamlabs", "Ko-Fi", "DonorDrive", "FourthWall").
        ///
        /// First checks the event type directly, then falls back to arg heuristics
        /// from the Donation Tracker's detection logic.
        /// Returns "Unknown" if it cannot be determined.
        /// </summary>
        public string GetDonationServiceName()
        {
            // Detect via arg heuristics — works across all SB versions without
            // requiring EventType enum values that may not exist in all builds.
            try
            {
                string dummy;
                decimal dummyD;

                if (_CPH.TryGetArg("donorName",       out dummy) ||
                    _CPH.TryGetArg("donorAmount",      out dummyD))
                    return "DonorDrive";

                if (_CPH.TryGetArg("donationFrom",     out dummy) ||
                    _CPH.TryGetArg("donationAmount",   out dummyD))
                    return "Streamlabs";

                if (_CPH.TryGetArg("from",             out dummy) &&
                    _CPH.TryGetArg("messageId",        out dummy))
                    return "Ko-Fi";

                if (_CPH.TryGetArg("tipAvatar",        out dummy) ||
                    _CPH.TryGetArg("tipId",            out dummy))
                    return "StreamElements";

                if (_CPH.TryGetArg("fw.username",      out dummy))
                    return "FourthWall";

                if (_CPH.TryGetArg("avatar",           out dummy) &&
                    !_CPH.TryGetArg("donorAvatarUrl",  out dummy))
                    return "TipeeeStream";

                string source;
                if (_CPH.TryGetArg("__source", out source) && !string.IsNullOrWhiteSpace(source))
                    return source;
            }
            catch { }

            return "Unknown";
        }

        /// <summary>
        /// Returns a profile picture URL for the donor by name.
        /// Does NOT call any API — only reads from stored user vars across all
        /// three platforms. Wraps GetProfilePictureFromUserVars().
        ///
        /// Returns empty string if the donor is not a known viewer.
        /// </summary>
        public string GetDonationProfilePicture(string donorName)
        {
            // Try flexible arg first (some services provide avatar in the event)
            string fromArgs = GetFlexibleArg(
                new[] { "profileImage", "profileImageUrl", "donorAvatarUrl",
                        "avatarUrl", "avatar", "tipAvatar", "fw.buyer_avatar_url" }, "");

            if (!string.IsNullOrWhiteSpace(fromArgs))
                return fromArgs;

            // Fall back to stored user vars
            return GetProfilePictureFromUserVars(donorName);
        }
    }
}
