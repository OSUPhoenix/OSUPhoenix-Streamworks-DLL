// =============================================================================
// OSWTools — Theme/OSWTheme.Watermark.cs
//
// Adds embedded-resource logo loading + a reusable watermark paint helper to
// the existing OSWTheme static class. Partial class pattern so the base
// OSWTheme.cs file stays untouched.
//
// SETUP (one-time, Visual Studio):
//   1. Add "StreamWorks 500 px.png" to the OSWTools project (any folder;
//      Theme/Resources/ recommended for organization).
//   2. Right-click the file → Properties → Build Action = "Embedded Resource".
//   3. Rebuild. The image is now compiled into OSWTools.dll.
//
// USAGE from any tool that references OSWTools.dll:
//
//   // Paint handler — wire it like any other OSWTheme paint helper:
//   myPanel.Paint += OSWTheme.PaintWatermark;
//
//   // Or grab the image directly if you need custom rendering:
//   Image logo = OSWTheme.GetEmbeddedLogo();
//   if (logo != null) { /* do custom drawing */ }
//
// IMAGE RESOLUTION:
//   The loader searches embedded resources for any file ending in one of
//   the known logo filenames (case-insensitive). This tolerates renames
//   of the source file without forcing a matching code change.
// =============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace OSWTools.Theme
{
    public static partial class OSWTheme
    {
        // ── Embedded logo cache ──────────────────────────────────────────────
        //
        // Image objects are expensive to construct and GDI resources are
        // limited, so we load the PNG from the embedded resource exactly once
        // per process and reuse the same Image for every paint call.
        //
        // _logoLoadAttempted ensures we don't retry failed loads on every
        // paint (which would spam log entries and slow down the UI).
        // ─────────────────────────────────────────────────────────────────────
        private static Image _cachedLogo;
        private static bool  _logoLoadAttempted;
        private static readonly object _logoLock = new object();

        // Color matrix for 15% opacity. The [3,3] element of a 5x5 color
        // matrix controls the alpha channel — 1.0 = fully opaque,
        // 0.0 = transparent. Built once and reused.
        private static readonly ImageAttributes _watermarkAttrs = BuildWatermarkAttrs();

        // Filename candidates the loader will match (case-insensitive suffix
        // match). Add variants here if you rename the source file.
        private static readonly string[] _logoResourceCandidates = new[]
        {
            "StreamWorks 500 px.png",
            "StreamWorks_500_px.png",   // .NET converts spaces to underscores
                                        // in embedded resource names
            "StreamWorks-500-px.png",
            "streamworks.png"
        };

        /// <summary>
        /// Returns the embedded StreamWorks logo as an Image, or null if the
        /// resource isn't present (e.g. PNG wasn't marked "Embedded Resource"
        /// in the project). Safe to call repeatedly — the image is cached.
        /// </summary>
        public static Image GetEmbeddedLogo()
        {
            // Fast path — already attempted, return whatever we got (could be null)
            if (_logoLoadAttempted) return _cachedLogo;

            lock (_logoLock)
            {
                if (_logoLoadAttempted) return _cachedLogo;
                _logoLoadAttempted = true;

                try
                {
                    Assembly asm = typeof(OSWTheme).Assembly;
                    string[] resourceNames = asm.GetManifestResourceNames();

                    // Find the first resource whose name ends with one of our
                    // known logo filenames. Case-insensitive because resource
                    // naming conventions are surprisingly unreliable.
                    string match = resourceNames.FirstOrDefault(rn =>
                        _logoResourceCandidates.Any(candidate =>
                            rn.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)));

                    if (match == null) return null;

                    using (Stream stream = asm.GetManifestResourceStream(match))
                    {
                        if (stream == null) return null;

                        // Important: Image.FromStream keeps the stream locked
                        // for the lifetime of the Image. We copy the bytes to a
                        // MemoryStream we control so we can dispose the source
                        // stream immediately but still use the Image long-term.
                        var mem = new MemoryStream();
                        stream.CopyTo(mem);
                        mem.Position = 0;
                        _cachedLogo = Image.FromStream(mem);
                    }
                }
                catch
                {
                    // Silent fail — watermark is cosmetic, never crash the UI
                    _cachedLogo = null;
                }

                return _cachedLogo;
            }
        }

        /// <summary>
        /// Panel Paint handler that renders the StreamWorks logo as a centered,
        /// responsively-scaled, 15%-opacity watermark in the sender panel.
        ///
        /// Wire up with:
        ///   myPanel.Paint += OSWTheme.PaintWatermark;
        ///
        /// Behavior:
        ///   - Centers the logo in the panel's client area
        ///   - Scales to fit with a 40px margin, preserving aspect ratio
        ///   - Never upscales beyond the image's native size (would look bad)
        ///   - Silently does nothing if the image isn't embedded
        ///   - Does not erase the panel background — call your BackColor-fill
        ///     handler first if you need a solid base (most panels already
        ///     paint their BackColor via the default Paint pipeline)
        /// </summary>
        public static void PaintWatermark(object sender, PaintEventArgs e)
        {
            Image logo = GetEmbeddedLogo();
            if (logo == null) return;

            Control ctl = sender as Control;
            if (ctl == null) return;

            Rectangle client = ctl.ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) return;

            // Compute the draw rectangle: centered, scaled to fit with margin,
            // but capped at the image's native size so we never upscale.
            const int margin = 40;
            int maxW = Math.Max(1, client.Width  - margin * 2);
            int maxH = Math.Max(1, client.Height - margin * 2);

            // Scale factor that fits the image in the available space while
            // preserving aspect ratio. Math.Min of the two axis scales gives
            // us "fit" (contain), not "fill" (cover).
            double sx = (double)maxW / logo.Width;
            double sy = (double)maxH / logo.Height;
            double scale = Math.Min(sx, sy);

            // Cap at 1.0 — don't upscale past native size
            if (scale > 1.0) scale = 1.0;

            int drawW = (int)(logo.Width  * scale);
            int drawH = (int)(logo.Height * scale);
            int drawX = client.X + (client.Width  - drawW) / 2;
            int drawY = client.Y + (client.Height - drawH) / 2;
            Rectangle destRect = new Rectangle(drawX, drawY, drawW, drawH);

            // High-quality scaling since this runs once per paint, not in a
            // tight loop. The perf difference is imperceptible for a single
            // 500x500 image but the visual quality improvement is obvious.
            InterpolationMode prevMode = e.Graphics.InterpolationMode;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            try
            {
                e.Graphics.DrawImage(
                    logo,
                    destRect,
                    0, 0, logo.Width, logo.Height,
                    GraphicsUnit.Pixel,
                    _watermarkAttrs);
            }
            finally
            {
                e.Graphics.InterpolationMode = prevMode;
            }
        }

        // Builds the cached ImageAttributes used for opacity. A color matrix
        // applies a linear transform to every pixel's RGBA channels before
        // drawing. The layout is (R, G, B, A, 1) — the 4th row/column is the
        // alpha axis, so scaling [3,3] scales the alpha channel.
        //
        // 0.15f = 15% opacity per the spec.
        private static ImageAttributes BuildWatermarkAttrs()
        {
            var matrix = new ColorMatrix(new float[][]
            {
                new float[] { 1f, 0f, 0f, 0f,   0f },  // R unchanged
                new float[] { 0f, 1f, 0f, 0f,   0f },  // G unchanged
                new float[] { 0f, 0f, 1f, 0f,   0f },  // B unchanged
                new float[] { 0f, 0f, 0f, 0.15f, 0f }, // A × 0.15
                new float[] { 0f, 0f, 0f, 0f,   1f }   // translation row
            });
            var attrs = new ImageAttributes();
            attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            return attrs;
        }
    }
}
