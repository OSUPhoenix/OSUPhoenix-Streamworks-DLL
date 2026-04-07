using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OSWTools.Theme
{
    // =========================================================================
    // OSWTheme — WinForms color palette, fonts, and paint helpers
    //
    // All values are System.Drawing types — the same ones you already use
    // when writing BackColor, ForeColor, Font, etc. on any WinForms control.
    //
    // USAGE in any script that references OSWTools.dll:
    //   using OSWTools.Theme;
    //
    //   pnl.BackColor  = OSWTheme.CBg;
    //   btn.BackColor  = OSWTheme.CAcc;
    //   btn.Font       = OSWTheme.FnB;
    //   pnlFooter.Paint += OSWTheme.PaintFooterGradient;
    // =========================================================================
    public static class OSWTheme
    {
        // ── Color helper ─────────────────────────────────────────────────
        // Converts a hex string like "#1A1A1A" into a System.Drawing.Color.
        // This is the same FC() you already use inside your scripts.
        public static Color FC(string hex) => ColorTranslator.FromHtml(hex);

        // ── Backgrounds ──────────────────────────────────────────────────
        /// <summary>Main window / form background.</summary>
        public static readonly Color CBg        = FC("#121212");

        /// <summary>Left panel, header, footer surface.</summary>
        public static readonly Color CPnl       = FC("#1C1C1C");

        /// <summary>Input controls, inner panel backgrounds.</summary>
        public static readonly Color CIn        = FC("#1E1E1E");

        /// <summary>Bottom of the window gradient (dark red).</summary>
        public static readonly Color CGradBottom = FC("#230000");

        // ── Borders & Dividers ────────────────────────────────────────────
        /// <summary>Divider lines, button borders, section separators.</summary>
        public static readonly Color CDiv       = FC("#464646");

        /// <summary>Lighter border — used on small control outlines.</summary>
        public static readonly Color CDivLight  = FC("#787878");

        // ── Accent — OSW Red ──────────────────────────────────────────────
        /// <summary>Primary action color — Save button, selected tab.</summary>
        public static readonly Color CAcc       = FC("#DC2828");

        /// <summary>Hover state for accent controls.</summary>
        public static readonly Color CAccHov    = FC("#E83535");

        /// <summary>Pressed / active state.</summary>
        public static readonly Color CAccPrs    = FC("#B41E1E");

        // ── Text ──────────────────────────────────────────────────────────
        /// <summary>Primary text — labels, control text.</summary>
        public static readonly Color CTxt       = Color.White;

        /// <summary>Secondary / muted text — hints, help labels, subtitles.</summary>
        public static readonly Color CDim       = FC("#888888");

        /// <summary>Link label color.</summary>
        public static readonly Color CLnk       = FC("#64C8FF");

        /// <summary>Plain-English preview text (amber).</summary>
        public static readonly Color CPrev      = FC("#FFC864");

        // ── State Colors ──────────────────────────────────────────────────
        public static readonly Color CSuccess   = FC("#4CAF50");
        public static readonly Color CWarning   = FC("#FFC107");
        public static readonly Color CError     = FC("#F44336");
        public static readonly Color CInfo      = FC("#2196F3");

        // ── Fonts ─────────────────────────────────────────────────────────
        // Defined once here instead of repeated in every script.

        /// <summary>Standard body font — Segoe UI 9pt.</summary>
        public static readonly Font Fn    = new Font("Segoe UI", 9f);

        /// <summary>Bold body font — Segoe UI 9pt Bold.</summary>
        public static readonly Font FnB   = new Font("Segoe UI", 9f, FontStyle.Bold);

        /// <summary>Small font — Segoe UI 8pt.</summary>
        public static readonly Font FnSm  = new Font("Segoe UI", 8f);

        /// <summary>Small italic — used for help text, hints.</summary>
        public static readonly Font FnH   = new Font("Segoe UI", 8f, FontStyle.Italic);

        /// <summary>Section header font — Segoe UI 9pt Bold.</summary>
        public static readonly Font FnSec = new Font("Segoe UI", 9f, FontStyle.Bold);

        /// <summary>Card title font — Segoe UI 9.5pt Bold.</summary>
        public static readonly Font FnCrd = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        /// <summary>Small tag/pill font — Segoe UI 7pt Bold.</summary>
        public static readonly Font FnTag = new Font("Segoe UI", 7f, FontStyle.Bold);

        /// <summary>Large title font — Segoe UI 15pt Bold.</summary>
        public static readonly Font FnT   = new Font("Segoe UI", 15f, FontStyle.Bold);

        // ── Paint Helpers ─────────────────────────────────────────────────
        // These are Paint event handlers you wire up with +=
        // The DLL handles all the brush creation and cleanup internally.

        /// <summary>
        /// Paints the standard OSW footer gradient (dark -> dark red, top to bottom).
        /// Wire up with:  pnlFooter.Paint += OSWTheme.PaintFooterGradient;
        /// </summary>
        public static void PaintFooterGradient(object sender, PaintEventArgs e)
        {
            var pnl = (Panel)sender;
            using (var brush = new LinearGradientBrush(
                pnl.ClientRectangle,
                CPnl,           // top — blends with the panel above
                CGradBottom,    // bottom — dark red
                LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(brush, pnl.ClientRectangle);
            }
            // FIX: Bug #3 — Wrapped Pen in using block.
            //      Previously: e.Graphics.DrawLine(new Pen(CDiv, 1), ...);
            //      The Pen was allocated but never disposed, leaking a GDI handle
            //      on every repaint. Paint events fire frequently (resize, focus,
            //      overlapping windows), so this would eventually exhaust the
            //      ~10,000 GDI handle limit and crash the process.
            using (var pen = new Pen(CDiv, 1))
            {
                e.Graphics.DrawLine(pen, 0, 0, pnl.Width, 0);
            }
        }

        /// <summary>
        /// Paints a simple dark panel background with a bottom-edge divider.
        /// Useful for header panels.
        /// Wire up with:  pnlHeader.Paint += OSWTheme.PaintHeaderBg;
        /// </summary>
        public static void PaintHeaderBg(object sender, PaintEventArgs e)
        {
            var pnl = (Panel)sender;
            // FIX: Bug #4 — Wrapped SolidBrush in using block (was leaking GDI handle).
            using (var brush = new SolidBrush(CPnl))
            {
                e.Graphics.FillRectangle(brush, pnl.ClientRectangle);
            }
            // FIX: Bug #5 — Wrapped Pen in using block (was leaking GDI handle).
            using (var pen = new Pen(CDiv, 1))
            {
                e.Graphics.DrawLine(pen, 0, pnl.Height - 1, pnl.Width, pnl.Height - 1);
            }
        }

        /// <summary>
        /// Paints a right-edge border on a panel (used on left-side list panels).
        /// Wire up with:  pnlLeft.Paint += OSWTheme.PaintRightBorder;
        /// </summary>
        public static void PaintRightBorder(object sender, PaintEventArgs e)
        {
            var pnl = (Panel)sender;
            // FIX: Bug #6 — Wrapped Pen in using block (was leaking GDI handle).
            using (var pen = new Pen(CDiv, 1))
            {
                e.Graphics.DrawLine(pen, pnl.Width - 1, 0, pnl.Width - 1, pnl.Height);
            }
        }

        // ── Logo ──────────────────────────────────────────────────────────

        /// <summary>StreamWorks logo URL used in OSW tool brand headers.</summary>
        public const string LogoUrl =
            "https://i0.wp.com/osuphoenix.tv/wp-content/uploads/2025/11/StreamWorks-500-px.webp?resize=150%2C150&ssl=1";
    }
}
