// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/UpdateRequiredForm.cs                DLL +
//
//  A dark-themed WinForms dialog that pops in front of the user
//  when an OSW tool requires a newer OSWTools.dll than the one
//  currently installed.
//
//  WHO CALLS THIS:
//    Versioning.cs → OSWLib.Register(...) — when the compatibility
//    check fails AND the dialog hasn't already been shown for this
//    tool in the current SB session.
//
//  WHY A DIALOG INSTEAD OF A TOAST:
//    Toasts (ShowToastNotification) appear in a tray corner and are
//    very easy to miss — especially mid-stream. An "update required"
//    notification deserves a modal-style in-your-face presentation
//    that the user explicitly acknowledges with an OK click.
//
//  THREADING NOTE — IMPORTANT:
//    This form is shown on a dedicated STA thread via Versioning.cs's
//    ShowIncompatibilityDialogAsync() helper. The STA thread is
//    NOT joined — i.e. the caller (Register()) returns immediately
//    after kicking off the dialog. This is intentional so the tool's
//    Execute() doesn't freeze waiting for the user to click OK.
//
//  INTERIM-VS-FUTURE NOTE:
//    This dialog is the INTERIM per-tool warning system. The future
//    plan is a single master-sheet sweep at SB startup that surfaces
//    ALL out-of-date OSW products in one consolidated dialog. When
//    that lands, set OSWLib._masterUpdateCheckActive = true and the
//    per-tool dialog will silently stand down. No edits to this file
//    needed at that point.
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OSWTools.Theme;

namespace OSWTools.Versioning
{
    /// <summary>
    /// Small modal dialog telling the user that one of their installed
    /// OSW tools needs a newer OSWTools.dll than the one currently loaded.
    /// </summary>
    internal class UpdateRequiredForm : Form
    {
        // ── Inputs (captured in constructor, used during BuildUI) ─────────
        private readonly string _toolName;
        private readonly string _installedVersion;
        private readonly string _requiredVersion;
        private readonly bool   _isBreakingChange;
        private readonly string _releasesUrl;

        // ── Constructor ───────────────────────────────────────────────────
        //  The four pieces of context come from CompatibilityResult.
        //  releasesUrl is constructed by the caller from OSWVersion.GitHubOwner
        //  + OSWVersion.GitHubRepo so it auto-corrects when the repo name
        //  typo gets fixed.
        public UpdateRequiredForm(
            string toolName,
            string installedVersion,
            string requiredVersion,
            bool   isBreakingChange,
            string releasesUrl)
        {
            _toolName         = toolName         ?? "An OSW tool";
            _installedVersion = installedVersion ?? "?";
            _requiredVersion  = requiredVersion  ?? "?";
            _isBreakingChange = isBreakingChange;
            _releasesUrl      = releasesUrl      ?? "";
            BuildUI();
        }

        // ── UI Construction ───────────────────────────────────────────────
        //  Layout mirrors UpdatePromptForm.cs but compressed — this is a
        //  one-shot acknowledge dialog, not a download flow. No progress
        //  bar, no release notes pane, just the essentials.
        private void BuildUI()
        {
            // ── Form properties ──────────────────────────────────────────
            Text            = "OSW Tools — Update Required";
            Width           = 460;
            Height          = _isBreakingChange ? 280 : 250;  // grow if we show the breaking banner
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            TopMost         = true;                            // keep it visible over SB / OBS
            ShowInTaskbar   = true;                            // BUT taskbar visible so user can reach it
            BackColor       = OSWTheme.CBg;
            ForeColor       = OSWTheme.CTxt;
            Font            = OSWTheme.Fn;

            // ── Gradient background (matches UpdatePromptForm aesthetic) ─
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
            var picLogo = new PictureBox
            {
                ImageLocation = OSWTheme.LogoUrl,
                Location      = new Point(20, 18),
                Size          = new Size(44, 44),
                SizeMode      = PictureBoxSizeMode.StretchImage,
                BackColor     = Color.Transparent
            };
            Controls.Add(picLogo);

            // ── Title ────────────────────────────────────────────────────
            //  We name the tool explicitly so the user knows which tool is
            //  complaining. Different tools may complain at different times
            //  (the GIF Display today, some other tool tomorrow) and the
            //  user shouldn't have to guess.
            var lblTitle = new Label
            {
                Text      = "Update Required",
                Font      = OSWTheme.FnT,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(74, 22)
            };
            Controls.Add(lblTitle);

            var lblSubtitle = new Label
            {
                Text      = _toolName + " needs a newer OSWTools.dll",
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(74, 50)
            };
            Controls.Add(lblSubtitle);

            // ── Divider under the header ─────────────────────────────────
            var divider = new Label
            {
                Height    = 1,
                BackColor = OSWTheme.CDiv,
                AutoSize  = false,
                Location  = new Point(20, 80),
                Width     = ClientSize.Width - 40
            };
            Controls.Add(divider);

            // ── Version comparison line ──────────────────────────────────
            //  Yellow CWarning for "needs newer", red CError if the gap is
            //  a MAJOR version bump (breaking change). The user's eye lands
            //  on the colour first; the numbers are the supporting detail.
            var lblVersionInfo = new Label
            {
                Text      = "Installed:  v" + _installedVersion
                          + "        Required:  v" + _requiredVersion + " or newer",
                Font      = OSWTheme.FnB,
                ForeColor = _isBreakingChange ? OSWTheme.CError : OSWTheme.CWarning,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(22, 96)
            };
            Controls.Add(lblVersionInfo);

            // ── Optional breaking-change banner ──────────────────────────
            //  Only shows when CompatibilityResult.IsBreakingChange is true
            //  (i.e. installed MAJOR < required MAJOR). Tells the user that
            //  this isn't an "extra features" update but a hard requirement.
            int yAfterBanner = 124;
            if (_isBreakingChange)
            {
                var lblBreaking = new Label
                {
                    Text      = "⚠  This is a MAJOR update — the tool will not work correctly until you update.",
                    Font      = OSWTheme.FnSm,
                    ForeColor = OSWTheme.CError,
                    BackColor = Color.Transparent,
                    AutoSize  = false,
                    Location  = new Point(22, 124),
                    Size      = new Size(ClientSize.Width - 44, 20)
                };
                Controls.Add(lblBreaking);
                yAfterBanner = 152;
            }

            // ── Friendly explainer ───────────────────────────────────────
            var lblExplain = new Label
            {
                Text      = "Open the GitHub releases page to grab the latest OSWTools.dll, "
                          + "then drop it into your Streamer.bot folder and restart SB.",
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(22, yAfterBanner),
                Size      = new Size(ClientSize.Width - 44, 40)
            };
            Controls.Add(lblExplain);

            // ── Clickable link to GitHub releases ────────────────────────
            //  LinkLabel is the right control here — it's keyboard-accessible,
            //  it renders with a clear hyperlink visual, and it gives us a
            //  LinkClicked event hook for opening the URL via the default
            //  browser. We use Process.Start with UseShellExecute=true so
            //  Windows hands the URL off to the user's default browser.
            var linkUpdate = new LinkLabel
            {
                Text         = "→  Open GitHub releases",
                Font         = OSWTheme.FnB,
                LinkColor    = OSWTheme.CLnk,
                ActiveLinkColor = OSWTheme.CAccHov,
                VisitedLinkColor = OSWTheme.CLnk,
                BackColor    = Color.Transparent,
                AutoSize     = true,
                Location     = new Point(22, yAfterBanner + 50)
            };
            linkUpdate.LinkClicked += (s, e) =>
            {
                try
                {
                    // UseShellExecute = true tells Windows "open this with
                    // whatever app is registered to handle http URLs",
                    // which is the user's default browser.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = _releasesUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // If the browser launch fails for any reason (no default
                    // browser, sandboxed env, weird policy), at least change
                    // the link text so the user knows something went wrong
                    // and can copy-paste the URL manually.
                    linkUpdate.Text      = "(Could not open browser — copy URL: " + _releasesUrl + ")";
                    linkUpdate.LinkColor = OSWTheme.CError;
                }
            };
            Controls.Add(linkUpdate);

            // ── OK button (bottom-right) ─────────────────────────────────
            //  We deliberately offer only ONE button — "OK". This is an
            //  acknowledgement, not a decision. Skip/Cancel would imply
            //  the user can dismiss the underlying problem, which they
            //  can't — the tool will keep failing until they update.
            var btnOk = new Button
            {
                Text      = "OK",
                Font      = OSWTheme.FnB,
                ForeColor = OSWTheme.CTxt,
                BackColor = OSWTheme.CAcc,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(100, 32),
                Location  = new Point(ClientSize.Width - 120, ClientSize.Height - 50)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            // Pressing Enter or Esc dismisses the dialog the same way
            // OK does. Esc → CancelButton, Enter → AcceptButton.
            AcceptButton = btnOk;
            CancelButton = btnOk;
        }
    }
}
