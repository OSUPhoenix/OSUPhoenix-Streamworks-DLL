// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Extensions/PredictionExtensions.cs          DLL v1.1.0
//
//  Twitch Prediction wrappers for the partial OSWLib class.
//  Used in Phase 2 of OSW GIF Display (GIF Battle mode) and any
//  future tool that wants to create / resolve / cancel predictions.
//
//  WHY THIS IS IN THE DLL:
//    Prediction creation and resolution involves boilerplate: null
//    checks, reflection to read the returned prediction/outcome IDs
//    from SB's opaque return types, error handling, and debug logging.
//    Centralising this means all future tools get it for free.
//
//  PLACEMENT:
//    Extensions/ → partial OSWLib, CPH-dependent.
//    No csproj change needed — the existing wildcard glob picks it up.
//
//  ── IMPORTANT: VERIFY SIGNATURES AGAINST YOUR SB VERSION ─────────
//    Streamer.bot's CPH interface evolves between releases.
//    The method names and return types below match SB 0.2.x+.
//    If the compiler complains about a method not existing, check
//    SB's "Execute Code" editor autocomplete for the current names.
//
//  USAGE:
//    var lib = new OSWLib(CPH, "GIF Display");
//
//    // Create — returns null on failure
//    string predId = lib.TwitchCreatePrediction("Whose GIF wins?", "Pink Team", "Blue Team", 60);
//
//    // Resolve (pass the winning outcome ID from the prediction object)
//    bool ok = lib.TwitchResolvePrediction(predId, winngId);
//
//    // Cancel
//    lib.TwitchCancelPrediction(predId);
// ═══════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using OSWTools.Utilities;   // ReflectionHelper

namespace OSWTools
{
    public partial class OSWLib
    {
        // ── TwitchCreatePrediction ────────────────────────────────────
        /// <summary>
        /// Creates a Twitch Prediction with exactly two outcomes and
        /// returns a <see cref="TwitchPredictionResult"/> containing
        /// the prediction ID and the two outcome IDs.
        ///
        /// Returns <c>null</c> if creation failed (already active,
        /// API error, etc.).
        ///
        /// <para>Prediction colours are always:
        ///   Outcome 1 → Pink (BLUE in Twitch's API confusingly, but
        ///               displayed as PINK in the Twitch UI)
        ///   Outcome 2 → Blue
        /// </para>
        /// </summary>
        /// <param name="title">The question shown to viewers, max 45 chars.</param>
        /// <param name="outcome1">Label for the first option  (Pink side).</param>
        /// <param name="outcome2">Label for the second option (Blue side).</param>
        /// <param name="durationSeconds">How long the prediction stays open (1–1800).</param>
        public TwitchPredictionResult TwitchCreatePrediction(
            string title,
            string outcome1,
            string outcome2,
            int    durationSeconds)
        {
            try
            {
                // SB's CPH.TwitchPredictionCreate takes (title, outcomes, duration).
                // It returns an opaque object — we use ReflectionHelper to pull out
                // the IDs rather than casting to a specific type that may change.
                var outcomes = new List<string> { outcome1, outcome2 };
                var result   = _CPH.TwitchPredictionCreate(title, outcomes, durationSeconds);

                if (result == null)
                {
                    LogWarn("TwitchCreatePrediction: null returned from CPH — check if a prediction is already active.");
                    return null;
                }

                // The returned object has an "Id" for the prediction itself
                // and an "Outcomes" list, each with their own "Id".
                string predId = ReflectionHelper.GetString(result, "Id");
                if (string.IsNullOrEmpty(predId))
                {
                    // Some SB versions use lowercase "id"
                    predId = ReflectionHelper.GetString(result, "id");
                }

                if (string.IsNullOrEmpty(predId))
                {
                    LogError("TwitchCreatePrediction: could not read prediction ID from result object.");
                    return null;
                }

                // Outcomes is a collection — pull the first two IDs.
                // We try both "Outcomes" and "outcomes" for version safety.
                string outId1 = null, outId2 = null;

                var outcomeList = ReflectionHelper.GetString(result, "Outcomes");
                // ReflectionHelper works property-by-property, so we need a
                // dynamic approach to iterate the outcomes list.
                // Use the raw result object and extract via dynamic cast fallback.
                try
                {
                    dynamic dyn = result;
                    var outs = dyn.Outcomes;

                    int i = 0;
                    foreach (var o in outs)
                    {
                        if (i == 0) outId1 = ReflectionHelper.GetString(o, "Id");
                        if (i == 1) outId2 = ReflectionHelper.GetString(o, "Id");
                        i++;
                        if (i >= 2) break;
                    }
                }
                catch (Exception dynEx)
                {
                    // If dynamic access fails, log and continue — the prediction
                    // was still created, we just can't auto-resolve it later.
                    LogWarn("TwitchCreatePrediction: could not read outcome IDs — " + dynEx.Message);
                }

                LogDebug(string.Format(
                    "Prediction created | ID:{0} | Out1:{1} | Out2:{2}",
                    predId, outId1 ?? "?", outId2 ?? "?"
                ));

                return new TwitchPredictionResult
                {
                    PredictionId = predId,
                    Outcome1Id   = outId1,
                    Outcome2Id   = outId2
                };
            }
            catch (Exception ex)
            {
                LogError("TwitchCreatePrediction failed: " + ex.Message);
                return null;
            }
        }

        // ── TwitchResolvePrediction ───────────────────────────────────
        /// <summary>
        /// Resolves a Twitch Prediction, declaring the winning outcome.
        /// </summary>
        /// <param name="predictionId">The prediction ID from <see cref="TwitchCreatePrediction"/>.</param>
        /// <param name="winngId">The outcome ID (Outcome1Id or Outcome2Id) that won.</param>
        /// <returns>True on success, false if the call failed.</returns>
public bool TwitchResolvePrediction(string predictionId, string winningOutcomeId)
{
    if (string.IsNullOrEmpty(predictionId) || string.IsNullOrEmpty(winningOutcomeId))
    {
        LogWarn("TwitchResolvePrediction: predictionId or winningOutcomeId is null/empty — skipping.");
        return false;
    }

    try
    {
        _CPH.TwitchPredictionResolve(predictionId, winningOutcomeId);
        LogDebug("Prediction resolved | PredID:" + predictionId + " | Winner:" + winningOutcomeId);
        return true;
    }
    catch (Exception ex)
    {
        LogError("TwitchResolvePrediction failed: " + ex.Message);
        return false;
    }
}
        // ── TwitchCancelPrediction ────────────────────────────────────
        /// <summary>
        /// Cancels an active Twitch Prediction and refunds all points.
        /// Use this if a GIF Battle is aborted (e.g. !gifbattle cancel).
        /// </summary>
        /// <param name="predictionId">The prediction ID to cancel.</param>
        /// <returns>True on success, false if the call failed.</returns>
        public bool TwitchCancelPrediction(string predictionId)
        {
            if (string.IsNullOrEmpty(predictionId))
            {
                LogWarn("TwitchCancelPrediction: predictionId is null/empty — skipping.");
                return false;
            }

            try
            {
                _CPH.TwitchPredictionCancel(predictionId);
                LogDebug("Prediction cancelled | PredID:" + predictionId);
                return true;
            }
            catch (Exception ex)
            {
                LogError("TwitchCancelPrediction failed: " + ex.Message);
                return false;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  TwitchPredictionResult
    //  Carries the IDs needed to later resolve or cancel a prediction.
    //  Store this in a session global (serialised JSON) in SB so it
    //  survives between the battle-start action and the resolve action.
    //
    //  Example of storing it:
    //    lib.SetGlobal("gif_activePrediction",
    //        JsonHelper.Serialize(predResult), persisted: false);
    //
    //  Example of reading it back:
    //    var pred = JsonHelper.SafeDeserialize<TwitchPredictionResult>(
    //        lib.GetGlobalSession("gif_activePrediction", ""),
    //        null);
    // ═══════════════════════════════════════════════════════════════
    public class TwitchPredictionResult
    {
        /// <summary>The prediction's unique Twitch ID.</summary>
        public string PredictionId { get; set; }

        /// <summary>ID of the first outcome (Pink / Challenger A).</summary>
        public string Outcome1Id   { get; set; }

        /// <summary>ID of the second outcome (Blue / Challenger B).</summary>
        public string Outcome2Id   { get; set; }
    }
}
