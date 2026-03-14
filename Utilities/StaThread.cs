using System;
using System.Threading;
using System.Windows.Forms;

namespace OSWTools.Utilities
{
    /// <summary>
    /// Launches a WinForms window on a dedicated STA thread.
    ///
    /// WHY THIS EXISTS:
    /// Streamer.bot executes C# scripts on MTA (multi-threaded apartment) threads.
    /// WinForms requires STA (single-threaded apartment) to function correctly.
    /// If you open a Form directly on an MTA thread you get clipboard errors,
    /// drag-and-drop failures, and unpredictable crashes.
    ///
    /// This helper creates a clean STA thread, runs your window on it,
    /// and blocks the calling thread until the window closes — so you can
    /// safely read DialogResult or output values afterward.
    ///
    /// USAGE:
    ///   StaThread.Run(() =>
    ///   {
    ///       Application.EnableVisualStyles();
    ///       using (var form = new MySettingsForm())
    ///       {
    ///           form.StartPosition = FormStartPosition.CenterScreen;
    ///           if (form.ShowDialog() == DialogResult.OK)
    ///           {
    ///               // read form results here — still on the STA thread
    ///           }
    ///       }
    ///   });
    ///   // execution continues here once the window is closed
    /// </summary>
    public static class StaThread
    {
        /// <summary>
        /// Runs <paramref name="action"/> on a dedicated STA thread and blocks
        /// until it completes. Any exception thrown inside is re-thrown on the
        /// calling thread.
        /// </summary>
        public static void Run(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");

            Exception caught = null;

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;   // don't keep the process alive if SB exits
            thread.Start();
            thread.Join();                // block caller until window is closed

            // Re-throw on the calling thread so the script can log it via CPH
            if (caught != null)
                throw new Exception("StaThread caught an exception: " + caught.Message, caught);
        }
    }
}
