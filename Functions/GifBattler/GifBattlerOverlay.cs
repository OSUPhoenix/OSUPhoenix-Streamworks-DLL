// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerOverlay.cs    DLL v1.3.0
//
//  Everything the engine SAYS to the HTML overlay over the WebSocket.
//
//  WHY THIS FILE EXISTS:
//    The JSON "contract" between C# and the overlay (event names +
//    field names) used to be written inline in five different scripts.
//    Keeping every message in one file means:
//      • you can see the whole contract at a glance, and
//      • step 3 (customization) only has to add fields HERE to reach
//        the overlay — no hunting through actions.
//
//  CONTRACT IS UNCHANGED:
//    Event names and field names below match the current HTML exactly,
//    so the overlay keeps working with no edits in this step.
// ═══════════════════════════════════════════════════════════════════
using Newtonsoft.Json;
using Streamer.bot.Plugin.Interface;

namespace OSWTools.Functions.GifBattler
{
    internal sealed class GifBattlerOverlay
    {
        private readonly IInlineInvokeProxy _cph;

        internal GifBattlerOverlay(IInlineInvokeProxy cph)
        {
            _cph = cph;
        }

        private void Send(object payload)
        {
            _cph.WebsocketBroadcastString(JsonConvert.SerializeObject(payload));
        }

        /// <summary>
        /// Single GIF pop-up.
        /// source: "link" (pasted Giphy URL) or "twitch" (native GIF Keyboard).
        ///
        /// ★ STEP 3: every setting the overlay needs rides along in the
        /// "display" block, so the overlay never has to remember anything
        /// and a settings change applies to the very next GIF.
        /// The old top-level fields (displayMode, durationMs) are kept so
        /// an un-updated overlay still works — it just ignores "display".
        /// durationMs = 0 means "stay until cleared".
        /// </summary>
        internal void ShowGif(GifBattlerConfig cfg, string url, string username, string tier, string source = "link")
        {
            Send(new
            {
                @event      = "gif_show",
                url         = url,
                username    = username,
                tier        = tier,
                source      = source,
                displayMode = cfg.DisplayMode,
                durationMs  = cfg.StayUntilCleared ? 0 : cfg.DurationSeconds * 1000,
                display     = DisplayBlock(cfg)
            });
        }

        /// <summary>
        /// Builds the "display" object the overlay reads. Public to the
        /// assembly so the tests can check the exact JSON contract.
        /// </summary>
        internal static object DisplayBlock(GifBattlerConfig cfg)
        {
            return new
            {
                // screen area
                stageCustom = cfg.UseCustomStageSize,
                stageW      = cfg.StageWidth,
                stageH      = cfg.StageHeight,
                // size
                maxW        = cfg.MaxGifWidth,
                maxH        = cfg.MaxGifHeight,
                enlarge     = cfg.EnlargeSmallGifs,
                byRole      = cfg.SizeByRole,
                scaleMod    = cfg.ScaleModPercent,
                scaleSub    = cfg.ScaleSubPercent,
                scaleViewer = cfg.ScaleViewerPercent,
                // placement
                mode        = cfg.DisplayMode,
                edgePad     = cfg.EdgePadding,
                offsetX     = cfg.OffsetX,
                offsetY     = cfg.OffsetY,
                // time
                stay        = cfg.StayUntilCleared,
                // look
                glowMode    = cfg.GlowMode,
                glowColor   = cfg.GlowColor,
                glowSize    = cfg.GlowSize,
                border      = cfg.BorderWidth,
                radius      = cfg.CornerRadius,
                opacity     = cfg.OpacityPercent,
                username    = cfg.ShowUsername,
                entrance    = cfg.Entrance
            };
        }

        /// <summary>Side-by-side battle scene (A = pink/left, B = blue/right).</summary>
        internal void BattleStart(GifBattlerConfig cfg,
                                  string urlA, string userA, string avatarA,
                                  string urlB, string userB, string avatarB)
        {
            Send(new
            {
                @event     = "gif_battle_start",
                urlA       = urlA,
                userA      = userA,
                avatarA    = avatarA,
                urlB       = urlB,
                userB      = userB,
                avatarB    = avatarB,
                // +10s so the scene outlives the poll and can show the result
                durationMs = (cfg.BattleVotingSeconds + 10) * 1000
            });
        }

        /// <summary>Live vote bars. Percentages, not raw counts.</summary>
        internal void VoteUpdate(double pinkPct, double bluePct)
        {
            Send(new { @event = "vote_update", p1Pct = pinkPct, p2Pct = bluePct });
        }

        /// <summary>winnerSide is "A" or "B".</summary>
        internal void BattleResult(string winnerSide, string winnerUsername)
        {
            Send(new
            {
                @event         = "gif_battle_result",
                winner         = winnerSide,
                winnerUsername = winnerUsername
            });
        }

        /// <summary>Clears whatever is on screen.</summary>
        internal void Dismiss()
        {
            Send(new { @event = "gif_dismiss" });
        }
    }
}
