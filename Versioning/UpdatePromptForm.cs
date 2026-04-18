// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/UpdatePromptForm.cs
//
//  A dark-themed WinForms dialog that prompts the user to update
//  OSWTools.dll when a new version is available on GitHub.
//
//  This form is NOT called directly — it's shown by the
//  CheckForUpdates() method on OSWLib (see UpdateManager.cs).
//
//  FLOW:
//    1. Form opens showing current vs latest version + release notes
//    2. User clicks "Download & Install" (or "Skip This Version")
//    3. Progress bar shows download progress
//    4. After download, a batch script is staged to swap the DLL
//    5. User is told to restart Streamer.bot to apply the update
//
//  WHY A BATCH SCRIPT?
//    Windows locks loaded DLLs. While Streamer.bot is running,
//    OSWTools.dll cannot be overwritten. The batch script waits for
//    SB to close, then swaps the file. This is handled by
//    UpdateChecker.ApplyUpdate() which already existed in the DLL.
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using OSWTools.Theme;

namespace OSWTools.Versioning
{
    internal class UpdatePromptForm : Form
    {
        // ── Controls ──────────────────────────────────────────────────────
        private Label       _lblTitle;
        private Label       _lblVersionInfo;
        private Label       _lblNotesHeader;
        private TextBox     _txtNotes;
        private ProgressBar _progressBar;
        private Label       _lblStatus;
        private Button      _btnUpdate;
        private Button      _btnSkip;
        private PictureBox  _picLogo;

        // ── State ─────────────────────────────────────────────────────────
        private readonly UpdateCheckResult _checkResult;
        private bool _downloading = false;

        /// <summary>
        /// After the form closes, this is true if the update was downloaded
        /// and staged. The caller can use this to log a message or show a toast.
        /// </summary>
        public bool UpdateStaged { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────

        public UpdatePromptForm(UpdateCheckResult checkResult)
        {
            _checkResult = checkResult ?? throw new ArgumentNullException("checkResult");
            BuildUI();
        }

        // ── UI Construction ───────────────────────────────────────────────

        private void BuildUI()
        {
            // ── Form properties ───────────────────────────────────────────
            Text            = "OSWTools Update Available";
            Width           = 520;
            Height          = 440;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = OSWTheme.CBg;
            ForeColor       = OSWTheme.CTxt;
            Font            = OSWTheme.Fn;

            // ── Gradient background ───────────────────────────────────────
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

            // ── Logo ──────────────────────────────────────────────────────
            _picLogo = new PictureBox
            {
                ImageLocation = OSWTheme.LogoUrl,
                Location      = new Point(20, 15),
                Size          = new Size(50, 50),
                SizeMode      = PictureBoxSizeMode.StretchImage,
                BackColor     = Color.Transparent
            };
            Controls.Add(_picLogo);

            // ── Title ─────────────────────────────────────────────────────
            _lblTitle = new Label
            {
                Text      = "OSWTools Update Available",
                Font      = OSWTheme.FnT,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(80, 22)
            };
            Controls.Add(_lblTitle);

            // ── Version info ──────────────────────────────────────────────
            _lblVersionInfo = new Label
            {
                Text      = "Installed:  v" + _checkResult.InstalledVersion
                          + "        Latest:  v" + _checkResult.LatestVersion,
                Font      = OSWTheme.FnB,
                ForeColor = OSWTheme.CWarning,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(22, 75)
            };
            Controls.Add(_lblVersionInfo);

            // ── Divider ───────────────────────────────────────────────────
            var divider = new Label
            {
                Height    = 1,
                BackColor = OSWTheme.CDiv,
                AutoSize  = false,
                Location  = new Point(20, 100),
                Width     = ClientSize.Width - 40
            };
            Controls.Add(divider);

            // ── Release notes header ──────────────────────────────────────
            _lblNotesHeader = new Label
            {
                Text      = "What's New:",
                Font      = OSWTheme.FnSec,
                ForeColor = OSWTheme.CLnk,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(22, 110)
            };
            Controls.Add(_lblNotesHeader);

            // ── Release notes body ────────────────────────────────────────
            _txtNotes = new TextBox
            {
                Multiline   = true,
                ReadOnly    = true,
                ScrollBars  = ScrollBars.Vertical,
                WordWrap    = true,
                BackColor   = OSWTheme.CIn,
                ForeColor   = OSWTheme.CTxt,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = OSWTheme.Fn,
                Location    = new Point(22, 135),
                Size        = new Size(ClientSize.Width - 44, 150),
                Text        = string.IsNullOrWhiteSpace(_checkResult.ReleaseNotes)
                                ? "(No release notes provided.)"
                                : _checkResult.ReleaseNotes
            };
            Controls.Add(_txtNotes);

            // ── Progress bar ──────────────────────────────────────────────
            _progressBar = new ProgressBar
            {
                Minimum  = 0,
                Maximum  = 100,
                Value    = 0,
                Location = new Point(22, 298),
                Size     = new Size(ClientSize.Width - 44, 20),
                Visible  = false
            };
            Controls.Add(_progressBar);

            // ── Status label ──────────────────────────────────────────────
            _lblStatus = new Label
            {
                Text      = "",
                Font      = OSWTheme.FnSm,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(22, 322),
                Visible   = false
            };
            Controls.Add(_lblStatus);

            // ── Buttons ───────────────────────────────────────────────────
            _btnUpdate = new Button
            {
                Text      = "Download && Install",
                Font      = OSWTheme.FnB,
                ForeColor = OSWTheme.CTxt,
                BackColor = OSWTheme.CAcc,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(180, 36),
                Location  = new Point(ClientSize.Width - 210, ClientSize.Height - 65)
            };
            _btnUpdate.FlatAppearance.BorderSize  = 0;
            _btnUpdate.Click += OnUpdateClick;
            Controls.Add(_btnUpdate);

            _btnSkip = new Button
            {
                Text      = "Skip",
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CDim,
                BackColor = OSWTheme.CPnl,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(80, 36),
                Location  = new Point(22, ClientSize.Height - 65)
            };
            _btnSkip.FlatAppearance.BorderSize  = 1;
            _btnSkip.FlatAppearance.BorderColor = OSWTheme.CDiv;
            _btnSkip.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(_btnSkip);

            CancelButton = _btnSkip;
        }

        // ── Download & Install Handler ────────────────────────────────────

        private async void OnUpdateClick(object sender, EventArgs e)
        {
            if (_downloading) return;
            _downloading = true;

            // Swap UI to download mode
            _btnUpdate.Enabled = false;
            _btnUpdate.Text    = "Downloading...";
            _btnSkip.Enabled   = false;
            _progressBar.Visible = true;
            _lblStatus.Visible   = true;
            _lblStatus.Text      = "Downloading update from GitHub...";

            try
            {
                // ── Step 1: Download ──────────────────────────────────────
                var progress = new Progress<int>(pct =>
                {
                    // Progress<T> marshals to the UI thread automatically
                    _progressBar.Value = pct;
                    _lblStatus.Text    = "Downloading... " + pct + "%";
                });

                bool downloaded = await DownloadWithProgress(progress);

                if (!downloaded)
                {
                    _lblStatus.ForeColor = OSWTheme.CError;
                    _lblStatus.Text      = "Download failed. Check your internet connection and try again.";
                    _btnUpdate.Enabled   = true;
                    _btnUpdate.Text      = "Retry Download";
                    _btnSkip.Enabled     = true;
                    _downloading         = false;
                    return;
                }

                // ── Step 2: Stage the swap script ─────────────────────────
                _lblStatus.Text = "Staging update...";
                bool applied = UpdateChecker.ApplyUpdate();

                if (!applied)
                {
                    _lblStatus.ForeColor = OSWTheme.CError;
                    _lblStatus.Text      = "Staging failed — downloaded file may be missing.";
                    _btnUpdate.Enabled   = true;
                    _btnSkip.Enabled     = true;
                    _downloading         = false;
                    return;
                }

                // ── Step 3: Success — tell user to restart ────────────────
                _progressBar.Value   = 100;
                _lblStatus.ForeColor = OSWTheme.CSuccess;
                _lblStatus.Text      = "Update staged! Restart Streamer.bot to apply.";

                _btnUpdate.Text      = "Close && Restart SB";
                _btnUpdate.BackColor = OSWTheme.CSuccess;
                _btnUpdate.Enabled   = true;

                // Rewire the button to close the form with OK
                _btnUpdate.Click -= OnUpdateClick;
                _btnUpdate.Click += (s2, e2) =>
                {
                    DialogResult = DialogResult.OK;
                    Close();
                };

                UpdateStaged = true;
            }
            catch (Exception ex)
            {
                _lblStatus.ForeColor = OSWTheme.CError;
                _lblStatus.Text      = "Error: " + ex.Message;
                _btnUpdate.Enabled   = true;
                _btnUpdate.Text      = "Retry Download";
                _btnSkip.Enabled     = true;
                _downloading         = false;
            }
        }

        // ── Async download wrapper ────────────────────────────────────────
        // Runs the download on a background thread so the UI stays responsive.
        // Progress<T> automatically marshals the callback to the UI thread.

        private Task<bool> DownloadWithProgress(IProgress<int> progress)
        {
            return Task.Run(() => UpdateChecker.DownloadUpdateAsync(progress).GetAwaiter().GetResult());
        }
    }
}
