// =============================================================================
// OSWTools — Extensions/BUEPayloads.cs                               (NEW)
//
// "Blow Up Emote" (module code: BUE) — the messages sent to the OBS widget.
//
// WHY THIS FILE EXISTS
//   The widget (BUEWidget.html) and the C# side agree on the SHAPE of the JSON
//   they pass over the WebSocket. That shape is a contract. In v1 the chat
//   action built the JSON itself, and the Set Emote action built its own copy.
//   Two copies drift apart: add a field to one, forget the other, and the
//   widget behaves differently depending on which action ran last.
//
//   Now there is ONE place that builds each message. Anything that needs to
//   talk to the widget calls BuePayloads.Update(...) or BuePayloads.State(...)
//   and gets exactly the same shape. Change the contract here, once.
//
// THE TWO MESSAGES
//   BUE_Update  "a chat message just grew the balloon" - the widget animates it.
//   BUE_State   "here is how things are right now" - the widget just syncs, no
//               growth animation. Sent when the emote changes, when settings are
//               saved, and when a widget (re)connects and asks for it.
//
//   Both carry `settings`, so changing a setting reaches the widget on the very
//   next message with no extra step.
// =============================================================================

using Newtonsoft.Json;

namespace OSWTools.BlowUpEmote
{
    public static class BuePayloads
    {
        /// <summary>Message for "a chat message grew the balloon".</summary>
        public static string Update(
            BueTargetConfig config,
            BueState state,
            BueContributionResult result,
            string user,
            string platform,
            string emoteName,
            string emoteImage)
        {
            var payload = new
            {
                type = "BUE_Update",
                targetId = config.TargetId,
                fillPercent = state.FillPercent,
                growthApplied = result.GrowthApplied,
                matchCount = result.MatchCount,
                contributor = user,
                platform = platform,
                popped = result.Popped,
                active = BlowUpEmoteEngine.IsBalloonActive(config, state),   // false after a pop when StartHidden
                popBehavior = config.PopBehavior.ToString(),
                totalPops = state.TotalPops,
                emoteName = emoteName ?? "",
                emoteImage = emoteImage ?? "",
                settings = Settings(config)
            };
            return JsonConvert.SerializeObject(payload);
        }

        /// <summary>Message for "here is the current situation" (no growth animation).</summary>
        /// <param name="reset">true = the round was cancelled: the widget empties, hides and clears its round board with no pop.</param>
        /// <param name="started">true = a round has just been started: the widget springs the balloon in.</param>
        public static string State(BueTargetConfig config, BueState state, string emoteName, string emoteImage, bool started = false, bool reset = false)
        {
            var payload = new
            {
                type = "BUE_State",
                targetId = config.TargetId,
                fillPercent = state.FillPercent,
                totalPops = state.TotalPops,
                active = BlowUpEmoteEngine.IsBalloonActive(config, state),
                started = started,
                reset = reset,
                lastPoppedBy = state.LastPoppedBy ?? "",
                emoteName = emoteName ?? "",
                emoteImage = emoteImage ?? "",
                settings = Settings(config)
            };
            return JsonConvert.SerializeObject(payload);
        }

        // The settings the widget cares about. Add new widget-facing settings here.
        private static object Settings(BueTargetConfig c)
        {
            return new
            {
                showProgress = c.ShowProgress,
                deflatePerSecond = c.DeflatePerSecond      // widget draws the drain between messages
            };
        }
    }
}
