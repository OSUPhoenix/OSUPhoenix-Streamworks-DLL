// ═══════════════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/MasterUpdateForm.cs                       DLL +
//
//  Consolidated "Updates Available" dialog.
//
//  WHO CALLS THIS:
//    Versioning.cs → OSWLib.RunMasterUpdateCheck() — when the master sheet
//    check finds one or more installed products are out of date.
//
//  WHY ONE DIALOG INSTEAD OF MANY:
//    The per-tool UpdateRequiredForm pops once per tool, which is fine for
//    incompatibility (rare, blocking) but obnoxious for "there are 3 updates
//    available" (common, informational). This dialog shows everything in a
//    single list and the user reads/clicks at their own pace.
//
//  THREADING NOTE — IMPORTANT:
//    This form is shown on a dedicated STA thread via Versioning.cs's
//    ShowMasterUpdateDialogAsync() helper. The STA thread is NOT joined —
//    i.e. the caller returns immediately after kicking off the dialog. This
//    is intentional so the startup action's Execute() doesn't freeze
//    waiting for the user to click OK during the SB launch sequence.
//
//  SILENT-WHEN-NOTHING-TO-DO:
//    The caller (RunMasterUpdateCheck) only shows this form when the
//    outdated list is non-empty. There is NO "Everything is up to date"
//    state in this dialog — by design, per the spec. Silence IS the success
//    signal.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OSWTools.Theme;

namespace OSWTools.Versioning
{
    /// <summary>
    /// Modal dialog listing every installed OSW product that is out of date
    /// according to the master product registry sheet. Each row shows the
    /// product name, version delta, and a clickable link to the product's
    /// download page.
    /// </summary>
    internal class MasterUpdateForm : Form
    {
        // ── Inputs ────────────────────────────────────────────────────────
        private readonly List<OSWLib.OutdatedProduct> _outdated;

        // Layout constants — kept here so the form's height calculation can
        // see them too. Tweak in one place if you need to change row spacing.
        //
        // SPACING REVISION (May 2026): bumped logo to 2x size and added
        // breathing room across the board after the first user test showed
        // the OK button clipping at the bottom and overall cramped feel.
        private const int LogoSize             = 88;   // 2x previous 44 — request from spacing pass
        private const int RowHeight            = 80;   // up from 64 — more vertical gap within each row
        private const int HeaderHeight         = 150;  // up from 100 — accommodates the 88px logo
        private const int FooterHeight         = 80;   // up from 60 — fixes OK button clipping
        private const int MaxVisibleRows       = 6;    // unchanged
        private const int FormWidth            = 540;  // up from 520 — small bump for the larger header

        public MasterUpdateForm(List<OSWLib.OutdatedProduct> outdated)
        {
            // Defensive null-safety — caller shouldn't ever pass null but if
            // they do, render an empty list (which the caller-side guard
            // should have prevented anyway).
            _outdated = outdated ?? new List<OSWLib.OutdatedProduct>();
            BuildUI();
        }

        // ── UI Construction ───────────────────────────────────────────────
        private void BuildUI()
        {
            // ── Sizing ───────────────────────────────────────────────────
            // Form height = header + visible rows + footer. If the list has
            // more than MaxVisibleRows, we cap the list area height and let
            // a scroll panel handle the overflow.
            int visibleRows = Math.Min(_outdated.Count, MaxVisibleRows);
            int listHeight  = visibleRows * RowHeight;
            int totalHeight = HeaderHeight + listHeight + FooterHeight + 10; // +10 padding

            // ── Form properties ──────────────────────────────────────────
            Text            = "OSW Tools — Updates Available";
            Width           = FormWidth;
            Height          = totalHeight;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            TopMost         = true;                            // sits over SB and OBS
            ShowInTaskbar   = true;
            BackColor       = OSWTheme.CBg;
            ForeColor       = OSWTheme.CTxt;
            Font            = OSWTheme.Fn;

            // ── Gradient background ──────────────────────────────────────
            Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(
                    ClientRectangle,
                    OSWTheme.CBg,
                    OSWTheme.CGradBottom,
                    LinearGradientMode.Vertical))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }
            };

            // ── Logo (top-left) ──────────────────────────────────────────
            // 2x size (88×88) — anchors the header visually. Top padding of
            // 24px gives the logo space above; the title/subtitle to the
            // right are centered vertically against it.
            var picLogo = new PictureBox
            {
                ImageLocation = OSWTheme.LogoUrl,
                Location      = new Point(28, 24),
                Size          = new Size(LogoSize, LogoSize),
                SizeMode      = PictureBoxSizeMode.StretchImage,
                BackColor     = Color.Transparent
            };
            Controls.Add(picLogo);

            // ── Title ────────────────────────────────────────────────────
            // Positioned to sit roughly centered against the taller logo,
            // with the subtitle stacked beneath it. Left offset = logo
            // left (28) + logo width (88) + gap (16) = 132.
            var lblTitle = new Label
            {
                Text      = "Updates Available",
                Font      = OSWTheme.FnT,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(132, 40)
            };
            Controls.Add(lblTitle);

            // ── Subtitle — count of products needing updates ────────────
            // Pluralisation: "1 OSW product has..." vs "N OSW products have..."
            string pluralProducts = _outdated.Count == 1 ? "product has" : "products have";
            var lblSubtitle = new Label
            {
                Text      = _outdated.Count + " OSW " + pluralProducts + " updates available.",
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(132, 78)
            };
            Controls.Add(lblSubtitle);

            // ── Divider under header ─────────────────────────────────────
            // Sits 16px above the bottom of the header block so the list
            // below has comfortable separation.
            var divider = new Label
            {
                Height    = 1,
                BackColor = OSWTheme.CDiv,
                AutoSize  = false,
                Location  = new Point(28, HeaderHeight - 16),
                Width     = ClientSize.Width - 56
            };
            Controls.Add(divider);

            // ── Scrollable list panel ────────────────────────────────────
            // Panel hosts the rows and scrolls if the list overflows. We
            // hard-cap visible height to MaxVisibleRows × RowHeight; the
            // user scrolls inside the panel for the rest.
            var listPanel = new Panel
            {
                Location    = new Point(0, HeaderHeight),
                Size        = new Size(ClientSize.Width, listHeight),
                BackColor   = Color.Transparent,
                AutoScroll  = true
            };
            Controls.Add(listPanel);

            // ── Render one row per outdated product ──────────────────────
            for (int i = 0; i < _outdated.Count; i++)
                listPanel.Controls.Add(BuildRow(_outdated[i], i));

            // ── OK button (bottom-right) ─────────────────────────────────
            // Single button — this dialog is informational/acknowledgement.
            // The "actions" (downloads) happen via the per-row link clicks;
            // dismissing the dialog doesn't make the problem go away, but
            // the user doesn't need a separate Skip/Cancel/Dismiss option.
            //
            // Position math: 28px from right edge (matches header margin),
            // 28px from bottom edge (gives the button visible breathing room
            // — the previous 50px offset was clipping behind the bottom
            // edge on tested DPI scales).
            var btnOk = new Button
            {
                Text      = "OK",
                Font      = OSWTheme.FnB,
                ForeColor = OSWTheme.CTxt,
                BackColor = OSWTheme.CAcc,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(110, 36),
                Location  = new Point(ClientSize.Width - 138, ClientSize.Height - 60)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            AcceptButton = btnOk;
            CancelButton = btnOk;
        }

        /// <summary>
        /// Builds one row panel showing a single product's update info.
        /// Layout (left to right):
        ///   [Display name + reason tag]   [installed → latest]   [link]
        /// Each row sits at index * RowHeight relative to the parent panel.
        /// </summary>
        private Panel BuildRow(OSWLib.OutdatedProduct p, int index)
        {
            var row = new Panel
            {
                Location  = new Point(0, index * RowHeight),
                Size      = new Size(ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4, RowHeight),
                BackColor = Color.Transparent
            };

            // ── Product name (left, top) ────────────────────────────────
            // Bold, primary text — this is the row's anchor identifier.
            // Top padding bumped from 8 → 14 for the taller row.
            var lblName = new Label
            {
                Text      = p.DisplayName ?? p.Code ?? "(unknown)",
                Font      = OSWTheme.FnCrd,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(28, 14)
            };
            row.Controls.Add(lblName);

            // ── Version line ────────────────────────────────────────────
            // The text changes shape based on Reason:
            //   widget       — "v1.0.0 → v1.1.0"
            //   dll          — "needs OSWTools v1.0.5 (you have v1.0.1)"
            //   widget+dll   — "v1.0.0 → v1.1.0  ·  also needs OSWTools v1.0.5"
            string versionText;
            Color  versionColor;
            switch (p.Reason)
            {
                case "widget":
                    versionText  = "v" + (p.InstalledVersion ?? "?") + "  →  v" + (p.LatestVersion ?? "?");
                    versionColor = OSWTheme.CWarning;
                    break;
                case "dll":
                    versionText  = "Needs OSWTools v" + (p.RequiredDllVersion ?? "?")
                                 + " (you have v" + (p.CurrentDllVersion ?? "?") + ")";
                    versionColor = OSWTheme.CError;  // DLL gaps are more serious — widget can't run
                    break;
                case "widget+dll":
                    versionText  = "v" + (p.InstalledVersion ?? "?") + "  →  v" + (p.LatestVersion ?? "?")
                                 + "    also needs OSWTools v" + (p.RequiredDllVersion ?? "?");
                    versionColor = OSWTheme.CError;
                    break;
                default:
                    versionText  = "(update available)";
                    versionColor = OSWTheme.CWarning;
                    break;
            }
            var lblVer = new Label
            {
                Text      = versionText,
                Font      = OSWTheme.FnSm,
                ForeColor = versionColor,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(28, 42)
            };
            row.Controls.Add(lblVer);

            // ── Webpage link (right side) ───────────────────────────────
            // Per spec: opens the user's default browser via Process.Start.
            // If no DownloadUrl is set in the sheet, show "(no link)" greyed
            // out — better than a clickable link that goes nowhere.
            bool hasLink = !string.IsNullOrWhiteSpace(p.DownloadUrl);
            if (hasLink)
            {
                var link = new LinkLabel
                {
                    Text             = "→ Open page",
                    Font             = OSWTheme.FnB,
                    LinkColor        = OSWTheme.CLnk,
                    ActiveLinkColor  = OSWTheme.CAccHov,
                    VisitedLinkColor = OSWTheme.CLnk,
                    BackColor        = Color.Transparent,
                    AutoSize         = true,
                    Location         = new Point(row.Width - 120, 30)
                };
                link.LinkClicked += (s, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = p.DownloadUrl,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // Browser launch failed — change link text so the user
                        // knows what to copy manually. Same fallback as
                        // UpdateRequiredForm.
                        link.Text      = "(can't open — see log for URL)";
                        link.LinkColor = OSWTheme.CError;
                    }
                };
                row.Controls.Add(link);
            }
            else
            {
                var lblNoLink = new Label
                {
                    Text      = "(no link)",
                    Font      = OSWTheme.FnSm,
                    ForeColor = OSWTheme.CDim,
                    BackColor = Color.Transparent,
                    AutoSize  = true,
                    Location  = new Point(row.Width - 90, 34)
                };
                row.Controls.Add(lblNoLink);
            }

            // ── Bottom row divider — visually separates rows ────────────
            // Skip on the last row so we don't draw a line at the bottom edge.
            if (index < _outdated.Count - 1)
            {
                var rowDiv = new Label
                {
                    Height    = 1,
                    BackColor = OSWTheme.CDiv,
                    AutoSize  = false,
                    Location  = new Point(28, RowHeight - 1),
                    Width     = row.Width - 56
                };
                row.Controls.Add(rowDiv);
            }

            return row;
        }
    }
}
