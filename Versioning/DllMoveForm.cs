// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Versioning/DllMoveForm.cs                       NEW FILE
//
//  Dark-themed popup telling the user that OSWTools.dll is moving to
//  Streamer.bot's "dlls" folder and that a restart finishes the job.
//
//  WHY A POPUP INSTEAD OF A TOAST:
//    The move only happens when Streamer.bot closes. A toast is easy to
//    miss, and a user who never restarts never gets the move. This
//    dialog asks once per session and offers to restart for them.
//
//  WHO SHOWS IT:
//    DllInstaller.ShowRestartPromptAsync(), on its own STA thread that
//    is NOT joined — the tool that triggered it keeps running.
//
//  TWO MODES:
//    Restart mode (DllMoving / ReferencesOnly): the automatic fix is
//      scheduled; offer to restart now.
//    Manual mode (ManualMoveNeeded / ManualReferencesNeeded) — NEW:
//      the automatic fix failed several times. Shown ONCE with plain
//      steps to do it by hand, plus a button that opens the
//      Streamer.bot folder. No restart button.
//
//  BUTTONS (restart mode):
//    "Restart Streamer.bot" → RestartChosen = true. The caller then runs
//                             SbRestart.CloseAndRelaunch().
//    "Later"                → nothing. The move still happens the next
//                             time the user closes Streamer.bot.
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OSWTools.Theme;

namespace OSWTools.Versioning
{
    internal class DllMoveForm : Form
    {
        /// <summary>True if the user clicked "Restart Streamer.bot".</summary>
        public bool RestartChosen { get; private set; }

        private readonly RestartReason _reason;

        public DllMoveForm(RestartReason reason)
        {
            _reason = reason;
            BuildUI();
        }

        private bool IsManual
        {
            get { return _reason == RestartReason.ManualMoveNeeded || _reason == RestartReason.ManualReferencesNeeded; }
        }

        // ── Text for each mode (kept together so wording is easy to edit) ─

        private string TitleText
        {
            get { return IsManual ? "Manual Step Needed" : "Restart Needed"; }
        }

        private string SubtitleText
        {
            get
            {
                switch (_reason)
                {
                    case RestartReason.DllMoving:              return "OSWTools is moving to Streamer.bot's \"dlls\" folder";
                    case RestartReason.ReferencesOnly:         return "Some actions need their OSWTools reference updated";
                    case RestartReason.ManualMoveNeeded:       return "OSWTools couldn't move itself to the \"dlls\" folder";
                    default:                                   return "OSWTools couldn't update your actions automatically";
                }
            }
        }

        private string BodyText
        {
            get
            {
                switch (_reason)
                {
                    case RestartReason.DllMoving:
                        return "Streamer.bot asks that add-on DLLs live in its \"dlls\" folder, so "
                             + "OSWTools.dll is moving there. Your actions will be updated to the "
                             + "new location automatically.\r\n\r\n"
                             + "This finishes when Streamer.bot restarts. Everything keeps working until then.";
                    case RestartReason.ReferencesOnly:
                        return "Some of your actions still point to an old OSWTools.dll location. "
                             + "They'll be updated to the \"dlls\" folder automatically.\r\n\r\n"
                             + "This finishes when Streamer.bot restarts.";
                    case RestartReason.ManualMoveNeeded:
                        return "1.  Close Streamer.bot.\r\n"
                             + "2.  Move OSWTools.dll from the Streamer.bot folder into its \"dlls\" folder.\r\n"
                             + "3.  Reopen Streamer.bot. If an OSW action won't compile, open it, "
                             + "remove the old OSWTools.dll reference and add dlls\\OSWTools.dll.";
                    default:
                        return "1.  Open any OSW action that won't compile.\r\n"
                             + "2.  In its References tab, remove the old OSWTools.dll entry.\r\n"
                             + "3.  Right-click > Add reference from file, and pick dlls\\OSWTools.dll "
                             + "inside your Streamer.bot folder.";
                }
            }
        }

        private string NoteText
        {
            get
            {
                return IsManual
                    ? "This message won't show again. Details: OSWData\\Updater\\install.log"
                    : "Not a good time? Choose Later — it finishes the next time you close Streamer.bot.";
            }
        }

        private void BuildUI()
        {

            // ── Form properties ──────────────────────────────────────────
            Text            = "OSW Tools — " + TitleText;
            Width           = 480;
            Height          = 300;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            TopMost         = true;     // don't let it hide behind SB / OBS
            ShowInTaskbar   = true;     // reachable from the taskbar too
            BackColor       = OSWTheme.CBg;
            ForeColor       = OSWTheme.CTxt;
            Font            = OSWTheme.Fn;

            Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(
                    ClientRectangle, OSWTheme.CBg, OSWTheme.CGradBottom, LinearGradientMode.Vertical))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }
            };

            // ── Header: logo, title, subtitle ────────────────────────────
            Controls.Add(new PictureBox
            {
                ImageLocation = OSWTheme.LogoUrl,
                Location      = new Point(20, 18),
                Size          = new Size(44, 44),
                SizeMode      = PictureBoxSizeMode.StretchImage,
                BackColor     = Color.Transparent
            });

            Controls.Add(new Label
            {
                Text      = TitleText,
                Font      = OSWTheme.FnT,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(74, 22)
            });

            Controls.Add(new Label
            {
                Text      = SubtitleText,
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Location  = new Point(74, 50)
            });

            Controls.Add(new Label
            {
                Height    = 1,
                BackColor = OSWTheme.CDiv,
                AutoSize  = false,
                Location  = new Point(20, 80),
                Width     = ClientSize.Width - 40
            });

            // ── Explanation ──────────────────────────────────────────────
            //  AutoSize = false + a fixed Size makes the label word-wrap.
            Controls.Add(new Label
            {
                Text      = BodyText,
                Font      = OSWTheme.Fn,
                ForeColor = OSWTheme.CTxt,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(22, 94),
                Size      = new Size(ClientSize.Width - 44, 90)
            });

            Controls.Add(new Label
            {
                Text      = NoteText,
                Font      = OSWTheme.FnH,
                ForeColor = OSWTheme.CDim,
                BackColor = Color.Transparent,
                AutoSize  = false,
                Location  = new Point(22, 186),
                Size      = new Size(ClientSize.Width - 44, 20)
            });

            if (IsManual) AddManualButtons();
            else          AddRestartButtons();
        }

        // ── Buttons: restart mode ─────────────────────────────────────────

        private void AddRestartButtons()
        {
            var btnRestart = MakeButton("Restart Streamer.bot", OSWTheme.CAcc, 170);
            btnRestart.Location = new Point(ClientSize.Width - 190, ClientSize.Height - 50);
            btnRestart.Click += (s, e) =>
            {
                RestartChosen = true;
                DialogResult  = DialogResult.OK;
                Close();
            };
            Controls.Add(btnRestart);

            var btnLater = MakeButton("Later", OSWTheme.CPnl, 100);
            btnLater.Location = new Point(ClientSize.Width - 300, ClientSize.Height - 50);
            btnLater.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(btnLater);

            // Enter and Esc both mean "Later". A restart should be a
            // deliberate click, never a stray key press mid-stream.
            AcceptButton = btnLater;
            CancelButton = btnLater;
        }

        // ── Buttons: manual mode (NEW) ────────────────────────────────────

        private void AddManualButtons()
        {
            var btnOk = MakeButton("OK", OSWTheme.CAcc, 100);
            btnOk.Location = new Point(ClientSize.Width - 120, ClientSize.Height - 50);
            btnOk.Click += (s, e) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnOk);

            // Opens the Streamer.bot folder in File Explorer, so the user
            // can see both the dlls\ folder and OSWData\Updater\install.log.
            var btnFolder = MakeButton("Open Streamer.bot Folder", OSWTheme.CPnl, 190);
            btnFolder.Location = new Point(ClientSize.Width - 320, ClientSize.Height - 50);
            btnFolder.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = DllInstaller.SbFolder,
                        UseShellExecute = true      // "open this folder the normal way" = Explorer
                    });
                }
                catch { /* nothing useful to do if Explorer won't open */ }
            };
            Controls.Add(btnFolder);

            AcceptButton = btnOk;
            CancelButton = btnOk;
        }

        /// <summary>Flat, themed button. Position is set by the caller.</summary>
        private static Button MakeButton(string text, Color back, int width)
        {
            var b = new Button
            {
                Text      = text,
                Font      = OSWTheme.FnB,
                ForeColor = OSWTheme.CTxt,
                BackColor = back,
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(width, 32)
            };
            b.FlatAppearance.BorderColor = OSWTheme.CDiv;
            b.FlatAppearance.BorderSize  = back == OSWTheme.CAcc ? 0 : 1;
            return b;
        }
    }
}
