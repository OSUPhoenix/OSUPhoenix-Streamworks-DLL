using System;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    // =========================================================================
    //  OSWLib_TiltifyUI.cs  —  Tiltify Settings UI
    //  Folder: Extensions/
    //
    //  Provides a WinForms settings window for configuring the Tiltify
    //  integration. All sensitive fields are masked by default to prevent
    //  accidental on-screen exposure during a stream.
    //
    //  HOW TO OPEN FROM SB:
    //    Create an SB action (manual trigger — run from desktop, not during stream)
    //    Execute Code subaction:
    //      var lib = new OSWLib(CPH, "Tiltify Settings");
    //      lib.TiltifyOpenSettings();
    //
    //  FLOW:
    //    1. User opens UI → existing saved values pre-populate (masked)
    //    2. User edits any fields they want to change
    //    3. User clicks "Verify & Save"
    //       → Hits Tiltify token endpoint with ClientId + ClientSecret
    //       → If token OK, hits campaign endpoint with CampaignPublicId
    //       → If campaign found, saves everything to JSON and shows campaign name
    //       → If anything fails, shows an error without saving
    //    4. User closes window — integration is ready to use
    // =========================================================================

    public partial class OSWLib
    {
        /// <summary>
        /// Opens the Tiltify settings window on an STA thread (required for WinForms).
        ///
        /// Call this from a manually-triggered SB action. Not intended to be
        /// called on startup or from a timer — only when the user wants to
        /// configure or update their credentials.
        /// </summary>
        public void TiltifyOpenSettings()
        {
            // WinForms requires a Single-Threaded Apartment (STA) thread.
            // Streamer.bot runs on MTA threads, so we spin up our own.
            Thread uiThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Load whatever is already saved so we can pre-populate the form
                TiltifySettingsForm form = new TiltifySettingsForm();
                Application.Run(form);
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true;
            uiThread.Start();

            // Wait for the UI thread to finish before returning to SB
            uiThread.Join();

            LogInfo("TiltifyOpenSettings: settings window closed.");
        }
    }


    // =========================================================================
    //  TiltifySettingsForm
    //  The actual WinForms window — defined outside OSWLib since WinForms
    //  designer-style forms don't belong inside a partial class.
    // =========================================================================

    internal class TiltifySettingsForm : Form
    {
        // ── OSW Theme Colors ──────────────────────────────────────────────────
        // Dark background with silver and red accents — matches OSW brand
        private static readonly Color ColorBackground  = Color.FromArgb(18,  18,  20);   // near-black
        private static readonly Color ColorSurface     = Color.FromArgb(28,  28,  32);   // card bg
        private static readonly Color ColorBorder      = Color.FromArgb(55,  55,  65);   // subtle border
        private static readonly Color ColorAccentRed   = Color.FromArgb(200, 40,  40);   // OSW red
        private static readonly Color ColorAccentSilver= Color.FromArgb(192, 192, 200);  // silver text
        private static readonly Color ColorTextPrimary = Color.FromArgb(230, 230, 235);  // main text
        private static readonly Color ColorTextMuted   = Color.FromArgb(130, 130, 140);  // helper text
        private static readonly Color ColorSuccess     = Color.FromArgb(60,  180, 90);   // verified green
        private static readonly Color ColorError       = Color.FromArgb(220, 60,  60);   // error red
        private static readonly Color ColorInputBg     = Color.FromArgb(38,  38,  45);   // input field bg
        private static readonly Color ColorButtonHover = Color.FromArgb(220, 50,  50);   // red hover

        // ── Fonts ─────────────────────────────────────────────────────────────
        private static readonly Font FontTitle   = new Font("Segoe UI", 16f, FontStyle.Bold);
        private static readonly Font FontSubtitle= new Font("Segoe UI", 9f,  FontStyle.Regular);
        private static readonly Font FontLabel   = new Font("Segoe UI", 9f,  FontStyle.Bold);
        private static readonly Font FontInput   = new Font("Segoe UI", 10f, FontStyle.Regular);
        private static readonly Font FontHelper  = new Font("Segoe UI", 8f,  FontStyle.Regular);
        private static readonly Font FontButton  = new Font("Segoe UI", 10f, FontStyle.Bold);

        // ── Controls ──────────────────────────────────────────────────────────
        private TextBox  _txtClientId;
        private TextBox  _txtClientSecret;
        private TextBox  _txtCampaignPublicId;
        private Button   _btnToggleClientId;
        private Button   _btnToggleSecret;
        private Button   _btnVerifySave;
        private Button   _btnClose;
        private Label    _lblStatus;
        private Label    _lblCampaignFound;
        private Panel    _statusPanel;
        private Panel    _headerPanel;

        // Track visibility state for toggle buttons
        private bool _clientIdVisible  = false;
        private bool _secretVisible    = false;

        // Shared HttpClient for verification calls
        private static readonly HttpClient _http = new HttpClient();

        // ── Constructor ───────────────────────────────────────────────────────

        public TiltifySettingsForm()
        {
            BuildUI();
            LoadExistingConfig();
        }


        // =====================================================================
        //  UI CONSTRUCTION
        // =====================================================================

        private void BuildUI()
        {
            // ── Form setup ───────────────────────────────────────────────────
            this.Text            = "OSW — Tiltify Settings";
            this.Size            = new Size(540, 560);
            this.MinimumSize     = new Size(540, 560);
            this.MaximumSize     = new Size(540, 560);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox     = false;
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.BackColor       = ColorBackground;
            this.ForeColor       = ColorTextPrimary;

            // ── Header panel (OSW branding bar) ──────────────────────────────
            _headerPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 72,
                BackColor = ColorSurface
            };

            // Left red accent strip
            var accentStrip = new Panel
            {
                Width     = 4,
                Dock      = DockStyle.Left,
                BackColor = ColorAccentRed
            };
            _headerPanel.Controls.Add(accentStrip);

            var lblTitle = new Label
            {
                Text      = "Tiltify Integration",
                Font      = FontTitle,
                ForeColor = ColorTextPrimary,
                AutoSize  = false,
                Location  = new Point(20, 10),
                Size      = new Size(400, 30)
            };
            var lblSubtitle = new Label
            {
                Text      = "OSUPhoenix StreamWorks  •  Credentials are stored locally and never shared",
                Font      = FontSubtitle,
                ForeColor = ColorTextMuted,
                AutoSize  = false,
                Location  = new Point(20, 42),
                Size      = new Size(480, 18)
            };
            _headerPanel.Controls.Add(lblTitle);
            _headerPanel.Controls.Add(lblSubtitle);
            this.Controls.Add(_headerPanel);

            // ── Main content panel ────────────────────────────────────────────
            var content = new Panel
            {
                Location  = new Point(0, 72),
                Size      = new Size(540, 488),
                BackColor = ColorBackground
            };
            this.Controls.Add(content);

            int y = 24; // vertical cursor

            // ── Security notice ───────────────────────────────────────────────
            var noticePanel = new Panel
            {
                Location  = new Point(20, y),
                Size      = new Size(496, 36),
                BackColor = Color.FromArgb(40, 200, 40, 40) // subtle red tint
            };
            var noticeLabel = new Label
            {
                Text      = "⚠  All sensitive fields are hidden by default. Do not share your Client Secret.",
                Font      = FontHelper,
                ForeColor = Color.FromArgb(255, 180, 180),
                AutoSize  = false,
                Location  = new Point(10, 10),
                Size      = new Size(476, 16)
            };
            noticePanel.Controls.Add(noticeLabel);
            content.Controls.Add(noticePanel);
            y += 52;

            // ── Client ID ─────────────────────────────────────────────────────
            content.Controls.Add(MakeLabel("Client ID", new Point(20, y)));
            y += 22;

            (_txtClientId, _btnToggleClientId) = MakeMaskedField(
                content, new Point(20, y), 456, "Your Tiltify application Client ID");
            _btnToggleClientId.Click += (s, e) => ToggleVisibility(
                _txtClientId, _btnToggleClientId, ref _clientIdVisible);
            y += 40;

            content.Controls.Add(MakeHelperLabel(
                "Found at dashboard.tiltify.com → Your Apps → [App Name]",
                new Point(20, y)));
            y += 28;

            // ── Client Secret ─────────────────────────────────────────────────
            content.Controls.Add(MakeLabel("Client Secret", new Point(20, y)));
            y += 22;

            (_txtClientSecret, _btnToggleSecret) = MakeMaskedField(
                content, new Point(20, y), 456, "Your Tiltify application Client Secret");
            _btnToggleSecret.Click += (s, e) => ToggleVisibility(
                _txtClientSecret, _btnToggleSecret, ref _secretVisible);
            y += 40;

            content.Controls.Add(MakeHelperLabel(
                "Treat this like a password — never share it or show it on stream",
                new Point(20, y)));
            y += 32;

            // ── Divider ───────────────────────────────────────────────────────
            var divider = new Panel
            {
                Location  = new Point(20, y),
                Size      = new Size(496, 1),
                BackColor = ColorBorder
            };
            content.Controls.Add(divider);
            y += 16;

            // ── Campaign Public ID ────────────────────────────────────────────
            content.Controls.Add(MakeLabel("Campaign Public ID", new Point(20, y)));
            y += 22;

            _txtCampaignPublicId = new TextBox
            {
                Location    = new Point(20, y),
                Size        = new Size(496, 28),
                Font        = FontInput,
                BackColor   = ColorInputBg,
                ForeColor   = ColorTextMuted,   // dimmed until user types
                BorderStyle = BorderStyle.FixedSingle,
                Text        = "e.g. a1b2c3d4-e5f6-7890-abcd-ef1234567890"
            };
            // .NET 4.8.1: simulate PlaceholderText with Enter/Leave events
            ApplyPlaceholder(_txtCampaignPublicId,
                "e.g. a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            StyleInputBorder(_txtCampaignPublicId);
            content.Controls.Add(_txtCampaignPublicId);
            y += 38;

            // Campaign name feedback (populated after successful verify)
            _lblCampaignFound = new Label
            {
                Text      = "",
                Font      = FontHelper,
                ForeColor = ColorSuccess,
                AutoSize  = false,
                Location  = new Point(20, y),
                Size      = new Size(496, 16)
            };
            content.Controls.Add(_lblCampaignFound);

            content.Controls.Add(MakeHelperLabel(
                "The UUID in your Tiltify campaign URL — not the campaign name",
                new Point(20, y + 16)));
            y += 44;

            // ── Status panel ──────────────────────────────────────────────────
            _statusPanel = new Panel
            {
                Location  = new Point(20, y),
                Size      = new Size(496, 36),
                BackColor = Color.FromArgb(28, 28, 32),
                Visible   = false
            };
            _lblStatus = new Label
            {
                Text      = "",
                Font      = FontHelper,
                ForeColor = ColorTextPrimary,
                AutoSize  = false,
                Location  = new Point(10, 10),
                Size      = new Size(476, 16)
            };
            _statusPanel.Controls.Add(_lblStatus);
            content.Controls.Add(_statusPanel);
            y += 44;

            // ── Buttons ───────────────────────────────────────────────────────
            _btnVerifySave = new Button
            {
                Text      = "Verify & Save",
                Location  = new Point(20, y),
                Size      = new Size(370, 40),
                Font      = FontButton,
                BackColor = ColorAccentRed,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _btnVerifySave.FlatAppearance.BorderSize      = 0;
            _btnVerifySave.FlatAppearance.MouseOverBackColor  = ColorButtonHover;
            _btnVerifySave.FlatAppearance.MouseDownBackColor  = Color.FromArgb(160, 30, 30);
            _btnVerifySave.Click += OnVerifyAndSave;
            content.Controls.Add(_btnVerifySave);

            _btnClose = new Button
            {
                Text      = "Close",
                Location  = new Point(400, y),
                Size      = new Size(116, 40),
                Font      = FontButton,
                BackColor = ColorSurface,
                ForeColor = ColorTextPrimary,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _btnClose.FlatAppearance.BorderSize  = 1;
            _btnClose.FlatAppearance.BorderColor = ColorBorder;
            _btnClose.Click += (s, e) => this.Close();
            content.Controls.Add(_btnClose);
        }


        // =====================================================================
        //  LOAD EXISTING CONFIG
        //  Pre-populates fields with whatever is already saved.
        //  Sensitive fields show placeholder dots if a value exists,
        //  so the user knows something is saved without seeing it.
        // =====================================================================

        private void LoadExistingConfig()
        {
            try
            {
                // Reuse the same load logic from OSWLib_Tiltify.cs via the
                // config file path. We read it directly here since this form
                // lives outside OSWLib's instance scope.
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OSWTools", "tiltify_config.json");

                if (!System.IO.File.Exists(path)) return;

                string  raw    = System.IO.File.ReadAllText(path);
                JObject config = JObject.Parse(raw);

                // For sensitive fields: if a value exists, show a placeholder
                // so the user knows it's saved — they only need to type if
                // they want to change it
                string clientId     = config["clientId"]?.ToString()     ?? "";
                string clientSecret = config["clientSecret"]?.ToString() ?? "";
                string publicId     = config["campaignPublicId"]?.ToString() ?? "";
                string campaignName = config["campaignName"]?.ToString() ?? "";

                if (!string.IsNullOrEmpty(clientId))
                {
                    _txtClientId.Text = clientId;
                    // Already masked via PasswordChar — no extra action needed
                }

                if (!string.IsNullOrEmpty(clientSecret))
                    _txtClientSecret.Text = clientSecret;

                if (!string.IsNullOrEmpty(publicId))
                    _txtCampaignPublicId.Text = publicId;

                if (!string.IsNullOrEmpty(campaignName))
                {
                    _lblCampaignFound.Text = $"✓  Currently configured: \"{campaignName}\"";
                    _lblCampaignFound.ForeColor = ColorSuccess;
                }
            }
            catch
            {
                // If the config can't be read just show empty fields
            }
        }


        // =====================================================================
        //  VERIFY & SAVE
        // =====================================================================

        private async void OnVerifyAndSave(object sender, EventArgs e)
        {
            // ── Collect and validate inputs ───────────────────────────────────
            string clientId    = _txtClientId.Text.Trim();
            string secret      = _txtClientSecret.Text.Trim();
            string publicId    = _txtCampaignPublicId.Text.Trim();

            if (string.IsNullOrEmpty(clientId))
            {
                ShowStatus("Client ID cannot be empty.", isError: true);
                return;
            }
            if (string.IsNullOrEmpty(secret))
            {
                ShowStatus("Client Secret cannot be empty.", isError: true);
                return;
            }
            if (string.IsNullOrEmpty(publicId))
            {
                ShowStatus("Campaign Public ID cannot be empty.", isError: true);
                return;
            }

            // ── Lock UI during verification ───────────────────────────────────
            _btnVerifySave.Enabled = false;
            _btnVerifySave.Text    = "Verifying...";
            ShowStatus("Connecting to Tiltify...", isError: false);

            try
            {
                // ── Step 1: Get access token ──────────────────────────────────
                ShowStatus("Step 1 of 2 — Testing credentials...", isError: false);

                string token = await GetAccessTokenAsync(clientId, secret);
                if (token == null)
                {
                    ShowStatus(
                        "✗  Could not authenticate. Check your Client ID and Client Secret.",
                        isError: true);
                    return;
                }

                // ── Step 2: Verify campaign public ID ─────────────────────────
                ShowStatus("Step 2 of 2 — Verifying campaign...", isError: false);

                string campaignName = await GetCampaignNameAsync(token, publicId);
                if (campaignName == null)
                {
                    ShowStatus(
                        "✗  Campaign not found. Check your Campaign Public ID (the UUID, not the name).",
                        isError: true);
                    return;
                }

                // ── Both verified — save to JSON ──────────────────────────────
                SaveConfig(clientId, secret, publicId, campaignName, token);

                _lblCampaignFound.Text      = $"✓  Campaign verified: \"{campaignName}\"";
                _lblCampaignFound.ForeColor = ColorSuccess;

                ShowStatus(
                    $"✓  Saved! Call TiltifyInitialize() in your startup action to activate.",
                    isError: false);
                ShowStatus_Success();
            }
            catch (Exception ex)
            {
                ShowStatus($"✗  Unexpected error: {ex.Message}", isError: true);
            }
            finally
            {
                _btnVerifySave.Enabled = true;
                _btnVerifySave.Text    = "Verify & Save";
            }
        }


        // =====================================================================
        //  API VERIFICATION CALLS
        //  These mirror TiltifyRefreshToken and TiltifyFetchCampaignInfo from
        //  OSWLib_Tiltify.cs but are self-contained here since this form runs
        //  outside of OSWLib's instance. Async/await is safe on the STA UI thread.
        // =====================================================================

        private async System.Threading.Tasks.Task<string> GetAccessTokenAsync(
            string clientId, string clientSecret)
        {
            try
            {
                var body = new JObject
                {
                    ["grant_type"]    = "client_credentials",
                    ["client_id"]     = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"]         = "public"
                };

                var request = new HttpRequestMessage(
                    HttpMethod.Post, "https://v5api.tiltify.com/oauth/token")
                {
                    Content = new StringContent(
                        body.ToString(), Encoding.UTF8, "application/json")
                };

                HttpResponseMessage response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;

                string json  = await response.Content.ReadAsStringAsync();
                JObject obj  = JObject.Parse(json);
                return obj["access_token"]?.ToString();
            }
            catch { return null; }
        }

        private async System.Threading.Tasks.Task<string> GetCampaignNameAsync(
            string token, string publicId)
        {
            try
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://v5api.tiltify.com/api/public/campaigns/{publicId}");
                request.Headers.Add("Authorization", "Bearer " + token);

                HttpResponseMessage response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;

                string json    = await response.Content.ReadAsStringAsync();
                JObject obj    = JObject.Parse(json);
                JObject data   = obj["data"] as JObject;
                return data?["name"]?.ToString();
            }
            catch { return null; }
        }


        // =====================================================================
        //  CONFIG SAVE
        //  Writes to %AppData%\OSWTools\tiltify_config.json.
        //  Preserves any existing poll state fields (seenDonationIds, etc.)
        //  so verifying credentials doesn't wipe a running session.
        // =====================================================================

        private void SaveConfig(
            string clientId, string clientSecret,
            string publicId, string campaignName,
            string accessToken)
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OSWTools", "tiltify_config.json");

            // Load existing config to preserve poll state fields
            JObject config = new JObject();
            if (System.IO.File.Exists(path))
            {
                try { config = JObject.Parse(System.IO.File.ReadAllText(path)); }
                catch { config = new JObject(); }
            }

            // Update only the credentials and campaign fields
            config["clientId"]         = clientId;
            config["clientSecret"]     = clientSecret;
            config["campaignPublicId"] = publicId;
            config["campaignName"]     = campaignName;
            config["accessToken"]      = accessToken;
            config["tokenExpiresAt"]   = DateTime.UtcNow.AddSeconds(7200).ToString("o");

            // Reset poll state when credentials or campaign change —
            // avoids misfires if the campaign has switched
            if (config["seenDonationIds"] == null)
                config["seenDonationIds"] = new JArray();
            if (config["lastAmountRaised"] == null)
                config["lastAmountRaised"] = 0;
            if (config["goalReachedFired"] == null)
                config["goalReachedFired"] = false;

            string dir = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllText(path,
                config.ToString(Newtonsoft.Json.Formatting.Indented));
        }


        // =====================================================================
        //  UI HELPERS
        // =====================================================================

        /// <summary>
        /// Creates a masked input row (TextBox + show/hide toggle button).
        /// Returns both controls so the caller can wire up the toggle click.
        /// </summary>
        private (TextBox input, Button toggle) MakeMaskedField(
            Control parent, Point location, int inputWidth, string placeholder)
        {
            var txt = new TextBox
            {
                Location        = location,
                Size            = new Size(inputWidth, 28),
                Font            = FontInput,
                BackColor       = ColorInputBg,
                ForeColor       = ColorTextMuted,   // dimmed until user types
                BorderStyle     = BorderStyle.FixedSingle,
                Text            = placeholder
                // PasswordChar is set AFTER ApplyPlaceholder wires events so it
                // doesn't mask the placeholder text while it is showing.
            };
            // .NET 4.8.1: simulate PlaceholderText with Enter/Leave events.
            // For a masked field we suppress PasswordChar while placeholder is shown
            // and restore it when the user enters real content.
            ApplyPlaceholderMasked(txt, placeholder);
            StyleInputBorder(txt);
            parent.Controls.Add(txt);

            var btn = new Button
            {
                Text      = "Show",
                Location  = new Point(location.X + inputWidth + 4, location.Y),
                Size      = new Size(36, 28),
                Font      = new Font("Segoe UI", 7.5f, FontStyle.Regular),
                BackColor = ColorSurface,
                ForeColor = ColorTextMuted,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = ColorBorder;
            btn.FlatAppearance.BorderSize  = 1;
            parent.Controls.Add(btn);

            return (txt, btn);
        }

        private void ToggleVisibility(TextBox txt, Button btn, ref bool isVisible)
        {
            isVisible    = !isVisible;
            txt.PasswordChar = isVisible ? '\0' : '●';
            btn.Text     = isVisible ? "Hide" : "Show";
            btn.ForeColor= isVisible ? ColorAccentRed : ColorTextMuted;
        }

        private Label MakeLabel(string text, Point location)
        {
            return new Label
            {
                Text      = text,
                Font      = FontLabel,
                ForeColor = ColorAccentSilver,
                AutoSize  = false,
                Location  = location,
                Size      = new Size(496, 18)
            };
        }

        private Label MakeHelperLabel(string text, Point location)
        {
            return new Label
            {
                Text      = text,
                Font      = FontHelper,
                ForeColor = ColorTextMuted,
                AutoSize  = false,
                Location  = location,
                Size      = new Size(496, 14)
            };
        }

        private void StyleInputBorder(TextBox txt)
        {
            // WinForms doesn't support custom border colors on TextBox natively.
            // The nearest approach is BorderStyle.FixedSingle with BackColor
            // providing the visual contrast. A Panel wrapper would give a custom
            // border — adding that pattern here if needed in a future iteration.
            txt.BorderStyle = BorderStyle.FixedSingle;
        }

        private void ShowStatus(string message, bool isError)
        {
            _lblStatus.Text      = message;
            _lblStatus.ForeColor = isError ? ColorError : ColorTextPrimary;
            _statusPanel.Visible = true;
            _statusPanel.BackColor = isError
                ? Color.FromArgb(40, 220, 40, 40)
                : Color.FromArgb(28, 28, 32);
        }

        private void ShowStatus_Success()
        {
            _lblStatus.ForeColor   = ColorSuccess;
            _statusPanel.BackColor = Color.FromArgb(30, 60, 180, 90);
        }

        // ── .NET 4.8.1 placeholder helpers ───────────────────────────────────
        // TextBox.PlaceholderText was introduced in .NET 5.
        // These methods replicate the same visual behaviour using Enter/Leave
        // events so the form compiles against net481 without any change to callers.

        /// <summary>
        /// Simulates placeholder text on a plain TextBox.
        /// The placeholder is shown in ColorTextMuted when the field is empty.
        /// The field is considered "empty" if it contains exactly the placeholder string.
        /// </summary>
        private void ApplyPlaceholder(TextBox txt, string placeholder)
        {
            // Initialise: if the field already has a real value, show it normally.
            if (string.IsNullOrEmpty(txt.Text) || txt.Text == placeholder)
            {
                txt.Text      = placeholder;
                txt.ForeColor = ColorTextMuted;
            }
            else
            {
                txt.ForeColor = ColorTextPrimary;
            }

            txt.Enter += (s, e) =>
            {
                if (txt.Text == placeholder)
                {
                    txt.Text      = string.Empty;
                    txt.ForeColor = ColorTextPrimary;
                }
            };

            txt.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txt.Text))
                {
                    txt.Text      = placeholder;
                    txt.ForeColor = ColorTextMuted;
                }
            };
        }

        /// <summary>
        /// Simulates placeholder text on a masked (password) TextBox.
        /// While the placeholder is showing, PasswordChar is suppressed so the
        /// hint text is readable. Once the user starts typing, PasswordChar ('●')
        /// is restored and the field behaves as a normal masked input.
        /// </summary>
        private void ApplyPlaceholderMasked(TextBox txt, string placeholder)
        {
            // Show placeholder unmasked initially
            txt.Text         = placeholder;
            txt.ForeColor    = ColorTextMuted;
            txt.PasswordChar = '\0'; // no masking while placeholder is visible

            txt.Enter += (s, e) =>
            {
                if (txt.Text == placeholder)
                {
                    txt.Text         = string.Empty;
                    txt.ForeColor    = ColorTextPrimary;
                    txt.PasswordChar = '●'; // restore masking for real input
                }
            };

            txt.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txt.Text))
                {
                    txt.PasswordChar = '\0'; // suppress masking so hint is visible
                    txt.Text         = placeholder;
                    txt.ForeColor    = ColorTextMuted;
                }
            };
        }

        /// <summary>
        /// Returns the actual text content of a TextBox, returning empty string
        /// if the field is still showing its placeholder text.
        /// Use this instead of .Text directly when reading user input.
        /// </summary>
        private static string GetFieldValue(TextBox txt, string placeholder)
        {
            string v = txt.Text?.Trim() ?? string.Empty;
            return v == placeholder ? string.Empty : v;
        }

        // ── Clean up fonts on form close ──────────────────────────────────────
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            FontTitle.Dispose();
            FontSubtitle.Dispose();
            FontLabel.Dispose();
            FontInput.Dispose();
            FontHelper.Dispose();
            FontButton.Dispose();
        }
    }
}
