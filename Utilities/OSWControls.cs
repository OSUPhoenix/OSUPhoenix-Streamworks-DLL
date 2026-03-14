using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using OSWTools.Theme;

namespace OSWTools.Utilities
{
    /// <summary>
    /// WinForms control factory methods for the OSW dark theme.
    ///
    /// These replace the MkBtn / MkLink / MkInput / MkCombo helpers that
    /// were previously copy-pasted into every script's settings form.
    ///
    /// USAGE:
    ///   using OSWTools.Utilities;
    ///
    ///   var btn  = OSWControls.MkBtn("Save", OSWTheme.CAcc);
    ///   var lnk  = OSWControls.MkLink("Discord", "https://discord.gg/TGPwXM7Kfv");
    ///   var txt  = OSWControls.MkInput();
    ///   var combo= OSWControls.MkCombo(new[]{"Option A","Option B"});
    ///   var chk  = OSWControls.MkCheck("Enable feature");
    /// </summary>
    public static class OSWControls
    {
        // ── Buttons ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates a flat dark-themed button.
        /// </summary>
        public static Button MkBtn(string text, Color backColor)
        {
            var b = new Button
            {
                Text      = text,
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CTxt,
                BackColor = backColor,
                FlatStyle = FlatStyle.Flat,
                Height    = 26
            };
            b.FlatAppearance.BorderSize  = 1;
            b.FlatAppearance.BorderColor = OSWTheme.CDiv;
            return b;
        }

        /// <summary>
        /// Creates a flat dark-themed button at a specific position and size.
        /// Useful for absolute-positioned forms.
        /// </summary>
        public static Button MkBtn(string text, int x, int y, int width, int height)
        {
            var b = MkBtn(text, OSWTheme.CPnl);
            b.SetBounds(x, y, width, height);
            return b;
        }

        // ── Links ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a LinkLabel that opens a URL in the default browser.
        /// Text and URL can be different strings.
        /// </summary>
        public static LinkLabel MkLink(string text, string url)
        {
            var l = new LinkLabel
            {
                Text      = text,
                Font      = OSWTheme.FnSm,
                BackColor = Color.Transparent,
                AutoSize  = true
            };
            l.LinkColor = OSWTheme.CLnk;
            l.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* best effort */ }
            };
            return l;
        }

        /// <summary>
        /// Adds the four standard OSW footer links (Website, Contact, Discord, Docs)
        /// to a FlowLayoutPanel.
        ///
        /// USAGE:
        ///   var links = new FlowLayoutPanel { ... };
        ///   OSWControls.AddOswFooterLinks(links, "https://your-docs-url");
        /// </summary>
        public static void AddOswFooterLinks(FlowLayoutPanel panel, string docsUrl)
        {
            panel.Controls.Add(MkLink("Explore Website",  "https://osuphoenix.tv/"));
            panel.Controls.Add(MkLink("More Imports",     "https://shop.osuphoenix.tv/pages/streamer-bot-widgets"));
            panel.Controls.Add(MkLink("Contact",          "mailto:OSUPhoenix.Gaming@gmail.com"));
            panel.Controls.Add(MkLink("Join Discord",     "https://discord.gg/TGPwXM7Kfv"));
            if (!string.IsNullOrWhiteSpace(docsUrl))
                panel.Controls.Add(MkLink("Instructions", docsUrl));
        }

        // ── Text inputs ───────────────────────────────────────────────────

        /// <summary>
        /// Creates a single-line dark-themed TextBox.
        /// </summary>
        public static TextBox MkInput()
        {
            return new TextBox
            {
                BackColor   = OSWTheme.CIn,
                ForeColor   = OSWTheme.CTxt,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = OSWTheme.Fn,
                Height      = 26
            };
        }

        /// <summary>
        /// Creates a multi-line dark-themed TextBox with a vertical scrollbar.
        /// </summary>
        public static TextBox MkMultilineInput(int height = 60)
        {
            return new TextBox
            {
                BackColor   = OSWTheme.CIn,
                ForeColor   = OSWTheme.CTxt,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = OSWTheme.Fn,
                Height      = height,
                Multiline   = true,
                ScrollBars  = ScrollBars.Vertical
            };
        }

        // ── ComboBoxes ────────────────────────────────────────────────────

        /// <summary>
        /// Creates a flat dark-themed drop-down ComboBox pre-populated with items.
        /// Selects the first item automatically.
        /// </summary>
        public static ComboBox MkCombo(object[] items)
        {
            var c = new ComboBox
            {
                BackColor     = OSWTheme.CIn,
                ForeColor     = OSWTheme.CTxt,
                FlatStyle     = FlatStyle.Flat,
                Font          = OSWTheme.Fn,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Height        = 26
            };
            if (items != null) c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        // ── CheckBoxes ────────────────────────────────────────────────────

        /// <summary>
        /// Creates a dark-themed CheckBox.
        /// </summary>
        public static CheckBox MkCheck(string text, bool isChecked = false)
        {
            return new CheckBox
            {
                Text      = text,
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                Checked   = isChecked,
                Height    = 24,
                AutoSize  = false,
                Padding   = new Padding(2, 0, 0, 0)
            };
        }

        // ── Labels ────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a cyan section-header label (e.g. "IDENTITY", "TRIGGER").
        /// </summary>
        public static Label MkSecHdr(string text)
        {
            return new Label
            {
                Text      = text,
                Font      = OSWTheme.FnSec,
                ForeColor = OSWTheme.CLnk,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Height    = 22,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        /// <summary>
        /// Creates a 1px horizontal divider line.
        /// </summary>
        public static Label MkDivider()
        {
            return new Label
            {
                Height    = 1,
                BackColor = OSWTheme.CDiv,
                AutoSize  = false
            };
        }

        /// <summary>
        /// Creates a small muted field label (e.g. "OBS Scene Name").
        /// </summary>
        public static Label MkFieldLbl(string text)
        {
            return new Label
            {
                Text      = text,
                Font      = OSWTheme.FnSm,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Height    = 18
            };
        }

        // ── NumericUpDown ─────────────────────────────────────────────────

        /// <summary>
        /// Creates a dark-themed NumericUpDown spinner.
        /// </summary>
        public static NumericUpDown MkSpinner(decimal min, decimal max, decimal value, int decimalPlaces = 0)
        {
            return new NumericUpDown
            {
                Minimum       = min,
                Maximum       = max,
                Value         = Math.Max(min, Math.Min(max, value)),
                DecimalPlaces = decimalPlaces,
                BackColor     = OSWTheme.CIn,
                ForeColor     = OSWTheme.CTxt,
                Font          = OSWTheme.Fn,
                Height        = 26
            };
        }
    }
}
