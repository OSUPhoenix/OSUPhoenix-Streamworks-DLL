using System;

namespace OSWTools
{
    /// <summary>
    /// Merch purchase event helpers.
    ///
    /// Merch events come from FourthWall, Shopify, StreamElements, Streamlabs,
    /// and Ko-fi Shop — all with different arg names for the same concepts.
    /// These helpers normalise them into a single consistent API.
    ///
    /// TYPICAL USAGE in a merch purchase handler action:
    ///
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   string buyer    = lib.GetMerchBuyerName();
    ///   string product  = lib.GetMerchProductName();
    ///   string service  = lib.GetMerchServiceName();
    ///
    ///   // Resolve which streaming platform the buyer belongs to
    ///   string platform = lib.ResolveUserPlatform(buyer, "OSUP_SAS_Progress");
    ///   string pic      = lib.GetProfilePictureFromUserVars(buyer);
    /// </summary>
    public partial class OSWLib
    {
        // ── Merch buyer ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the buyer's name from the current merch purchase event args.
        ///
        /// Service → arg used:
        ///   FourthWall       → fw.username
        ///   Shopify          → shopify.name
        ///   StreamElements   → merchUsername
        ///   Streamlabs       → merchandiseFrom
        ///   Ko-fi Shop       → from
        /// </summary>
        public string GetMerchBuyerName()
        {
            // Try service-specific args in the most likely order
            return GetFlexibleArg(
                new[] { "fw.username", "shopify.name", "merchUsername",
                        "merchandiseFrom", "from", "userName", "user" },
                string.Empty);
        }

        // ── Merch product ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the product/item name from the current merch purchase event args.
        ///
        /// Service → arg used:
        ///   FourthWall       → fw.variants[#].name
        ///   Shopify          → shopify.line_items[#].name
        ///   StreamElements   → itemName
        ///   Streamlabs       → itemName
        ///   Ko-fi Shop       → productName
        /// </summary>
        public string GetMerchProductName()
        {
            return GetFlexibleArg(
                new[] { "fw.variants[#].name", "shopify.line_items[#].name",
                        "itemName", "productName", "product" },
                string.Empty);
        }

        // ── Merch service ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the name of the merch service that triggered this event
        /// (e.g. "FourthWall", "Shopify", "StreamElements", "Streamlabs", "Ko-fi").
        ///
        /// Checks the event type first, then falls back to arg heuristics.
        /// Returns "Unknown" if it cannot be determined.
        /// </summary>
        public string GetMerchServiceName()
        {
            // Detect via arg heuristics — works across all SB versions without
            // requiring EventType enum values that may not exist in all builds.
            try
            {
                string dummy;
                if (_CPH.TryGetArg("fw.username",                out dummy)) return "FourthWall";
                if (_CPH.TryGetArg("shopify.line_items[#].name", out dummy)) return "Shopify";
                if (_CPH.TryGetArg("merchUsername",              out dummy)) return "StreamElements";
                if (_CPH.TryGetArg("merchandiseFrom",            out dummy)) return "Streamlabs";
            }
            catch { }

            return "Unknown";
        }

        // ── FourthWall specifics ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the buyer's avatar URL from a FourthWall order event.
        /// Returns empty string if not available.
        /// </summary>
        public string GetFourthWallBuyerAvatar()
        {
            return GetFlexibleArg(new[] { "fw.buyer_avatar_url" }, string.Empty);
        }

        /// <summary>
        /// Returns the buyer's email from a FourthWall order event.
        /// Returns empty string if not available.
        /// </summary>
        public string GetFourthWallBuyerEmail()
        {
            return GetFlexibleArg(new[] { "fw.buyer_email" }, string.Empty);
        }

        /// <summary>
        /// Returns the order total amount from a FourthWall event.
        /// Returns 0 if not available.
        /// </summary>
        public decimal GetFourthWallOrderAmount()
        {
            return GetFlexibleDecimal(new[] { "fw.amount", "fw.total" }, 0m);
        }
    }
}
