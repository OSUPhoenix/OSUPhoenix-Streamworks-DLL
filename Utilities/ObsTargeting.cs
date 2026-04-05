using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    /// <summary>
    /// OSWObsTargeting — OBS canvas-space targeting and Move Source Filter alignment.
    /// Part of OSWTools\Utilities\OSWObsTargeting.cs
    ///
    /// Calculates where a mover source needs to be positioned so it lands
    /// centered on a target source, regardless of where the target sits on
    /// the OBS canvas or what alignment either source uses.
    ///
    /// Designed to work hand-in-hand with OSWObsBuilder — call EnsureMoveSourceFilterExists
    /// first to guarantee the filter exists, then AimMoveFilterAtSource to point it.
    ///
    /// TYPICAL USAGE (full setup in ~5 lines):
    ///
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // One-time setup — safe to call every run
    ///   lib.EnsureSceneExists("[My Widget Scene]");
    ///   lib.EnsureSourceExists("[My Widget Scene]", "[V] MySprite", "ffmpeg_source", "{...}");
    ///   lib.EnsureMoveSourceFilterExists("[My Widget Scene]", "[V] MySprite", "To Target", "", 0, 0, 1, 1, 100, 100, 0, 2000);
    ///
    ///   // Point the filter at wherever the camera is right now
    ///   lib.AimMoveFilterAtSource("[My Widget Scene]", "[V] MySprite", "To Target", "Facecam");
    ///
    /// The method auto-detects the current active OBS scene so you don't need
    /// to pass it — it will find the target source even inside nested scenes.
    /// </summary>
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────
        // PRIMARY PUBLIC METHOD
        // ─────────────────────────────────────────────

        /// <summary>
        /// Calculates the canvas-space center of targetSource and updates filterName
        /// on nestedScene so that moverSource will animate to land centered on it.
        ///
        /// Handles all OBS alignment combinations correctly for both the target
        /// and the mover, so position is accurate regardless of how either source
        /// is aligned in the scene.
        ///
        /// The current active OBS scene is detected automatically — you don't need
        /// to pass it. The target is searched recursively through nested scenes.
        /// </summary>
        /// <param name="nestedScene">
        ///   The scene that owns the mover source and the Move Source Filter.
        ///   e.g. "[OSUR&D Widget] Poke-Man's Ball"
        /// </param>
        /// <param name="moverSource">
        ///   The source that physically moves on screen.
        ///   e.g. "[V] Pokeball"
        /// </param>
        /// <param name="filterName">
        ///   The name of the Move Source Filter to update.
        ///   e.g. "To Target"
        /// </param>
        /// <param name="targetSource">
        ///   The source to aim at — typically a camera or image in the active scene.
        ///   e.g. "Facecam"
        /// </param>
        /// <param name="scaleAdjust">
        ///   Optional percentage to scale the mover up or down relative to its
        ///   current OBS scale. 0 = no change, 50 = 50% larger, -25 = 25% smaller.
        ///   Defaults to 0.
        /// </param>
        /// <param name="obsConnection">OBS connection index. Defaults to 0.</param>
        /// <returns>True if the filter was updated successfully, false otherwise.</returns>
        public bool AimMoveFilterAtSource(
            string nestedScene,
            string moverSource,
            string filterName,
            string targetSource,
            double scaleAdjust    = 0.0,
            int    obsConnection  = 0)
        {
            try
            {
                // Step 1 — find which scene is active right now
                string currentScene = _CPH.ObsGetCurrentScene();
                if (string.IsNullOrEmpty(currentScene))
                {
                    LogError("[Targeting] Could not determine the current OBS scene.");
                    return false;
                }

                // Step 2 — locate the target source anywhere in the scene tree
                int targetItemId = GetSceneItemId(currentScene, targetSource, obsConnection);
                if (targetItemId == -1)
                {
                    LogError($"[Targeting] Target source '{targetSource}' not found in scene '{currentScene}'.");
                    return false;
                }

                // Step 3 — read the target's full transform from OBS
                OBSTransform targetTransform = GetTransform(currentScene, targetItemId, obsConnection);
                if (targetTransform == null)
                {
                    LogError($"[Targeting] Could not read transform for '{targetSource}' in '{currentScene}'.");
                    return false;
                }

                // Step 4 — locate the mover source inside the nested scene
                int moverItemId = GetSceneItemId(nestedScene, moverSource, obsConnection);
                if (moverItemId == -1)
                {
                    LogError($"[Targeting] Mover source '{moverSource}' not found in scene '{nestedScene}'.");
                    return false;
                }

                // Step 5 — read the mover's full transform from OBS
                // This gives us real source dimensions (no hardcoding needed)
                OBSTransform moverTransform = GetTransform(nestedScene, moverItemId, obsConnection);
                if (moverTransform == null)
                {
                    LogError($"[Targeting] Could not read transform for '{moverSource}' in '{nestedScene}'.");
                    return false;
                }

                // Step 6 — calculate the canvas-space center of the target
                // OBS position (posX/posY) is the location of the alignment ANCHOR POINT,
                // not necessarily the top-left corner. We resolve the anchor using the
                // alignment bitfield so we always get the true center regardless of how
                // the target source is aligned in the scene.
                double targetCenterX = ResolveAnchorToCenter(
                    targetTransform.PosX,
                    targetTransform.Width,
                    isLeft:   (targetTransform.Alignment & 1) != 0,
                    isRight:  (targetTransform.Alignment & 2) != 0);

                double targetCenterY = ResolveAnchorToCenter(
                    targetTransform.PosY,
                    targetTransform.Height,
                    isLeft:   (targetTransform.Alignment & 4) != 0,   // top = bit 2
                    isRight:  (targetTransform.Alignment & 8) != 0);  // bottom = bit 3

                LogDebug($"[Targeting] Target '{targetSource}' canvas center: ({targetCenterX:F1}, {targetCenterY:F1})");

                // Step 7 — apply scale adjustment to the mover
                // scaleAdjust is a percentage on top of whatever scale OBS already has
                double adjustedScaleX = moverTransform.ScaleX * (1.0 + scaleAdjust / 100.0);
                double adjustedScaleY = moverTransform.ScaleY * (1.0 + scaleAdjust / 100.0);

                // Scaled pixel dimensions of the mover on the canvas
                double scaledMoverW = moverTransform.SourceWidth  * adjustedScaleX;
                double scaledMoverH = moverTransform.SourceHeight * adjustedScaleY;

                LogDebug($"[Targeting] Mover '{moverSource}' scaled size: ({scaledMoverW:F1} x {scaledMoverH:F1}), scale: ({adjustedScaleX:F3}, {adjustedScaleY:F3})");

                // Step 8 — calculate where to put the mover's ANCHOR POINT so its
                // visual center lands on the target's center.
                //
                // The top-left corner of the mover should be at:
                //   (targetCenterX - scaledMoverW/2, targetCenterY - scaledMoverH/2)
                //
                // Then we shift from top-left corner to the mover's own anchor point
                // using the same bitfield logic, so the filter position is correct
                // regardless of how the mover source is aligned.
                double moverAnchorOffsetX = ResolveTopLeftToAnchor(
                    scaledMoverW,
                    isLeft:   (moverTransform.Alignment & 1) != 0,
                    isRight:  (moverTransform.Alignment & 2) != 0);

                double moverAnchorOffsetY = ResolveTopLeftToAnchor(
                    scaledMoverH,
                    isLeft:   (moverTransform.Alignment & 4) != 0,
                    isRight:  (moverTransform.Alignment & 8) != 0);

                int finalPosX = (int)Math.Round((targetCenterX - scaledMoverW / 2.0) + moverAnchorOffsetX);
                int finalPosY = (int)Math.Round((targetCenterY - scaledMoverH / 2.0) + moverAnchorOffsetY);

                LogDebug($"[Targeting] Filter '{filterName}' final position: ({finalPosX}, {finalPosY})");

                // Step 9 — push the calculated values into the Move Source Filter
                var filterSettings = new
                {
                    pos   = new { x = finalPosX, y = finalPosY },
                    scale = new { x = Math.Round(adjustedScaleX, 3),
                                  y = Math.Round(adjustedScaleY, 3) },
                    bounds = new { x = (int)targetTransform.Width,
                                   y = (int)targetTransform.Height }
                };

                string filterSettingsJson = JsonConvert.SerializeObject(filterSettings);
                _CPH.ObsSendRaw(
                    "SetSourceFilterSettings",
                    $"{{\"sourceName\":\"{nestedScene}\"," +
                    $"\"filterName\":\"{filterName}\"," +
                    $"\"filterSettings\":{filterSettingsJson}}}",
                    obsConnection);

                LogInfo($"[Targeting] '{filterName}' aimed at '{targetSource}' → pos ({finalPosX}, {finalPosY}), scale ({adjustedScaleX:F3}, {adjustedScaleY:F3})");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"[Targeting] AimMoveFilterAtSource failed: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // TRANSFORM HELPER
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reads a scene item's full OBS transform and returns it as a typed object.
        /// Returns null if the call fails or the response is missing expected fields.
        /// </summary>
        private OBSTransform GetTransform(string sceneName, int sceneItemId, int obsConnection)
        {
            try
            {
                string json = _CPH.ObsSendRaw(
                    "GetSceneItemTransform",
                    $"{{\"sceneName\":\"{sceneName}\",\"sceneItemId\":{sceneItemId}}}",
                    obsConnection);

                if (string.IsNullOrEmpty(json))
                    return null;

                var root      = JsonConvert.DeserializeObject<JObject>(json);
                var transform = root?["sceneItemTransform"];

                if (transform == null)
                    return null;

                return new OBSTransform
                {
                    PosX         = transform["positionX"]?.Value<double>() ?? 0,
                    PosY         = transform["positionY"]?.Value<double>() ?? 0,
                    Width        = transform["width"]?.Value<double>()     ?? 0,
                    Height       = transform["height"]?.Value<double>()    ?? 0,
                    ScaleX       = transform["scaleX"]?.Value<double>()    ?? 1,
                    ScaleY       = transform["scaleY"]?.Value<double>()    ?? 1,
                    SourceWidth  = transform["sourceWidth"]?.Value<double>()  ?? 0,
                    SourceHeight = transform["sourceHeight"]?.Value<double>() ?? 0,
                    Alignment    = transform["alignment"]?.Value<int>()    ?? 0,
                    Rotation     = transform["rotation"]?.Value<double>()  ?? 0
                };
            }
            catch (Exception ex)
            {
                LogError($"[Targeting] GetTransform failed for item {sceneItemId} in '{sceneName}': {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────
        // ALIGNMENT MATH HELPERS
        // ─────────────────────────────────────────────

        /// <summary>
        /// Given an OBS anchor position along one axis, resolves it to the CENTER
        /// of the source along that axis.
        ///
        /// OBS alignment is a bitfield. For each axis, exactly one of three states applies:
        ///   isLeft=true  → anchor is at the leading edge  (left or top)
        ///   isRight=true → anchor is at the trailing edge (right or bottom)
        ///   both false   → anchor is at the center (OBS default)
        ///
        /// Works identically for X (left/right) and Y (top/bottom) — just pass
        /// the correct bits for the axis you're resolving.
        ///
        /// Example for X axis:
        ///   isLeft  = (alignment & 1) != 0   // bit 0 = left
        ///   isRight = (alignment & 2) != 0   // bit 1 = right
        ///
        /// Example for Y axis:
        ///   isLeft  = (alignment & 4) != 0   // bit 2 = top
        ///   isRight = (alignment & 8) != 0   // bit 3 = bottom
        /// </summary>
        private static double ResolveAnchorToCenter(double anchorPos, double size, bool isLeft, bool isRight)
        {
            // Resolve anchor to the leading edge (top-left corner equivalent)
            double leadingEdge;
            if      (isLeft)  leadingEdge = anchorPos;               // anchor IS the leading edge
            else if (isRight) leadingEdge = anchorPos - size;        // anchor is trailing, shift back
            else              leadingEdge = anchorPos - size / 2.0;  // anchor is center, shift back

            return leadingEdge + size / 2.0;
        }

        /// <summary>
        /// Given the desired TOP-LEFT corner position of a source, calculates where
        /// the anchor point should be placed, accounting for the source's alignment.
        ///
        /// This is the inverse of ResolveAnchorToCenter — used to convert our
        /// calculated "where the top-left should be" back into the anchor coordinate
        /// that OBS and Move Source Filter actually use for positioning.
        ///
        /// Returns the OFFSET from the top-left corner to the anchor point.
        /// Add this to your top-left position to get the anchor position.
        ///
        /// Uses the same isLeft/isRight convention as ResolveAnchorToCenter.
        /// </summary>
        private static double ResolveTopLeftToAnchor(double size, bool isLeft, bool isRight)
        {
            if      (isLeft)  return 0;           // anchor is already at the leading edge
            else if (isRight) return size;        // anchor is at the trailing edge
            else              return size / 2.0;  // anchor is at center
        }
    }

    // ─────────────────────────────────────────────
    // DATA TRANSFER OBJECT
    // ─────────────────────────────────────────────

    /// <summary>
    /// Typed representation of the OBS GetSceneItemTransform response.
    /// Used internally by OSWObsTargeting — not exposed on the public API.
    ///
    /// Key distinction:
    ///   SourceWidth/SourceHeight — the NATIVE resolution of the media file or capture
    ///   Width/Height             — the RENDERED size on the OBS canvas after scaling
    ///   ScaleX/ScaleY            — the multiplier between source and rendered size
    ///
    /// For targeting math we use SourceWidth/SourceHeight × Scale for the mover
    /// (to get its true canvas footprint) and Width/Height for the target
    /// (OBS already gives us the rendered size directly).
    /// </summary>
    internal class OBSTransform
    {
        public double PosX         { get; set; }  // Canvas position of anchor point (X)
        public double PosY         { get; set; }  // Canvas position of anchor point (Y)
        public double Width        { get; set; }  // Rendered width on canvas
        public double Height       { get; set; }  // Rendered height on canvas
        public double ScaleX       { get; set; }  // Horizontal scale multiplier
        public double ScaleY       { get; set; }  // Vertical scale multiplier
        public double SourceWidth  { get; set; }  // Native source width (unscaled)
        public double SourceHeight { get; set; }  // Native source height (unscaled)
        public int    Alignment    { get; set; }  // OBS alignment bitfield
        public double Rotation     { get; set; }  // Rotation in degrees
    }
}
