// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerLayout.cs     DLL v1.3.0
//
//  The "how big and where" math for a single GIF.
//
//  WHY THIS FILE EXISTS:
//    The REAL layout happens in the overlay (JavaScript), because only
//    the browser knows how big a GIF really is once it has loaded. But
//    the Settings window wants to SHOW you what your numbers will do,
//    so it needs the same math in C#.
//
//    These two functions are a line-for-line twin of fitGif() and
//    placeGif() in the overlay HTML. The test suite runs random cases
//    through both and fails if they ever disagree — so if you change
//    one, change the other.
//
//  COORDINATES:
//    Everything here is in "design pixels": the screen area is
//    areaW x areaH (your Auto size or Custom size). The overlay turns
//    design pixels into real pixels with one scale factor at the end.
//
//  WHAT COUNTS AS "THE GIF":
//    The picture itself is w x h. Around it we reserve room for
//      • the glow  (GlowPad — so a glow is never cut off by the edge;
//        reserved only for GIFs that actually have one)
//      • the @username label under it (LabelHeight)
//    and keep that whole bundle inside the screen area.
// ═══════════════════════════════════════════════════════════════════
using System;

namespace OSWTools.Functions.GifBattler
{
    internal static class GifBattlerLayout
    {
        /// <summary>Room reserved under the picture for "@username".</summary>
        internal const int LabelHeight = 24;

        /// <summary>
        /// JavaScript's Math.round rounds halves UP; C#'s Math.Round rounds
        /// halves to even. Using this everywhere keeps both sides identical.
        /// </summary>
        internal static int RoundHalfUp(double v)
        {
            return (int)Math.Floor(v + 0.5);
        }

        /// <summary>
        /// Does THIS GIF get a glow? "off" never; "color" and "random" always;
        /// "role" only for mods (gold) and subs (purple) — everyone else gets none.
        /// ★ Asked per GIF so a viewer with no glow is not kept away from the
        /// edge by room that nothing will fill (an offset of 20 means 20).
        /// </summary>
        internal static bool HasGlow(GifBattlerConfig c, string tier)
        {
            switch (c.GlowMode)
            {
                case "off":  return false;
                case "role": return tier == "mod" || tier == "sub";
                default:     return true;
            }
        }

        /// <summary>
        /// How far the glow reaches past the picture edge, in pixels:
        /// ring thickness + blur radius + the extra spread we add (blur/4).
        /// 0 when this GIF has no glow, so nothing is reserved.
        /// </summary>
        internal static int GlowPad(GifBattlerConfig c, string tier)
        {
            if (!HasGlow(c, tier)) return 0;
            return c.BorderWidth + (int)Math.Ceiling(c.GlowSize * 1.25);
        }

        /// <summary>Box scale for a viewer role: 100% when "size by role" is off.</summary>
        internal static int RolePercent(GifBattlerConfig c, string tier)
        {
            if (!c.SizeByRole) return 100;
            if (tier == "mod") return c.ScaleModPercent;
            if (tier == "sub") return c.ScaleSubPercent;
            return c.ScaleViewerPercent;
        }

        /// <summary>
        /// Step 1 — how big is the picture?
        ///
        /// Take the max box (scaled for the viewer's role), shrink the box
        /// if the screen is smaller than it, then scale the GIF by the SMALLER
        /// of (box width / GIF width) and (box height / GIF height).
        /// Using the smaller ratio is what keeps the proportions: a tall
        /// skinny GIF runs out of HEIGHT first, so height becomes the limit
        /// and the width simply follows. The GIF is never stretched.
        /// </summary>
        internal static void Fit(GifBattlerConfig c, string tier,
                                 double natW, double natH, int areaW, int areaH,
                                 out int w, out int h)
        {
            if (natW < 1) natW = 1;
            if (natH < 1) natH = 1;

            int pad    = GlowPad(c, tier);
            int labelH = c.ShowUsername ? LabelHeight : 0;
            bool full  = c.DisplayMode == "fullscreen";

            double boxW, boxH;
            if (full)
            {
                // "Large & centred": a square box covering ~85% of the short side.
                boxW = boxH = Math.Min(areaW, areaH) * 0.85;
            }
            else
            {
                double pct = RolePercent(c, tier) / 100.0;
                boxW = c.MaxGifWidth  * pct;
                boxH = c.MaxGifHeight * pct;
            }

            // Never bigger than the screen area (minus glow + label room).
            boxW = Math.Min(boxW, areaW - 2 * pad);
            boxH = Math.Min(boxH, areaH - 2 * pad - labelH);
            if (boxW < 1) boxW = 1;
            if (boxH < 1) boxH = 1;

            double k = Math.Min(boxW / natW, boxH / natH);
            if (!c.EnlargeSmallGifs && !full) k = Math.Min(k, 1.0);

            w = Math.Max(1, RoundHalfUp(natW * k));
            h = Math.Max(1, RoundHalfUp(natH * k));
        }

        /// <summary>
        /// Step 2 — where does the picture's top-left corner go?
        /// rnd(lo, hi) must return a whole number from lo to hi inclusive;
        /// the overlay passes Math.random, the tests pass fixed values.
        /// </summary>
        internal static void Place(GifBattlerConfig c, string tier, int w, int h,
                                   int areaW, int areaH, Func<int, int, int> rnd,
                                   out int x, out int y)
        {
            int pad    = GlowPad(c, tier);
            int labelH = c.ShowUsername ? LabelHeight : 0;
            int totalH = h + labelH;

            // The legal range for the picture's top-left corner.
            int minX = pad, maxX = areaW - w - pad;
            int minY = pad, maxY = areaH - totalH - pad;

            string mode = c.DisplayMode;

            if (mode == "random")
            {
                // Keep EdgePadding from every edge when there is room for it;
                // if the screen is too small for that, use the glow-safe range.
                int loX = Math.Max(minX, c.EdgePadding), hiX = Math.Min(maxX, areaW - w - c.EdgePadding);
                int loY = Math.Max(minY, c.EdgePadding), hiY = Math.Min(maxY, areaH - totalH - c.EdgePadding);
                if (hiX < loX) { loX = minX; hiX = maxX; }
                if (hiY < loY) { loY = minY; hiY = maxY; }
                x = hiX < loX ? RoundHalfUp((areaW - w) / 2.0) : rnd(loX, hiX);
                y = hiY < loY ? RoundHalfUp((areaH - totalH) / 2.0) : rnd(loY, hiY);
                return;
            }

            if (mode == "random-corner")
            {
                int corner = rnd(0, 3);                    // 0 TL, 1 TR, 2 BL, 3 BR
                int e = c.EdgePadding;
                x = (corner % 2 == 0) ? e : areaW - w - e;
                y = (corner < 2)      ? e : areaH - totalH - e;
                x = Clamp(x, minX, maxX, areaW, w);
                y = Clamp(y, minY, maxY, areaH, totalH);
                return;
            }

            if (mode == "fullscreen")
            {
                x = Clamp(RoundHalfUp((areaW - w) / 2.0), minX, maxX, areaW, w);
                y = Clamp(RoundHalfUp((areaH - totalH) / 2.0), minY, maxY, areaH, totalH);
                return;
            }

            // Fixed spot: "<row>-<column>", e.g. "top-left", "middle-center".
            string[] parts = mode.Split('-');
            string row = parts.Length > 0 ? parts[0] : "middle";
            string col = parts.Length > 1 ? parts[1] : "center";

            // On an EDGE axis the offset is the distance in from that edge.
            // On a CENTRED axis it is a nudge (positive = right / down).
            if      (col == "left")  x = c.OffsetX;
            else if (col == "right") x = areaW - w - c.OffsetX;
            else                     x = RoundHalfUp((areaW - w) / 2.0) + c.OffsetX;

            if      (row == "top")    y = c.OffsetY;
            else if (row == "bottom") y = areaH - totalH - c.OffsetY;
            else                      y = RoundHalfUp((areaH - totalH) / 2.0) + c.OffsetY;

            // Whatever the offsets say, never leave the screen or clip the glow.
            x = Clamp(x, minX, maxX, areaW, w);
            y = Clamp(y, minY, maxY, areaH, totalH);
        }

        // ── Plain-English summary (shown in the Settings window) ─────────

        /// <summary>
        /// "Settings like 40 px offsets are abstract until you read what they
        /// DO." Builds the amber sentence block at the bottom of the Settings
        /// window. Lives here (not in the form) so the tests can check it
        /// without needing a WinForms window.
        /// </summary>
        internal static string Describe(GifBattlerConfig c)
        {
            var sb = new System.Text.StringBuilder();

            // 1) the screen area
            if (c.UseCustomStageSize)
                sb.Append("Screen area: " + c.StageWidth + " x " + c.StageHeight
                          + ", scaled to fit your OBS browser source.");
            else
                sb.Append("Screen area: your OBS browser source, whatever size it is.");

            // 2) size
            if (c.DisplayMode == "fullscreen")
            {
                sb.Append("\nSize: large and centred, about 85% of the screen's short side.");
            }
            else
            {
                if (c.SizeByRole)
                    sb.Append("\nA GIF fits inside " + Box(c, c.ScaleModPercent) + " for mods, "
                              + Box(c, c.ScaleSubPercent) + " for subs and "
                              + Box(c, c.ScaleViewerPercent) + " for everyone else");
                else
                    sb.Append("\nA GIF fits inside " + Box(c, 100) + " for everyone");
                sb.Append(c.EnlargeSmallGifs
                    ? ", never stretched (small GIFs are enlarged to fill it)."
                    : ", never stretched (small GIFs stay their own size).");
            }

            // 3) placement
            sb.Append("\nAppears: " + DescribePlacement(c) + ".");

            // 4) time + look
            sb.Append("\n" + (c.StayUntilCleared
                ? "Stays until you clear it (the next GIF replaces it)."
                : "Stays " + c.DurationSeconds + " second" + (c.DurationSeconds == 1 ? "" : "s") + "."));
            sb.Append(" Glow: " + DescribeGlow(c) + ".");

            return sb.ToString();
        }

        private static string Box(GifBattlerConfig c, int percent)
        {
            int w = RoundHalfUp(c.MaxGifWidth * percent / 100.0);
            int h = RoundHalfUp(c.MaxGifHeight * percent / 100.0);
            return w + " x " + h;
        }

        private static string DescribePlacement(GifBattlerConfig c)
        {
            switch (c.DisplayMode)
            {
                case "random":
                    return "anywhere on screen, at least " + c.EdgePadding + " px from every edge";
                case "random-corner":
                    return "in a random corner, " + c.EdgePadding + " px in from the edges";
                case "fullscreen":
                    return "large and centred";
            }

            string[] p = c.DisplayMode.Split('-');
            string row = p.Length > 0 ? p[0] : "middle";
            string col = p.Length > 1 ? p[1] : "center";
            string where = row == "middle" && col == "center" ? "the centre"
                         : row == "middle" ? "the " + col + " side, halfway down"
                         : col == "center" ? "the " + row + ", centred left to right"
                         : "the " + row + "-" + col + " corner";

            var bits = new System.Collections.Generic.List<string>();
            if (col != "center") bits.Add(c.OffsetX + " px in from the " + col);
            else if (c.OffsetX != 0) bits.Add(Math.Abs(c.OffsetX) + " px " + (c.OffsetX > 0 ? "right" : "left") + " of centre");
            if (row != "middle") bits.Add(c.OffsetY + " px in from the " + row);
            else if (c.OffsetY != 0) bits.Add(Math.Abs(c.OffsetY) + " px " + (c.OffsetY > 0 ? "below" : "above") + " centre");

            return "always at " + where + (bits.Count > 0 ? " (" + string.Join(", ", bits.ToArray()) + ")" : "");
        }

        private static string DescribeGlow(GifBattlerConfig c)
        {
            switch (c.GlowMode)
            {
                case "off":    return "off";
                case "color":  return c.GlowColor + " on every GIF";
                case "random": return "a random colour for each GIF";
                default:       return "gold for mods, purple for subs, none for everyone else";
            }
        }

        // v clamped into [lo, hi]; if the range is empty, centre it instead.
        private static int Clamp(int v, int lo, int hi, int area, int size)
        {
            if (hi < lo) return RoundHalfUp((area - size) / 2.0);
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
