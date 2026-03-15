using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    /// <summary>
    /// OBS WebSocket helper methods.
    ///
    /// All methods use CPH.ObsSendRaw() to talk to OBS via WebSocket.
    /// They are safe to call even when OBS is not connected — they return
    /// sensible defaults and log a warning instead of throwing.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // Scene management
    ///   string scene = lib.ObsGetCurrentScene();
    ///   bool   exists = lib.ObsSceneExists("My Scene");
    ///   lib.ObsCreateScene("My Scene");
    ///
    ///   // Source management
    ///   bool   found = lib.ObsSourceExistsInScene("My Scene", "My Source");
    ///   int    id    = lib.ObsGetSceneItemId("My Scene", "My Source");
    ///   lib.ObsCreateMediaSource("My Scene", "My Source", @"C:\video.mp4");
    ///   lib.ObsCreateBrowserSource("My Scene", "My Source", "http://localhost:1234");
    ///   lib.ObsCreateColorSource("My Scene", "Background", "#1C1C1C");
    ///
    ///   // Transforms
    ///   var t = lib.ObsGetSceneItemTransform("My Scene", itemId);
    ///   double x = t.PositionX;
    ///
    ///   // Move Source filters (requires OBS Move Source Filter plugin)
    ///   lib.ObsCreateMoveFilter("My Scene", "To Target", "My Source",
    ///       posX: 100, posY: 200, scaleX: 1.0, scaleY: 1.0,
    ///       width: 1920, height: 1080, rotation: 0, durationMs: 2000);
    ///   lib.ObsUpdateMoveFilter("My Scene", "To Target", posX: 100, posY: 200, durationMs: 2000);
    ///   lib.ObsEnsureMoveFilter(...);   // create only if not already present
    /// </summary>
    public partial class OSWLib
    {
        // ── Scene management ──────────────────────────────────────────────────────

        /// <summary>Returns the name of the currently active OBS scene.</summary>
        public string ObsGetCurrentScene()
        {
            try { return _CPH.ObsGetCurrentScene(); }
            catch
            {
                LogWarn("ObsGetCurrentScene failed.");
                return string.Empty;
            }
        }

        /// <summary>Returns true if a scene with the given name exists in OBS.</summary>
        public bool ObsSceneExists(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return false;
            try
            {
                string json = _CPH.ObsSendRaw("GetSceneList", "{}", 0);
                JObject response = JsonConvert.DeserializeObject<JObject>(json);
                JArray scenes = response["scenes"] as JArray;
                if (scenes == null) return false;
                foreach (JToken scene in scenes)
                    if (string.Equals(scene["sceneName"]?.ToString(), sceneName, StringComparison.Ordinal))
                        return true;
                return false;
            }
            catch
            {
                LogWarn("ObsSceneExists failed for: " + sceneName);
                return false;
            }
        }

        /// <summary>
        /// Creates a new scene in OBS. Does nothing if the scene already exists.
        /// </summary>
        public void ObsCreateScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return;
            if (ObsSceneExists(sceneName)) return;
            try
            {
                _CPH.ObsSendRaw("CreateScene",
                    "{\"sceneName\":\"" + Esc(sceneName) + "\"}", 0);
                LogInfo("OBS scene created: " + sceneName);
            }
            catch { LogWarn("ObsCreateScene failed for: " + sceneName); }
        }

        // ── Source / item management ──────────────────────────────────────────────

        /// <summary>
        /// Returns true if a source with the given name exists directly in the scene.
        /// Does NOT search nested scenes — use ObsGetSceneItemId for recursive lookup.
        /// </summary>
        public bool ObsSourceExistsInScene(string sceneName, string sourceName)
        {
            return ObsGetSceneItemId(sceneName, sourceName, recursive: false) != -1;
        }

        /// <summary>
        /// Returns the sceneItemId for a source in a scene.
        /// When recursive is true (default), searches nested scenes as well.
        /// Returns -1 if not found.
        /// </summary>
        public int ObsGetSceneItemId(string sceneName, string sourceName, bool recursive = true)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || string.IsNullOrWhiteSpace(sourceName))
                return -1;
            try
            {
                return ObsGetSceneItemIdInternal(sceneName, sourceName, recursive);
            }
            catch
            {
                LogWarn("ObsGetSceneItemId failed — scene: " + sceneName + " source: " + sourceName);
                return -1;
            }
        }

        private int ObsGetSceneItemIdInternal(string sceneName, string sourceName, bool recursive)
        {
            string json = _CPH.ObsSendRaw("GetSceneItemList",
                "{\"sceneName\":\"" + Esc(sceneName) + "\"}", 0);
            if (string.IsNullOrWhiteSpace(json)) return -1;

            JObject response = JsonConvert.DeserializeObject<JObject>(json);
            JArray items = response["sceneItems"] as JArray;
            if (items == null) return -1;

            foreach (JToken item in items)
            {
                if (string.Equals(item["sourceName"]?.ToString(), sourceName, StringComparison.Ordinal))
                    return Convert.ToInt32(item["sceneItemId"]);

                if (recursive && string.Equals(item["sourceType"]?.ToString(), "scene", StringComparison.OrdinalIgnoreCase))
                {
                    int nested = ObsGetSceneItemIdInternal(item["sourceName"].ToString(), sourceName, recursive);
                    if (nested != -1) return nested;
                }
            }
            return -1;
        }

        /// <summary>
        /// Creates a media (video/gif) source in a scene using ffmpeg_source.
        /// Does nothing if the source already exists in the scene.
        /// </summary>
        public void ObsCreateMediaSource(string sceneName, string sourceName, string filePath, bool looping = true)
        {
            if (ObsSourceExistsInScene(sceneName, sourceName)) return;
            try
            {
                string escaped = filePath.Replace("\\", "\\\\");
                string json = "{\"sceneName\":\"" + Esc(sceneName) + "\"," +
                              "\"inputName\":\"" + Esc(sourceName) + "\"," +
                              "\"inputKind\":\"ffmpeg_source\"," +
                              "\"inputSettings\":{" +
                              "\"local_file\":\"" + escaped + "\"," +
                              "\"looping\":" + (looping ? "true" : "false") + "," +
                              "\"is_local_file\":true}," +
                              "\"sceneItemEnabled\":true}";
                _CPH.ObsSendRaw("CreateInput", json, 0);
                LogInfo("OBS media source created: " + sourceName + " in " + sceneName);
            }
            catch { LogWarn("ObsCreateMediaSource failed — " + sourceName); }
        }

        /// <summary>
        /// Creates a browser source in a scene.
        /// Does nothing if the source already exists in the scene.
        /// </summary>
        public void ObsCreateBrowserSource(string sceneName, string sourceName, string url,
            int width = 1920, int height = 1080)
        {
            if (ObsSourceExistsInScene(sceneName, sourceName)) return;
            try
            {
                string json = "{\"sceneName\":\"" + Esc(sceneName) + "\"," +
                              "\"inputName\":\"" + Esc(sourceName) + "\"," +
                              "\"inputKind\":\"browser_source\"," +
                              "\"inputSettings\":{" +
                              "\"url\":\"" + Esc(url) + "\"," +
                              "\"width\":" + width + "," +
                              "\"height\":" + height + "}," +
                              "\"sceneItemEnabled\":true}";
                _CPH.ObsSendRaw("CreateInput", json, 0);
                LogInfo("OBS browser source created: " + sourceName + " in " + sceneName);
            }
            catch { LogWarn("ObsCreateBrowserSource failed — " + sourceName); }
        }

        /// <summary>
        /// Creates a color source in a scene.
        /// Does nothing if the source already exists in the scene.
        /// colorHex is a standard hex string e.g. "#1C1C1C".
        /// </summary>
        public void ObsCreateColorSource(string sceneName, string sourceName, string colorHex = "#000000",
            int width = 1920, int height = 1080)
        {
            if (ObsSourceExistsInScene(sceneName, sourceName)) return;
            try
            {
                // OBS color format is ABGR as a uint
                uint obsColor = HexToObsColor(colorHex);
                string json = "{\"sceneName\":\"" + Esc(sceneName) + "\"," +
                              "\"inputName\":\"" + Esc(sourceName) + "\"," +
                              "\"inputKind\":\"color_source_v3\"," +
                              "\"inputSettings\":{" +
                              "\"color\":" + obsColor + "," +
                              "\"width\":" + width + "," +
                              "\"height\":" + height + "}," +
                              "\"sceneItemEnabled\":true}";
                _CPH.ObsSendRaw("CreateInput", json, 0);
                LogInfo("OBS color source created: " + sourceName + " in " + sceneName);
            }
            catch { LogWarn("ObsCreateColorSource failed — " + sourceName); }
        }

        // ── Scene item transforms ─────────────────────────────────────────────────

        /// <summary>
        /// Holds the key transform properties of an OBS scene item.
        /// All values default to 0 if the transform could not be read.
        /// </summary>
        public class ObsTransform
        {
            public double PositionX { get; set; }
            public double PositionY { get; set; }
            public double Width     { get; set; }
            public double Height    { get; set; }
            public double ScaleX    { get; set; }
            public double ScaleY    { get; set; }
            public double Rotation  { get; set; }
            public int    Alignment { get; set; }
        }

        /// <summary>
        /// Returns the transform data for a scene item by its sceneItemId.
        /// Returns a zeroed ObsTransform if the call fails.
        /// </summary>
        public ObsTransform ObsGetSceneItemTransform(string sceneName, int sceneItemId)
        {
            try
            {
                string json = _CPH.ObsSendRaw("GetSceneItemTransform",
                    "{\"sceneName\":\"" + Esc(sceneName) + "\",\"sceneItemId\":" + sceneItemId + "}", 0);
                JObject response = JsonConvert.DeserializeObject<JObject>(json);
                Dictionary<string, object> t = response["sceneItemTransform"]
                    ?.ToObject<Dictionary<string, object>>();
                if (t == null) return new ObsTransform();

                return new ObsTransform
                {
                    PositionX = GetDouble(t, "positionX"),
                    PositionY = GetDouble(t, "positionY"),
                    Width     = GetDouble(t, "width"),
                    Height    = GetDouble(t, "height"),
                    ScaleX    = GetDouble(t, "scaleX"),
                    ScaleY    = GetDouble(t, "scaleY"),
                    Rotation  = GetDouble(t, "rotation"),
                    Alignment = (int)GetDouble(t, "alignment")
                };
            }
            catch
            {
                LogWarn("ObsGetSceneItemTransform failed — scene: " + sceneName + " id: " + sceneItemId);
                return new ObsTransform();
            }
        }

        // ── Move Source filters ───────────────────────────────────────────────────
        // Requires the OBS Move Source Filter plugin to be installed.

        /// <summary>
        /// Creates a Move Source filter on a scene/source if it doesn't already exist.
        /// Use ObsEnsureMoveFilter when you want create-or-skip behaviour.
        /// </summary>
        public void ObsCreateMoveFilter(string sourceName, string filterName, string targetSource,
            double posX, double posY, double scaleX, double scaleY,
            double width, double height, double rotation, int durationMs)
        {
            try
            {
                var settings = new Dictionary<string, object>
                {
                    { "source",   targetSource },
                    { "pos",      new { x = posX,   y = posY   } },
                    { "scale",    new { x = scaleX, y = scaleY } },
                    { "rot",      rotation },
                    { "duration", durationMs },
                    { "bounds",   new { x = width,  y = height } }
                };

                string json = JsonConvert.SerializeObject(new
                {
                    sourceName   = sourceName,
                    filterName   = filterName,
                    filterKind   = "move_source_filter",
                    filterSettings = settings
                });

                _CPH.ObsSendRaw("CreateSourceFilter", json, 0);
                LogInfo("OBS move filter created: " + filterName + " on " + sourceName);
            }
            catch { LogWarn("ObsCreateMoveFilter failed — " + filterName + " on " + sourceName); }
        }

        /// <summary>
        /// Creates a Move Source filter only if one with that name doesn't already exist.
        /// This is the safe "ensure it exists" version — idempotent, call freely.
        /// </summary>
        public void ObsEnsureMoveFilter(string sourceName, string filterName, string targetSource,
            double posX, double posY, double scaleX, double scaleY,
            double width, double height, double rotation, int durationMs)
        {
            if (ObsFilterExists(sourceName, filterName)) return;
            ObsCreateMoveFilter(sourceName, filterName, targetSource,
                posX, posY, scaleX, scaleY, width, height, rotation, durationMs);
        }

        /// <summary>
        /// Updates the position, scale, and duration of an existing Move Source filter.
        /// Uses the transform_text format expected by the Move Source Filter plugin.
        /// </summary>
        public void ObsUpdateMoveFilter(string sourceName, string filterName,
            double posX, double posY, double scaleX = 1.0, double scaleY = 1.0,
            double rotation = 0, int durationMs = 2000)
        {
            try
            {
                string transformText = string.Format(
                    "pos: x {0} y {1} rot: {2} scale: x {3} y {4} crop: l 0 t 0 r 0 b 0",
                    posX, posY,
                    rotation.ToString("F1"),
                    scaleX.ToString("F3"),
                    scaleY.ToString("F3"));

                string json = JsonConvert.SerializeObject(new
                {
                    sourceName     = sourceName,
                    filterName     = filterName,
                    filterSettings = new
                    {
                        transform_text = transformText,
                        duration       = durationMs
                    }
                });

                _CPH.ObsSendRaw("SetSourceFilterSettings", json, 0);
            }
            catch { LogWarn("ObsUpdateMoveFilter failed — " + filterName + " on " + sourceName); }
        }

        /// <summary>
        /// Returns true if a filter with the given name exists on the source.
        /// </summary>
        public bool ObsFilterExists(string sourceName, string filterName)
        {
            try
            {
                string json = _CPH.ObsSendRaw("GetSourceFilterList",
                    "{\"sourceName\":\"" + Esc(sourceName) + "\"}", 0);
                JObject response = JsonConvert.DeserializeObject<JObject>(json);
                JArray filters = response["filters"] as JArray;
                if (filters == null) return false;
                foreach (JToken f in filters)
                    if (string.Equals(f["filterName"]?.ToString(), filterName, StringComparison.Ordinal))
                        return true;
                return false;
            }
            catch { return false; }
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

        /// <summary>Escapes a string for use inside a JSON string literal.</summary>
        private string Esc(string s)
        {
            return (s ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        /// <summary>Safely reads a double from a string-keyed dictionary.</summary>
        private double GetDouble(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToDouble(v); }
            catch { return 0; }
        }

        /// <summary>
        /// Converts a CSS hex color (#RRGGBB or #AARRGGBB) to the ABGR uint
        /// format that OBS uses for color sources.
        /// </summary>
        private uint HexToObsColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6) hex = "FF" + hex;       // add full alpha
                uint argb = Convert.ToUInt32(hex, 16);
                byte a = (byte)((argb >> 24) & 0xFF);
                byte r = (byte)((argb >> 16) & 0xFF);
                byte g = (byte)((argb >> 8)  & 0xFF);
                byte b = (byte)(argb          & 0xFF);
                return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r; // ABGR
            }
            catch { return 0xFF000000; } // opaque black
        }
    }
}
