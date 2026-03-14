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
    /// Streamer.bot is running. We download to OSWTools.dll.pending and write
    /// a .bat file that swaps the files after Streamer.bot closes.
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
        /// Writes a batch script that swaps the pending DLL into place after
        /// Streamer.bot closes, then launches it in the background.
        /// Returns false if no staged update exists.
        /// Tell the user to close and reopen Streamer.bot after calling this.
        /// </summary>
        public static bool ApplyUpdate()
        {
            string pendingPath = GetPendingUpdatePath();
            if (!File.Exists(pendingPath)) return false;

            string dllPath    = GetCurrentDllPath();
            string dir        = Path.GetDirectoryName(dllPath);
            string scriptPath = Path.Combine(dir, "osw_update.bat");

            // PING is used as a batch sleep: pinging localhost N times = ~N-1 seconds.
            string script =
                "@echo off\r\n" +
                "ping 127.0.0.1 -n 4 > nul\r\n" +
                "copy /Y \"" + pendingPath + "\" \"" + dllPath + "\"\r\n" +
                "del \"" + pendingPath + "\"\r\n" +
                "del \"%~f0\"\r\n";

            File.WriteAllText(scriptPath, script);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = scriptPath,
                WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow  = true,
                UseShellExecute = true
            });

            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>Returns true if a downloaded update is waiting to be applied.</summary>
        public static bool IsPendingUpdatePresent()
        {
            return File.Exists(GetPendingUpdatePath());
        }

        private static string GetPendingUpdatePath()
        {
            return Path.Combine(Path.GetDirectoryName(GetCurrentDllPath()), "OSWTools.dll.pending");
        }

        private static string GetCurrentDllPath()
        {
            return typeof(UpdateChecker).Assembly.Location;
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
