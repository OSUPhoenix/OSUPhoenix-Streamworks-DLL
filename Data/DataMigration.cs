using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace OSWTools.Data
{
    /// <summary>The result of running a single migration step.</summary>
    public class MigrationResult
    {
        public bool     Succeeded   { get; set; }
        public string   ToolName    { get; set; }
        public string   Description { get; set; }
        public string   Message     { get; set; }
        public DateTime RanAt       { get; set; }
    }

    /// <summary>
    /// Base class for a one-time data migration step.
    /// Subclass this to migrate data from globals to files, or to upgrade a schema.
    ///
    /// Example:
    ///   public class MigrateGlobalsToFiles : MigrationStep
    ///   {
    ///       public override string ToolName      { get { return "Achievements"; } }
    ///       public override string Description   { get { return "Move data from global vars to JSON files"; } }
    ///       public override string TargetVersion { get { return "1.0.0"; } }
    ///
    ///       public override bool Migrate()
    ///       {
    ///           // read old data, write via OSWData.Save(...)
    ///           return true; // true = success, step won't run again
    ///       }
    ///   }
    /// </summary>
    public abstract class MigrationStep
    {
        /// <summary>The tool this migration belongs to. Must match the toolName used in OSWData calls.</summary>
        public abstract string ToolName { get; }

        /// <summary>Human-readable description shown in migration logs.</summary>
        public abstract string Description { get; }

        /// <summary>
        /// Version tag for this step. Used to track whether it has already run.
        /// Use a unique string per step — e.g. "1.0.0", "2.0.0-schema", etc.
        /// </summary>
        public abstract string TargetVersion { get; }

        /// <summary>Performs the migration. Return true on success, false on failure.</summary>
        public abstract bool Migrate();
    }

    /// <summary>
    /// Runs one-time data migration steps for OSW tools.
    ///
    /// Each registered step runs exactly once — completion is tracked in a
    /// _meta.json file per tool. Re-running is safe: already-completed steps
    /// are skipped automatically.
    ///
    /// Usage (call once at tool startup):
    ///   DataMigration.RegisterStep(new MigrateGlobalsToFiles());
    ///   var results = DataMigration.RunPendingMigrations("Achievements");
    ///   foreach (var r in results)
    ///       if (!r.Succeeded) LogError(r.Message);
    /// </summary>
    public static class DataMigration
    {
        private static readonly Dictionary<string, List<MigrationStep>> _steps
            = new Dictionary<string, List<MigrationStep>>(StringComparer.OrdinalIgnoreCase);

        private const string MetaFileName = "_meta";

        // ── Registration ──────────────────────────────────────────────────────────

        /// <summary>Registers a migration step. Steps run in registration order.</summary>
        public static void RegisterStep(MigrationStep step)
        {
            if (!_steps.ContainsKey(step.ToolName))
                _steps[step.ToolName] = new List<MigrationStep>();

            _steps[step.ToolName].Add(step);
        }

        // ── Run ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs all pending migration steps for a tool.
        /// A step is pending if it has not been recorded as completed in _meta.json.
        /// Returns results for every step that was attempted.
        /// </summary>
        public static List<MigrationResult> RunPendingMigrations(string toolName)
        {
            List<MigrationResult> results = new List<MigrationResult>();

            List<MigrationStep> steps;
            if (!_steps.TryGetValue(toolName, out steps) || steps.Count == 0)
                return results;

            ToolMeta meta = LoadMeta(toolName);

            foreach (MigrationStep step in steps)
            {
                if (meta.CompletedSteps.Contains(step.TargetVersion))
                    continue; // already ran

                bool success = false;
                string message;

                try
                {
                    success = step.Migrate();
                    message = success
                        ? "Migration '" + step.Description + "' completed successfully."
                        : "Migration '" + step.Description + "' returned false (check your Migrate() logic).";
                }
                catch (Exception ex)
                {
                    message = "Migration '" + step.Description + "' threw an exception: " + ex.Message;
                }

                results.Add(new MigrationResult
                {
                    Succeeded   = success,
                    ToolName    = toolName,
                    Description = step.Description,
                    Message     = message,
                    RanAt       = DateTime.Now
                });

                if (success)
                {
                    meta.CompletedSteps.Add(step.TargetVersion);
                    SaveMeta(toolName, meta);
                }
            }

            return results;
        }

        // ── Helpers for Writing Migration Steps ───────────────────────────────────

        /// <summary>
        /// Reads raw text from a file path.
        /// Returns null if the file does not exist.
        /// </summary>
        public static string ReadRawFile(string filePath)
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }

        /// <summary>
        /// Deserializes JSON from a file path into the given type.
        /// Returns the type's default value if the file doesn't exist or can't be parsed.
        /// </summary>
        public static T ReadOldJson<T>(string filePath)
        {
            if (!File.Exists(filePath)) return default(T);
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(filePath));
            }
            catch (Exception)
            {
                return default(T);
            }
        }

        // ── Meta (internal) ───────────────────────────────────────────────────────

        private class ToolMeta
        {
            public HashSet<string> CompletedSteps { get; set; }

            public ToolMeta()
            {
                CompletedSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static ToolMeta LoadMeta(string toolName)
        {
            return OSWData.LoadOrDefault(toolName, MetaFileName, new ToolMeta());
        }

        private static void SaveMeta(string toolName, ToolMeta meta)
        {
            OSWData.Save(toolName, MetaFileName, meta);
        }
    }
}
