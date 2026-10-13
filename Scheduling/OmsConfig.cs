// OSWTools/Scheduling/OmsConfig.cs
//
// Data model for OSWData/OMS/config.json
//
// This file defines the shape of everything OMS saves to disk.
// Rules for this file:
//   - No logic here. Pure data classes only.
//   - All classes are internal — SB action scripts never touch these directly.
//   - All properties have safe defaults so LoadOrDefault() always gives a usable object.
//   - Access token is intentionally absent — it's session-only and never persisted.
//
// Encryption policy (enforced in OmsConfigManager, not here):
//   - google.refreshToken  → DPAPI encrypted on disk (belongs to OSW's OAuth app)
//   - everything else      → plain text (user's own data, on their own machine)

using System.Collections.Generic;

namespace OSWTools.Scheduling
{
    // Root object — maps 1:1 to config.json
    internal class OmsConfig
    {
        public OmsPlatforms    Platforms    { get; set; } = new OmsPlatforms();
        public OmsIntegrations Integrations { get; set; } = new OmsIntegrations();
        public OmsTwitchConfig Twitch       { get; set; } = new OmsTwitchConfig();
        public OmsYouTubeConfig YouTube     { get; set; } = new OmsYouTubeConfig();
        public OmsGoogleConfig Google       { get; set; } = new OmsGoogleConfig();
        public OmsDiscordConfig Discord     { get; set; } = new OmsDiscordConfig();

        // Windows timezone ID (e.g. "Central Standard Time").
        // Stored as the Windows name because that's what TimeZoneInfo.FindSystemTimeZoneById()
        // expects, and what we show in the dropdown. Converted to IANA at API call time.
        public string Timezone { get; set; } = "Central Standard Time";
    }

    // Which streaming platforms the user is active on.
    // At least one must be true — enforced in the settings form, not here.
    internal class OmsPlatforms
    {
        public bool Twitch  { get; set; } = true;
        public bool YouTube { get; set; } = false;
    }

    // Optional integrations that can be toggled independently of platform selection.
    // Google Calendar requires Google OAuth regardless of whether YouTube is enabled.
    // Discord requires only a webhook URL — no OAuth needed.
    internal class OmsIntegrations
    {
        public bool GoogleCalendar { get; set; } = false;
        public bool Discord        { get; set; } = false;
    }

    // Twitch credentials and channel info.
    // ClientId and ClientSecret belong to the USER's own Twitch developer app —
    // they are not OSW credentials and therefore are NOT encrypted.
    // BroadcasterId is fetched automatically from the Helix API when the user saves
    // settings — they never type this themselves.
    internal class OmsTwitchConfig
    {
        public string ClientId      { get; set; } = "";
        public string ClientSecret  { get; set; } = "";

        // Numeric Twitch user ID. Required by the schedule segment API endpoint.
        // Auto-fetched via GetUsers on settings save — stored so we don't re-fetch every time.
        public string BroadcasterId { get; set; } = "";

        public string ChannelName   { get; set; } = "";
    }

    // YouTube channel info.
    // No credentials stored here — YouTube auth flows through Google OAuth (OmsGoogleConfig).
    // ChannelId is fetched from the YouTube Data API after OAuth succeeds, same pattern
    // as BroadcasterId on the Twitch side.
    internal class OmsYouTubeConfig
    {
        public string ChannelHandle { get; set; } = "";

        // Fetched automatically after first successful Google auth.
        // Required for liveBroadcasts.insert API calls.
        public string ChannelId     { get; set; } = "";
    }

    // Google OAuth state.
    // IMPORTANT: Only the refresh token lives here.
    // The access token (short-lived, ~1 hour) is held in memory by OmsOAuthManager
    // and is never written to disk at all.
    //
    // The refresh token IS written to disk, but is DPAPI-encrypted by OmsConfigManager
    // before Save() is called. What you see in config.json for this field is a base64
    // ciphertext blob, not the raw token.
    //
    // The refresh token covers both Google Calendar and YouTube — one token, one auth flow,
    // both scopes. The user authorizes once.
    internal class OmsGoogleConfig
    {
        // Stored encrypted. OmsConfigManager handles encrypt-on-save / decrypt-on-load.
        // In memory (after Load()) this is always the plaintext value.
        // On disk (in config.json) this is always the DPAPI ciphertext.
        public string RefreshToken { get; set; } = "";
    }

    // Discord webhook configuration.
    // All plain text — this is entirely the user's own data.
    // AvatarSource can be either:
    //   - A URL (https://...) → stored as-is, sent directly to Discord
    //   - A filename          → relative to OSWData/OMS/, e.g. "avatar.png"
    //                          The actual image file is stored separately by OmsConfigManager.
    internal class OmsDiscordConfig
    {
        public string WebhookUrl   { get; set; } = "";
        public string Username     { get; set; } = "";

        // Either a URL or a filename relative to OSWData/OMS/
        // OmsDiscordPoster checks IsUrl() to decide how to handle it
        public string AvatarSource { get; set; } = "";
    }
}
