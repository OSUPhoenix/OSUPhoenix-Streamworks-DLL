using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OSWTools.Data
{
    /// <summary>
    /// Internal helper that writes JSON files atomically and safely.
    ///
    /// ATOMIC WRITE STRATEGY:
    ///   1. Write content to a .tmp file.
    ///   2. Back up the existing file to .bak.
    ///   3. Move .tmp over the target — File.Move is atomic on the same drive.
    ///
    /// PER-FILE LOCKING:
    ///   Each file path gets its own SemaphoreSlim(1,1) so concurrent writes
    ///   to different files don't block each other unnecessarily.
    ///
    /// This class is internal — your tools use OSWData, not SafeWriter directly.
    /// </summary>
    internal static class SafeWriter
    {
        // One lock per file path.
        private static readonly Dictionary<string, SemaphoreSlim> _locks
            = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lockDictLock = new object();

        // ── Write ─────────────────────────────────────────────────────────────────

        /// <summary>Writes content to a file atomically. Creates the file if it doesn't exist.</summary>
        public static async Task WriteAsync(string filePath, string content)
        {
            SemaphoreSlim semaphore = GetLock(filePath);
            await semaphore.WaitAsync();
            try
            {
                await WriteAtomicAsync(filePath, content);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>Synchronous version. Prefer WriteAsync in most cases.</summary>
        public static void Write(string filePath, string content)
        {
            WriteAsync(filePath, content).GetAwaiter().GetResult();
        }

        // ── Read ──────────────────────────────────────────────────────────────────

        /// <summary>Reads the file content. Returns null if the file does not exist.</summary>
        public static async Task<string> ReadAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            SemaphoreSlim semaphore = GetLock(filePath);
            await semaphore.WaitAsync();
            try
            {
                return await ReadAllTextAsync(filePath);
            }
            finally
            {
                semaphore.Release();
            }
        }

        // ── Backup / Restore ──────────────────────────────────────────────────────

        /// <summary>
        /// Restores the .bak backup file over the main file if a backup exists.
        /// Returns true if a backup was found and restored.
        /// </summary>
        public static bool RestoreBackup(string filePath)
        {
            string bakPath = FileManager.GetBackupPath(filePath);
            if (!File.Exists(bakPath)) return false;

            SemaphoreSlim semaphore = GetLock(filePath);
            semaphore.Wait();
            try
            {
                File.Copy(bakPath, filePath, overwrite: true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                semaphore.Release();
            }
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private static async Task WriteAtomicAsync(string filePath, string content)
        {
            string tmpPath = FileManager.GetTempPath(filePath);
            string bakPath = FileManager.GetBackupPath(filePath);

            // Step 1: Write to .tmp
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(bytes, 0, bytes.Length);
                await fs.FlushAsync();
            }

            // Step 2: Back up the current file if it exists
            if (File.Exists(filePath))
                File.Copy(filePath, bakPath, overwrite: true);

            // Step 3: Move .tmp to final path (atomic on same drive)
            File.Delete(filePath);
            File.Move(tmpPath, filePath);
        }

        // File.ReadAllTextAsync doesn't exist in net472 — use StreamReader
        private static async Task<string> ReadAllTextAsync(string filePath)
        {
            using (StreamReader sr = new StreamReader(filePath, Encoding.UTF8))
            {
                return await sr.ReadToEndAsync();
            }
        }

        private static SemaphoreSlim GetLock(string filePath)
        {
            lock (_lockDictLock)
            {
                SemaphoreSlim sem;
                if (!_locks.TryGetValue(filePath, out sem))
                {
                    sem = new SemaphoreSlim(1, 1);
                    _locks[filePath] = sem;
                }
                return sem;
            }
        }
    }
}
