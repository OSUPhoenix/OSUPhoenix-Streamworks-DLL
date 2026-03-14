using System;
using System.Collections.Generic;
using System.IO;

namespace OSWTools.Data
{
    /// <summary>
    /// Internal helper that constructs all file paths used by OSWData and SafeWriter.
    /// All OSW data lives under: {Streamer.bot folder}/OSWData/{ToolName}/{fileName}.json
    ///
    /// This class is internal — your scripts use OSWData, not FileManager directly.
    /// </summary>
    internal static class FileManager
    {
        // ── Root ──────────────────────────────────────────────────────────────────

        // AppDomain.CurrentDomain.BaseDirectory points to wherever Streamer.bot.exe
        // lives, making OSWData a sibling folder to the executable.
        private static string DataRoot
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OSWData"); }
        }

        // ── Path builders ─────────────────────────────────────────────────────────

        /// <summary>Returns the folder for a specific tool. e.g. .../OSWData/SAS/</summary>
        public static string GetToolFolder(string toolName)
        {
            return Path.Combine(DataRoot, Sanitize(toolName));
        }

        /// <summary>
        /// Returns the full path for a data file.
        /// Appends .json automatically if no extension is present.
        /// </summary>
        public static string GetFilePath(string toolName, string fileName)
        {
            string name = Path.HasExtension(fileName) ? fileName : fileName + ".json";
            return Path.Combine(GetToolFolder(toolName), Sanitize(name));
        }

        /// <summary>Returns the .tmp write path for a given file path.</summary>
        public static string GetTempPath(string filePath)   { return filePath + ".tmp"; }

        /// <summary>Returns the .bak backup path for a given file path.</summary>
        public static string GetBackupPath(string filePath) { return filePath + ".bak"; }

        // ── Folder management ─────────────────────────────────────────────────────

        /// <summary>
        /// Creates the OSWData root folder if it doesn't exist.
        /// Called once during OSWLib static initialization.
        /// </summary>
        public static void EnsureRootFolder()
        {
            Directory.CreateDirectory(DataRoot);
        }

        /// <summary>Creates a tool's data folder if it doesn't already exist.</summary>
        public static void EnsureToolFolder(string toolName)
        {
            Directory.CreateDirectory(GetToolFolder(toolName));
        }

        /// <summary>Returns true if the tool's data folder exists.</summary>
        public static bool ToolFolderExists(string toolName)
        {
            return Directory.Exists(GetToolFolder(toolName));
        }

        /// <summary>Returns all .json files in a tool's data folder.</summary>
        public static IEnumerable<string> GetAllFiles(string toolName)
        {
            string folder = GetToolFolder(toolName);
            if (!Directory.Exists(folder)) return new string[0];
            return Directory.GetFiles(folder, "*.json");
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c.ToString(), "_");
            return name;
        }
    }
}
