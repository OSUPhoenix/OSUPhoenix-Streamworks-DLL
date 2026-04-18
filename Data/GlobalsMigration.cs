// =============================================================================
// OSWTools — Data/GlobalsMigration.cs
//
// One-shot helper for the "migrate from SB globals → OSWData JSON" pattern.
//
// DIFFERENT FROM DataMigration.cs:
//   DataMigration.cs is a step-based framework with per-tool meta tracking
//   for complex multi-step upgrades. GlobalsMigration is a simpler primitive:
//   "if this JSON file doesn't exist yet, run this builder to create it."
//
//   Every OSW tool doing v1.0 → v2.0 upgrades needs this simpler pattern.
//   Tools with more complex schema migrations should use DataMigration.cs.
//
// USAGE:
//   // Before (~25 lines):
//   if (OSWData.Exists("SAS", "settings")) return;
//   var s = new SasSettings {
//       VisualMode      = CPH.GetGlobalVar<string>("OSUP_Visual_Mode", true) ?? "Custom",
//       RunCustomVisual = SafeGetBool("OSUP_SAS_Visual_RunCustom"),
//       // ...19 more fields...
//   };
//   OSWData.Save("SAS", "settings", s);
//
//   // After (~10 lines):
//   GlobalsMigration.MigrateIfMissing("SAS", "settings", CPH, r => new SasSettings {
//       VisualMode      = r.GetString("OSUP_Visual_Mode", "Custom"),
//       RunCustomVisual = r.GetBool("OSUP_SAS_Visual_RunCustom"),
//       // ...19 more fields...
//   });
//
// BEHAVIOR:
//   - If the JSON file already exists → no-op, returns false
//   - If the builder throws → catches, logs via the SB log, returns false
//     (doesn't crash the tool's Execute() path)
//   - Save failure is logged but swallowed so we don't retry on every startup
//
// RETURN VALUE:
//   true   = migration ran AND succeeded (a file was written)
//   false  = migration was skipped (file existed) OR migration failed
//
//   If you need to distinguish "already existed" from "failed", check
//   OSWData.Exists() yourself before calling — but that's usually not needed
//   since downstream code will load the file regardless.
// =============================================================================

using System;
using Streamer.bot.Plugin.Interface;

namespace OSWTools.Data
{
    public static class GlobalsMigration
    {
        /// <summary>
        /// If {toolName}/{fileName}.json doesn't exist yet, invoke the builder
        /// to construct an instance of T from globals, then save it.
        ///
        /// The builder receives a SafeGlobalReader so it can pull values
        /// without try/catch boilerplate. Fallbacks are passed as the second
        /// arg to each reader method.
        ///
        /// Returns true if a new file was successfully written. Returns false
        /// if the file already existed OR if any step failed (failures are
        /// logged; the tool is expected to continue with default values).
        /// </summary>
        public static bool MigrateIfMissing<T>(
            string toolName,
            string fileName,
            IInlineInvokeProxy cph,
            Func<SafeGlobalReader, T> buildFromGlobals)
        {
            if (cph == null)            throw new ArgumentNullException("cph");
            if (buildFromGlobals == null) throw new ArgumentNullException("buildFromGlobals");
            if (string.IsNullOrWhiteSpace(toolName))  throw new ArgumentException("toolName required");
            if (string.IsNullOrWhiteSpace(fileName))  throw new ArgumentException("fileName required");

            // Fast path — already migrated
            if (OSWData.Exists(toolName, fileName))
                return false;

            T instance;
            try
            {
                var reader = new SafeGlobalReader(cph, persisted: true);
                instance = buildFromGlobals(reader);
            }
            catch (Exception ex)
            {
                cph.LogWarn("[" + toolName + "] Migration builder failed for '"
                            + fileName + "': " + ex.Message);
                return false;
            }

            try
            {
                OSWData.Save(toolName, fileName, instance);
                cph.LogInfo("[" + toolName + "] Migrated '" + fileName
                            + "' from globals to OSWData JSON.");
                return true;
            }
            catch (Exception ex)
            {
                cph.LogWarn("[" + toolName + "] Save failed during migration of '"
                            + fileName + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Variant for migrating a single JSON-serialized global var. Use this
        /// when the legacy data was already JSON (e.g. the whole achievements
        /// array in one global) rather than scattered across many globals.
        ///
        /// If the global exists but is empty or unparseable, the fallback
        /// value is written instead.
        /// </summary>
        public static bool MigrateJsonGlobalIfMissing<T>(
            string toolName,
            string fileName,
            IInlineInvokeProxy cph,
            string sourceGlobalKey,
            T fallbackOnError)
        {
            if (cph == null) throw new ArgumentNullException("cph");
            if (string.IsNullOrWhiteSpace(toolName))         throw new ArgumentException("toolName required");
            if (string.IsNullOrWhiteSpace(fileName))         throw new ArgumentException("fileName required");
            if (string.IsNullOrWhiteSpace(sourceGlobalKey))  throw new ArgumentException("sourceGlobalKey required");

            if (OSWData.Exists(toolName, fileName))
                return false;

            T instance = fallbackOnError;

            try
            {
                string json = cph.GetGlobalVar<string>(sourceGlobalKey, true) ?? "";
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
                    if (parsed != null) instance = parsed;
                }
            }
            catch (Exception ex)
            {
                cph.LogWarn("[" + toolName + "] JSON global '" + sourceGlobalKey
                            + "' couldn't be parsed, using fallback: " + ex.Message);
                // instance stays as fallbackOnError
            }

            try
            {
                OSWData.Save(toolName, fileName, instance);
                cph.LogInfo("[" + toolName + "] Migrated JSON global '"
                            + sourceGlobalKey + "' → '" + fileName + "'.");
                return true;
            }
            catch (Exception ex)
            {
                cph.LogWarn("[" + toolName + "] Save failed during JSON migration of '"
                            + fileName + "': " + ex.Message);
                return false;
            }
        }
    }
}
