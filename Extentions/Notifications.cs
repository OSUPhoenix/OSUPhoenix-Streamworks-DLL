namespace OSWTools
{
    /// <summary>
    /// Notification helpers for surfacing messages outside of the SB log.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///   lib.ShowToast("Achievement System", "Config updated successfully.");
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Shows a Windows toast notification in the Streamer.bot notification area.
        /// Safe to call — falls back to a log warning if the toast API fails.
        /// </summary>
        public void ShowToast(string title, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _CPH.ShowToastNotification(title ?? _ToolName, message);
            }
            catch
            {
                // Toast unavailable (e.g. running headless) — fall back to log
                LogWarn("Toast: " + message);
            }
        }

        /// <summary>
        /// Shows a toast notification using the tool name as the title.
        /// </summary>
        public void ShowToast(string message)
        {
            ShowToast(_ToolName, message);
        }
    }
}
