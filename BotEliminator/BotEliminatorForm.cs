using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace OSWTools.BotEliminator
{
    internal class BotEliminatorForm : Form
    {
        private TextBox _txtTwitch;
        private TextBox _txtYouTube;
        private TextBox _txtKick;
        private PictureBox _pic1;
        private PictureBox _pic2;
        private Button _btnSave;
        private Button _btnCancel;

        public BotEliminatorData Result { get; private set; }

        public BotEliminatorForm(BotEliminatorData initial)
        {
            Text = "Bot Exclusion Manager";
            Width = 760;
            Height = 560;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            Color gradStart = ColorTranslator.FromHtml("#410000");
            Color gradEnd   = ColorTranslator.FromHtml("#1C1C1C");
            Paint += (s, e) => e.Graphics.FillRectangle(
                new LinearGradientBrush(ClientRectangle, gradStart, gradEnd, 90F),
                ClientRectangle);

            var lblTitle = new Label
            {
                Text      = "Bot Exclusion Manager",
                Font      = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Size      = new Size(500, 40),
                Location  = new Point((ClientSize.Width - 500) / 2, 10)
            };
            Controls.Add(lblTitle);

            var lblInstr = new Label
            {
                Text      = "Enter one username per line to exclude from achievements:",
                AutoSize  = true,
                BackColor = Color.Transparent,
                Location  = new Point(20, lblTitle.Bottom + 8)
            };
            Controls.Add(lblInstr);

            int colW = (ClientSize.Width - 60) / 3;

            // Twitch column
            var lblTwitch = new Label
            {
 Text = "Twitch Handles:",
                AutoSize = true,
                Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#6441a5"),
                BackColor = Color.Transparent,
                Location = new Point(20, lblInstr.Bottom + 10)
            };
            Controls.Add(lblTwitch);

            _txtTwitch = new TextBox
            {
                Multiline   = true,
                ScrollBars  = ScrollBars.Vertical,
                Size        = new Size(colW, 260),
                Location    = new Point(20, lblTwitch.Bottom + 5),
                Text        = ListToText(initial.Twitch)
            };
            Controls.Add(_txtTwitch);

            // YouTube column
            var lblYouTube = new Label
            {
                Text = "YouTube Handles:",
                AutoSize = true,
                Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#FF0000"),
                BackColor = Color.Transparent,
                Location = new Point(30 + colW, lblInstr.Bottom + 10)
            };
            Controls.Add(lblYouTube);

            _txtYouTube = new TextBox
            {
                Multiline   = true,
                ScrollBars  = ScrollBars.Vertical,
                Size        = new Size(colW, 260),
                Location    = new Point(30 + colW, lblYouTube.Bottom + 5),
                Text        = ListToText(initial.YouTube)
            };
            Controls.Add(_txtYouTube);

            // Kick column
            var lblKick = new Label
            {
                Text = "Kick Handles:",
                AutoSize = true,
                Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold),
                ForeColor = ColorTranslator.FromHtml("#00e701"),
                BackColor = Color.Transparent,
                Location = new Point(40 + (colW * 2), lblInstr.Bottom + 10)
            };
            Controls.Add(lblKick);

            _txtKick = new TextBox
            {
                Multiline   = true,
                ScrollBars  = ScrollBars.Vertical,
                Size        = new Size(colW, 260),
                Location    = new Point(40 + (colW * 2), lblKick.Bottom + 5),
                Text        = ListToText(initial.Kick)
            };
            Controls.Add(_txtKick);

            // Footer links
            int linkY = _txtTwitch.Bottom + 12;
            Controls.Add(MakeLink("More Imports",                          "https://shop.osuphoenix.tv/",                                                                          new Point(20,  linkY)));
            Controls.Add(MakeLink("Contact: OSUPhoenix.Gaming@gmail.com",  "mailto:OSUPhoenix.Gaming@gmail.com",                                                                   new Point(150, linkY)));
            Controls.Add(MakeLink("Join Discord",                          "https://discord.gg/TGPwXM7Kfv",                                                                        new Point(20,  linkY + 25)));
            Controls.Add(MakeLink("Docs & Instructions",                   "https://osuphoenix.notion.site/OSUPhoenix-s-Bot-Eliminator-202e67e6e2b2800cb326f492480e5064?pvs=4",   new Point(150, linkY + 25)));

            _pic1 = new PictureBox
            {
                ImageLocation = "https://avatars.githubusercontent.com/u/89166980?s=200&v=4",
                Location      = new Point(ClientSize.Width - 170, linkY - 5),
                Size          = new Size(75, 75),
                SizeMode      = PictureBoxSizeMode.StretchImage,
                BackColor     = Color.Transparent
            };
            Controls.Add(_pic1);

            _pic2 = new PictureBox
            {
                                ImageLocation = "https://i0.wp.com/osuphoenix.tv/wp-content/uploads/2025/11/StreamWorks-500-px.webp?resize=150%2C150&ssl=1",
                Location      = new Point(ClientSize.Width - 90, linkY - 5),
                Size          = new Size(75, 75),
                SizeMode      = PictureBoxSizeMode.StretchImage,
                BackColor     = Color.Transparent
            };
            Controls.Add(_pic2);

            // Save / Cancel
            _btnSave = new Button
            {
                Text         = "Save",
                Width        = 95,
                Location     = new Point((ClientSize.Width / 2) - 110, ClientSize.Height - 65),
                DialogResult = DialogResult.OK
            };
            _btnCancel = new Button
            {
                Text         = "Cancel",
                Width        = 95,
                Location     = new Point((ClientSize.Width / 2) + 15, ClientSize.Height - 65),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(_btnSave);
            Controls.Add(_btnCancel);
            CancelButton = _btnCancel;

            KeyEventHandler allowNewline = (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                    e.SuppressKeyPress = false;
            };
            _txtTwitch.KeyDown  += allowNewline;
            _txtYouTube.KeyDown += allowNewline;
            _txtKick.KeyDown    += allowNewline;

            _btnSave.Click += (s, e) =>
            {
                Result = new BotEliminatorData
                {
                    Twitch  = TextToList(_txtTwitch.Text),
                    YouTube = TextToList(_txtYouTube.Text),
                    Kick    = TextToList(_txtKick.Text)
                };
                Close();
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string ListToText(List<string> list)
        {
            return list == null ? "" : string.Join("\r\n", list);
        }

        private static List<string> TextToList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            return text
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('@').ToLowerInvariant())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static LinkLabel MakeLink(string text, string url, Point location)
        {
            var l = new LinkLabel
            {
                Text      = text,
                AutoSize  = true,
                BackColor = Color.Transparent,
                Location  = location
            };
            l.LinkClicked += (s, e) => System.Diagnostics.Process.Start(url);
            return l;
        }
    }
}
