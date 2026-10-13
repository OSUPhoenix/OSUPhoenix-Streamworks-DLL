// =============================================================================
// OSWTools — Extensions/OSWLib_BueUI.cs                              (NEW)
//
// "Blow Up Emote" (module code: BUE) — the Settings window.
//
// HOW TO OPEN FROM SB (same pattern as the Tiltify settings):
//   Create an action "Blow Up Emote - Settings" (manual trigger), then add a
//   Run C# Code sub-action:
//       var lib = new OSWLib(CPH, "Blow Up Emote");
//       lib.BueOpenSettings("default");
//   (BUESettings.cs is exactly that.)
//
// FLOW
//   1. Load a private COPY of the config (BueStore.LoadConfigCopy).
//   2. Show the window on its own STA thread (WinForms requires STA; Streamer.bot
//      runs scripts on MTA threads - StaThread.Run handles the difference).
//   3. Save pressed -> validate -> BueStore.SaveConfig (writes config_<id>.json).
//   4. Back on the normal thread: send the widget the new state so it updates
//      immediately (new emote picture, progress bar on/off).
//
// DESIGN NOTES
//   * The window edits a COPY. Cancel simply throws the copy away, so nothing
//     can be half-saved.
//   * The amber text at the bottom is a live plain-English preview of what the
//     numbers mean (BlowUpEmoteEngine.DescribeSettings). Settings like "adds
//     2.5% per entry" are abstract until you see "about 40 emotes to fill".
//   * The emote name and picture are only written back if you CHANGED them.
//     If your config was hand-edited into something the window can't show
//     (Mode=All, several emotes...), saving other settings won't flatten it.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using OSWTools.BlowUpEmote;
using OSWTools.Theme;
using OSWTools.Utilities;

namespace OSWTools
{
    public partial class OSWLib
    {
        /// <summary>
        /// Opens the Blow Up Emote settings window. Blocks until it closes.
        /// Returns true if the user saved. Run it from a MANUAL action.
        /// </summary>
        public bool BueOpenSettings(string targetId = "default")
        {
            if (string.IsNullOrWhiteSpace(targetId)) targetId = "default";

            BueMigrateLegacy(targetId);

            BueTargetConfig config = BueStore.LoadConfigCopy(targetId);
            string path = BueStore.ConfigPath(targetId);
            bool saved = false;
            string saveError = null;

            DpiHelper.EnsureDpiAware();

            StaThread.Run(() =>
            {
                Application.EnableVisualStyles();
                try { Application.SetCompatibleTextRenderingDefault(false); }
                catch (InvalidOperationException) { /* already set earlier in this process - fine */ }

                using (var form = new BueSettingsForm(config, path))
                {
                    form.StartPosition = FormStartPosition.CenterScreen;
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        // Pure file I/O - safe on the STA thread.
                        try
                        {
                            BueStore.SaveConfig(targetId, form.Result);
                            saved = true;
                        }
                        catch (Exception ex)
                        {
                            saveError = ex.Message;
                        }
                    }
                }
            });

            if (saveError != null)
                LogError("[BUE] Settings could not be saved: " + saveError);

            if (saved)
            {
                BueBroadcastState(targetId);
                LogInfo("[BUE] Settings saved to " + path);
            }
            else if (saveError == null)
            {
                LogInfo("[BUE] Settings window closed without saving.");
            }

            return saved;
        }
    }

    // =========================================================================
    //  BueSettingsForm - the window. Lives outside OSWLib, like TiltifySettingsForm.
    // =========================================================================
    internal class BueSettingsForm : Form
    {
        /// <summary>The edited config. Only valid when DialogResult is OK.</summary>
        public BueTargetConfig Result { get; private set; }

        private const int FormW = 600;
        private const int PadX = 22;
        private const int ContentW = FormW - PadX * 2 - 20;   // 20 = room for a scrollbar

        private readonly BueTargetConfig _cfg;
        private readonly string _configPath;
        private readonly string _initialEmote;
        private readonly string _initialImage;
        private bool _loading = true;
        private int _y;

        private Panel _body;
        private Panel _cur;                 // where Place() puts controls right now (a tab page)
        private readonly Panel[] _pages = new Panel[3];
        private readonly Button[] _tabBtns = new Button[3];
        private int _pagesTop;
        private Panel _footer;

        private TextBox _txtEmote;
        private TextBox _txtImage;
        private TextBox _txtAllowed;
        private TextBox _txtBanned;
        private RadioButton _rbStreamer;
        private RadioButton _rbChat;
        private ComboBox _cboCount;
        private NumericUpDown _numGrowth;
        private NumericUpDown _numMax;
        private CheckBox _chkRandom;
        private NumericUpDown _numRndMin;
        private NumericUpDown _numRndMax;
        private NumericUpDown _numDeflate;
        private CheckBox _chkProgress;
        private CheckBox _chkHidden;

        private Label _lblPreview;
        private Label _lblError;
        private Button _btnSave;
        private Button _btnCancel;

        public BueSettingsForm(BueTargetConfig config, string configPath)
        {
            _cfg = config ?? new BueTargetConfig();
            _configPath = configPath ?? "";
            _initialEmote = BlowUpEmoteEngine.CurrentEmote(_cfg);
            _initialImage = _cfg.ImageOverrideUrl ?? "";

            Text = "Blow Up Emote - Settings";
            ClientSize = new Size(FormW, 720);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = OSWTheme.CBg;
            ForeColor = OSWTheme.CTxt;
            Font = OSWTheme.Fn;

            BuildUi();
            LoadFromConfig();

            _loading = false;
            UpdatePreview();
        }

        // ---------------------------------------------------------------------
        // Layout helpers. The body is a scrollable panel; controls are stacked
        // top to bottom using a running Y position (_y).
        // ---------------------------------------------------------------------

        private T Place<T>(T control, int height) where T : Control
        {
            control.Left = PadX;
            control.Top = _y;
            control.Width = ContentW;
            control.Height = height;
            _cur.Controls.Add(control);
            _y += height;
            return control;
        }

        private void Gap(int pixels) { _y += pixels; }

        private Label Help(string text, int height)
        {
            var l = new Label
            {
                Text = text,
                Font = OSWTheme.FnH,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize = false
            };
            return Place(l, height);
        }

        private void Section(string title)
        {
            Gap(10);
            Place(OSWControls.MkSecHdr(title), 22);
            Place(OSWControls.MkDivider(), 1);
            Gap(8);
        }

        private RadioButton MkRadio(string text)
        {
            return new RadioButton
            {
                Text = text,
                Font = OSWTheme.Fn,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize = false
            };
        }

        // ---------------------------------------------------------------------
        // Tabs. The window used to be one long scrolling list, so new settings
        // were off-screen. Now there are three short pages and a row of buttons.
        // (Plain buttons instead of a TabControl: WinForms' TabControl can't be
        // given a dark theme cleanly, buttons can.)
        // ---------------------------------------------------------------------
        private static readonly string[] TabNames = { "Emote", "Round", "Growth" };

        private void BuildTabStrip()
        {
            _y += 4;
            int w = (ContentW + 20) / TabNames.Length;
            for (int i = 0; i < TabNames.Length; i++)
            {
                int idx = i;
                var b = OSWControls.MkBtn(TabNames[i], OSWTheme.CIn);
                b.SetBounds(PadX + i * w, _y, w - 6, 30);
                b.Click += (o, e) => ShowPage(idx);
                _body.Controls.Add(b);
                _tabBtns[i] = b;
            }
            _y += 38;
            _pagesTop = _y;
        }

        // Start the next tab page and make Place() write into it.
        private void NewPage(int index)
        {
            var pg = new Panel
            {
                Left = 0,
                Top = _pagesTop,
                Width = FormW,
                Height = ClientSize.Height - 132 - _pagesTop,
                AutoScroll = true,
                BackColor = OSWTheme.CBg,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            _body.Controls.Add(pg);
            _pages[index] = pg;
            _cur = pg;
            _y = 0;
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] != null) _pages[i].Visible = i == index;
                if (_tabBtns[i] != null) _tabBtns[i].BackColor = i == index ? OSWTheme.CAcc : OSWTheme.CIn;
            }
        }

        private void BuildUi()
        {
            _body = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = OSWTheme.CBg };
            _cur = _body;
            _footer = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = OSWTheme.CPnl };

            // Order matters for docking: Fill first, then Bottom.
            Controls.Add(_body);
            Controls.Add(_footer);

            _y = 16;

            // -- title ---------------------------------------------------------
            var title = new Label
            {
                Text = "Blow Up Emote",
                Font = OSWTheme.FnT,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Place(title, 34);

            var sub = new Label
            {
                Text = "Balloon '" + _cfg.TargetId + "'   -   saved to: " + _configPath,
                Font = OSWTheme.FnSm,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize = false,
                AutoEllipsis = true
            };
            Place(sub, 18);

            string folder = "";
            try { folder = Path.GetDirectoryName(_configPath) ?? ""; } catch (Exception) { folder = ""; }
            if (folder.Length > 0)
            {
                var link = OSWControls.MkLink("Open settings folder", folder);
                link.Left = PadX;
                link.Top = _y;
                _cur.Controls.Add(link);
                _y += 20;
            }

            BuildTabStrip();
            NewPage(0);   // tab: Emote
            // -- EMOTE ---------------------------------------------------------
            Section("DEFAULT EMOTE (optional)");

            Place(OSWControls.MkFieldLbl("Emote name"), 18);
            _txtEmote = Place(OSWControls.MkInput(), 26);
            Help("Used when a round is started WITHOUT an emote (a Start Round trigger with no text). Leave it blank " +
                 "if the emote always comes with the trigger, e.g. a viewer's reward or  !blowup Kappa.  It does NOT limit " +
                 "what can be picked or counted while 'Hide the balloon between rounds' is on. If that is off, this is " +
                 "the one emote the balloon counts. Exactly as in chat; no spaces.", 62);

            Gap(6);
            Place(OSWControls.MkFieldLbl("Picture link (optional)"), 18);
            _txtImage = Place(OSWControls.MkInput(), 26);
            Help("Leave blank to find the picture automatically. If the balloon shows a placeholder face, " +
                 "paste an https:// link to the emote image here.", 32);

            if (_cfg.Mode != EmoteSourceMode.SpecificList || (_cfg.SpecificEmotes != null && _cfg.SpecificEmotes.Count > 1))
            {
                int n = _cfg.SpecificEmotes == null ? 0 : _cfg.SpecificEmotes.Count;
                var adv = new Label
                {
                    Text = "Advanced setup detected: this balloon counts emotes by mode '" + _cfg.Mode + "' and " + n +
                           " listed emote(s). Leave the two fields above untouched to keep that. Changing either " +
                           "replaces it with the single emote entered here.",
                    Font = OSWTheme.FnH,
                    ForeColor = OSWTheme.CPrev,
                    BackColor = Color.Transparent,
                    AutoSize = false
                };
                Place(adv, 46);
            }

            // -- WHO CHOOSES THE EMOTE -----------------------------------------
            Section("WHO CAN CHANGE THE EMOTE");

            _rbStreamer = Place(MkRadio("Streamer only - viewers can't choose or change the emote"), 24);
            _rbChat = Place(MkRadio("Chat can change it"), 24);
            Help("Viewers pick the emote through the 'Start Round' and 'Set Emote' actions. Add whichever triggers you want to those actions " +
                 "in Streamer.bot (channel point reward, bits, sub, command, power-up...) and set who may use them there. " +
                 "The viewer's text must contain a real emote.", 62);

            _pages[0].AutoScrollMinSize = new Size(0, _y + 16);

            NewPage(1);   // tab: Round
            // -- ROUND ---------------------------------------------------------
            Section("ROUND");

            _chkHidden = Place(OSWControls.MkCheck("Hide the balloon between rounds"), 24);
            Help("The balloon stays hidden, and emotes do nothing, until a round is started with the 'Blow Up Emote - Start Round' action (attach a reward, command, bits... trigger; its text is the emote). After a pop it hides and stays off until the next start. If viewers start rounds, set 'Chat can change it' above.", 72);

            // -- WHICH EMOTES CHAT MAY PICK -------------------------------------
            Section("EMOTES CHAT MAY PICK");

            Place(OSWControls.MkFieldLbl("Allowed emotes (blank = any real emote)"), 18);
            _txtAllowed = OSWControls.MkInput();
            _txtAllowed.Multiline = true;
            _txtAllowed.ScrollBars = ScrollBars.Vertical;
            Place(_txtAllowed, 56);
            Help("Separate with commas, spaces or new lines. If you list any, viewers can only start a round with these.", 30);

            Gap(6);
            Place(OSWControls.MkFieldLbl("Banned emotes"), 18);
            _txtBanned = OSWControls.MkInput();
            _txtBanned.Multiline = true;
            _txtBanned.ScrollBars = ScrollBars.Vertical;
            Place(_txtBanned, 56);
            Help("Viewers can never pick these (banned beats allowed). You, the streamer, are never blocked by either list.", 30);

            // -- DISPLAY -------------------------------------------------------
            Section("DISPLAY");

            _chkProgress = Place(OSWControls.MkCheck("Show progress information (progress bar and percentage)"), 24);

            _pages[1].AutoScrollMinSize = new Size(0, _y + 16);

            NewPage(2);   // tab: Growth
            // -- GROWTH --------------------------------------------------------
            Section("GROWTH");

            Place(OSWControls.MkFieldLbl("How growth is counted"), 18);
            _cboCount = Place(OSWControls.MkCombo(new object[]
            {
                "Per emote - every matching emote in a message counts",
                "Per chat message - a message counts once, however many emotes it has"
            }), 26);

            Gap(6);
            _chkRandom = Place(OSWControls.MkCheck("Random amount per entry (unpredictable pops)"), 24);

            Gap(4);
            Place(OSWControls.MkFieldLbl("Each valid entry grows the balloon by (%)"), 18);
            _numGrowth = Place(OSWControls.MkSpinner(0.01m, 100m, 2m, 2), 26);
            _numGrowth.Width = 120;
            _numGrowth.Increment = 0.5m;

            Gap(4);
            Place(OSWControls.MkFieldLbl("Random: smallest amount one entry can add (%)"), 18);
            _numRndMin = Place(OSWControls.MkSpinner(0.01m, 100m, 1m, 2), 26);
            _numRndMin.Width = 120;
            _numRndMin.Increment = 0.5m;
            Place(OSWControls.MkFieldLbl("Random: largest amount one entry can add (%)"), 18);
            _numRndMax = Place(OSWControls.MkSpinner(0.01m, 100m, 5m, 2), 26);
            _numRndMax.Width = 120;
            _numRndMax.Increment = 0.5m;
            Help("Every entry rolls its own number in this range. A message worth 3 entries rolls 3 times.", 18);

            Gap(6);
            Place(OSWControls.MkFieldLbl("Limit counted per message (0 = no limit)"), 18);
            _numMax = Place(OSWControls.MkSpinner(0m, 1000m, 5m, 0), 26);
            _numMax.Width = 120;
            Help("Only used when every emote counts. Stops one person typing the emote 50 times to pop it alone.", 18);

            // -- DEFLATION -----------------------------------------------------
            Section("DEFLATION");

            Place(OSWControls.MkFieldLbl("Deflates by (% per second, 0 = off)"), 18);
            _numDeflate = Place(OSWControls.MkSpinner(0m, 100m, 0m, 2), 26);
            _numDeflate.Width = 120;
            _numDeflate.Increment = 0.05m;
            Help("The balloon slowly loses air while it is on screen, so chat has to keep up to pop it. " +
                 "Hundredths work: 0.25 loses a quarter of a percent every second.", 32);

            _pages[2].AutoScrollMinSize = new Size(0, _y + 16);

            ShowPage(0);


            // -- footer --------------------------------------------------------
            _lblPreview = new Label
            {
                Left = PadX,
                Top = 10,
                Width = FormW - PadX * 2,
                Height = 56,
                Font = OSWTheme.Fn,
                ForeColor = OSWTheme.CPrev,
                BackColor = Color.Transparent,
                AutoSize = false
            };
            _footer.Controls.Add(_lblPreview);

            _lblError = new Label
            {
                Left = PadX,
                Top = 70,
                Width = FormW - PadX * 2,
                Height = 20,
                Font = OSWTheme.FnB,
                ForeColor = OSWTheme.CError,
                BackColor = Color.Transparent,
                AutoSize = false
            };
            _footer.Controls.Add(_lblError);

            _btnCancel = OSWControls.MkBtn("Cancel", OSWTheme.CPnl);
            _btnCancel.SetBounds(FormW - PadX - 100, 96, 100, 28);
            _btnCancel.DialogResult = DialogResult.Cancel;
            _footer.Controls.Add(_btnCancel);

            _btnSave = OSWControls.MkBtn("Save", OSWTheme.CAcc);
            _btnSave.SetBounds(FormW - PadX - 100 - 10 - 120, 96, 120, 28);
            _btnSave.Click += OnSaveClick;
            _footer.Controls.Add(_btnSave);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;

            // Update the preview and clear any error whenever anything changes.
            _txtEmote.TextChanged += OnAnyChange;
            _txtImage.TextChanged += OnAnyChange;
            _txtAllowed.TextChanged += OnAnyChange;
            _txtBanned.TextChanged += OnAnyChange;
            _rbStreamer.CheckedChanged += OnAnyChange;
            _rbChat.CheckedChanged += OnAnyChange;
            _cboCount.SelectedIndexChanged += OnAnyChange;
            _numGrowth.ValueChanged += OnAnyChange;
            _numMax.ValueChanged += OnAnyChange;
            _chkRandom.CheckedChanged += OnAnyChange;
            _numRndMin.ValueChanged += OnAnyChange;
            _numRndMax.ValueChanged += OnAnyChange;
            _numDeflate.ValueChanged += OnAnyChange;
            _chkProgress.CheckedChanged += OnAnyChange;
            _chkHidden.CheckedChanged += OnAnyChange;
        }

        // ---------------------------------------------------------------------
        // Config -> controls, and controls -> config
        // ---------------------------------------------------------------------

        private static decimal Clamp(decimal v, decimal lo, decimal hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void LoadFromConfig()
        {
            _txtEmote.Text = _initialEmote;
            _txtImage.Text = _initialImage;
            _txtAllowed.Text = string.Join(", ", _cfg.AllowedEmotes ?? new List<string>());
            _txtBanned.Text = string.Join(", ", _cfg.BannedEmotes ?? new List<string>());

            _rbStreamer.Checked = _cfg.EmoteControl == EmoteControlMode.StreamerOnly;
            _rbChat.Checked = _cfg.EmoteControl == EmoteControlMode.ChatTriggered;

            _cboCount.SelectedIndex = _cfg.CountMode == GrowthCountMode.PerMessage ? 1 : 0;

            _numGrowth.Value = Clamp((decimal)_cfg.GrowthPerUse, _numGrowth.Minimum, _numGrowth.Maximum);
            _numMax.Value = Clamp(_cfg.MaxMatchesPerMessage, _numMax.Minimum, _numMax.Maximum);
            _chkRandom.Checked = _cfg.RandomGrowth;
            _numRndMin.Value = Clamp((decimal)_cfg.GrowthMinPercent, _numRndMin.Minimum, _numRndMin.Maximum);
            _numRndMax.Value = Clamp((decimal)_cfg.GrowthMaxPercent, _numRndMax.Minimum, _numRndMax.Maximum);
            _numDeflate.Value = Clamp((decimal)_cfg.DeflatePerSecond, _numDeflate.Minimum, _numDeflate.Maximum);

            _chkProgress.Checked = _cfg.ShowProgress;
            _chkHidden.Checked = _cfg.StartHidden;

            SyncEnabledStates();
        }

        // The per-message limit only means something when every emote counts.
        private void SyncEnabledStates()
        {
            _numMax.Enabled = _cboCount.SelectedIndex == 0;
            _numGrowth.Enabled = !_chkRandom.Checked;     // random on: the fixed amount is not used
            _numRndMin.Enabled = _chkRandom.Checked;
            _numRndMax.Enabled = _chkRandom.Checked;
        }

        // Copy the simple settings from the controls into the config. The emote
        // name and picture are handled separately (see OnSaveClick).
        private void ReadControlsInto(BueTargetConfig c)
        {
            c.EmoteControl = _rbChat.Checked ? EmoteControlMode.ChatTriggered : EmoteControlMode.StreamerOnly;
            c.CountMode = _cboCount.SelectedIndex == 1 ? GrowthCountMode.PerMessage : GrowthCountMode.PerEmote;
            c.GrowthPerUse = (double)_numGrowth.Value;
            c.MaxMatchesPerMessage = (int)_numMax.Value;
            c.RandomGrowth = _chkRandom.Checked;
            c.GrowthMinPercent = (double)_numRndMin.Value;
            c.GrowthMaxPercent = (double)_numRndMax.Value;
            c.DeflatePerSecond = (double)_numDeflate.Value;
            c.ShowProgress = _chkProgress.Checked;
            c.StartHidden = _chkHidden.Checked;
            c.AllowedEmotes = BlowUpEmoteEngine.ParseEmoteList(_txtAllowed.Text);
            c.BannedEmotes = BlowUpEmoteEngine.ParseEmoteList(_txtBanned.Text);
        }

        private void OnAnyChange(object sender, EventArgs e)
        {
            if (_loading) return;
            SyncEnabledStates();
            _lblError.Text = "";
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            // Build the preview from a throwaway copy so the real config is only
            // changed when Save is pressed.
            var temp = new BueTargetConfig();
            ReadControlsInto(temp);
            _lblPreview.Text = BlowUpEmoteEngine.DescribeSettings(temp);
        }

        // ---------------------------------------------------------------------
        // Save
        // ---------------------------------------------------------------------

        private void ShowError(string text)
        {
            _lblError.Text = text;
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            string emote = _txtEmote.Text.Trim();
            string image = _txtImage.Text.Trim();

            if (emote.IndexOfAny(new[] { ' ', '\t' }) >= 0)
            {
                ShowError("An emote name can't contain spaces.");
                return;
            }

            if (image.Length > 0 && !BlowUpEmoteEngine.IsHttpsUrl(image))
            {
                ShowError("The picture link must start with https://");
                return;
            }

            if (_chkRandom.Checked && _numRndMin.Value > _numRndMax.Value)
            {
                ShowError("The smallest random amount can't be larger than the largest.");
                return;
            }

            // With rounds OFF the balloon counts this one emote, so it is required.
            // With rounds ON it is only a default, so blank is fine.
            if (emote.Length == 0 && !_chkHidden.Checked && _cfg.Mode == EmoteSourceMode.SpecificList)
            {
                ShowError("Enter the emote name (it is required while 'Hide the balloon between rounds' is off).");
                return;
            }

            ReadControlsInto(_cfg);

            // Only touch the emote settings if the user actually changed them.
            bool changed = !string.Equals(emote, _initialEmote, StringComparison.OrdinalIgnoreCase)
                           || !string.Equals(image, _initialImage, StringComparison.Ordinal);
            if (changed)   // includes clearing the box: a blank default is valid
                BlowUpEmoteEngine.ApplyEmoteSelection(_cfg, emote, image);

            Result = _cfg;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
