using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OSWTools.Versioning
{
    /// <summary>The result of an update check against GitHub.</summary>
    public class UpdateCheckResult
    {
        public bool   UpdateAvailable  { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion    { get; set; }
        public bool   CheckSucceeded   { get; set; }
        public string ErrorMessage     { get; set; }
        public bool   UpdateStaged     { get; set; }
        public string ReleaseNotes     { get; set; }
    }

    /// <summary>
    /// Handles the full update lifecycle for OSWTools.dll.
    ///
    /// Three-stage process:
    ///   1. CheckAsync()          - Hit GitHub API, compare versions.
    ///   2. DownloadUpdateAsync() - Download new DLL to a staging file.
    ///   3. ApplyUpdate()         - Write a swap batch script, tell user to restart.
    ///
    /// Windows locks loaded DLL files, so we cannot replace OSWTools.dll while
    /// Streamer.bot is running. We download to OSWData\Updater\OSWTools.dll.pending
    /// and DllInstaller writes a .bat file that installs it into
    /// &lt;SB folder&gt;\dlls\ after Streamer.bot closes.
    /// </summary>
    public static class UpdateChecker
    {
        // ── Check ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks GitHub for the latest release of OSWTools. Makes a network call.
        /// Usage:
        ///   var result = await UpdateChecker.CheckAsync();
        ///   if (result.UpdateAvailable) MessageBox.Show("Update available: " + result.LatestVersion);
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync()
        {
            try
            {
                string json;
                using (WebClient wc = new WebClient())
                {
                    // GitHub API requires a User-Agent header.
                    wc.Headers.Add("User-Agent", "OSWTools/" + OSWVersion.Current);
                    json = await wc.DownloadStringTaskAsync(OSWVersion.GitHubApiLatest);
                }

                JObject obj   = JObject.Parse(json);
                string rawTag = obj["tag_name"] != null ? obj["tag_name"].ToString() : string.Empty;
                string latest = rawTag.TrimStart('v', 'V').Trim();
                string notes  = obj["body"] != null ? obj["body"].ToString() : string.Empty;

                System.Version installed;
                System.Version latestVer;

                if (!System.Version.TryParse(OSWVersion.Current, out installed) ||
                    !System.Version.TryParse(latest, out latestVer))
                {
                    return Failure("Could not parse version strings.");
                }

                return new UpdateCheckResult
                {
                    CheckSucceeded   = true,
                    UpdateAvailable  = latestVer > installed,
                    InstalledVersion = OSWVersion.Current,
                    LatestVersion    = latest,
                    ReleaseNotes     = notes,
                    UpdateStaged     = IsPendingUpdatePresent(),
                    ErrorMessage     = string.Empty
                };
            }
            catch (Exception ex)
            {
                return Failure("Update check failed: " + ex.Message);
            }
        }

        // ── Download ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads the latest OSWTools.dll from GitHub into a staging file.
        /// The actual file replacement happens in ApplyUpdate() after restart.
        /// Progress is reported as 0-100 if a progress object is provided.
        /// </summary>
        public static async Task<bool> DownloadUpdateAsync(IProgress<int> progress = null)
        {
            try
            {
                string pendingPath = GetPendingUpdatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(pendingPath));   // NEW: OSWData\Updater may not exist yet

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "OSWTools/" + OSWVersion.Current);

                    if (progress != null)
                    {
                        wc.DownloadProgressChanged += (s, e) => progress.Report(e.ProgressPercentage);
                    }

                    await wc.DownloadFileTaskAsync(OSWVersion.GitHubDllDownload, pendingPath);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ── Apply ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Schedules the downloaded update to be installed once Streamer.bot
        /// closes. The install always goes to &lt;SB folder&gt;\dlls\OSWTools.dll,
        /// and an old copy in the SB root folder is removed at the same time.
        /// Returns false if no staged update exists.
        /// Tell the user to close and reopen Streamer.bot after calling this.
        ///
        /// CHANGED: this used to write its own .bat that waited a fixed 3
        /// seconds and then copied over the DLL — while Streamer.bot was still
        /// running and the DLL was still locked. DllInstaller's script waits
        /// for Streamer.bot to actually exit before touching any files.
        /// </summary>
        public static bool ApplyUpdate()
        {
            if (!File.Exists(GetPendingUpdatePath())) return false;
            return DllInstaller.ScheduleSwapOnExit();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>Returns true if a downloaded update is waiting to be applied.</summary>
        public static bool IsPendingUpdatePresent()
        {
            return File.Exists(GetPendingUpdatePath());
        }

        // CHANGED: the download now waits in OSWData\Updater\ instead of next
        // to the DLL, so stray .pending files never clutter the dlls folder.
        private static string GetPendingUpdatePath()
        {
            return DllInstaller.PendingPath;
        }

        private static UpdateCheckResult Failure(string message)
        {
            return new UpdateCheckResult
            {
                CheckSucceeded   = false,
                UpdateAvailable  = false,
                InstalledVersion = OSWVersion.Current,
                LatestVersion    = string.Empty,
                ErrorMessage     = message,
                ReleaseNotes     = string.Empty
            };
        }
    }
}
