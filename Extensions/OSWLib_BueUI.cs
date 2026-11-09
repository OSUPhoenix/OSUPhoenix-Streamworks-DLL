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
// LOOK (v2.8): the window now follows the same theme as the other OSWTools windows
//   (Achievement System): logo + title header, a real TabControl whose pages carry the
//   OSW watermark, a gradient footer with the OSW links and a "Save & Close" button.
//   All colours/fonts come from OSWTheme, all controls from OSWControls, so a
//   theme change in the DLL reaches this window too.
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

        private const int FormW = 680;
        private const int PadX = 22;
        private const int ContentW = FormW - PadX * 2 - 24;   // 24 = room for a scrollbar

        // The footer links point here. TODO: replace with the Blow Up Emote guide page once it exists.
        private const string DocsUrl = "https://osuphoenix.notion.site/OSUPhoenix-s-Achievement-System-1fee67e6e2b280c8b150e7463a618c60";

        private readonly BueTargetConfig _cfg;
        private readonly string _configPath;
        private readonly string _initialEmote;
        private readonly string _initialImage;
        private bool _loading = true;
        private int _y;

        private TabControl _tabs;
        private TabPage _cur;                   // the tab page Place() writes into right now
        private readonly TabPage[] _pages = new TabPage[3];

        private CheckBox _chk7tv;
        private CheckBox _chkBttv;
        private CheckBox _chkFfz;
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
        private CheckBox _chkAutoEnd;
        private CheckBox _chkAnnounce;
        private Button _btnBarColor;
        private string _barColorHex = "#ff7a1a";
        private NumericUpDown _numAutoEnd;
        private NumericUpDown _numAutoWarn;

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
            ClientSize = new Size(FormW, 780);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = OSWTheme.CBg;
            ForeColor = OSWTheme.CTxt;
            Font = OSWTheme.Fn;
            DoubleBuffered = true;   // less flicker when the watermark repaints

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
        // Tabs. A real TabControl, like the Achievement System window. Each page
        // scrolls by itself (TabPage.AutoScroll) and carries the OSW watermark.
        // ---------------------------------------------------------------------
        private static readonly string[] TabNames = { "Emote", "Round", "Growth" };

        // Start writing into tab page 'index'. Place() / Section() now add to it.
        private void NewPage(int index)
        {
            var pg = _pages[index];
            pg.AutoScroll = true;
            _cur = pg;
            _y = 12;
        }

        // Tell the page how tall its content is so the scrollbar appears when needed.
        private void FinishPage(int index)
        {
            _pages[index].AutoScrollMinSize = new Size(0, _y + 16);
        }

        private void BuildUi()
        {
            // -- the three tab pages ---------------------------------------------
            _tabs = new TabControl { Dock = DockStyle.Fill, Font = OSWTheme.FnB, BackColor = OSWTheme.CBg };
            for (int i = 0; i < TabNames.Length; i++)
            {
                var pg = new TabPage(TabNames[i]) { BackColor = OSWTheme.CBg, Padding = new Padding(0) };
                pg.Paint += OSWTheme.PaintWatermark;   // the faint OSW logo behind the settings
                _tabs.TabPages.Add(pg);
                _pages[i] = pg;
            }

            // -- status strip: live preview + error, just above the footer ----------
            var strip = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = OSWTheme.CPnl };
            _lblPreview = new Label
            {
                Left = PadX,
                Top = 8,
                Width = FormW - PadX * 2,
                Height = 56,
                Font = OSWTheme.Fn,
                ForeColor = OSWTheme.CPrev,
                BackColor = Color.Transparent,
                AutoSize = false
            };
            _lblError = new Label
            {
                Left = PadX,
                Top = 66,
                Width = FormW - PadX * 2,
                Height = 20,
                Font = OSWTheme.FnB,
                ForeColor = OSWTheme.CError,
                BackColor = Color.Transparent,
                AutoSize = false
            };
            strip.Controls.Add(_lblPreview);
            strip.Controls.Add(_lblError);

            // -- footer: OSW links on the left, Cancel + Save & Close on the right ----
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = OSWTheme.CPnl };
            footer.Paint += OSWTheme.PaintFooterGradient;
            var links = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Location = new Point(10, 16)
            };
            OSWControls.AddOswFooterLinks(links, DocsUrl);
            footer.Controls.Add(links);

            _btnSave = OSWControls.MkBtn("Save & Close", OSWTheme.CAcc);
            _btnSave.Size = new Size(140, 32);
            _btnSave.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _btnSave.Click += OnSaveClick;
            _btnCancel = OSWControls.MkBtn("Cancel", OSWTheme.CIn);
            _btnCancel.Size = new Size(90, 32);
            _btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _btnCancel.DialogResult = DialogResult.Cancel;
            footer.Resize += (s, e) =>
            {
                int top = (footer.Height - _btnSave.Height) / 2;
                _btnSave.Location = new Point(footer.Width - _btnSave.Width - 14, top);
                _btnCancel.Location = new Point(_btnSave.Left - _btnCancel.Width - 8, top);
            };
            footer.Controls.Add(_btnSave);
            footer.Controls.Add(_btnCancel);

            // -- header: logo + title ---------------------------------------------
            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = OSWTheme.CPnl };
            header.Paint += OSWTheme.PaintHeaderBg;
            var logo = new PictureBox
            {
                ImageLocation = OSWTheme.LogoUrl,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(52, 52),
                Location = new Point(10, 6),
                BackColor = Color.Transparent
            };
            var title = new Label
            {
                Text = "Blow Up Emote",
                Font = OSWTheme.FnT,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            header.Controls.Add(title);
            header.Controls.Add(logo);

            // Docking order: the Fill control first, then the edges. Whatever is added
            // last is placed first, so the footer ends up lowest and the header on top.
            Controls.Add(_tabs);
            Controls.Add(strip);
            Controls.Add(footer);
            Controls.Add(header);

            NewPage(0);   // tab: Emote

            // where settings are saved (small, quiet line at the top of the first tab)
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
                _y += 22;
            }

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

            // -- EMOTE SERVICES -------------------------------------------------
            Section("WHICH EMOTES ARE REAL EMOTES");

            Help("Twitch's own emotes (channel + global) are always recognised. Twitch does not know emotes from the " +
                 "services below, so tick the ones your chat uses. BUE downloads the list for YOUR channel plus each " +
                 "service's global emotes, and uses the same list to find the picture.", 62);
            _chk7tv = Place(OSWControls.MkCheck("7TV"), 24);
            _chkBttv = Place(OSWControls.MkCheck("BetterTTV (BTTV)"), 24);
            _chkFfz = Place(OSWControls.MkCheck("FrankerFaceZ (FFZ)"), 24);
            Help("Names must match exactly, capitals included. To check a service, run the 'Blow Up Emote - Refresh Emotes' " +
                 "action: it writes how many emotes each service returned (or why not) to bue_log.txt. " +
                 "A word no enabled service knows is still refused unless you put it in 'Allowed emotes' on the Round tab.", 62);

            FinishPage(0);

            NewPage(1);   // tab: Round
            // -- ROUND ---------------------------------------------------------
            Section("ROUND");

            _chkHidden = Place(OSWControls.MkCheck("Hide the balloon between rounds"), 24);
            Help("The balloon stays hidden, and emotes do nothing, until a round is started with the 'Blow Up Emote - Start Round' action (attach a reward, command, bits... trigger; its text is the emote). After a pop it hides and stays off until the next start. If viewers start rounds, set 'Chat can change it' above.", 72);

            Gap(6);
            _chkAnnounce = Place(OSWControls.MkCheck("Post round messages in chat (started, popped, ended)"), 24);
            Help("Chat is told when a round starts (what to spam), who popped it, or that it ended without a pop. " +
                 "Sent to every platform you have enabled. Replies to one viewer (a rejected entry, a refund) are always sent.", 48);

            Gap(6);
            _chkAutoEnd = Place(OSWControls.MkCheck("End the round if the balloon sits empty"), 24);
            Place(OSWControls.MkFieldLbl("Ends after it has been at 0% for this many seconds"), 18);
            _numAutoEnd = Place(OSWControls.MkSpinner(10m, 3600m, 120m, 0), 26);
            _numAutoEnd.Width = 120;
            _numAutoEnd.Increment = 10m;
            Place(OSWControls.MkFieldLbl("Emote flashes red for the last ___ seconds of that countdown (0 = no warning)"), 18);
            _numAutoWarn = Place(OSWControls.MkSpinner(0m, 3600m, 10m, 0), 26);
            _numAutoWarn.Width = 120;
            Help("The countdown only runs while the balloon is at 0%: from the start of a round until someone adds to it, and again whenever deflation drains it empty. " +
                 "Any entry stops it. When it runs out the round ends like Cancel - no pop, no winner. " +
                 "A balloon above 0% that is not deflating never empties, so it never times out. Needs the 'Blow Up Emote - Expire Check' action.", 62);

            // -- WHICH EMOTES CHAT MAY PICK -------------------------------------
            Section("EMOTES CHAT MAY PICK");

            Place(OSWControls.MkFieldLbl("Allowed emotes (blank = any real emote)"), 18);
            _txtAllowed = OSWControls.MkInput();
            _txtAllowed.Multiline = true;
            _txtAllowed.ScrollBars = ScrollBars.Vertical;
            Place(_txtAllowed, 56);
            Help("Separate with commas, spaces or new lines. If you list any, viewers can only start a round with these. A name listed here is ALSO accepted as an emote even when no service knows it (the picture link then comes from you, or the balloon shows a placeholder).", 62);

            Gap(6);
            Place(OSWControls.MkFieldLbl("Banned emotes"), 18);
            _txtBanned = OSWControls.MkInput();
            _txtBanned.Multiline = true;
            _txtBanned.ScrollBars = ScrollBars.Vertical;
            Place(_txtBanned, 56);
            Help("Viewers can never pick these (banned beats allowed). These lists apply to everyone, you included.", 30);

            // -- DISPLAY -------------------------------------------------------
            Section("DISPLAY");

            _chkProgress = Place(OSWControls.MkCheck("Show progress information (progress bar and percentage)"), 24);

            Gap(4);
            Place(OSWControls.MkFieldLbl("Progress bar color (click to change)"), 18);
            _btnBarColor = Place(new Button
            {
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            }, 28);
            _btnBarColor.Width = 160;
            _btnBarColor.FlatAppearance.BorderSize = 1;
            _btnBarColor.Click += OnPickBarColor;

            FinishPage(1);

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

            FinishPage(2);

            // -- wiring ----------------------------------------------------------
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
            _chkAutoEnd.CheckedChanged += OnAnyChange;
            _chkAnnounce.CheckedChanged += OnAnyChange;
            _numAutoEnd.ValueChanged += OnAnyChange;
            _numAutoWarn.ValueChanged += OnAnyChange;
            _chk7tv.CheckedChanged += OnAnyChange;
            _chkBttv.CheckedChanged += OnAnyChange;
            _chkFfz.CheckedChanged += OnAnyChange;
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
            _chkAutoEnd.Checked = _cfg.AutoEndEnabled;
            _chkAnnounce.Checked = _cfg.AnnounceInChat;
            _chk7tv.Checked = _cfg.Use7TV;
            _chkBttv.Checked = _cfg.UseBTTV;
            _chkFfz.Checked = _cfg.UseFFZ;
            SetBarColor(BlowUpEmoteEngine.NormalizeHexColor(_cfg.ProgressBarColor, "#ff7a1a"));
            _numAutoEnd.Value = Clamp((decimal)_cfg.AutoEndSeconds, _numAutoEnd.Minimum, _numAutoEnd.Maximum);
            _numAutoWarn.Value = Clamp((decimal)_cfg.AutoEndWarnSeconds, _numAutoWarn.Minimum, _numAutoWarn.Maximum);

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
            c.AutoEndEnabled = _chkAutoEnd.Checked;
            c.AnnounceInChat = _chkAnnounce.Checked;
            c.Use7TV = _chk7tv.Checked;
            c.UseBTTV = _chkBttv.Checked;
            c.UseFFZ = _chkFfz.Checked;
            c.ProgressBarColor = _barColorHex;
            c.AutoEndSeconds = (int)_numAutoEnd.Value;
            c.AutoEndWarnSeconds = (int)_numAutoWarn.Value;
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

        // Colour picker: the button itself shows the colour (and its code).
        private void SetBarColor(string hex)
        {
            _barColorHex = hex;
            Color color = ColorTranslator.FromHtml(hex);
            _btnBarColor.BackColor = color;
            _btnBarColor.ForeColor = color.GetBrightness() > 0.55f ? Color.Black : Color.White;   // readable on any colour
            _btnBarColor.Text = hex.ToUpperInvariant();
        }

        private void OnPickBarColor(object sender, EventArgs e)
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = ColorTranslator.FromHtml(_barColorHex);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Color c = dlg.Color;
                SetBarColor("#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"));
                OnAnyChange(sender, e);
            }
        }

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

            if (_chkAutoEnd.Checked && _numAutoWarn.Value > _numAutoEnd.Value)
            {
                ShowError("The red warning can't be longer than the time before the round ends.");
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
