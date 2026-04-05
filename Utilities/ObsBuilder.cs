using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    /// <summary>
    /// OSWObsBuilder — OBS auto-provisioning helpers.
    /// Part of OSWTools\Utilities\OSWObsBuilder.cs
    ///
    /// All methods follow an "EnsureXxxExists" pattern:
    ///   - Check whether the thing already exists in OBS
    ///   - Create it only if missing
    ///   - Return true if it exists or was created successfully
    ///   - Return false and log the reason if something went wrong
    ///
    /// Every method is safe to call on every tool startup —
    /// it will never duplicate something that's already in OBS.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///   lib.EnsureSceneExists("My Scene");
    ///   lib.EnsureSourceExists("My Scene", "My Source", "image_source", "{\"file\":\"C:\\\\img.png\"}");
    ///   lib.EnsureMoveSourceFilterExists("My Scene", "Fly In", "Camera", 0, 0, 1.0, 1.0, 100, 100, 0, 2000);
    ///   int id = lib.GetSceneItemId("My Scene", "Camera");
    /// </summary>
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────
        // SCENES
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether a scene exists in OBS and creates it if not.
        /// Returns true if the scene exists or was created successfully.
        /// </summary>
        /// <param name="sceneName">The exact OBS scene name to check or create.</param>
        /// <param name="obsConnection">OBS connection index. Defaults to 0.</param>
        public bool EnsureSceneExists(string sceneName, int obsConnection = 0)
        {
            try
            {
                if (ObsSceneExists(sceneName, obsConnection))
                    return true;

                _CPH.ObsSendRaw("CreateScene", $"{{\"sceneName\":\"{sceneName}\"}}", obsConnection);
                LogInfo($"[ObsBuilder] Created scene: '{sceneName}'");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"[ObsBuilder] EnsureSceneExists failed for '{sceneName}': {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // SOURCES / INPUTS
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether a source exists in a scene and creates it if not.
        ///
        /// inputKind examples:
        ///   "ffmpeg_source"         — video / GIF / WebM
        ///   "image_source"          — still image
        ///   "browser_source"        — browser / HTML overlay
        ///   "wasapi_output_capture" — desktop audio capture
        ///   "wasapi_input_capture"  — microphone capture
        ///
        /// inputSettingsJson is the raw OBS JSON settings string for that source type.
        /// Examples:
        ///   ffmpeg_source:  "{\"local_file\":\"C:\\\\file.webm\",\"looping\":true,\"is_local_file\":true}"
        ///   image_source:   "{\"file\":\"C:\\\\image.png\"}"
        ///   browser_source: "{\"url\":\"http://localhost:8080\",\"width\":1920,\"height\":1080}"
        ///
        /// Returns true if the source exists or was created successfully.
        /// </summary>
        /// <param name="sceneName">The scene to add the source to.</param>
        /// <param name="sourceName">The exact name for the source.</param>
        /// <param name="inputKind">The OBS input kind identifier string.</param>
        /// <param name="inputSettingsJson">Raw OBS JSON settings for this input kind.</param>
        /// <param name="obsConnection">OBS connection index. Defaults to 0.</param>
        public bool EnsureSourceExists(
            string sceneName,
            string sourceName,
            string inputKind,
            string inputSettingsJson,
            int obsConnection = 0)
        {
            try
            {
                if (ObsSourceExistsInScene(sceneName, sourceName, obsConnection))
                    return true;

                // CreateInput both registers the input globally and adds it to the scene
                string createJson = $"{{" +
                    $"\"sceneName\":\"{sceneName}\"," +
                    $"\"inputName\":\"{sourceName}\"," +
                    $"\"inputKind\":\"{inputKind}\"," +
                    $"\"inputSettings\":{inputSettingsJson}," +
                    $"\"sceneItemEnabled\":true" +
                    $"}}";

                _CPH.ObsSendRaw("CreateInput", createJson, obsConnection);
                LogInfo($"[ObsBuilder] Created source '{sourceName}' ({inputKind}) in scene '{sceneName}'");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"[ObsBuilder] EnsureSourceExists failed for '{sourceName}' in '{sceneName}': {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // FILTERS — GENERIC
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether a filter exists on a source and creates it if not.
        /// This is the generic version — works for any OBS filter type.
        ///
        /// filterKind examples:
        ///   "move_source_filter"      — Move Source Filter plugin
        ///   "color_correction_filter" — built-in color correction
        ///   "gpu_delay"               — built-in render delay
        ///   "chroma_key_filter"       — built-in chroma key
        ///
        /// filterSettingsJson is the raw OBS JSON settings for that filter type.
        /// Example for color correction: "{\"brightness\":0.5,\"contrast\":0.2}"
        ///
        /// Returns true if the filter exists or was created successfully.
        /// </summary>
        /// <param name="sourceName">The source (or scene) to add the filter to.</param>
        /// <param name="filterName">The display name for the filter in OBS.</param>
        /// <param name="filterKind">The OBS filter kind identifier string.</param>
        /// <param name="filterSettingsJson">Raw OBS JSON settings for this filter kind.</param>
        /// <param name="obsConnection">OBS connection index. Defaults to 0.</param>
        public bool EnsureFilterExists(
            string sourceName,
            string filterName,
            string filterKind,
            string filterSettingsJson,
            int obsConnection = 0)
        {
            try
            {
                if (ObsFilterExistsOnSource(sourceName, filterName, obsConnection))
                    return true;

                string createJson = $"{{" +
                    $"\"sourceName\":\"{sourceName}\"," +
                    $"\"filterName\":\"{filterName}\"," +
                    $"\"filterKind\":\"{filterKind}\"," +
                    $"\"filterSettings\":{filterSettingsJson}" +
                    $"}}";

                _CPH.ObsSendRaw("CreateSourceFilter", createJson, obsConnection);
                LogInfo($"[ObsBuilder] Created filter '{filterName}' ({filterKind}) on '{sourceName}'");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"[ObsBuilder] EnsureFilterExists failed for '{filterName}' on '{sourceName}': {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // FILTERS — MOVE SOURCE FILTER (typed convenience wrapper)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether a Move Source Filter exists on a source and creates it if not.
        /// Typed convenience wrapper around EnsureFilterExists for the move_source_filter plugin.
        ///
        /// Parameters map directly to Move Source Filter settings:
        ///   targetSource  — the source the filter targets (pass empty string if unused)
        ///   posX / posY   — destination position in OBS canvas pixels
        ///   scaleX/scaleY — scale multiplier (1.0 = original size)
        ///   width/height  — bounds size
        ///   rotation      — rotation in degrees
        ///   duration      — animation duration in milliseconds
        ///
        /// Returns true if the filter exists or was created successfully.
        /// </summary>
        public bool EnsureMoveSourceFilterExists(
            string sourceName,
            string filterName,
            string targetSource,
            double posX,
            double posY,
            double scaleX,
            double scaleY,
            double width,
            double height,
            double rotation,
            int    duration,
            int    obsConnection = 0)
        {
            try
            {
                // Build settings as a typed anonymous object so JsonConvert
                // handles all the nesting and escaping — no hand-crafted JSON strings
                var settings = new
                {
                    source   = targetSource,
                    pos      = new { x = posX,   y = posY },
                    scale    = new { x = scaleX, y = scaleY },
                    bounds   = new { x = width,  y = height },
                    rot      = rotation,
                    duration = duration
                };

                string filterSettingsJson = JsonConvert.SerializeObject(settings);

                // Delegate to the generic method so all existence-check
                // and creation logic lives in exactly one place
                return EnsureFilterExists(
                    sourceName,
                    filterName,
                    "move_source_filter",
                    filterSettingsJson,
                    obsConnection);
            }
            catch (Exception ex)
            {
                LogError($"[ObsBuilder] EnsureMoveSourceFilterExists failed for '{filterName}' on '{sourceName}': {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // SCENE ITEM LOOKUP
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns the sceneItemId for a named source in a scene.
        /// Searches recursively into nested scenes automatically.
        /// Returns -1 if the source is not found anywhere in the scene tree.
        ///
        /// Useful when you need to perform transform or visibility operations
        /// that require the sceneItemId rather than just the source name.
        /// </summary>
        /// <param name="sceneName">The top-level scene to search.</param>
        /// <param name="sourceName">The source name to find.</param>
        /// <param name="obsConnection">OBS connection index. Defaults to 0.</param>
        public int GetSceneItemId(string sceneName, string sourceName, int obsConnection = 0)
        {
            try
            {
                string responseJson = _CPH.ObsSendRaw(
                    "GetSceneItemList",
                    $"{{\"sceneName\":\"{sceneName}\"}}",
                    obsConnection);

                if (string.IsNullOrEmpty(responseJson))
                {
                    LogWarn($"[ObsBuilder] GetSceneItemList returned empty for scene '{sceneName}'");
                    return -1;
                }

                var response   = JsonConvert.DeserializeObject<JObject>(responseJson);
                var sceneItems = response["sceneItems"] as JArray;

                if (sceneItems == null)
                {
                    LogWarn($"[ObsBuilder] No sceneItems found in scene '{sceneName}'");
                    return -1;
                }

                foreach (var item in sceneItems)
                {
                    string itemName = item["sourceName"]?.ToString();

                    if (itemName == sourceName)
                        return Convert.ToInt32(item["sceneItemId"]);

                    // Recurse into any nested scenes automatically
                    if (item["sourceType"]?.ToString() == "scene")
                    {
                        int nestedId = GetSceneItemId(itemName, sourceName, obsConnection);
                        if (nestedId != -1)
                            return nestedId;
                    }
                }

                LogWarn($"[ObsBuilder] Source '{sourceName}' not found in scene '{sceneName}'");
                return -1;
            }
            catch (Exception ex)
            {
                LogError($"[ObsBuilder] GetSceneItemId failed for '{sourceName}' in '{sceneName}': {ex.Message}");
                return -1;
            }
        }

        // ─────────────────────────────────────────────
        // PRIVATE HELPERS
        // Pure checkers — no logging, used only by the Ensure methods above.
        // Prefixed with "Obs" to avoid name collisions with other partial files.
        // ─────────────────────────────────────────────

        private bool ObsSceneExists(string sceneName, int obsConnection)
        {
            string json     = _CPH.ObsSendRaw("GetSceneList", "{}", obsConnection);
            var    response = JsonConvert.DeserializeObject<JObject>(json);
            var    scenes   = response["scenes"] as JArray;

            if (scenes == null) return false;

            foreach (var scene in scenes)
            {
                if (scene["sceneName"]?.ToString() == sceneName)
                    return true;
            }

            return false;
        }

        private bool ObsSourceExistsInScene(string sceneName, string sourceName, int obsConnection)
        {
            string json     = _CPH.ObsSendRaw("GetSceneItemList", $"{{\"sceneName\":\"{sceneName}\"}}", obsConnection);
            var    response = JsonConvert.DeserializeObject<JObject>(json);
            var    items    = response["sceneItems"] as JArray;

            if (items == null) return false;

            foreach (var item in items)
            {
                if (item["sourceName"]?.ToString() == sourceName)
                    return true;
            }

            return false;
        }

        private bool ObsFilterExistsOnSource(string sourceName, string filterName, int obsConnection)
        {
            string json     = _CPH.ObsSendRaw("GetSourceFilterList", $"{{\"sourceName\":\"{sourceName}\"}}", obsConnection);
            var    response = JsonConvert.DeserializeObject<JObject>(json);
            var    filters  = response["filters"] as JArray;

            if (filters == null) return false;

            foreach (var filter in filters)
            {
                if (filter["filterName"]?.ToString() == filterName)
                    return true;
            }

            return false;
        }
    }
}
