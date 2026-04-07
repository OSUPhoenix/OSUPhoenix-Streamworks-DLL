using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OSWTools.Utilities
{
    // =========================================================================
    //  ColorUtils  —  Color conversion helper for OSWTools
    //
    //  All methods are static — call them directly without instantiating:
    //      var rgba = ColorUtils.HexToRGBA("#FF6A00");
    //      long obs  = ColorUtils.RGBAToOBSColor(255, 106, 0, 180);
    //
    //  ── OBS COLOR FORMAT (ARGB) ──────────────────────────────────────────────
    //  OBS stores colors as a 32-bit integer in ARGB byte order:
    //      Bit 31–24 → Alpha   (most significant byte)
    //      Bit 23–16 → Red
    //      Bit  15–8 → Green
    //      Bit   7–0 → Blue    (least significant byte)
    //
    //  This matches what System.Drawing.Color.ToArgb() produces — confirmed by
    //  the StreamUP DLL which uses (long)(uint)color.ToArgb() to build OBS longs.
    //
    //  NOTE: An earlier version of this file incorrectly documented the format
    //  as ABGR (Alpha, Blue, Green, Red). It is actually ARGB. The test color
    //  0x6E000000 (black) masked the bug since R=0 and B=0 are identical when
    //  swapped. Any non-black color would have produced wrong results.
    //
    //  Example:
    //      OBS integer : 1845493760  →  0x6E000000
    //      OBS UI shows: #6e000000
    //      Decoded     : A=110 (43% opacity), R=0, G=0, B=0  →  black
    //
    //  ── WHY NO System.Drawing.Color? ─────────────────────────────────────────
    //  System.Drawing.Color is available in Streamer.bot and would simplify some
    //  of this code. We deliberately avoid it here to keep ColorUtils free of
    //  that dependency — making it easier to port or test outside of SB. The
    //  byte-shift arithmetic used below is equivalent to what Color.ToArgb() and
    //  Color.FromArgb() do internally.
    // =========================================================================

    public static class ColorUtils
    {
        // ── Hex ↔ RGBA ────────────────────────────────────────────────────────

        /// <summary>
        /// Converts a CSS-style hex color string to RGBA byte components.
        /// Accepts: #RGB, #RRGGBB, #RRGGBBAA, #AARRGGBB (with or without #).
        ///
        /// 8-character hex is treated as AARRGGBB — matching the OBS UI display
        /// format where alpha leads. This is the same interpretation used by
        /// OBS Studio's own color picker.
        ///
        /// Examples:
        ///   ColorUtils.HexToRGBA("#FF6A00")         → R=255, G=106, B=0,   A=255
        ///   ColorUtils.HexToRGBA("#806E000000")     → R=0,   G=0,   B=0,   A=128
        ///   ColorUtils.HexToRGBA("#F60")            → R=255, G=102, B=0,   A=255
        ///
        /// Returns (0, 0, 0, 0) if the string cannot be parsed.
        /// </summary>
        public static (byte R, byte G, byte B, byte A) HexToRGBA(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return (0, 0, 0, 0);

            hex = hex.TrimStart('#').Trim();

            try
            {
                switch (hex.Length)
                {
                    case 3: // #RGB → expand each nibble to a full byte
                        byte r3 = Convert.ToByte(new string(hex[0], 2), 16);
                        byte g3 = Convert.ToByte(new string(hex[1], 2), 16);
                        byte b3 = Convert.ToByte(new string(hex[2], 2), 16);
                        return (r3, g3, b3, 255);

                    case 6: // #RRGGBB — fully opaque
                        byte r6 = Convert.ToByte(hex.Substring(0, 2), 16);
                        byte g6 = Convert.ToByte(hex.Substring(2, 2), 16);
                        byte b6 = Convert.ToByte(hex.Substring(4, 2), 16);
                        return (r6, g6, b6, 255);

                    case 8: // #AARRGGBB — OBS UI format (alpha first)
                        byte a8 = Convert.ToByte(hex.Substring(0, 2), 16);
                        byte r8 = Convert.ToByte(hex.Substring(2, 2), 16);
                        byte g8 = Convert.ToByte(hex.Substring(4, 2), 16);
                        byte b8 = Convert.ToByte(hex.Substring(6, 2), 16);
                        return (r8, g8, b8, a8);

                    default:
                        return (0, 0, 0, 0);
                }
            }
            catch { return (0, 0, 0, 0); }
        }

        /// <summary>
        /// Converts RGBA byte components to a hex color string.
        /// Output: #RRGGBB when alpha is 255, or #AARRGGBB otherwise.
        ///
        /// Examples:
        ///   ColorUtils.RGBAToHex(255, 106, 0)       → "#FF6A00"
        ///   ColorUtils.RGBAToHex(255, 106, 0, 128)  → "#80FF6A00"
        /// </summary>
        public static string RGBAToHex(byte r, byte g, byte b, byte a = 255)
        {
            if (a == 255)
                return string.Format("#{0:X2}{1:X2}{2:X2}", r, g, b);
            else
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", a, r, g, b);
        }


        // ── Universal String Parser ───────────────────────────────────────────

        /// <summary>
        /// Parses any common color string format into RGBA byte components.
        /// Accepts:
        ///   • Hex strings:        "#FF6A00", "#AARRGGBB", "#RGB"
        ///   • CSS rgb/rgba:       "rgb(255, 106, 0)", "rgba(255, 106, 0, 0.5)"
        ///   • CSS float alpha:    "rgba(255, 106, 0, 0.5)"  — alpha 0.0–1.0
        ///   • Integer alpha:      "rgba(255, 106, 0, 128)"  — alpha 0–255
        ///   • Named CSS colors:   "red", "cornflowerblue"
        ///   • Comma-separated:    "255, 106, 0" or "255, 106, 0, 128"
        ///
        /// Named color support uses a built-in lookup covering the most common
        /// CSS named colors (avoids requiring System.Drawing.ColorTranslator).
        ///
        /// Returns (0, 0, 0, 0) if the string cannot be parsed.
        /// </summary>
        public static (byte R, byte G, byte B, byte A) ParseColorString(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return (0, 0, 0, 0);

            string s = input.Trim();

            // ── Hex string ───────────────────────────────────────────────────
            if (s.StartsWith("#"))
                return HexToRGBA(s);

            // ── rgb() / rgba() ────────────────────────────────────────────────
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var matches = Regex.Matches(s, @"[\d.]+");
                if (matches.Count >= 3)
                {
                    byte r = ClampByte((int)double.Parse(matches[0].Value, CultureInfo.InvariantCulture));
                    byte g = ClampByte((int)double.Parse(matches[1].Value, CultureInfo.InvariantCulture));
                    byte b = ClampByte((int)double.Parse(matches[2].Value, CultureInfo.InvariantCulture));
                    byte a = 255;
                    if (matches.Count >= 4)
                    {
                        double rawA = double.Parse(matches[3].Value, CultureInfo.InvariantCulture);
                        // Float alpha (0.0–1.0) vs integer alpha (2–255) — detect by decimal point
                        a = rawA <= 1.0 && matches[3].Value.Contains(".")
                            ? (byte)Math.Round(rawA * 255.0)
                            : ClampByte((int)rawA);
                    }
                    return (r, g, b, a);
                }
                return (0, 0, 0, 0);
            }

            // ── Comma-separated R,G,B or R,G,B,A ────────────────────────────
            if (Regex.IsMatch(s, @"^\d"))
            {
                var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    byte r = ClampByte(int.Parse(parts[0], CultureInfo.InvariantCulture));
                    byte g = ClampByte(int.Parse(parts[1], CultureInfo.InvariantCulture));
                    byte b = ClampByte(int.Parse(parts[2], CultureInfo.InvariantCulture));
                    byte a = parts.Length >= 4
                        ? ClampByte(int.Parse(parts[3], CultureInfo.InvariantCulture))
                        : (byte)255;
                    return (r, g, b, a);
                }
            }

            // ── Named CSS colors (common subset) ─────────────────────────────
            switch (s.ToLowerInvariant())
            {
                case "black":       return (0,   0,   0,   255);
                case "white":       return (255, 255, 255, 255);
                case "red":         return (255, 0,   0,   255);
                case "lime":        return (0,   255, 0,   255);
                case "blue":        return (0,   0,   255, 255);
                case "yellow":      return (255, 255, 0,   255);
                case "cyan":
                case "aqua":        return (0,   255, 255, 255);
                case "magenta":
                case "fuchsia":     return (255, 0,   255, 255);
                case "orange":      return (255, 165, 0,   255);
                case "purple":      return (128, 0,   128, 255);
                case "green":       return (0,   128, 0,   255);
                case "pink":        return (255, 192, 203, 255);
                case "gray":
                case "grey":        return (128, 128, 128, 255);
                case "silver":      return (192, 192, 192, 255);
                case "maroon":      return (128, 0,   0,   255);
                case "navy":        return (0,   0,   128, 255);
                case "teal":        return (0,   128, 128, 255);
                case "olive":       return (128, 128, 0,   255);
                case "coral":       return (255, 127, 80,  255);
                case "salmon":      return (250, 128, 114, 255);
                case "gold":        return (255, 215, 0,   255);
                case "violet":      return (238, 130, 238, 255);
                case "indigo":      return (75,  0,   130, 255);
                case "transparent": return (0,   0,   0,   0);
                default:            return (0,   0,   0,   0);
            }
        }

        /// <summary>
        /// Parses any common color string format and returns an OBS color integer (ARGB).
        /// Combines ParseColorString and RGBAToOBSColor in one step.
        ///
        /// Returns 0 if the string cannot be parsed.
        /// </summary>
        public static long ParseToOBSColor(string input)
        {
            var (r, g, b, a) = ParseColorString(input);
            return RGBAToOBSColor(r, g, b, a);
        }


        // ── OBS Color (ARGB integer) ↔ RGBA ───────────────────────────────────

        /// <summary>
        /// Converts an OBS color integer (ARGB format) to RGBA byte components.
        ///
        /// OBS byte layout (matching System.Drawing.Color.ToArgb()):
        ///   Bits 31–24 → Alpha   (most significant)
        ///   Bits 23–16 → Red
        ///   Bits  15–8 → Green
        ///   Bits   7–0 → Blue    (least significant)
        ///
        /// Example:
        ///   ColorUtils.OBSColorToRGBA(1845493760)
        ///   → 0x6E000000 → A=110, R=0, G=0, B=0
        ///   → returns (R=0, G=0, B=0, A=110)   ← black at ~43% opacity
        /// </summary>
        public static (byte R, byte G, byte B, byte A) OBSColorToRGBA(long obsColor)
        {
            byte a = (byte)((obsColor >> 24) & 0xFF);
            byte r = (byte)((obsColor >> 16) & 0xFF); // ARGB: Red is in byte 2
            byte g = (byte)((obsColor >>  8) & 0xFF);
            byte b = (byte)((obsColor >>  0) & 0xFF); // ARGB: Blue is in byte 0
            return (r, g, b, a);
        }

        /// <summary>
        /// Converts RGBA byte components to an OBS color integer (ARGB format).
        ///
        /// Equivalent to (long)(uint)Color.FromArgb(a, r, g, b).ToArgb()
        /// which is exactly how StreamUP and OBS itself build these integers.
        ///
        /// Example:
        ///   ColorUtils.RGBAToOBSColor(0, 0, 0, 110)  → 1845493760  (0x6E000000)
        /// </summary>
        public static long RGBAToOBSColor(byte r, byte g, byte b, byte a = 255)
        {
            // ARGB: Alpha in high byte, Red next, Green, Blue in low byte
            return ((long)a << 24) | ((long)r << 16) | ((long)g << 8) | b;
        }

        /// <summary>
        /// Converts an OBS color integer to a hex string as displayed in OBS Studio.
        /// Output format: #AARRGGBB  (matches the OBS color picker display).
        ///
        /// Example:
        ///   ColorUtils.OBSColorToHex(1845493760)  → "#6E000000"
        /// </summary>
        public static string OBSColorToHex(long obsColor)
        {
            var (r, g, b, a) = OBSColorToRGBA(obsColor);
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", a, r, g, b);
        }

        /// <summary>
        /// Converts an OBS color integer to a standard CSS hex string.
        /// Output: #RRGGBB (alpha ignored) or #RRGGBBAA (alpha appended, CSS4).
        ///
        /// Note: CSS4 hex alpha (#RRGGBBAA) works in modern browsers but not all
        /// streaming overlay tools. Use OBSColorToCSSRgba() for the broadest
        /// compatibility when alpha matters.
        /// </summary>
        public static string OBSColorToCSSHex(long obsColor, bool includeAlpha = false)
        {
            var (r, g, b, a) = OBSColorToRGBA(obsColor);
            if (includeAlpha)
                return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", r, g, b, a);
            else
                return string.Format("#{0:X2}{1:X2}{2:X2}", r, g, b);
        }

        /// <summary>
        /// Converts a hex string (OBS UI format #AARRGGBB or standard #RRGGBB)
        /// to an OBS color integer (ARGB format).
        ///
        /// Example:
        ///   ColorUtils.HexToOBSColor("#6E000000")  → 1845493760
        /// </summary>
        public static long HexToOBSColor(string hex)
        {
            var (r, g, b, a) = HexToRGBA(hex);
            return RGBAToOBSColor(r, g, b, a);
        }

        /// <summary>
        /// Returns the alpha component of an OBS color integer as a value
        /// between 0.0 (fully transparent) and 1.0 (fully opaque).
        ///
        /// Useful for setting opacity sliders or CSS rgba() alpha values.
        ///
        /// Example:
        ///   ColorUtils.OBSColorToAlphaFloat(1845493760)  → 0.431  (~43%)
        /// </summary>
        public static double OBSColorToAlphaFloat(long obsColor)
        {
            byte a = (byte)((obsColor >> 24) & 0xFF);
            return Math.Round(a / 255.0, 3);
        }


        // ── RGBA ↔ CSS rgba() String ──────────────────────────────────────────

        /// <summary>
        /// Converts RGBA byte components to a CSS rgba() string.
        /// Alpha is expressed as a float 0.0–1.0 as CSS expects.
        ///
        /// Example:
        ///   ColorUtils.RGBAToCSSRgba(255, 106, 0, 128)  → "rgba(255, 106, 0, 0.502)"
        /// </summary>
        public static string RGBAToCSSRgba(byte r, byte g, byte b, byte a = 255)
        {
            double alphaFloat = Math.Round(a / 255.0, 3);
            return string.Format("rgba({0}, {1}, {2}, {3})", r, g, b, alphaFloat);
        }

        /// <summary>
        /// Converts an OBS color integer directly to a CSS rgba() string.
        /// Convenient one-liner for injecting OBS colors into HTML overlays.
        ///
        /// Example:
        ///   ColorUtils.OBSColorToCSSRgba(1845493760)  → "rgba(0, 0, 0, 0.431)"
        /// </summary>
        public static string OBSColorToCSSRgba(long obsColor)
        {
            var (r, g, b, a) = OBSColorToRGBA(obsColor);
            return RGBAToCSSRgba(r, g, b, a);
        }


        // ── RGBA ↔ Normalized Floats (0.0–1.0) ───────────────────────────────

        /// <summary>
        /// Converts RGBA byte components (0–255) to normalized float components (0.0–1.0).
        /// Used by some graphics APIs and shader systems.
        ///
        /// Example:
        ///   ColorUtils.RGBAToFloat(255, 106, 0, 128)
        ///   → (R=1.0, G=0.416, B=0.0, A=0.502)
        /// </summary>
        public static (double R, double G, double B, double A) RGBAToFloat(byte r, byte g, byte b, byte a = 255)
        {
            return (
                Math.Round(r / 255.0, 3),
                Math.Round(g / 255.0, 3),
                Math.Round(b / 255.0, 3),
                Math.Round(a / 255.0, 3)
            );
        }

        /// <summary>
        /// Converts normalized float components (0.0–1.0) to RGBA byte components (0–255).
        /// Values are clamped to the valid range before conversion.
        /// </summary>
        public static (byte R, byte G, byte B, byte A) FloatToRGBA(double r, double g, double b, double a = 1.0)
        {
            // FIX: Bug #2 — Replaced Math.Clamp (not available in .NET Framework 4.8.1)
            //      with ClampDouble helper. Math.Clamp was added in .NET Core 2.0 only.
            return (
                (byte)(ClampDouble(r, 0.0, 1.0) * 255),
                (byte)(ClampDouble(g, 0.0, 1.0) * 255),
                (byte)(ClampDouble(b, 0.0, 1.0) * 255),
                (byte)(ClampDouble(a, 0.0, 1.0) * 255)
            );
        }


        // ── RGBA ↔ HSL ────────────────────────────────────────────────────────

        /// <summary>
        /// Converts RGBA byte components to HSL (Hue, Saturation, Lightness).
        ///   H → 0.0–360.0 degrees
        ///   S → 0.0–1.0
        ///   L → 0.0–1.0
        ///   A → 0.0–1.0 (passed through, converted from byte)
        ///
        /// HSL is useful for hue-shifting (recoloring UI elements) or checking
        /// whether a color is "light" or "dark" for contrast decisions.
        /// </summary>
        public static (double H, double S, double L, double A) RGBAToHSL(byte r, byte g, byte b, byte a = 255)
        {
            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;

            double max   = Math.Max(rf, Math.Max(gf, bf));
            double min   = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            double l = (max + min) / 2.0;
            double s = 0.0;
            double h = 0.0;

            if (delta > 0.00001)
            {
                s = l < 0.5
                    ? delta / (max + min)
                    : delta / (2.0 - max - min);

                if (max == rf)
                    h = ((gf - bf) / delta) % 6.0;
                else if (max == gf)
                    h = (bf - rf) / delta + 2.0;
                else
                    h = (rf - gf) / delta + 4.0;

                h *= 60.0;
                if (h < 0) h += 360.0;
            }

            return (Math.Round(h, 2), Math.Round(s, 3), Math.Round(l, 3), Math.Round(a / 255.0, 3));
        }

        /// <summary>
        /// Converts HSL values back to RGBA byte components.
        ///   H → 0.0–360.0 degrees
        ///   S → 0.0–1.0
        ///   L → 0.0–1.0
        ///   A → 0.0–1.0
        /// </summary>
        public static (byte R, byte G, byte B, byte A) HSLToRGBA(double h, double s, double l, double a = 1.0)
        {
            double r, g, b;

            if (s < 0.00001)
            {
                r = g = b = l; // achromatic (grey)
            }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;
                r = HueToChannel(p, q, h / 360.0 + 1.0 / 3.0);
                g = HueToChannel(p, q, h / 360.0);
                b = HueToChannel(p, q, h / 360.0 - 1.0 / 3.0);
            }

            return (
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(b * 255),
                // FIX: Bug #2 — Replaced Math.Clamp with ClampDouble
                (byte)Math.Round(ClampDouble(a, 0.0, 1.0) * 255)
            );
        }

        // Internal helper for HSL → RGB channel conversion
        private static double HueToChannel(double p, double q, double t)
        {
            if (t < 0) t += 1.0;
            if (t > 1) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }


        // ── RGBA ↔ HSV ────────────────────────────────────────────────────────

        /// <summary>
        /// Converts RGBA byte components to HSV (Hue, Saturation, Value).
        ///   H → 0.0–360.0 degrees
        ///   S → 0.0–1.0
        ///   V → 0.0–1.0  (Value = brightness)
        ///   A → 0.0–1.0  (passed through unchanged)
        ///
        /// HSV maps more naturally to perceived brightness than HSL's Lightness,
        /// which is why color pickers tend to use it.
        /// </summary>
        public static (double H, double S, double V, double A) RGBAToHSV(byte r, byte g, byte b, byte a = 255)
        {
            double rf = r / 255.0;
            double gf = g / 255.0;
            double bf = b / 255.0;

            double max   = Math.Max(rf, Math.Max(gf, bf));
            double min   = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            double v = max;
            double s = max < 0.00001 ? 0.0 : delta / max;
            double h = 0.0;

            if (delta > 0.00001)
            {
                if (max == rf)
                    h = ((gf - bf) / delta) % 6.0;
                else if (max == gf)
                    h = (bf - rf) / delta + 2.0;
                else
                    h = (rf - gf) / delta + 4.0;

                h *= 60.0;
                if (h < 0) h += 360.0;
            }

            return (Math.Round(h, 2), Math.Round(s, 3), Math.Round(v, 3), Math.Round(a / 255.0, 3));
        }

        /// <summary>
        /// Converts HSV values back to RGBA byte components.
        ///   H → 0.0–360.0 degrees
        ///   S → 0.0–1.0
        ///   V → 0.0–1.0
        ///   A → 0.0–1.0
        /// </summary>
        public static (byte R, byte G, byte B, byte A) HSVToRGBA(double h, double s, double v, double a = 1.0)
        {
            double r, g, b;

            if (s < 0.00001)
            {
                r = g = b = v; // achromatic
            }
            else
            {
                h = h % 360.0;
                if (h < 0) h += 360.0;
                double sector = h / 60.0;
                int    i      = (int)Math.Floor(sector);
                double f      = sector - i;
                double p      = v * (1.0 - s);
                double q      = v * (1.0 - s * f);
                double t_val  = v * (1.0 - s * (1.0 - f));

                switch (i % 6)
                {
                    case 0: r = v;     g = t_val; b = p;     break;
                    case 1: r = q;     g = v;     b = p;     break;
                    case 2: r = p;     g = v;     b = t_val; break;
                    case 3: r = p;     g = q;     b = v;     break;
                    case 4: r = t_val; g = p;     b = v;     break;
                    default: r = v;    g = p;     b = q;     break;
                }
            }

            return (
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(b * 255),
                // FIX: Bug #2 — Replaced Math.Clamp with ClampDouble
                (byte)Math.Round(ClampDouble(a, 0.0, 1.0) * 255)
            );
        }


        // ── Luminance & Contrast ──────────────────────────────────────────────

        /// <summary>
        /// Returns the relative luminance of an RGB color (0.0–1.0).
        /// Uses the WCAG 2.1 formula for perceptual brightness.
        /// </summary>
        public static double GetLuminance(byte r, byte g, byte b)
        {
            double Linearize(byte c)
            {
                double v = c / 255.0;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
        }

        /// <summary>
        /// Returns the best foreground color (black or white hex string) for text
        /// displayed on top of a given background color.
        /// </summary>
        public static string GetContrastColor(byte r, byte g, byte b)
        {
            return GetLuminance(r, g, b) > 0.179 ? "#000000" : "#FFFFFF";
        }

        /// <summary>
        /// Returns the best foreground hex color for text on an OBS color background.
        /// </summary>
        public static string GetContrastColorFromOBS(long obsColor)
        {
            var (r, g, b, _) = OBSColorToRGBA(obsColor);
            return GetContrastColor(r, g, b);
        }

        /// <summary>
        /// Returns the best foreground OBS color integer (black or white) for text
        /// displayed on top of an OBS color background.
        /// </summary>
        public static long GetContrastOBSColorFromOBS(long obsColor)
        {
            var (r, g, b, _) = OBSColorToRGBA(obsColor);
            return GetLuminance(r, g, b) > 0.179
                ? 4278190080L   // opaque black
                : 4294967295L;  // opaque white
        }


        // ── Random Color ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns a random fully-opaque RGBA color.
        /// </summary>
        public static (byte R, byte G, byte B, byte A) GetRandomColor()
        {
            var rng = new Random();
            return ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
        }

        /// <summary>
        /// Returns a random color as a #RRGGBB hex string.
        /// </summary>
        public static string GetRandomColorHex()
        {
            var (r, g, b, _) = GetRandomColor();
            return RGBAToHex(r, g, b);
        }

        /// <summary>
        /// Returns a random color as an OBS color integer (fully opaque).
        /// </summary>
        public static long GetRandomOBSColor()
        {
            var (r, g, b, a) = GetRandomColor();
            return RGBAToOBSColor(r, g, b, a);
        }


        // ── Color Lerp (Interpolation) ────────────────────────────────────────

        /// <summary>
        /// Linearly interpolates between two RGBA colors.
        /// t = 0.0 → fully colorA,  t = 1.0 → fully colorB.
        ///
        /// Example:
        ///   ColorUtils.LerpRGBA((255,0,0,255), (0,255,0,255), 0.5)
        ///   → (127, 127, 0, 255)
        /// </summary>
        public static (byte R, byte G, byte B, byte A) LerpRGBA(
            (byte R, byte G, byte B, byte A) colorA,
            (byte R, byte G, byte B, byte A) colorB,
            double t)
        {
            // FIX: Bug #2 — Replaced Math.Clamp with ClampDouble
            t = ClampDouble(t, 0.0, 1.0);
            return (
                (byte)Math.Round(colorA.R + (colorB.R - colorA.R) * t),
                (byte)Math.Round(colorA.G + (colorB.G - colorA.G) * t),
                (byte)Math.Round(colorA.B + (colorB.B - colorA.B) * t),
                (byte)Math.Round(colorA.A + (colorB.A - colorA.A) * t)
            );
        }

        /// <summary>
        /// Linearly interpolates between two OBS color integers.
        /// Returns the result as an OBS color integer (ARGB format).
        ///
        /// t = 0.0 → fully obsColorA,  t = 1.0 → fully obsColorB.
        /// </summary>
        public static long LerpOBSColor(long obsColorA, long obsColorB, double t)
        {
            var ca     = OBSColorToRGBA(obsColorA);
            var cb     = OBSColorToRGBA(obsColorB);
            var lerped = LerpRGBA(ca, cb, t);
            return RGBAToOBSColor(lerped.R, lerped.G, lerped.B, lerped.A);
        }


        // ── Alpha Adjustment ──────────────────────────────────────────────────

        /// <summary>
        /// Returns a new OBS color integer with the alpha channel replaced.
        /// </summary>
        public static long SetOBSColorAlpha(long obsColor, byte newAlpha)
        {
            var (r, g, b, _) = OBSColorToRGBA(obsColor);
            return RGBAToOBSColor(r, g, b, newAlpha);
        }

        /// <summary>
        /// Returns a new OBS color integer with alpha set from a float (0.0–1.0).
        /// </summary>
        public static long SetOBSColorAlphaFloat(long obsColor, double alpha)
        {
            // FIX: Bug #2 — Replaced Math.Clamp with ClampDouble
            byte a = (byte)(ClampDouble(alpha, 0.0, 1.0) * 255);
            return SetOBSColorAlpha(obsColor, a);
        }


        // ── Internal Helpers ──────────────────────────────────────────────────

        private static byte ClampByte(int value)
        {
            if (value < 0)   return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        // FIX: Bug #2 — Added ClampDouble helper to replace Math.Clamp.
        //      Math.Clamp was introduced in .NET Core 2.0 and does NOT exist
        //      in .NET Framework 4.8.1 (the target for this project/Streamer.bot).
        //      This helper uses Math.Max + Math.Min which are available in all
        //      .NET Framework versions. Produces identical results to Math.Clamp.
        private static double ClampDouble(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
