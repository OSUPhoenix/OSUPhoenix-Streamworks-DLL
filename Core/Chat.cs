namespace OSWTools
{
    /// <summary>
    /// Chat message helpers.
    ///
    /// USAGE:
    ///   var lib = new OSWLib(CPH, "My Tool");
    ///
    ///   // Send to whatever platform triggered the current action
    ///   lib.SendMessage("Hello chat!");
    ///
    ///   // Send to a specific platform
    ///   lib.SendMessage("Hello!", "twitch");
    ///
    ///   // Send as bot account (if configured in SB)
    ///   lib.SendMessage("Hello!", asBot: true);
    ///
    ///   // Apply token substitution before sending
    ///   lib.SendTemplate("Now showing clips from {user}!", "userName", "OSUPhoenix");
    /// </summary>
    public partial class OSWLib
    {
        /// <summary>
        /// Sends a chat message on the current platform.
        /// </summary>
        public void SendMessage(string message, bool asBot = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _CPH.SendMessage(message, asBot);
            }
            catch
            {
                LogError("SendMessage failed: " + message);
            }
        }

        /// <summary>
        /// Sends a message to YouTube chat specifically.
        /// </summary>
        public void SendYouTubeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try { _CPH.SendYouTubeMessage(message); }
            catch { LogError("SendYouTubeMessage failed: " + message); }
        }

        /// <summary>
        /// Sends a message to Kick chat specifically.
        /// </summary>
        public void SendKickMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try { _CPH.SendKickMessage(message); }
            catch { LogError("SendKickMessage failed: " + message); }
        }

        /// <summary>
        /// Sends the same message to all three platforms (Twitch, YouTube, Kick).
        /// Use for announcements that should reach every chat simultaneously.
        /// </summary>
        public void BroadcastMessage(string message, bool asBot = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            SendMessage(message, asBot);
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
                case "youtube": SendYouTubeMessage(message); break;
                case "kick":    SendKickMessage(message);    break;
                default:        SendMessage(message, asBot); break;
            }
        }

        /// <summary>
        /// Sends a whisper/reply to a specific user on the current platform.
        /// </summary>
        public void SendReply(string message, bool asBot = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            try
            {
                _CPH.SendMessage(message, asBot);
            }
            catch
            {
                LogError("SendReply failed: " + message);
            }
        }

        /// <summary>
        /// Sends a message built from a template string by replacing
        /// {token} placeholders with provided values.
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
            SendMessage(message);
        }

        // ── Internal ──────────────────────────────────────────────────────────────

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
