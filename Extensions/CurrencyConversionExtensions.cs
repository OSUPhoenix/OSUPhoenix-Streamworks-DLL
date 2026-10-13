////CurrencyConversionExtensions.cs//
// =============================================================================
// NEW FILE — Extensions\CurrencyConversionExtensions.cs
// (Separate from the existing CurrencyExtensions.cs / ParseCurrencyArg, which
//  is left untouched.)
//
// Money that can arrive in ANY currency — YouTube Super Chats, and later
// donations, Tiltify, etc. Three jobs:
//
//   1. KNOW currencies: every currency Windows knows, with its symbol and
//      how many decimals it uses (JPY and KRW use none: "¥500", not "¥500.00").
//          OSWCurrencies.GetAll() / Get("JPY") / LocalCode()
//
//   2. SHOW money the streamer's way: the symbol and decimals of the chosen
//      currency, with THIS PC's regional number style (1,234.50 vs 1.234,50).
//          OSWCurrencies.Format(12.5m, "EUR")   → "€12.50" on a US PC
//
//   3. CONVERT between currencies with daily reference rates, so totals
//      don't add ¥500 to $5 as if they were the same unit.
//          lib.ConvertCurrency(500m, "JPY", "USD")
//
// RATE SOURCES (both free, no API key, fetched at most every 12 hours):
//   • Frankfurter (api.frankfurter.dev) — European Central Bank reference
//     rates, ~30 major currencies. Used first.
//   • open.er-api.com — 160+ currencies, fills the gaps Frankfurter can't
//     (e.g. ARS, CLP). Their free tier asks for attribution: "Rates By
//     Exchange Rate API" (https://www.exchangerate-api.com) — include it in
//     product credits/README.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    public class OSWCurrencyInfo
    {
        /// <summary>ISO 4217 code, e.g. "USD".</summary>
        public string Code { get; set; } = "";
        public string EnglishName { get; set; } = "";
        public string Symbol { get; set; } = "";
        /// <summary>Decimal places normally shown (2 for USD/EUR, 0 for JPY/KRW).</summary>
        public int DecimalDigits { get; set; } = 2;
        public override string ToString() => Code + " — " + EnglishName + " (" + Symbol + ")"; // ComboBox-friendly
    }

    public static class OSWCurrencies
    {
        // Built once, the first time anything asks. Lazy<T> handles the
        // "two threads asked at the same moment" case for us — the list is
        // built exactly once and everyone gets the same copy.
        private static readonly Lazy<Dictionary<string, OSWCurrencyInfo>> Catalog
            = new Lazy<Dictionary<string, OSWCurrencyInfo>>(BuildCatalog);

        /// <summary>Every currency this PC knows, sorted by code — ready for a dropdown.</summary>
        public static List<OSWCurrencyInfo> GetAll() => Catalog.Value.Values.OrderBy(c => c.Code).ToList();

        /// <summary>One currency by ISO code, or null if unknown.</summary>
        public static OSWCurrencyInfo Get(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            Catalog.Value.TryGetValue(code.Trim().ToUpperInvariant(), out var info);
            return info;
        }

        /// <summary>This PC's regional currency (Windows "Region" setting), e.g. "USD".</summary>
        public static string LocalCode()
        {
            try { return new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol; }
            catch { return "USD"; } // "invariant" or unusual culture settings have no region
        }

        /// <summary>
        /// Money as text: the currency's symbol and decimals, in THIS PC's
        /// number style. trimWholeAmounts drops ".00" on round amounts
        /// ("$50" instead of "$50.00") — nice for goal targets.
        /// </summary>
        public static string Format(decimal amount, string code, bool trimWholeAmounts = false)
        {
            var info = Get(code) ?? new OSWCurrencyInfo { Code = code ?? "", Symbol = code ?? "", DecimalDigits = 2 };
            // Clone: CurrentCulture's own NumberFormat is read-only, and
            // changing a shared copy would affect every other format call.
            var nfi = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
            nfi.CurrencySymbol = info.Symbol;
            nfi.CurrencyDecimalDigits = (trimWholeAmounts && amount == decimal.Truncate(amount)) ? 0 : info.DecimalDigits;
            return amount.ToString("C", nfi);
        }

        /// <summary>YouTube-style "micros" (1,000,000 = 1.00) → an amount.</summary>
        public static decimal FromMicros(long micros) => micros / 1000000m;

        private static Dictionary<string, OSWCurrencyInfo> BuildCatalog()
        {
            // Windows doesn't offer "list all currencies", but it knows every
            // region and each region's currency. Several regions can share a
            // currency (EUR across Europe; USD, CAD and others all use "$"), so
            // for each currency we prefer the region whose code matches the
            // currency's first two letters (USD → US, CAD → CA, JPY → JP).
            // Those are the "home" regions — their symbol and decimals are the
            // ones people expect.
            var byCode = new Dictionary<string, OSWCurrencyInfo>(StringComparer.OrdinalIgnoreCase);
            var isHome = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                RegionInfo region;
                try { region = new RegionInfo(culture.Name); } catch { continue; }
                string code = region.ISOCurrencySymbol;
                if (string.IsNullOrEmpty(code) || code.Length != 3) continue;
                bool home = code.StartsWith(region.TwoLetterISORegionName, StringComparison.OrdinalIgnoreCase)
                            || (code == "EUR" && region.TwoLetterISORegionName == "DE"); // EUR has no "EU" region
                if (byCode.ContainsKey(code) && (isHome[code] || !home)) continue;
                byCode[code] = new OSWCurrencyInfo
                {
                    Code = code,
                    EnglishName = region.CurrencyEnglishName,
                    Symbol = NormalizeSymbol(culture.NumberFormat.CurrencySymbol),
                    DecimalDigits = culture.NumberFormat.CurrencyDecimalDigits
                };
                isHome[code] = home;
            }
            return byCode;
        }

        // Some regions report "fullwidth" symbols (￥ U+FFE5 for yen in
        // ja-JP/zh-CN, depending on the Windows version). Fullwidth forms are
        // designed for East Asian text layout — on an overlay next to Latin
        // text they look oversized and oddly spaced. Swap them for the
        // standard characters, which every audience recognizes.
        private static string NormalizeSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return symbol;
            return symbol.Replace('\uFFE5', '\u00A5')  // ￥ → ¥
                         .Replace('\uFFE6', '\u20A9')  // ￦ → ₩
                         .Replace('\uFFE1', '\u00A3')  // ￡ → £
                         .Replace('\uFFE0', '\u00A2')  // ￠ → ¢
                         .Replace('\uFF04', '$');       // ＄ → $
        }
    }

    public partial class OSWLib
    {
        private static readonly Dictionary<string, Tuple<Dictionary<string, decimal>, DateTime>> RateCache
            = new Dictionary<string, Tuple<Dictionary<string, decimal>, DateTime>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object RateLock = new object();
        private static readonly TimeSpan RateTtl = TimeSpan.FromHours(12);

        /// <summary>
        /// Converts an amount between currencies with the latest daily rates.
        /// Same currency in and out = returned as-is, no network call.
        /// </summary>
        public OSWApiResult<decimal> ConvertCurrency(decimal amount, string fromCode, string toCode)
        {
            fromCode = (fromCode ?? "").Trim().ToUpperInvariant();
            toCode = (toCode ?? "").Trim().ToUpperInvariant();
            if (fromCode.Length == 0 || toCode.Length == 0)
                return OSWApiResult<decimal>.Fail("Missing currency code.");
            if (fromCode == toCode)
                return OSWApiResult<decimal>.Ok(amount);

            var rates = GetExchangeRates(toCode);
            if (!rates.Success) return rates.As<decimal>();
            // Rates are "1 toCode buys this many of X", so going FROM X to
            // toCode divides: ¥500 ÷ 147 (yen per dollar) ≈ $3.40.
            if (!rates.Data.TryGetValue(fromCode, out decimal perUnit) || perUnit <= 0)
                return OSWApiResult<decimal>.Fail("No exchange rate available for " + fromCode + ".");
            return OSWApiResult<decimal>.Ok(amount / perUnit);
        }

        /// <summary>
        /// "1 baseCode = N of each other currency", cached for 12 hours.
        /// Reference rates update once a business day, so refetching more
        /// often would only repeat the same numbers.
        /// </summary>
        public OSWApiResult<Dictionary<string, decimal>> GetExchangeRates(string baseCode)
        {
            baseCode = (baseCode ?? "").Trim().ToUpperInvariant();
            lock (RateLock)
            {
                if (RateCache.TryGetValue(baseCode, out var hit) && DateTime.UtcNow - hit.Item2 < RateTtl)
                    return OSWApiResult<Dictionary<string, decimal>>.Ok(hit.Item1);
            }

            var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            string problems = "";

            // 1. Frankfurter (ECB). A base it doesn't cover is an error here —
            //    not fatal, the second source may cover it.
            try
            {
                var resp = OSWHttp.Send(new HttpRequestMessage(HttpMethod.Get,
                    "https://api.frankfurter.dev/v1/latest?base=" + Uri.EscapeDataString(baseCode)));
                string body = OSWHttp.ReadBody(resp);
                if (resp.IsSuccessStatusCode && JObject.Parse(body)["rates"] is JObject fr)
                    foreach (var p in fr.Properties()) rates[p.Name] = p.Value.Value<decimal>();
                else problems += " Frankfurter: " + (int)resp.StatusCode + ".";
            }
            catch (Exception ex) { problems += " Frankfurter: " + ex.Message; }

            // 2. open.er-api.com fills in currencies Frankfurter doesn't have.
            //    Frankfurter's numbers win where both have one (official ECB data).
            try
            {
                var resp = OSWHttp.Send(new HttpRequestMessage(HttpMethod.Get,
                    "https://open.er-api.com/v6/latest/" + Uri.EscapeDataString(baseCode)));
                string body = OSWHttp.ReadBody(resp);
                var j = resp.IsSuccessStatusCode ? JObject.Parse(body) : null;
                if (j != null && (string)j["result"] == "success" && j["rates"] is JObject er)
                {
                    foreach (var p in er.Properties())
                        if (!rates.ContainsKey(p.Name)) rates[p.Name] = p.Value.Value<decimal>();
                }
                else problems += " open.er-api: " + (int)resp.StatusCode + ".";
            }
            catch (Exception ex) { problems += " open.er-api: " + ex.Message; }

            if (rates.Count == 0)
                return OSWApiResult<Dictionary<string, decimal>>.Fail("Couldn't get exchange rates for " + baseCode + "." + problems);

            rates[baseCode] = 1m;
            lock (RateLock) { RateCache[baseCode] = Tuple.Create(rates, DateTime.UtcNow); }
            return OSWApiResult<Dictionary<string, decimal>>.Ok(rates);
        }
    }
}
