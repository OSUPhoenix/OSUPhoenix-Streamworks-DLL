// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerApproval.cs     DLL v1.5.0
//
//  The optional "approve this GIF?" window for !gif and the channel
//  points reward. Only used when ApprovalEnabled is on in the settings.
//
//  WHAT YOU SEE:
//    A small always-on-top window in the bottom-right corner of your main
//    screen: who asked, what they searched, the GIF playing, and
//    [Approve] [Deny] buttons with a countdown. Pressing either button
//    closes the window straight away. If nobody presses anything before
//    the countdown ends, that counts as Deny and the window closes itself.
//
//  WHY A SMALL NATIVE WINDOW (not a browser tab):
//    • It needs no web server and no extra software.
//    • It looks like the other OSWTools windows (same theme).
//    • It is easy to guarantee it closes — a browser window opened from a
//      script is hard to close again.
//
//  TWO DETAILS WORTH KNOWING:
//    1. It opens WITHOUT taking keyboard focus (ShowWithoutActivation).
//       If you are mid-game and hit Enter or Space, the keypress still goes
//       to your game, never to this window — so nothing gets approved by
//       accident. You click the buttons with the mouse.
//    2. Windows can only draw on top of "windowed" and "borderless" games,
//       not exclusive-fullscreen ones. (OBS game capture setups usually use
//       borderless; if yours does not show it, switch the game to borderless.)
//
//  ONE AT A TIME:
//    A lock makes requests wait their turn, so two windows never pile up.
//    Give the Streamer.bot actions their own queue as well (see the script
//    header) so waiting here never holds up your other actions.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using OSWTools.Theme;
using OSWTools.Utilities;

namespace OSWTools.Functions.GifBattler
{
    internal enum ApprovalResult
    {
        Approved,
        Denied,
        TimedOut,   // nobody chose in time (counts as a deny)
        Failed      // the window could not show the picture
    }

    internal sealed class ApprovalRequest
    {
        internal string UserName;
        internal string SearchText;
        internal byte[] GifBytes;
        internal int    TimeoutSeconds = 45;
    }

    internal static class GifBattlerApproval
    {
        /// <summary>Tests replace this with a fake so no window is needed.</summary>
        internal static Func<ApprovalRequest, ApprovalResult> Show = ShowWindow;

        private static readonly object Gate = new object();

        private static ApprovalResult ShowWindow(ApprovalRequest request)
        {
            lock (Gate)                                   // one window at a time
            {
                ApprovalResult result = ApprovalResult.Failed;

                DpiHelper.EnsureDpiAware();
                try
                {
                    StaThread.Run(() =>
                    {
                        Application.EnableVisualStyles();
                        try { Application.SetCompatibleTextRenderingDefault(false); }
                        catch (InvalidOperationException) { /* already set in this process - fine */ }

                        using (var form = new GifBattlerApprovalForm(request))
                        {
                            Application.Run(form);        // returns when the form closes
                            result = form.Result;
                        }
                    });
                }
                catch (Exception)
                {
                    result = ApprovalResult.Failed;
                }
                return result;
            }
        }
    }

    internal sealed class GifBattlerApprovalForm : Form
    {
        internal ApprovalResult Result { get; private set; } = ApprovalResult.Denied;   // closing with X = deny

        private readonly ApprovalRequest _req;
        private readonly MemoryStream _stream;        // must stay open while the picture is shown
        private readonly Image _image;
        private readonly Label _lblCountdown;
        private readonly Timer _timer;
        private int _left;

        internal const int PictureW = 420, PictureH = 260;

        public GifBattlerApprovalForm(ApprovalRequest request)
        {
            _req  = request;
            _left = request.TimeoutSeconds;

            // Load the picture first: if it cannot be shown, fail before any window appears.
            _stream = new MemoryStream(request.GifBytes);
            _image  = Image.FromStream(_stream);

            Text = "GIF approval";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;                           // on top of your other windows
            BackColor = OSWTheme.CBg;
            ForeColor = OSWTheme.CTxt;
            Font = OSWTheme.Fn;
            ClientSize = new Size(PictureW + 32, 420);
            StartPosition = FormStartPosition.Manual;

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(area.Left, area.Right - Width - 16),
                                 Math.Max(area.Top,  area.Bottom - Height - 16));

            var lblWho = new Label
            {
                Text = "@" + request.UserName + " wants to send a GIF",
                Font = OSWTheme.FnB, ForeColor = OSWTheme.CTxt, BackColor = Color.Transparent,
                AutoSize = false, AutoEllipsis = true, Left = 16, Top = 12, Width = PictureW, Height = 22
            };
            var lblWhat = new Label
            {
                Text = "Searched: " + request.SearchText,
                ForeColor = OSWTheme.CDim, BackColor = Color.Transparent,
                AutoSize = false, AutoEllipsis = true, Left = 16, Top = 36, Width = PictureW, Height = 20
            };
            var pic = new PictureBox
            {
                Image = _image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
                Left = 16, Top = 62, Width = PictureW, Height = PictureH
            };
            _lblCountdown = new Label
            {
                ForeColor = OSWTheme.CDim, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Left = 16, Top = 62 + PictureH + 8, Width = PictureW, Height = 20
            };

            var approve = OSWControls.MkBtn("Approve", Color.FromArgb(34, 139, 84));
            approve.Name = "btnApprove";
            approve.SetBounds(16, 62 + PictureH + 36, (PictureW - 12) / 2, 38);
            approve.Click += (s, e) => Finish(ApprovalResult.Approved);

            var deny = OSWControls.MkBtn("Deny", OSWTheme.CAcc);
            deny.Name = "btnDeny";
            deny.SetBounds(16 + (PictureW - 12) / 2 + 12, 62 + PictureH + 36, (PictureW - 12) / 2, 38);
            deny.Click += (s, e) => Finish(ApprovalResult.Denied);

            Controls.AddRange(new Control[] { lblWho, lblWhat, pic, _lblCountdown, approve, deny });

            // No AcceptButton / CancelButton on purpose: Enter and Esc do nothing here.

            UpdateCountdown();
            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                _left--;
                if (_left <= 0) Finish(ApprovalResult.TimedOut);
                else UpdateCountdown();
            };
            _timer.Start();
        }

        /// <summary>Do not take keyboard focus when the window appears (see the file header).</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        private void UpdateCountdown()
        {
            _lblCountdown.Text = "No answer in " + _left + "s counts as Deny";
        }

        private void Finish(ApprovalResult result)
        {
            if (_timer != null) _timer.Stop();
            Result = result;
            Close();                                  // the window is gone as soon as you choose
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) _timer.Dispose();
                if (_image != null) _image.Dispose();
                if (_stream != null) _stream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
