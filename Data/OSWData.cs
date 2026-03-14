using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace OSWTools.Data
{
    /// <summary>
    /// OSWData is the main public API for all data storage in OSW tools.
    /// FileManager and SafeWriter are internal implementation details.
    ///
    /// All data is stored as JSON files under:
    ///   {Streamer.bot folder}/OSWData/{toolName}/{fileName}.json
    ///
    /// Quick reference:
    ///   Save   -> OSWData.Save("Achievements", "progress", myObject);
    ///   Load   -> var obj = OSWData.Load("Achievements", "progress", new MyType());
    ///   Exists -> OSWData.Exists("Achievements", "progress")
    ///   Delete -> OSWData.Delete("Achievements", "progress")
    /// </summary>
    public static class OSWData
    {
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            Formatting           = Formatting.Indented,
            NullValueHandling    = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include
        };

        // ── Save ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Serializes an object to JSON and saves it to disk.
        /// Creates the tool folder automatically if it doesn't exist.
        /// Uses atomic write with backup — safe to call from concurrent actions.
        ///
        /// Example:
        ///   await OSWData.SaveAsync("Achievements", "progress", progressObject);
        /// </summary>
        public static async Task SaveAsync<T>(string toolName, string fileName, T data)
        {
            FileManager.EnsureToolFolder(toolName);
            string path    = FileManager.GetFilePath(toolName, fileName);
            string content = JsonConvert.SerializeObject(data, _settings);
            await SafeWriter.WriteAsync(path, content);
        }

        /// <summary>
        /// Synchronous version of SaveAsync.
        /// Use this when you cannot await (e.g. inside a non-async method).
        ///
        /// Example:
        ///   OSWData.Save("VendingMenu", "inventory", itemList);
        /// </summary>
        public static void Save<T>(string toolName, string fileName, T data)
        {
            SaveAsync(toolName, fileName, data).GetAwaiter().GetResult();
        }

        // ── Load ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads and deserializes a JSON file.
        /// Returns null if the file does not exist.
        ///
        /// Example:
        ///   var progress = OSWData.Load("Achievements", "progress", (UserProgress)null);
        ///   if (progress == null) progress = new UserProgress();
        /// </summary>
        public static T Load<T>(string toolName, string fileName)
        {
            string path = FileManager.GetFilePath(toolName, fileName);
            if (!File.Exists(path)) return default(T);

            string content = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<T>(content, _settings);
        }

        /// <summary>
        /// Loads a file and returns the provided default value if the file doesn't exist.
        /// Eliminates the null-check at the call site.
        ///
        /// Example:
        ///   var items = OSWData.LoadOrDefault("VendingMenu", "inventory", new List&lt;VendingItem&gt;());
        /// </summary>
        public static T LoadOrDefault<T>(string toolName, string fileName, T defaultValue)
        {
            T result = Load<T>(toolName, fileName);
            // For reference types, default(T) is null — return defaultValue instead.
            if (result == null) return defaultValue;
            return result;
        }

        // ── Exists / Delete ───────────────────────────────────────────────────────

        /// <summary>Returns true if a data file exists for the given tool and file name.</summary>
        public static bool Exists(string toolName, string fileName)
        {
            return File.Exists(FileManager.GetFilePath(toolName, fileName));
        }

        /// <summary>
        /// Deletes a data file and its .bak backup if they exist.
        /// Does nothing if the file is not found.
        /// </summary>
        public static void Delete(string toolName, string fileName)
        {
            string path = FileManager.GetFilePath(toolName, fileName);
            if (File.Exists(path)) File.Delete(path);

            string bak = FileManager.GetBackupPath(path);
            if (File.Exists(bak)) File.Delete(bak);
        }

        // ── Backup ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Restores the .bak backup over the main file.
        /// Returns true if a backup was found and restored.
        /// </summary>
        public static bool RestoreBackup(string toolName, string fileName)
        {
            return SafeWriter.RestoreBackup(FileManager.GetFilePath(toolName, fileName));
        }

        // ── Path Helpers ──────────────────────────────────────────────────────────

        /// <summary>Returns the full path to a data file (may or may not exist yet).</summary>
        public static string GetFilePath(string toolName, string fileName)
        {
            return FileManager.GetFilePath(toolName, fileName);
        }

        /// <summary>Returns the folder where a tool's data files are stored.</summary>
        public static string GetDataFolder(string toolName)
        {
            return FileManager.GetToolFolder(toolName);
        }

        /// <summary>Returns all saved .json file paths for a tool.</summary>
        public static IEnumerable<string> GetAllSavedFiles(string toolName)
        {
            return FileManager.GetAllFiles(toolName);
        }
    }
}
