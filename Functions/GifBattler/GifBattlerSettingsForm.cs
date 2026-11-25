// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerSettingsForm.cs   DLL v1.3.0
//
//  The GIF Battler Settings window.
//
//  HOW TO OPEN IT:
//    Create an action "GIF Battler - Settings" (no trigger needed), add an
//    Execute C# Method sub-action pointing at  OpenSettings  in the
//    GIF Battler script, and run it by hand from Streamer.bot.
//
//  FLOW (same recipe as the Blow Up Emote window):
//    1. The engine loads a private COPY of config.json and hands it here.
//    2. This window edits the copy. Cancel throws it away, so nothing can
//       ever be half-saved.
//    3. Save & Close -> the engine writes config.json and (if ticked)
//       sends a test GIF so you see the result on stream straight away.
//
//  LOOK:
//    Built from OSWTheme (colours / fonts) and OSWControls (buttons,
//    spinners...) exactly like the other OSWTools windows, so a theme
//    change in the DLL reaches this window too.
//
//  THE LIVE PREVIEW (top of the window):
//    A small picture of your screen area with a sample GIF drawn where it
//    would land, using GifBattlerLayout — the very same math the overlay
//    runs. Pick "Tall and skinny" to see the proportion rule at work, and
//    press "Shuffle" to see random placement choose a different spot. For
//    random placement the dashed box shows the area the GIF may land in.
//
//  ADDING A SETTING LATER:
//    1) property + clamp in GifBattlerConfig  2) a control in BuildUi
//    3) one line in LoadFromConfig and one in ReadControlsInto.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using OSWTools.Theme;
using OSWTools.Utilities;

namespace OSWTools.Functions.GifBattler
{
    // ───────────────────────────────────────────────────────────────
    //  The little "where will it land" picture
    // ───────────────────────────────────────────────────────────────
    internal sealed class GifBattlerPreview : Panel
    {
        internal GifBattlerConfig Config;
        internal string Tier = "mod";                 // "mod", "sub" or anything else = viewer
        internal double NatW = 800, NatH = 450;       // the sample GIF's own size
        internal int    Seed = 1;                     // changes on "Shuffle"

        internal GifBattlerPreview()
        {
            DoubleBuffered = true;                    // no flicker while spinners tick
            ResizeRedraw   = true;
            BackColor      = OSWTheme.CBg;
        }

        // The glow colour this GIF would get, or null for "no glow".
        private Color? GlowFor(GifBattlerConfig c)
        {
            switch (c.GlowMode)
            {
                case "off":    return null;
                case "color":  return ColorTranslator.FromHtml(c.GlowColor);
                case "random": return ColorFromHue((Seed * 67) % 360);
                default:
                    if (Tier == "mod") return ColorTranslator.FromHtml("#FFD700");
                    if (Tier == "sub") return ColorTranslator.FromHtml("#9B59B6");
                    return null;
            }
        }

        // A bright colour for a hue 0-359 (full saturation, medium-high brightness).
        private static Color ColorFromHue(int hue)
        {
            double h = (hue % 360) / 60.0;
            double x = 1 - Math.Abs(h % 2 - 1);
            double r = 0, g = 0, b = 0;
            if      (h < 1) { r = 1; g = x; }
            else if (h < 2) { r = x; g = 1; }
            else if (h < 3) { g = 1; b = x; }
            else if (h < 4) { g = x; b = 1; }
            else if (h < 5) { r = x; b = 1; }
            else            { r = 1; b = x; }
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var c = Config;
            if (c == null) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // The area we pretend is the screen: your custom size, or the
            // standard 1920 x 1080 as a stand-in for "whatever OBS has".
            int areaW = c.UseCustomStageSize ? c.StageWidth  : 1920;
            int areaH = c.UseCustomStageSize ? c.StageHeight : 1080;

            const int margin = 6;
            double s = Math.Min((Width - 2.0 * margin) / areaW, (Height - 2.0 * margin) / areaH);
            if (s <= 0) return;
            float ox = (float)((Width  - areaW * s) / 2.0);
            float oy = (float)((Height - areaH * s) / 2.0);

            // the "screen"
            var screen = new RectangleF(ox, oy, (float)(areaW * s), (float)(areaH * s));
            using (var fill = new SolidBrush(Color.FromArgb(8, 8, 8)))
                g.FillRectangle(fill, screen);
            using (var grid = new Pen(Color.FromArgb(40, 255, 255, 255)))
            {
                // thirds, so "top-left / middle / bottom-right" are easy to read
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(grid, ox + screen.Width * i / 3f, oy, ox + screen.Width * i / 3f, oy + screen.Height);
                    g.DrawLine(grid, ox, oy + screen.Height * i / 3f, ox + screen.Width, oy + screen.Height * i / 3f);
                }
            }
            using (var edge = new Pen(OSWTheme.CDivLight))
                g.DrawRectangle(edge, screen.X, screen.Y, screen.Width, screen.Height);
            using (var f = new Font("Segoe UI", 7.5f))
            using (var b = new SolidBrush(OSWTheme.CDim))
                g.DrawString(areaW + " x " + areaH, f, b, ox + 4, oy + 2);

            // size, then position — the same two steps the overlay takes
            int w, h, x, y;
            GifBattlerLayout.Fit(c, Tier, NatW, NatH, areaW, areaH, out w, out h);

            var rng = new Random(Seed);
            Func<int, int, int> rnd = (lo, hi) => rng.Next(lo, hi + 1);
            GifBattlerLayout.Place(c, Tier, w, h, areaW, areaH, rnd, out x, out y);

            int labelH = c.ShowUsername ? GifBattlerLayout.LabelHeight : 0;

            // Random placements: dashed outline of everywhere it MAY land.
            if (c.DisplayMode == "random")
            {
                int x0, y0, x1, y1;
                GifBattlerLayout.Place(c, Tier, w, h, areaW, areaH, (lo, hi) => lo, out x0, out y0);
                GifBattlerLayout.Place(c, Tier, w, h, areaW, areaH, (lo, hi) => hi, out x1, out y1);
                DashedRect(g, ox, oy, s, x0, y0, x1 + w - x0, y1 + h + labelH - y0);
            }
            else if (c.DisplayMode == "random-corner")
            {
                for (int k = 0; k < 4; k++)
                {
                    int kk = k, cx, cy;
                    GifBattlerLayout.Place(c, Tier, w, h, areaW, areaH, (lo, hi) => kk, out cx, out cy);
                    DashedRect(g, ox, oy, s, cx, cy, w, h + labelH);
                }
            }

            // the glow, a few see-through halos growing outward
            Color? glow = GlowFor(c);
            float rx = (float)(ox + x * s), ry = (float)(oy + y * s);
            float rw = (float)(w * s),      rh = (float)(h * s);
            if (glow.HasValue)
            {
                float reach = (float)(GifBattlerLayout.GlowPad(c, Tier) * s);
                for (int i = 4; i >= 1; i--)
                {
                    float grow = reach * i / 4f;
                    using (var halo = new SolidBrush(Color.FromArgb(22, glow.Value)))
                        g.FillRectangle(halo, rx - grow, ry - grow, rw + 2 * grow, rh + 2 * grow);
                }
            }

            // the GIF itself
            using (var body = new LinearGradientBrush(new RectangleF(rx, ry, Math.Max(1f, rw), Math.Max(1f, rh)),
                                                      Color.FromArgb(70, 70, 110), Color.FromArgb(30, 30, 52),
                                                      LinearGradientMode.ForwardDiagonal))
                g.FillRectangle(body, rx, ry, rw, rh);
            if (glow.HasValue && c.BorderWidth > 0)
                using (var ring = new Pen(glow.Value, Math.Max(1f, (float)(c.BorderWidth * s))))
                    g.DrawRectangle(ring, rx, ry, rw, rh);

            using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                g.DrawString(w + " x " + h, f, b, new RectangleF(rx, ry, rw, rh), sf);

            if (c.ShowUsername)
                using (var f = new Font("Segoe UI", 7f, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                    g.DrawString("@viewer", f, b, rx, ry + rh + 1);
        }

        private static void DashedRect(Graphics g, float ox, float oy, double s, int x, int y, int w, int h)
        {
            using (var pen = new Pen(Color.FromArgb(150, OSWTheme.CLnk)) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, (float)(ox + x * s), (float)(oy + y * s), (float)(w * s), (float)(h * s));
        }
    }

    // ───────────────────────────────────────────────────────────────
    //  The window
    // ───────────────────────────────────────────────────────────────
    internal sealed class GifBattlerSettingsForm : Form
    {
        /// <summary>The edited config. Only valid when DialogResult is OK.</summary>
        public GifBattlerConfig Result { get; private set; }

        /// <summary>True when "send a test GIF after saving" was ticked at Save time.</summary>
        public bool ShowTestAfterSave { get; private set; }

        private const int FormW    = 720;
        private const int PadX     = 22;
        private const int ContentW = FormW - PadX * 2 - 24;     // 24 = room for a scrollbar
        private const int NumW     = 110;                       // width of a spinner

        // The footer links point here. TODO: replace with the GIF Battler guide once it exists.
        private const string DocsUrl = "https://osuphoenix.notion.site/OSUPhoenix-s-Achievement-System-1fee67e6e2b280c8b150e7463a618c60";

        // Friendly names for the placement drop-down (value = what config.json stores).
        private static readonly string[][] PlacementChoices =
        {
            new[] { "Random - anywhere on screen",   "random" },
            new[] { "Random corner",                 "random-corner" },
            new[] { "Top left",                      "top-left" },
            new[] { "Top center",                    "top-center" },
            new[] { "Top right",                     "top-right" },
            new[] { "Middle left",                   "middle-left" },
            new[] { "Middle center",                 "middle-center" },
            new[] { "Middle right",                  "middle-right" },
            new[] { "Bottom left",                   "bottom-left" },
            new[] { "Bottom center",                 "bottom-center" },
            new[] { "Bottom right",                  "bottom-right" },
            new[] { "Large and centred (fullscreen)", "fullscreen" }
        };
        private static readonly string[][] GlowChoices =
        {
            new[] { "By viewer role (gold mods, purple subs)", "role" },
            new[] { "Off",                                      "off" },
            new[] { "One colour for every GIF",                 "color" },
            new[] { "A random colour for each GIF",             "random" }
        };
        private static readonly string[][] EntranceChoices =
        {
            new[] { "Automatic (bounce, fade or zoom to suit the spot)", "auto" },
            new[] { "Bounce", "bounce" },
            new[] { "Fade",   "fade" },
            new[] { "Zoom",   "zoom" }
        };

        private readonly GifBattlerConfig _cfg;
        private readonly string _configPath;
        private bool _loading = true;
        private int _y;

        private TabControl _tabs;
        private TabPage _cur;
        private readonly TabPage[] _pages = new TabPage[5];

        // preview
        private GifBattlerPreview _preview;
        private ComboBox _cboShape, _cboRole;
        private Button _btnShuffle;

        // Screen & Size
        private CheckBox _chkCustomStage;
        private NumericUpDown _numStageW, _numStageH, _numMaxW, _numMaxH;
        private CheckBox _chkEnlarge, _chkByRole;
        private NumericUpDown _numScaleMod, _numScaleSub, _numScaleViewer;

        // Placement
        private ComboBox _cboPlacement;
        private NumericUpDown _numEdge, _numOffX, _numOffY;

        // Look
        private ComboBox _cboGlow, _cboEntrance;
        private Button _btnGlowColor;
        private string _glowHex = "#DC2828";
        private NumericUpDown _numGlowSize, _numBorder, _numRadius, _numOpacity;
        private CheckBox _chkUsername;

        // Time & Rules
        private NumericUpDown _numDuration, _numGlobalCd, _numUserCd;
        private CheckBox _chkStay, _chkSubsOnly, _chkIgnoreBots, _chkNative, _chkNativeBattle, _chkAuto;
        private NumericUpDown _numWindow, _numVoting, _numChallenge;

        // Search & Approval  (★ STEP 5)
        private TextBox _txtKey, _txtBlocked;
        private CheckBox _chkShowKey, _chkApproval, _chkRefund;
        private ComboBox _cboRating;
        private NumericUpDown _numPick, _numApprovalSecs;
        private Button _btnTestKey;
        private Label _lblKeyResult;
        private static readonly string[][] RatingChoices =
        {
            new[]{ "G - everyone",                 "g"     },
            new[]{ "PG - mild (recommended)",      "pg"    },
            new[]{ "PG-13 - some adult humor",     "pg-13" },
            new[]{ "R - anything Giphy allows",    "r"     },
        };

        private Label _lblSummary, _lblError;
        private CheckBox _chkTest;
        private Button _btnSave, _btnCancel;

        public GifBattlerSettingsForm(GifBattlerConfig config, string configPath)
        {
            _cfg = config ?? new GifBattlerConfig();
            _configPath = configPath ?? "";

            Text = "GIF Battler - Settings";
            ClientSize = new Size(FormW, 830);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = OSWTheme.CBg;
            ForeColor = OSWTheme.CTxt;
            Font = OSWTheme.Fn;
            DoubleBuffered = true;

            BuildUi();
            LoadFromConfig();

            _loading = false;
            RefreshAll();
        }

        // ── layout helpers (same idea as the Blow Up Emote window) ──────
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

        // A label above a spinner, spinner kept narrow.
        private NumericUpDown Num(string label, decimal min, decimal max)
        {
            Place(OSWControls.MkFieldLbl(label), 18);
            var n = Place(OSWControls.MkSpinner(min, max, min), 26);
            n.Width = NumW;
            n.ValueChanged += OnAnyChange;
            Gap(4);
            return n;
        }

        private CheckBox Check(string text)
        {
            var c = Place(OSWControls.MkCheck(text), 24);
            c.CheckedChanged += OnAnyChange;
            return c;
        }

        private ComboBox Combo(string label, string[][] choices)
        {
            Place(OSWControls.MkFieldLbl(label), 18);
            var names = new object[choices.Length];
            for (int i = 0; i < choices.Length; i++) names[i] = choices[i][0];
            var c = Place(OSWControls.MkCombo(names), 26);
            c.Width = 360;
            c.SelectedIndexChanged += OnAnyChange;
            Gap(4);
            return c;
        }

        private static readonly string[] TabNames = { "Screen and Size", "Placement", "Look", "Time and Rules", "Search and Approval" };

        private void NewPage(int index)
        {
            var pg = _pages[index];
            pg.AutoScroll = true;
            _cur = pg;
            _y = 8;
        }

        private void FinishPage(int index)
        {
            _pages[index].AutoScrollMinSize = new Size(0, _y + 16);
        }

        // ── building the window ─────────────────────────────────────────
        private void BuildUi()
        {
            // tabs
            _tabs = new TabControl { Dock = DockStyle.Fill, Font = OSWTheme.FnB, BackColor = OSWTheme.CBg };
            for (int i = 0; i < TabNames.Length; i++)
            {
                var pg = new TabPage(TabNames[i]) { BackColor = OSWTheme.CBg, Padding = new Padding(0) };
                pg.Paint += OSWTheme.PaintWatermark;
                _tabs.TabPages.Add(pg);
                _pages[i] = pg;
            }

            // preview host: a row of pickers above the picture
            var previewHost = new Panel { Dock = DockStyle.Top, Height = 232, BackColor = OSWTheme.CBg };
            var pickers = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = OSWTheme.CBg };
            pickers.Controls.Add(new Label
            {
                Text = "PREVIEW  -  try it with:",
                Font = OSWTheme.FnSec, ForeColor = OSWTheme.CLnk, BackColor = Color.Transparent,
                AutoSize = false, Left = PadX, Top = 8, Width = 170, Height = 20
            });
            _cboShape = OSWControls.MkCombo(new object[] { "Wide GIF", "Tall and skinny GIF", "Square GIF" });
            _cboShape.SetBounds(PadX + 175, 4, 150, 26);
            _cboShape.SelectedIndexChanged += OnAnyChange;
            _cboRole = OSWControls.MkCombo(new object[] { "a mod", "a subscriber", "a viewer" });
            _cboRole.SetBounds(PadX + 335, 4, 120, 26);
            _cboRole.SelectedIndexChanged += OnAnyChange;
            _btnShuffle = OSWControls.MkBtn("Shuffle", OSWTheme.CIn);
            _btnShuffle.SetBounds(PadX + 465, 4, 90, 26);
            _btnShuffle.Click += (s, e) => { _preview.Seed++; RefreshAll(); };
            pickers.Controls.Add(_cboShape);
            pickers.Controls.Add(_cboRole);
            pickers.Controls.Add(_btnShuffle);
            _preview = new GifBattlerPreview { Dock = DockStyle.Fill };
            var previewFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(PadX, 0, PadX, 6), BackColor = OSWTheme.CBg };
            previewFrame.Controls.Add(_preview);
            previewHost.Controls.Add(previewFrame);
            previewHost.Controls.Add(pickers);

            // status strip: plain-English summary + error + test tickbox
            var strip = new Panel { Dock = DockStyle.Bottom, Height = 124, BackColor = OSWTheme.CPnl };
            _lblSummary = new Label
            {
                Left = PadX, Top = 6, Width = FormW - PadX * 2, Height = 78,
                Font = OSWTheme.Fn, ForeColor = OSWTheme.CPrev, BackColor = Color.Transparent, AutoSize = false
            };
            _lblError = new Label
            {
                Left = PadX, Top = 84, Width = FormW - PadX * 2, Height = 16,
                Font = OSWTheme.FnB, ForeColor = OSWTheme.CError, BackColor = Color.Transparent, AutoSize = false
            };
            _chkTest = OSWControls.MkCheck("Send a test GIF to the overlay after saving", true);
            _chkTest.SetBounds(PadX, 100, 420, 22);
            strip.Controls.Add(_lblSummary);
            strip.Controls.Add(_lblError);
            strip.Controls.Add(_chkTest);

            // footer
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = OSWTheme.CPnl };
            footer.Paint += OSWTheme.PaintFooterGradient;
            var links = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = Color.Transparent,
                Anchor = AnchorStyles.Left | AnchorStyles.Top, Location = new Point(10, 16)
            };
            OSWControls.AddOswFooterLinks(links, DocsUrl);
            footer.Controls.Add(links);
            _btnSave = OSWControls.MkBtn("Save & Close", OSWTheme.CAcc);
            _btnSave.UseMnemonic = false;            // show "Save & Close" literally (no underlined hotkey)
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

            // header
            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = OSWTheme.CPnl };
            header.Paint += OSWTheme.PaintHeaderBg;
            header.Controls.Add(new Label
            {
                Text = "GIF Battler", Font = OSWTheme.FnT, ForeColor = OSWTheme.CTxt, BackColor = Color.Transparent,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
            });
            header.Controls.Add(new PictureBox
            {
                ImageLocation = OSWTheme.LogoUrl, SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(52, 52), Location = new Point(10, 6), BackColor = Color.Transparent
            });

            // Fill first, then the edges: the last one added docks outermost.
            Controls.Add(_tabs);
            Controls.Add(previewHost);
            Controls.Add(strip);
            Controls.Add(footer);
            Controls.Add(header);

            BuildScreenAndSizeTab();
            BuildPlacementTab();
            BuildLookTab();
            BuildRulesTab();
            BuildSearchTab();
        }

        private void BuildScreenAndSizeTab()
        {
            NewPage(0);

            var sub = new Label
            {
                Text = "Saved to: " + _configPath, Font = OSWTheme.FnSm, ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent, AutoSize = false, AutoEllipsis = true
            };
            Place(sub, 18);
            string folder = "";
            try { folder = Path.GetDirectoryName(_configPath) ?? ""; } catch (Exception) { folder = ""; }
            if (folder.Length > 0)
            {
                var link = OSWControls.MkLink("Open settings folder", folder);
                link.Left = PadX; link.Top = _y;
                _cur.Controls.Add(link);
                _y += 22;
            }

            Section("SCREEN AREA");
            _chkCustomStage = Check("Use a custom screen size (otherwise the overlay uses your OBS browser source as it is)");
            Help("Bigger or smaller than 1920 x 1080 both work. GIFs are laid out inside this area, then scaled to fit your "
                 + "browser source - so nothing lands off-screen. For the best fit, give the OBS browser source the same shape "
                 + "(aspect ratio) as this area.", 44);
            _numStageW = Num("Area width (pixels)", GifBattlerConfig.Limits.StageMin, GifBattlerConfig.Limits.StageMax);
            _numStageH = Num("Area height (pixels)", GifBattlerConfig.Limits.StageMin, GifBattlerConfig.Limits.StageMax);

            Section("GIF SIZE");
            Help("Every GIF is shrunk (or enlarged) to fit INSIDE this box with its proportions kept. A tall skinny GIF reaches "
                 + "the height limit first, so its height is capped and its width follows - it is never stretched.", 44);
            _numMaxW = Num("Biggest GIF width (pixels)", GifBattlerConfig.Limits.MaxGifMin, GifBattlerConfig.Limits.MaxGifMax);
            _numMaxH = Num("Biggest GIF height (pixels)", GifBattlerConfig.Limits.MaxGifMin, GifBattlerConfig.Limits.MaxGifMax);
            _chkEnlarge = Check("Enlarge small GIFs to fill the box (off = small GIFs stay their own size)");
            Gap(6);
            _chkByRole = Check("Make the box smaller for subscribers and everyone else");
            Help("Percent of the box above. Mods and the broadcaster get the first number.", 18);
            _numScaleMod    = Num("Mods and broadcaster (%)", GifBattlerConfig.Limits.PercentMin, GifBattlerConfig.Limits.PercentMax);
            _numScaleSub    = Num("Subscribers (%)",          GifBattlerConfig.Limits.PercentMin, GifBattlerConfig.Limits.PercentMax);
            _numScaleViewer = Num("Everyone else (%)",        GifBattlerConfig.Limits.PercentMin, GifBattlerConfig.Limits.PercentMax);
            FinishPage(0);
        }

        private void BuildPlacementTab()
        {
            NewPage(1);
            Section("WHERE GIFS APPEAR");
            _cboPlacement = Combo("Placement", PlacementChoices);
            Help("Random picks a new spot each time, always keeping the whole GIF (glow and name included) inside the screen "
                 + "area. The other choices always use the same spot.", 32);

            Section("RANDOM PLACEMENT");
            _numEdge = Num("Stay at least this far from every screen edge (pixels)", GifBattlerConfig.Limits.PadMin, GifBattlerConfig.Limits.PadMax);
            Help("Also used by 'Random corner'.", 18);

            Section("FIXED SPOTS - CORNERS, SIDES AND CENTRE");
            Help("On a side or corner these are the distance IN from that edge. On a centre line they nudge the GIF instead "
                 + "(+ moves right / down, - moves left / up).", 32);
            _numOffX = Num("Across offset (X, pixels)", GifBattlerConfig.Limits.OffsetMin, GifBattlerConfig.Limits.OffsetMax);
            _numOffY = Num("Down offset (Y, pixels)",   GifBattlerConfig.Limits.OffsetMin, GifBattlerConfig.Limits.OffsetMax);
            FinishPage(1);
        }

        private void BuildLookTab()
        {
            NewPage(2);
            Section("GLOW");
            _cboGlow = Combo("Glow colour", GlowChoices);
            Place(OSWControls.MkFieldLbl("Colour for 'One colour' (click to change)"), 18);
            _btnGlowColor = Place(new Button { FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleCenter }, 28);
            _btnGlowColor.Width = 160;
            _btnGlowColor.FlatAppearance.BorderSize = 1;
            _btnGlowColor.Click += OnPickGlowColor;
            Gap(8);
            _numGlowSize = Num("Soft glow size (pixels, 0 = none)",     0, GifBattlerConfig.Limits.GlowMax);
            _numBorder   = Num("Solid ring thickness (pixels, 0 = none)", 0, GifBattlerConfig.Limits.BorderMax);

            Section("THE GIF");
            _numRadius  = Num("Rounded corners (pixels)", 0, GifBattlerConfig.Limits.RadiusMax);
            _numOpacity = Num("Opacity (%, lower = more see-through)", GifBattlerConfig.Limits.OpacityMin, GifBattlerConfig.Limits.OpacityMax);
            _chkUsername = Check("Show the viewer's @name under the GIF");
            Gap(6);
            _cboEntrance = Combo("How the GIF arrives", EntranceChoices);
            FinishPage(2);
        }

        private void BuildRulesTab()
        {
            NewPage(3);
            Section("TIME ON SCREEN");
            _chkStay = Check("Keep the GIF up until I clear it (your clear-GIF action, or the Clear button on the overlay)");
            Help("The next GIF simply replaces it - the overlay shows one at a time.", 18);
            _numDuration = Num("Otherwise show each GIF for (seconds)", GifBattlerConfig.Limits.SecondsMin, GifBattlerConfig.Limits.SecondsMax);

            Section("COOLDOWNS");
            _numGlobalCd = Num("Between ANY two GIFs (seconds, 0 = off)", 0, GifBattlerConfig.Limits.SecondsMax);
            _numUserCd   = Num("Before the same viewer can post again (seconds)", 0, GifBattlerConfig.Limits.SecondsMax);

            Section("WHO CAN PLAY");
            _chkSubsOnly   = Check("Only subscribers, VIPs and mods can post GIFs");
            _chkIgnoreBots = Check("Ignore accounts listed in Bot Eliminator");

            Section("TWITCH CHAT GIFS (Tier 2 and 3 GIF keyboard)");
            _chkNative       = Check("Show GIFs sent with Twitch's built-in GIF keyboard");
            _chkNativeBattle = Check("Let those GIFs start, join and answer battles like a pasted link");

            Section("BATTLES");
            _chkAuto = Check("Two GIFs close together start a battle automatically");
            _numWindow    = Num("Time for a second GIF to join (seconds)", 5, GifBattlerConfig.Limits.SecondsMax);
            _numVoting    = Num("Voting time (seconds)",                   10, 1800);
            _numChallenge = Num("Time to answer a challenge (seconds)",    10, GifBattlerConfig.Limits.SecondsMax);
            Help("Your Streamer.bot timers 'GIF - Battle Window Reset' and 'GIF - Challenge Expiry' should use the same "
                 + "numbers as the join and challenge times above.", 32);
            FinishPage(3);
        }

        // ★ STEP 5 — the optional !gif command and channel points reward.
        private void BuildSearchTab()
        {
            NewPage(4);
            Section("GIPHY SEARCH (optional)");
            Help("Lets viewers type words and get a GIF: with the !gif command and/or a channel points reward. "
                 + "To turn either off, just disable its Streamer.bot action. Search needs your own free Giphy key.", 48);

            Place(OSWControls.MkFieldLbl("Your Giphy API key"), 18);
            _txtKey = Place(OSWControls.MkInput(), 26);
            _txtKey.UseSystemPasswordChar = true;
            _txtKey.TextChanged += OnAnyChange;
            Gap(4);
            _chkShowKey = Check("Show the key");
            _chkShowKey.CheckedChanged += (s, e) => _txtKey.UseSystemPasswordChar = !_chkShowKey.Checked;

            _btnTestKey = Place(OSWControls.MkBtn("Test the key", OSWTheme.CAcc), 30);
            _btnTestKey.Width = 160;
            _btnTestKey.Click += OnTestKey;
            _lblKeyResult = Help("", 36);
            Place(OSWControls.MkLink("Get a free key at developers.giphy.com", "https://developers.giphy.com/dashboard/"), 20);

            Section("WHAT CAN BE FOUND");
            _cboRating = Combo("Content rating (Giphy filters this for you)", RatingChoices);
            _numPick = Num("Pick randomly from the top matches (1 = always the best match)",
                           GifBattlerConfig.Limits.PickMin, GifBattlerConfig.Limits.PickMax);
            Place(OSWControls.MkFieldLbl("Words that cannot be searched (one per line)"), 18);
            _txtBlocked = Place(OSWControls.MkMultilineInput(80), 80);
            _txtBlocked.TextChanged += OnAnyChange;
            Gap(4);

            Section("APPROVE BEFORE IT SHOWS (optional)");
            _chkApproval = Check("Ask me first: a small window opens on top with the GIF, Approve and Deny");
            Help("The window closes as soon as you choose. If you do nothing it counts as Deny.", 18);
            _numApprovalSecs = Num("Time to choose (seconds)", GifBattlerConfig.Limits.ApprovalMin, GifBattlerConfig.Limits.ApprovalMax);

            Section("CHANNEL POINTS REWARD");
            _chkRefund = Check("Refund the points when no GIF ends up on screen");
            Help("Works for rewards created inside Streamer.bot only. See the script header for setup.", 34);
            FinishPage(4);
        }

        // Tries a real search with the typed key so a typo shows up now, not mid-stream.
        private void OnTestKey(object sender, EventArgs e)
        {
            var probe = new GifBattlerConfig
            {
                GiphyApiKey = GifBattlerConfig.CleanKey(_txtKey.Text),
                GiphyRating = "g",
                GiphyPickFromTop = 1
            };

            _btnTestKey.Enabled = false;
            _lblKeyResult.ForeColor = OSWTheme.CDim;
            _lblKeyResult.Text = "Testing...";
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            try
            {
                SearchOutcome r = GifBattlerSearch.Find(probe, "hello");
                if (r.Status == SearchStatus.Ok)
                {
                    _lblKeyResult.ForeColor = Color.FromArgb(80, 200, 120);
                    _lblKeyResult.Text = "The key works.";
                }
                else
                {
                    _lblKeyResult.ForeColor = Color.FromArgb(240, 110, 110);
                    _lblKeyResult.Text = r.Status == SearchStatus.NoKey ? "Paste your key first."
                                       : r.Status == SearchStatus.Error ? (r.Detail ?? "Giphy could not be reached.")
                                       : "Giphy answered, but found nothing for the test word.";
                }
            }
            finally
            {
                Cursor = Cursors.Default;
                _btnTestKey.Enabled = true;
            }
        }

        // ── config <-> controls ─────────────────────────────────────────
        private static decimal Clamp(decimal v, decimal lo, decimal hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static void SetNum(NumericUpDown n, int v) { n.Value = Clamp(v, n.Minimum, n.Maximum); }

        private static int IndexOf(string[][] choices, string value)
        {
            for (int i = 0; i < choices.Length; i++)
                if (choices[i][1] == value) return i;
            return 0;
        }

        private void LoadFromConfig()
        {
            _cfg.Sanitize();

            _chkCustomStage.Checked = _cfg.UseCustomStageSize;
            SetNum(_numStageW, _cfg.StageWidth);   SetNum(_numStageH, _cfg.StageHeight);
            SetNum(_numMaxW, _cfg.MaxGifWidth);    SetNum(_numMaxH, _cfg.MaxGifHeight);
            _chkEnlarge.Checked = _cfg.EnlargeSmallGifs;
            _chkByRole.Checked  = _cfg.SizeByRole;
            SetNum(_numScaleMod, _cfg.ScaleModPercent);
            SetNum(_numScaleSub, _cfg.ScaleSubPercent);
            SetNum(_numScaleViewer, _cfg.ScaleViewerPercent);

            _cboPlacement.SelectedIndex = IndexOf(PlacementChoices, _cfg.DisplayMode);
            SetNum(_numEdge, _cfg.EdgePadding);
            SetNum(_numOffX, _cfg.OffsetX);        SetNum(_numOffY, _cfg.OffsetY);

            _cboGlow.SelectedIndex = IndexOf(GlowChoices, _cfg.GlowMode);
            SetGlowColor(_cfg.GlowColor);
            SetNum(_numGlowSize, _cfg.GlowSize);   SetNum(_numBorder, _cfg.BorderWidth);
            SetNum(_numRadius, _cfg.CornerRadius); SetNum(_numOpacity, _cfg.OpacityPercent);
            _chkUsername.Checked = _cfg.ShowUsername;
            _cboEntrance.SelectedIndex = IndexOf(EntranceChoices, _cfg.Entrance);

            _chkStay.Checked = _cfg.StayUntilCleared;
            SetNum(_numDuration, _cfg.DurationSeconds);
            SetNum(_numGlobalCd, _cfg.GlobalCooldownSeconds);
            SetNum(_numUserCd, _cfg.UserCooldownSeconds);
            _chkSubsOnly.Checked   = _cfg.RestrictToSubsAndVips;
            _chkIgnoreBots.Checked = _cfg.IgnoreBotsOnGifPosts;
            _chkNative.Checked       = _cfg.TwitchChatGifsEnabled;
            _chkNativeBattle.Checked = _cfg.TwitchChatGifsCanBattle;
            _chkAuto.Checked = _cfg.AutoBattleEnabled;
            SetNum(_numWindow, _cfg.AutoBattleWindowSeconds);
            SetNum(_numVoting, _cfg.BattleVotingSeconds);
            SetNum(_numChallenge, _cfg.ChallengeWindowSeconds);

            _txtKey.Text = _cfg.GiphyApiKey;
            _cboRating.SelectedIndex = IndexOf(RatingChoices, _cfg.GiphyRating);
            SetNum(_numPick, _cfg.GiphyPickFromTop);
            _txtBlocked.Text = string.Join(Environment.NewLine, _cfg.SearchBlockedWords);
            _chkApproval.Checked = _cfg.ApprovalEnabled;
            SetNum(_numApprovalSecs, _cfg.ApprovalTimeoutSeconds);
            _chkRefund.Checked = _cfg.RefundFailedRewards;
        }

        // Copies every control into the config copy. Settings that have no
        // control here (the allowed-domain lists) are left exactly as they were.
        private void ReadControlsInto(GifBattlerConfig c)
        {
            c.UseCustomStageSize = _chkCustomStage.Checked;
            c.StageWidth  = (int)_numStageW.Value;   c.StageHeight  = (int)_numStageH.Value;
            c.MaxGifWidth = (int)_numMaxW.Value;     c.MaxGifHeight = (int)_numMaxH.Value;
            c.EnlargeSmallGifs = _chkEnlarge.Checked;
            c.SizeByRole = _chkByRole.Checked;
            c.ScaleModPercent    = (int)_numScaleMod.Value;
            c.ScaleSubPercent    = (int)_numScaleSub.Value;
            c.ScaleViewerPercent = (int)_numScaleViewer.Value;

            c.DisplayMode = PlacementChoices[Math.Max(0, _cboPlacement.SelectedIndex)][1];
            c.EdgePadding = (int)_numEdge.Value;
            c.OffsetX = (int)_numOffX.Value;  c.OffsetY = (int)_numOffY.Value;

            c.GlowMode  = GlowChoices[Math.Max(0, _cboGlow.SelectedIndex)][1];
            c.GlowColor = _glowHex;
            c.GlowSize    = (int)_numGlowSize.Value;  c.BorderWidth    = (int)_numBorder.Value;
            c.CornerRadius = (int)_numRadius.Value;   c.OpacityPercent = (int)_numOpacity.Value;
            c.ShowUsername = _chkUsername.Checked;
            c.Entrance = EntranceChoices[Math.Max(0, _cboEntrance.SelectedIndex)][1];

            c.StayUntilCleared = _chkStay.Checked;
            c.DurationSeconds  = (int)_numDuration.Value;
            c.GlobalCooldownSeconds = (int)_numGlobalCd.Value;
            c.UserCooldownSeconds   = (int)_numUserCd.Value;
            c.RestrictToSubsAndVips = _chkSubsOnly.Checked;
            c.IgnoreBotsOnGifPosts  = _chkIgnoreBots.Checked;
            c.TwitchChatGifsEnabled   = _chkNative.Checked;
            c.TwitchChatGifsCanBattle = _chkNativeBattle.Checked;
            c.AutoBattleEnabled = _chkAuto.Checked;
            c.AutoBattleWindowSeconds = (int)_numWindow.Value;
            c.BattleVotingSeconds     = (int)_numVoting.Value;
            c.ChallengeWindowSeconds  = (int)_numChallenge.Value;

            c.GiphyApiKey = _txtKey.Text;
            c.GiphyRating = RatingChoices[Math.Max(0, _cboRating.SelectedIndex)][1];
            c.GiphyPickFromTop = (int)_numPick.Value;
            c.SearchBlockedWords = new List<string>(_txtBlocked.Text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries));
            c.ApprovalEnabled = _chkApproval.Checked;
            c.ApprovalTimeoutSeconds = (int)_numApprovalSecs.Value;
            c.RefundFailedRewards = _chkRefund.Checked;
        }

        // ── keeping the window in step with itself ─────────────────────
        private void SyncEnabledStates()
        {
            string mode = PlacementChoices[Math.Max(0, _cboPlacement.SelectedIndex)][1];
            bool random = mode == "random" || mode == "random-corner";
            bool full   = mode == "fullscreen";

            _numStageW.Enabled = _numStageH.Enabled = _chkCustomStage.Checked;

            // fullscreen sizes the GIF itself, so the size box does not apply
            _numMaxW.Enabled = _numMaxH.Enabled = _chkEnlarge.Enabled = _chkByRole.Enabled = !full;
            bool scales = _chkByRole.Checked && !full;
            _numScaleMod.Enabled = _numScaleSub.Enabled = _numScaleViewer.Enabled = scales;

            _numEdge.Enabled = random;
            _numOffX.Enabled = _numOffY.Enabled = !random && !full;

            string glow = GlowChoices[Math.Max(0, _cboGlow.SelectedIndex)][1];
            _btnGlowColor.Enabled = glow == "color";
            _numGlowSize.Enabled = _numBorder.Enabled = glow != "off";

            _numDuration.Enabled = !_chkStay.Checked;
            _chkNativeBattle.Enabled = _chkNative.Checked;
            _numWindow.Enabled = _chkAuto.Checked;
            _numApprovalSecs.Enabled = _chkApproval.Checked;
        }

        private void OnAnyChange(object sender, EventArgs e)
        {
            if (_loading) return;
            _lblError.Text = "";
            RefreshAll();
        }

        private void RefreshAll()
        {
            ReadControlsInto(_cfg);          // we only ever edit the private copy
            _cfg.Sanitize();
            SyncEnabledStates();

            switch (_cboShape.SelectedIndex)
            {
                case 1:  _preview.NatW = 300; _preview.NatH = 600; break;   // tall and skinny
                case 2:  _preview.NatW = 500; _preview.NatH = 500; break;   // square
                default: _preview.NatW = 800; _preview.NatH = 450; break;   // wide
            }
            _preview.Tier = _cboRole.SelectedIndex == 0 ? "mod" : (_cboRole.SelectedIndex == 1 ? "sub" : "viewer");
            _preview.Config = _cfg;
            _preview.Invalidate();

            _lblSummary.Text = GifBattlerLayout.Describe(_cfg);
        }

        // ── colour picker (the button shows the colour and its code) ───
        private void SetGlowColor(string hex)
        {
            _glowHex = GifBattlerConfig.NormalizeHex(hex, "#DC2828");
            Color color = ColorTranslator.FromHtml(_glowHex);
            _btnGlowColor.BackColor = color;
            _btnGlowColor.ForeColor = color.GetBrightness() > 0.55f ? Color.Black : Color.White;
            _btnGlowColor.Text = _glowHex;
        }

        private void OnPickGlowColor(object sender, EventArgs e)
        {
            using (var dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = ColorTranslator.FromHtml(_glowHex);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Color c = dlg.Color;
                SetGlowColor("#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"));
                OnAnyChange(sender, e);
            }
        }

        // ── save ───────────────────────────────────────────────────────
        private void OnSaveClick(object sender, EventArgs e)
        {
            // Nothing here can be "wrong": every spinner already has a safe range, and a
            // GIF box bigger than the screen area is simply shrunk to fit it.
            ReadControlsInto(_cfg);

            ShowTestAfterSave = _chkTest.Checked;
            Result = _cfg;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
