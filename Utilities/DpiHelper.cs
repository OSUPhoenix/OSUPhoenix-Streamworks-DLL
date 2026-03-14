using System;
using System.Runtime.InteropServices;

namespace OSWTools.Utilities
{
    /// <summary>
    /// Makes a WinForms window DPI-aware on the current monitor.
    ///
    /// Every OSW settings form that opens a window should call this first.
    /// It is safe to call multiple times — after the first successful call
    /// it becomes a no-op.
    ///
    /// USAGE (at the top of Execute() or ShowSettingsForm()):
    ///   DpiHelper.EnsureDpiAware();
    /// </summary>
    public static class DpiHelper
    {
        // Win32 P/Invoke declarations — these tell Windows how to scale
        // our window when the user has a non-100% display scale set.
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        // This is the best mode: the window scales independently per-monitor.
        private static readonly IntPtr PER_MON_V2 = new IntPtr(-4);

        // Flag so we only run the initialization once per process lifetime.
        private static bool _done = false;

        /// <summary>
        /// Call once before opening any WinForms window.
        /// Tries Per-Monitor V2 awareness first (Windows 10+),
        /// falls back to legacy SetProcessDPIAware on older systems.
        /// </summary>
        public static void EnsureDpiAware()
        {
            if (_done) return;

            try
            {
                // Per-Monitor V2 — best quality, crisp on all monitors
                if (SetThreadDpiAwarenessContext(PER_MON_V2) != IntPtr.Zero)
                {
                    _done = true;
                    return;
                }
            }
            catch { /* not available on older Windows — fall through */ }

            try
            {
                // Legacy fallback — still better than system DPI
                SetProcessDPIAware();
            }
            catch { /* ignore — best effort */ }

            _done = true;
        }
    }
}
