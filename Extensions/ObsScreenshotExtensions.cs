// =============================================================================
// OSWTools — Extensions/ObsScreenshotExtensions.cs
//
// OBS source/scene screenshot capture with optional cropping.
//
// USAGE:
//   string path = Path.Combine(folder, $"alert_{DateTime.Now:yyyyMMdd_HHmmss}.png");
//
//   bool ok = Lib.ObsCaptureSourceScreenshot(
//       sourceName: "[Alert] Achievements",
//       filePath:   path,
//       cropTo:     new Rectangle(0, 0, 1000, 250),  // optional
//       waitMs:     4000);                            // file-appearance timeout
//
// The method:
//   1. Sends OBS a `SaveSourceScreenshot` raw request
//   2. Polls for the file to appear on disk (up to waitMs)
//   3. If `cropTo` is supplied, re-saves the image with that region cropped
//   4. Returns true if a usable image ended up at filePath
// =============================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Capture an OBS source/scene to disk, optionally cropping.
        //
        // Returns: true if the file exists at filePath and is non-empty after
        // capture (and crop, if requested).
        // ─────────────────────────────────────────────────────────────────────
        public bool ObsCaptureSourceScreenshot(
            string sourceName,
            string filePath,
            Rectangle? cropTo = null,
            int waitMs = 4000)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                LogWarn("[Screenshot] No source name provided.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                LogWarn("[Screenshot] No file path provided.");
                return false;
            }

            try
            {
                // Ensure the destination folder exists (OBS won't create it)
                string folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                // 1) Send the raw OBS request
                var req = new JObject
                {
                    ["sourceName"]    = sourceName,
                    ["imageFormat"]   = "png",
                    ["imageFilePath"] = filePath
                };
                string raw = _CPH.ObsSendRaw("SaveSourceScreenshot",
                    req.ToString(Newtonsoft.Json.Formatting.None), 0);
                LogDebug("[Screenshot] OBS response: " + raw);

                // 2) Wait for the file to appear and become readable
                if (!WaitForFileReady(filePath, waitMs))
                {
                    LogWarn($"[Screenshot] File never appeared: {filePath}");
                    return false;
                }

                // 3) Optional in-place crop
                if (cropTo.HasValue)
                {
                    if (!CropImageInPlace(filePath, cropTo.Value))
                    {
                        LogWarn($"[Screenshot] Crop failed for {filePath}");
                        // File still exists uncropped — return true rather than throw away
                    }
                }

                LogInfo($"[Screenshot] Saved: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                LogError("[Screenshot] Capture failed: " + ex.Message);
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // INTERNAL helpers
        // ═════════════════════════════════════════════════════════════════════

        // Polls every 75ms for the file to exist AND be non-zero in size AND
        // be openable for read (handles race with OBS still writing).
        private bool WaitForFileReady(string path, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using (var fs = new FileStream(
                            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (fs.Length > 0)
                                return true;
                        }
                    }
                }
                catch { /* still locked / still writing */ }

                _CPH.Wait(75);
            }
            return false;
        }

        // Crops the image at filePath to the given rectangle. Writes to a .tmp
        // file first, then atomically swaps over the original.
        //
        // The rectangle is clamped to the image bounds — passing a region larger
        // than the source is OK, you just get the visible portion.
        private bool CropImageInPlace(string filePath, Rectangle cropTo)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                using (var ms = new MemoryStream(bytes))
                using (var src = new Bitmap(ms))
                {
                    int x = Math.Max(0, cropTo.X);
                    int y = Math.Max(0, cropTo.Y);
                    int w = Math.Min(cropTo.Width,  src.Width  - x);
                    int h = Math.Min(cropTo.Height, src.Height - y);

                    if (w <= 0 || h <= 0)
                    {
                        LogWarn($"[Screenshot] Crop region is zero-size after clamping " +
                                $"(req={cropTo}, src={src.Size})");
                        return false;
                    }

                    using (var cropped = src.Clone(
                        new Rectangle(x, y, w, h), PixelFormat.Format32bppArgb))
                    {
                        string tmp = filePath + ".tmp";
                        cropped.Save(tmp, ImageFormat.Png);
                        File.Copy(tmp, filePath, overwrite: true);
                        File.Delete(tmp);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarn("[Screenshot] Crop failed: " + ex.Message);
                return false;
            }
        }
    }
}
