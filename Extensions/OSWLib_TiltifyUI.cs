using System;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OSWTools.Theme;

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
    //    var lib = new OSWLib(CPH, "Tiltify Settings");
    //    lib.TiltifyOpenSettings();
    //
    //  CHANGELOG
    //  ---------
    //  v5:
    //  • Fixed FTL crash: "Parameter is not valid" at Font.GetHeight().
    //    Root cause: OSWTheme static Font fields can become invalid after a
    //    Streamer.bot AppDomain reload. Assigning a disposed GDI font to a
    //    Control.Font property throws ArgumentException.
    //    Fix: BuildUI() now constructs its own local Font instances using the
    //    same parameters as OSWTheme (same family/size/style), and disposes
    //    them in OnFormClosed. Colors and paint helpers from OSWTheme are safe
    //    because Color is a value type and Paint delegates don't hold GDI state.
    //
    //  v4: Applied OSWTheme colors, paint helpers, and embedded logo.
    //  v3: Moved config storage from %AppData% to OSWData\Tiltify\config.json.
    // =========================================================================

    public partial class OSWLib
    {
        /// <summary>
        /// Opens the Tiltify settings window on an STA thread (required for WinForms).
        /// Call from a manually-triggered SB action only.
        /// </summary>
        public void TiltifyOpenSettings()
        {
            Thread uiThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TiltifySettingsForm());
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.IsBackground = true;
            uiThread.Start();
            uiThread.Join();

            LogInfo("TiltifyOpenSettings: settings window closed.");
        }
    }


    // =========================================================================
    //  TiltifySettingsForm
    // =========================================================================

    internal class TiltifySettingsForm : Form
    {
        // ── OSWData identifiers — must match OSWLib_Tiltify.cs ────────────────
        private const string DATA_TOOL = "Tiltify";
        private const string DATA_FILE = "config";

        // ── Controls ──────────────────────────────────────────────────────────
        private TextBox _txtClientId;
        private TextBox _txtClientSecret;
        private TextBox _txtCampaignPublicId;
        private Button  _btnToggleClientId;
        private Button  _btnToggleSecret;
        private Button  _btnVerifySave;
        private Button  _btnClose;
        private Label   _lblStatus;
        private Label   _lblCampaignFound;
        private Panel   _statusPanel;

        // Track visibility state for toggle buttons
        private bool _clientIdVisible = false;
        private bool _secretVisible   = false;

        // ── Local fonts ───────────────────────────────────────────────────────
        // WHY LOCAL INSTEAD OF OSWTheme STATICS?
        // OSWTheme.Fn* are static readonly Font objects created when the DLL
        // first loads. If the Streamer.bot AppDomain is reloaded (which happens
        // on SB restart or code recompile) those GDI handles become invalid.
        // Assigning an invalid Font to Control.Font calls Font.GetHeight()
        // which throws System.ArgumentException: "Parameter is not valid."
        //
        // Creating fonts fresh inside the form guarantees valid GDI handles
        // for the lifetime of this window. We use the same typeface and sizes
        // as OSWTheme so the appearance is identical.
        // All are disposed in OnFormClosed — no GDI leaks.
        private Font _fnTitle;   // 15pt Bold  — window title
        private Font _fnSubtitle;// 9pt Regular — subtitle / header note
        private Font _fnLabel;   // 9pt Bold    — field labels
        private Font _fnInput;   // 10pt Regular — text inputs
        private Font _fnHelper;  // 8pt Italic  — helper/hint text
        private Font _fnButton;  // 9pt Bold    — buttons
        private Font _fnToggle;  // 7.5pt       — Show/Hide toggle

        // Shared HttpClient for verification calls
        private static readonly HttpClient _http = new HttpClient();

        // ── Constructor ───────────────────────────────────────────────────────

        public TiltifySettingsForm()
        {
            // Allocate fonts before BuildUI so every helper can reference them
            _fnTitle    = new Font("Segoe UI", 15f,  FontStyle.Bold);
            _fnSubtitle = new Font("Segoe UI", 9f,   FontStyle.Regular);
            _fnLabel    = new Font("Segoe UI", 9f,   FontStyle.Bold);
            _fnInput    = new Font("Segoe UI", 10f,  FontStyle.Regular);
            _fnHelper   = new Font("Segoe UI", 8f,   FontStyle.Italic);
            _fnButton   = new Font("Segoe UI", 9f,   FontStyle.Bold);
            _fnToggle   = new Font("Segoe UI", 7.5f, FontStyle.Regular);

            BuildUI();
            LoadExistingConfig();
        }


        // =====================================================================
        //  UI CONSTRUCTION
        // =====================================================================

        private void BuildUI()
        {
            this.Text            = "OSW — Tiltify Settings";
            this.Size            = new Size(540, 600);
            this.MinimumSize     = new Size(540, 600);
            this.MaximumSize     = new Size(540, 600);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox     = false;
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.BackColor       = OSWTheme.CBg;
            this.ForeColor       = OSWTheme.CTxt;

            // ── Header panel ──────────────────────────────────────────────────
            // PaintHeaderBg draws CPnl fill + bottom divider line.
            var header = new Panel { Dock = DockStyle.Top, Height = 80 };
            header.Paint += OSWTheme.PaintHeaderBg;

            // Left red accent strip
            header.Controls.Add(new Panel
            {
                Width     = 4,
                Dock      = DockStyle.Left,
                BackColor = OSWTheme.CAcc
            });

            // StreamWorks logo from embedded resource — null-safe if not embedded
            var logo = new PictureBox
            {
                Size      = new Size(52, 52),
                Location  = new Point(14, 14),
                SizeMode  = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Image     = OSWTheme.GetEmbeddedLogo()
            };
            header.Controls.Add(logo);

            header.Controls.Add(new Label
            {
                Text      = "Tiltify Integration",
                Font      = _fnTitle,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(76, 12),
                Size      = new Size(440, 28)
            });
            header.Controls.Add(new Label
            {
                Text      = "OSUPhoenix StreamWorks  •  Credentials stored in OSWData\\Tiltify\\config.json",
                Font      = _fnSubtitle,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(76, 46),
                Size      = new Size(440, 18)
            });
            this.Controls.Add(header);

            // ── Main content panel ────────────────────────────────────────────
            // PaintWatermark renders the StreamWorks logo at 15% opacity.
            var content = new Panel
            {
                Location  = new Point(0, 80),
                Size      = new Size(540, 468),
                BackColor = OSWTheme.CBg
            };
            content.Paint += OSWTheme.PaintWatermark;
            this.Controls.Add(content);

            int y = 20;

            // ── Security notice ───────────────────────────────────────────────
            var noticePanel = new Panel
            {
                Location  = new Point(20, y),
                Size      = new Size(496, 32),
                BackColor = OSWTheme.FC("#2A0000")
            };
            noticePanel.Controls.Add(new Label
            {
                Text      = "⚠  All sensitive fields are hidden by default. Do not share your Client Secret.",
                Font      = _fnSubtitle,
                ForeColor = OSWTheme.CWarning,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(10, 8),
                Size      = new Size(476, 16)
            });
            content.Controls.Add(noticePanel);
            y += 46;

            // ── Client ID ─────────────────────────────────────────────────────
            content.Controls.Add(MakeLabel("Client ID", new Point(20, y)));
            y += 20;

            (_txtClientId, _btnToggleClientId) = MakeMaskedField(
                content, new Point(20, y), 456, "Your Tiltify application Client ID");
            _btnToggleClientId.Click += (s, e) =>
                ToggleVisibility(_txtClientId, _btnToggleClientId, ref _clientIdVisible);
            y += 36;

            content.Controls.Add(MakeHelperLabel(
                "Found at dashboard.tiltify.com → Your Apps → [App Name]",
                new Point(20, y)));
            y += 24;

            // ── Client Secret ─────────────────────────────────────────────────
            content.Controls.Add(MakeLabel("Client Secret", new Point(20, y)));
            y += 20;

            (_txtClientSecret, _btnToggleSecret) = MakeMaskedField(
                content, new Point(20, y), 456, "Your Tiltify application Client Secret");
            _btnToggleSecret.Click += (s, e) =>
                ToggleVisibility(_txtClientSecret, _btnToggleSecret, ref _secretVisible);
            y += 36;

            content.Controls.Add(MakeHelperLabel(
                "Treat this like a password — never share it or show it on stream",
                new Point(20, y)));
            y += 28;

            // ── Divider ───────────────────────────────────────────────────────
            content.Controls.Add(new Panel
            {
                Location  = new Point(20, y),
                Size      = new Size(496, 1),
                BackColor = OSWTheme.CDiv
            });
            y += 14;

            // ── Campaign Public ID ────────────────────────────────────────────
            content.Controls.Add(MakeLabel("Campaign Public ID", new Point(20, y)));
            y += 20;

            _txtCampaignPublicId = new TextBox
            {
                Location    = new Point(20, y),
                Size        = new Size(496, 26),
                Font        = _fnInput,
                BackColor   = OSWTheme.CIn,
                ForeColor   = OSWTheme.CDim,
                BorderStyle = BorderStyle.FixedSingle
            };
            ApplyPlaceholder(_txtCampaignPublicId, "e.g. a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            content.Controls.Add(_txtCampaignPublicId);
            y += 34;

            _lblCampaignFound = new Label
            {
                Text      = "",
                Font      = _fnSubtitle,
                ForeColor = OSWTheme.CSuccess,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(20, y),
                Size      = new Size(496, 16)
            };
            content.Controls.Add(_lblCampaignFound);

            content.Controls.Add(MakeHelperLabel(
                "The UUID in your Tiltify campaign URL — not the campaign name",
                new Point(20, y + 17)));
            y += 42;

            // ── Status panel ──────────────────────────────────────────────────
            _statusPanel = new Panel
            {
                Location  = new Point(20, y),
                Size      = new Size(496, 32),
                BackColor = OSWTheme.CPnl,
                Visible   = false
            };
            _lblStatus = new Label
            {
                Text      = "",
                Font      = _fnSubtitle,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(10, 8),
                Size      = new Size(476, 16)
            };
            _statusPanel.Controls.Add(_lblStatus);
            content.Controls.Add(_statusPanel);
            y += 42;

            // ── Footer / button row ───────────────────────────────────────────
            // PaintFooterGradient blends CPnl → CGradBottom (dark red at base).
            var footer = new Panel
            {
                Location  = new Point(0, y),
                Size      = new Size(540, 468 - y),
                BackColor = OSWTheme.CPnl
            };
            footer.Paint += OSWTheme.PaintFooterGradient;
            content.Controls.Add(footer);

            _btnVerifySave = new Button
            {
                Text      = "Verify & Save",
                Location  = new Point(20, 16),
                Size      = new Size(360, 36),
                Font      = _fnButton,
                BackColor = OSWTheme.CAcc,
                ForeColor = OSWTheme.CTxt,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _btnVerifySave.FlatAppearance.BorderSize         = 0;
            _btnVerifySave.FlatAppearance.MouseOverBackColor = OSWTheme.CAccHov;
            _btnVerifySave.FlatAppearance.MouseDownBackColor = OSWTheme.CAccPrs;
            _btnVerifySave.Click += OnVerifyAndSave;
            footer.Controls.Add(_btnVerifySave);

            _btnClose = new Button
            {
                Text      = "Close",
                Location  = new Point(390, 16),
                Size      = new Size(126, 36),
                Font      = _fnButton,
                BackColor = OSWTheme.CPnl,
                ForeColor = OSWTheme.CTxt,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            _btnClose.FlatAppearance.BorderSize  = 1;
            _btnClose.FlatAppearance.BorderColor = OSWTheme.CDiv;
            _btnClose.Click += (s, e) => this.Close();
            footer.Controls.Add(_btnClose);
        }


        // =====================================================================
        //  LOAD EXISTING CONFIG
        // =====================================================================

        private void LoadExistingConfig()
        {
            try
            {
                if (!OSWTools.Data.OSWData.Exists(DATA_TOOL, DATA_FILE)) return;

                string raw = System.IO.File.ReadAllText(
                    OSWTools.Data.OSWData.GetFilePath(DATA_TOOL, DATA_FILE));
                if (string.IsNullOrWhiteSpace(raw)) return;

                JObject config = JObject.Parse(raw);

                string clientId     = config["clientId"]?.ToString()        ?? "";
                string clientSecret = config["clientSecret"]?.ToString()     ?? "";
                string publicId     = config["campaignPublicId"]?.ToString() ?? "";
                string campaignName = config["campaignName"]?.ToString()     ?? "";

                if (!string.IsNullOrEmpty(clientId))     _txtClientId.Text         = clientId;
                if (!string.IsNullOrEmpty(clientSecret)) _txtClientSecret.Text     = clientSecret;
                if (!string.IsNullOrEmpty(publicId))     _txtCampaignPublicId.Text = publicId;

                if (!string.IsNullOrEmpty(campaignName))
                {
                    _lblCampaignFound.Text      = $"✓  Currently configured: \"{campaignName}\"";
                    _lblCampaignFound.ForeColor = OSWTheme.CSuccess;
                }
            }
            catch { }
        }


        // =====================================================================
        //  VERIFY & SAVE
        // =====================================================================

        private async void OnVerifyAndSave(object sender, EventArgs e)
        {
            string clientId = _txtClientId.Text.Trim();
            string secret   = _txtClientSecret.Text.Trim();
            string publicId = _txtCampaignPublicId.Text.Trim();

            if (string.IsNullOrEmpty(clientId))
            { ShowStatus("Client ID cannot be empty.", isError: true); return; }
            if (string.IsNullOrEmpty(secret))
            { ShowStatus("Client Secret cannot be empty.", isError: true); return; }
            if (string.IsNullOrEmpty(publicId))
            { ShowStatus("Campaign Public ID cannot be empty.", isError: true); return; }

            _btnVerifySave.Enabled = false;
            _btnVerifySave.Text    = "Verifying...";
            ShowStatus("Connecting to Tiltify...", isError: false);

            try
            {
                ShowStatus("Step 1 of 2 — Testing credentials...", isError: false);
                string token = await GetAccessTokenAsync(clientId, secret);
                if (token == null)
                {
                    ShowStatus("✗  Could not authenticate. Check your Client ID and Client Secret.",
                        isError: true);
                    return;
                }

                ShowStatus("Step 2 of 2 — Verifying campaign...", isError: false);
                string campaignName = await GetCampaignNameAsync(token, publicId);
                if (campaignName == null)
                {
                    ShowStatus("✗  Campaign not found. Check your Campaign Public ID (the UUID, not the name).",
                        isError: true);
                    return;
                }

                SaveConfig(clientId, secret, publicId, campaignName, token);

                _lblCampaignFound.Text      = $"✓  Campaign verified: \"{campaignName}\"";
                _lblCampaignFound.ForeColor = OSWTheme.CSuccess;
                ShowStatus("✓  Saved! Run 'Tiltify - Init' (or restart Streamer.bot) to activate.",
                    isError: false);
                _lblStatus.ForeColor   = OSWTheme.CSuccess;
                _statusPanel.BackColor = OSWTheme.FC("#0A1F0A");
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
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                };
                var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;
                var obj = JObject.Parse(await response.Content.ReadAsStringAsync());
                return obj["access_token"]?.ToString();
            }
            catch { return null; }
        }

        private async System.Threading.Tasks.Task<string> GetCampaignNameAsync(
            string token, string publicId)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://v5api.tiltify.com/api/public/campaigns/{publicId}");
                request.Headers.Add("Authorization", "Bearer " + token);
                var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;
                var obj  = JObject.Parse(await response.Content.ReadAsStringAsync());
                var data = obj["data"] as JObject;
                return data?["name"]?.ToString();
            }
            catch { return null; }
        }


        // =====================================================================
        //  CONFIG SAVE
        // =====================================================================

        private void SaveConfig(string clientId, string clientSecret,
            string publicId, string campaignName, string accessToken)
        {
            var existing = OSWTools.Data.OSWData.LoadOrDefault<TiltifyConfigForUI>(
                DATA_TOOL, DATA_FILE, new TiltifyConfigForUI());

            existing.ClientId         = clientId;
            existing.ClientSecret     = clientSecret;
            existing.CampaignPublicId = publicId;
            existing.CampaignName     = campaignName;
            existing.AccessToken      = accessToken;
            existing.TokenExpiresAt   = DateTime.UtcNow.AddSeconds(7200);

            if (existing.SeenDonationIds == null)
                existing.SeenDonationIds = new System.Collections.Generic.List<string>();

            OSWTools.Data.OSWData.Save(DATA_TOOL, DATA_FILE, existing);
        }

        [Serializable]
        private class TiltifyConfigForUI
        {
            [JsonProperty("clientId")]         public string   ClientId         { get; set; } = "";
            [JsonProperty("clientSecret")]     public string   ClientSecret     { get; set; } = "";
            [JsonProperty("campaignPublicId")] public string   CampaignPublicId { get; set; } = "";
            [JsonProperty("campaignName")]     public string   CampaignName     { get; set; } = "";
            [JsonProperty("accessToken")]      public string   AccessToken      { get; set; } = "";
            [JsonProperty("tokenExpiresAt")]   public DateTime TokenExpiresAt   { get; set; } = DateTime.MinValue;
            [JsonProperty("seenDonationIds")]
            public System.Collections.Generic.List<string> SeenDonationIds { get; set; }
                = new System.Collections.Generic.List<string>();
            [JsonProperty("lastAmountRaised")] public double LastAmountRaised { get; set; } = 0;
            [JsonProperty("goalReachedFired")] public bool   GoalReachedFired { get; set; } = false;
        }


        // =====================================================================
        //  UI HELPERS
        // =====================================================================

        private (TextBox input, Button toggle) MakeMaskedField(
            Control parent, Point location, int inputWidth, string placeholder)
        {
            var txt = new TextBox
            {
                Location    = location,
                Size        = new Size(inputWidth, 26),
                Font        = _fnInput,
                BackColor   = OSWTheme.CIn,
                ForeColor   = OSWTheme.CDim,
                BorderStyle = BorderStyle.FixedSingle
            };
            ApplyPlaceholderMasked(txt, placeholder);
            parent.Controls.Add(txt);

            var btn = new Button
            {
                Text      = "Show",
                Location  = new Point(location.X + inputWidth + 4, location.Y),
                Size      = new Size(36, 26),
                Font      = _fnToggle,
                BackColor = OSWTheme.CPnl,
                ForeColor = OSWTheme.CDim,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = OSWTheme.CDiv;
            btn.FlatAppearance.BorderSize  = 1;
            parent.Controls.Add(btn);

            return (txt, btn);
        }

        private void ToggleVisibility(TextBox txt, Button btn, ref bool isVisible)
        {
            isVisible        = !isVisible;
            txt.PasswordChar = isVisible ? '\0' : '●';
            btn.Text         = isVisible ? "Hide" : "Show";
            btn.ForeColor    = isVisible ? OSWTheme.CAcc : OSWTheme.CDim;
        }

        private Label MakeLabel(string text, Point location) => new Label
        {
            Text      = text,
            Font      = _fnLabel,
            ForeColor = OSWTheme.CTxt,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Location  = location,
            Size      = new Size(496, 18)
        };

        private Label MakeHelperLabel(string text, Point location) => new Label
        {
            Text      = text,
            Font      = _fnHelper,
            ForeColor = OSWTheme.CDim,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Location  = location,
            Size      = new Size(496, 14)
        };

        private void ShowStatus(string message, bool isError)
        {
            _lblStatus.Text        = message;
            _lblStatus.ForeColor   = isError ? OSWTheme.CError : OSWTheme.CTxt;
            _statusPanel.Visible   = true;
            _statusPanel.BackColor = isError ? OSWTheme.FC("#2A0000") : OSWTheme.CPnl;
        }

        // ── .NET 4.8.1 placeholder helpers ───────────────────────────────────

        private void ApplyPlaceholder(TextBox txt, string placeholder)
        {
            txt.Text      = placeholder;
            txt.ForeColor = OSWTheme.CDim;

            txt.Enter += (s, e) =>
            {
                if (txt.Text == placeholder)
                { txt.Text = string.Empty; txt.ForeColor = OSWTheme.CTxt; }
            };
            txt.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txt.Text))
                { txt.Text = placeholder; txt.ForeColor = OSWTheme.CDim; }
            };
        }

        private void ApplyPlaceholderMasked(TextBox txt, string placeholder)
        {
            txt.Text         = placeholder;
            txt.ForeColor    = OSWTheme.CDim;
            txt.PasswordChar = '\0';

            txt.Enter += (s, e) =>
            {
                if (txt.Text == placeholder)
                {
                    txt.Text         = string.Empty;
                    txt.ForeColor    = OSWTheme.CTxt;
                    txt.PasswordChar = '●';
                }
            };
            txt.Leave += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txt.Text))
                {
                    txt.PasswordChar = '\0';
                    txt.Text         = placeholder;
                    txt.ForeColor    = OSWTheme.CDim;
                }
            };
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Dispose only the fonts this form created — not OSWTheme statics
            _fnTitle?.Dispose();
            _fnSubtitle?.Dispose();
            _fnLabel?.Dispose();
            _fnInput?.Dispose();
            _fnHelper?.Dispose();
            _fnButton?.Dispose();
            _fnToggle?.Dispose();
        }
    }
}
