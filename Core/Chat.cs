namespace OSWTools
{
    /// <summary>
    /// Chat message helpers.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // Send to a specific platform
    ///   lib.SendTwitchMessage("Hello Twitch!");
    ///   lib.SendYouTubeMessage("Hello YouTube!");
    ///   lib.SendKickMessage("Hello Kick!");
    ///
    ///   // Send as bot account (if configured in SB)
    ///   lib.SendTwitchMessage("Hello!", asBot: true);
    ///
    ///   // Send to ALL platforms at once
    ///   lib.BroadcastMessage("Hello everywhere!");
    ///
    ///   // Send to a platform by name string
    ///   lib.SendMessageToPlatform("youtube", "Hello!");
    ///
    ///   // Apply token substitution before sending (sends to Twitch)
    ///   lib.SendTemplate("Now showing clips from {user}!", "userName", "OSUPhoenix");
    /// </summary>
    public partial class OSWLib
    {
        // ── Platform-specific senders ─────────────────────────────────────────
        //
        // Each method matches the full CPH signature for its platform:
        //   Twitch:  CPH.SendMessage(message, useBot, fallback)
        //   YouTube: CPH.SendYouTubeMessage(message, useBot, fallback, broadcastId)
        //   Kick:    CPH.SendKickMessage(message, useBot, fallback)
        //
        // The asBot parameter controls useBot. When asBot is true, fallback
        // defaults to true as well (meaning: try bot first, fall back to
        // broadcaster if bot isn't connected). Pass fallback: false if you
        // want bot-only with no fallback.

        /// <summary>
        /// Sends a chat message to Twitch.
        ///
        /// Wraps CPH.SendMessage(message, useBot, fallback).
        /// </summary>
        /// <param name="message">The message text to send.</param>
        /// <param name="asBot">
        ///   true  → send using the Twitch Bot account.
        ///   false → send using the Twitch Broadcaster account (default).
        /// </param>
        /// <param name="fallback">
        ///   true  → if asBot is true and bot is offline, fall back to broadcaster (default).
        ///   false → if asBot is true and bot is offline, do nothing.
        /// </param>
        public void SendTwitchMessage(string message, bool asBot = false, bool fallback = true)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _CPH.SendMessage(message, asBot, fallback);
            }
            catch
            {
                LogError("SendTwitchMessage failed: " + message);
            }
        }

        /// <summary>
        /// Sends a message to YouTube chat.
        ///
        /// Wraps CPH.SendYouTubeMessage(message, useBot, fallback, broadcastId).
        /// </summary>
        /// <param name="message">The message text to send.</param>
        /// <param name="asBot">
        ///   true  → send using the YouTube Bot account.
        ///   false → send using the YouTube Broadcaster account (default).
        /// </param>
        /// <param name="fallback">
        ///   true  → if asBot is true and bot is offline, fall back to broadcaster (default).
        ///   false → if asBot is true and bot is offline, do nothing.
        /// </param>
        /// <param name="broadcastId">
        ///   Optional YouTube broadcast ID. Pass null to use the default/active broadcast.
        ///   Only needed for multi-broadcast setups.
        /// </param>
        public void SendYouTubeMessage(string message, bool asBot = false, bool fallback = true, string broadcastId = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _CPH.SendYouTubeMessage(message, asBot, fallback, broadcastId);
            }
            catch
            {
                LogError("SendYouTubeMessage failed: " + message);
            }
        }

        /// <summary>
        /// Sends a message to Kick chat.
        ///
        /// Wraps CPH.SendKickMessage(message, useBot, fallback).
        /// </summary>
        /// <param name="message">The message text to send.</param>
        /// <param name="asBot">
        ///   true  → send using the Kick Bot account.
        ///   false → send using the Kick Broadcaster account (default).
        /// </param>
        /// <param name="fallback">
        ///   true  → if asBot is true and bot is offline, fall back to broadcaster (default).
        ///   false → if asBot is true and bot is offline, do nothing.
        /// </param>
        public void SendKickMessage(string message, bool asBot = false, bool fallback = true)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _CPH.SendKickMessage(message, asBot, fallback);
            }
            catch
            {
                LogError("SendKickMessage failed: " + message);
            }
        }

        // ── Multi-platform senders ────────────────────────────────────────────

        /// <summary>
        /// Sends the same message to all three platforms (Twitch, YouTube, Kick).
        /// Use for announcements that should reach every chat simultaneously.
        ///
        /// Each platform is called by its explicit method — one send per
        /// platform, no duplicates.
        /// </summary>
        public void BroadcastMessage(string message, bool asBot = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            SendTwitchMessage(message, asBot);
            SendYouTubeMessage(message);
            SendKickMessage(message);
        }

        /// <summary>
        /// Sends a message to the specified platform only.
        /// platform: "twitch" | "youtube" | "kick"
        /// Falls back to Twitch for unknown platforms.
        /// </summary>
        public void SendMessageToPlatform(string platform, string message, bool asBot = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            switch ((platform ?? string.Empty).ToLowerInvariant())
            {
                case "youtube": SendYouTubeMessage(message, asBot); break;
                case "kick":    SendKickMessage(message, asBot);    break;
                default:        SendTwitchMessage(message, asBot);  break;
            }
        }

        // ── Backward-compatible sender ────────────────────────────────────────

        /// <summary>
        /// Sends a chat message to Twitch.
        ///
        /// NOTE: This method exists for backward compatibility with existing
        /// tool scripts. For new code, prefer SendTwitchMessage() which makes
        /// the target platform explicit.
        ///
        /// CPH.SendMessage() is Twitch-specific per the Streamer.bot docs.
        /// </summary>
        public void SendMessage(string message, bool asBot = false)
        {
            SendTwitchMessage(message, asBot);
        }

        // ── Reply ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a reply message on Twitch chat.
        ///
        /// NOTE: This currently calls SendTwitchMessage which does a regular
        /// send. For threaded replies, use CPH.TwitchReplyToMessage() directly
        /// with the message ID from args.
        /// </summary>
        public void SendReply(string message, bool asBot = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            SendTwitchMessage(message, asBot);
        }

        // ── Template ──────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a message built from a template string by replacing
        /// {token} placeholders with provided values. Sends to Twitch.
        ///
        /// Tokens and values are paired in order:
        ///   lib.SendTemplate("Hi {user}, you have {points} points!",
        ///       "user",   "OSUPhoenix",
        ///       "points", "500");
        /// </summary>
        public void SendTemplate(string template, params string[] tokenValuePairs)
        {
            if (string.IsNullOrWhiteSpace(template)) return;

            string message = ApplyTemplate(template, tokenValuePairs);
            SendTwitchMessage(message);
        }

        // ── Internal ──────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces {token} placeholders in a template string.
        /// tokenValuePairs must have an even number of elements: token, value, token, value...
        /// </summary>
        internal string ApplyTemplate(string template, params string[] tokenValuePairs)
        {
            if (string.IsNullOrWhiteSpace(template))       return template;
            if (tokenValuePairs == null)                    return template;
            if (tokenValuePairs.Length % 2 != 0)
            {
                LogWarn("ApplyTemplate: odd number of token/value pairs — last token ignored.");
            }

            string result = template;
            for (int i = 0; i + 1 < tokenValuePairs.Length; i += 2)
            {
                string token = tokenValuePairs[i]     ?? "";
                string value = tokenValuePairs[i + 1] ?? "";
                result = result.Replace("{" + token + "}", value);
            }
            return result;
        }
    }
}
