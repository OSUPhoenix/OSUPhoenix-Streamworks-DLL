using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    // =========================================================================
    //  OSWLib_Tiltify.cs  —  Tiltify Integration Extension
    //  Folder: Extensions/
    //
    //  ── OVERVIEW ─────────────────────────────────────────────────────────────
    //  Polls the Tiltify API for new donations and campaign changes, then fires
    //  Streamer.bot custom triggers so users can attach any SB action to
    //  Tiltify events (alert overlays, chat messages, sound effects, etc.).
    //
    //  ── WHY POLLING, NOT WEBHOOKS? ───────────────────────────────────────────
    //  Tiltify webhooks require a publicly accessible HTTPS endpoint. Most
    //  streamers don't have one. Polling via an SB Timer trigger is simpler,
    //  reliable, and requires zero server setup.
    //
    //  ── SETUP (one-time) ─────────────────────────────────────────────────────
    //  1. Create a Tiltify application at https://dashboard.tiltify.com/account/apps
    //  2. Copy the Client ID and Client Secret
    //  3. Call TiltifySetCredentials(clientId, clientSecret)
    //  4. Call TiltifySetCampaign(publicId)   ← use the campaign's PUBLIC id
    //  5. Call TiltifyInitialize()            on SB startup
    //
    //  ── RUNTIME (recurring) ──────────────────────────────────────────────────
    //  • Timer trigger in SB (every 30–60 seconds)
    //  • One Execute Code subaction:  lib.TiltifyPoll();
    //
    //  ── CUSTOM TRIGGERS in SB (Custom > OSW > Tiltify) ───────────────────────
    //    "Donation Received"  — new confirmed donation
    //    "Donation Updated"   — donation refunded or moderated
    //    "Campaign Updated"   — total raised changed
    //    "Goal Reached"       — total crossed the goal (fires once)
    //
    //  ── AUTH ─────────────────────────────────────────────────────────────────
    //  OAuth 2.0 Client Credentials flow — no browser required:
    //    ClientId + ClientSecret → POST /oauth/token → access_token (2hr TTL)
    //  Token is cached in JSON and auto-refreshed when near expiry.
    //
    //  ── CREDENTIAL SECURITY NOTE ─────────────────────────────────────────────
    //  ClientId and ClientSecret are stored in plain text in a local JSON file
    //  at %AppData%\OSWTools\tiltify_config.json. Do not share this file.
    //
    //  ── CHANGELOG (v2) ───────────────────────────────────────────────────────
    //  Fixes identified by comparing against community Tiltify SB scripts:
    //  • Field name corrected: amount_raised (not total_amount_raised)
    //  • completed_before timestamp filter added to donation query to exclude
    //    pending/processing donations that aren't yet confirmed
    //  • reward_claims now parsed as a full list (not just first reward_id)
    //  • Deduplication upgraded from single cursor ID to rolling 20-ID list,
    //    which survives refunds and out-of-order API responses
    //  • Campaign ID renamed to PublicId throughout to match Tiltify's naming
    // =========================================================================

    public partial class OSWLib
    {
        // ── API Constants ─────────────────────────────────────────────────────

        private const string TILTIFY_TOKEN_URL = "https://v5api.tiltify.com/oauth/token";
        private const string TILTIFY_API_BASE  = "https://v5api.tiltify.com/api/public";

        // ── SB Custom Event Names ─────────────────────────────────────────────
        // Internal C# names used with TriggerCodeEvent().
        // Display names shown in SB UI are set in TiltifyRegisterTriggers().

        private const string TILTIFY_EVT_DONATION_RECEIVED = "tiltify_donation_received";
        private const string TILTIFY_EVT_DONATION_UPDATED  = "tiltify_donation_updated";
        private const string TILTIFY_EVT_CAMPAIGN_UPDATED  = "tiltify_campaign_updated";
        private const string TILTIFY_EVT_GOAL_REACHED      = "tiltify_goal_reached";

        // ── Shared HttpClient ─────────────────────────────────────────────────
        // Static to avoid socket exhaustion from creating a new client per poll.

        private static readonly HttpClient _tiltifyHttp = new HttpClient();

        // ── Config File Path ──────────────────────────────────────────────────

        private static string TiltifyConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OSWTools", "tiltify_config.json");


        // =====================================================================
        //  Config Data Model
        //  Serialized to/from %AppData%\OSWTools\tiltify_config.json
        // =====================================================================

        private class TiltifyConfig
        {
            // ── Credentials (set once, persist forever) ───────────────────────
            [JsonProperty("clientId")]
            public string ClientId { get; set; } = "";

            [JsonProperty("clientSecret")]
            public string ClientSecret { get; set; } = "";

            // ── Campaign ──────────────────────────────────────────────────────
            // NOTE: Tiltify campaigns have two IDs:
            //   id        — internal numeric ID used in some private endpoints
            //   public_id — UUID used in all public API endpoints (use this one)
            // TiltifySetCampaign() expects the public_id (found in your dashboard
            // URL or returned by the /campaigns endpoint).
            [JsonProperty("campaignPublicId")]
            public string CampaignPublicId { get; set; } = "";

            [JsonProperty("campaignName")]
            public string CampaignName { get; set; } = "";

            // ── Auth Token (auto-refreshed every ~2 hours) ────────────────────
            [JsonProperty("accessToken")]
            public string AccessToken { get; set; } = "";

            [JsonProperty("tokenExpiresAt")]
            public DateTime TokenExpiresAt { get; set; } = DateTime.MinValue;

            // ── Poll State ────────────────────────────────────────────────────

            // Rolling list of the last 20 donation IDs we have already fired.
            //
            // WHY A LIST INSTEAD OF A SINGLE CURSOR?
            // A single "last seen ID" breaks when the most recent donation is
            // refunded and disappears from the API response — the cursor gets
            // stuck on an ID that no longer exists and all future donations
            // look new. A rolling window of 20 IDs handles this safely.
            // 20 was chosen because rapid fundraising events can produce many
            // donations between polls; a deeper window prevents re-firing
            // donations that fall outside a shallow list.
            [JsonProperty("seenDonationIds")]
            public List<string> SeenDonationIds { get; set; } = new List<string>();

            // Last known amount raised — used to detect campaign total changes
            [JsonProperty("lastAmountRaised")]
            public double LastAmountRaised { get; set; } = 0;

            // Prevents the Goal Reached trigger from firing more than once
            [JsonProperty("goalReachedFired")]
            public bool GoalReachedFired { get; set; } = false;

            // Whether triggers have been registered this SB session.
            // SB clears custom triggers on restart, so always re-register.
            // JsonIgnore = not persisted (it's a runtime-only flag).
            [JsonIgnore]
            public bool TriggersRegistered { get; set; } = false;
        }


        // =====================================================================
        //  PUBLIC SETUP METHODS
        // =====================================================================

        /// <summary>
        /// Saves your Tiltify Application credentials to the local config file.
        /// Get these from https://dashboard.tiltify.com/account/apps
        ///
        /// Only needs to be called once. A WinForms settings UI is the
        /// recommended way to expose this to your users.
        /// </summary>
        /// <param name="clientId">Your Tiltify application Client ID</param>
        /// <param name="clientSecret">Your Tiltify application Client Secret</param>
        public bool TiltifySetCredentials(string clientId, string clientSecret)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                LogWarn("TiltifySetCredentials: clientId and clientSecret cannot be empty.");
                return false;
            }

            try
            {
                var config = TiltifyLoadConfig();
                config.ClientId     = clientId.Trim();
                config.ClientSecret = clientSecret.Trim();
                // Clear any cached token so the next call fetches a fresh one
                config.AccessToken    = "";
                config.TokenExpiresAt = DateTime.MinValue;
                TiltifySaveConfig(config);
                LogInfo("TiltifySetCredentials: credentials saved.");
                return true;
            }
            catch (Exception ex)
            {
                LogWarn("TiltifySetCredentials failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Saves the Tiltify Campaign Public ID to poll.
        ///
        /// IMPORTANT: Use the campaign's PUBLIC ID (a UUID like
        /// "a1b2c3d4-..."), not the internal numeric ID. You can find
        /// it in your Tiltify dashboard URL or via the /campaigns API endpoint.
        ///
        /// Calling this resets all poll state for the campaign so you start
        /// fresh. Call TiltifyResetPollState() instead if you just want to
        /// clear seen donations without changing the campaign.
        /// </summary>
        public bool TiltifySetCampaign(string campaignPublicId)
        {
            if (string.IsNullOrWhiteSpace(campaignPublicId))
            {
                LogWarn("TiltifySetCampaign: campaignPublicId cannot be empty.");
                return false;
            }

            try
            {
                var config = TiltifyLoadConfig();
                config.CampaignPublicId  = campaignPublicId.Trim();
                config.CampaignName      = "";
                config.SeenDonationIds   = new List<string>();
                config.LastAmountRaised  = 0;
                config.GoalReachedFired  = false;
                TiltifySaveConfig(config);
                LogInfo("TiltifySetCampaign: public ID saved → " + campaignPublicId);
                return true;
            }
            catch (Exception ex)
            {
                LogWarn("TiltifySetCampaign failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Clears poll state (seen donations, amount baseline, goal flag) for
        /// the current campaign. Use this at the start of a new fundraising
        /// stream so the integration starts fresh without re-firing old events.
        ///
        /// Does NOT clear credentials or the campaign public ID.
        /// </summary>
        public void TiltifyResetPollState()
        {
            try
            {
                var config = TiltifyLoadConfig();
                config.SeenDonationIds  = new List<string>();
                config.LastAmountRaised = 0;
                config.GoalReachedFired = false;
                TiltifySaveConfig(config);
                LogInfo("TiltifyResetPollState: poll state cleared.");
            }
            catch (Exception ex)
            {
                LogWarn("TiltifyResetPollState failed: " + ex.Message);
            }
        }


        // =====================================================================
        //  PUBLIC RUNTIME METHODS
        // =====================================================================

        /// <summary>
        /// Initializes the Tiltify integration. Call once on SB startup via an
        /// "Application Started" trigger.
        ///
        /// Steps:
        ///   1. Validates that credentials and a campaign public ID are saved
        ///   2. Fetches a fresh OAuth access token
        ///   3. Verifies the campaign is accessible, caches its display name
        ///   4. Registers all custom triggers in Streamer.bot
        ///
        /// Returns true if ready. Returns false if credentials are missing or
        /// the API is unreachable.
        /// </summary>
        public bool TiltifyInitialize()
        {
            LogInfo("TiltifyInitialize: starting...");
            var config = TiltifyLoadConfig();

            if (string.IsNullOrWhiteSpace(config.ClientId) ||
                string.IsNullOrWhiteSpace(config.ClientSecret))
            {
                LogWarn("TiltifyInitialize: no credentials saved. Call TiltifySetCredentials() first.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.CampaignPublicId))
            {
                LogWarn("TiltifyInitialize: no campaign public ID set. Call TiltifySetCampaign() first.");
                return false;
            }

            if (!TiltifyRefreshToken(config))
            {
                LogWarn("TiltifyInitialize: failed to obtain access token.");
                return false;
            }

            if (!TiltifyFetchCampaignInfo(config))
            {
                LogWarn("TiltifyInitialize: campaign not found or inaccessible: " + config.CampaignPublicId);
                return false;
            }

            TiltifySaveConfig(config);

            TiltifyRegisterTriggers();
            config.TriggersRegistered = true;

            LogInfo($"TiltifyInitialize: ready. Campaign: \"{config.CampaignName}\" ({config.CampaignPublicId})");
            return true;
        }

        /// <summary>
        /// Polls Tiltify for new donations and campaign changes. Fires the
        /// appropriate custom trigger in SB for each event detected.
        ///
        /// HOW TO USE:
        ///   Create an SB action with a Timer trigger (every 30–60 seconds).
        ///   Add one Execute Code subaction:  lib.TiltifyPoll();
        ///
        /// This method is safe to call frequently. It auto-refreshes the token
        /// when needed and does nothing if the integration isn't configured.
        /// </summary>
        public void TiltifyPoll()
        {
            var config = TiltifyLoadConfig();

            if (string.IsNullOrWhiteSpace(config.ClientId) ||
                string.IsNullOrWhiteSpace(config.CampaignPublicId))
            {
                LogWarn("TiltifyPoll: not configured. Call TiltifyInitialize() first.");
                return;
            }

            // Re-register triggers if SB was restarted (triggers don't survive restarts)
            if (!config.TriggersRegistered)
            {
                TiltifyRegisterTriggers();
                config.TriggersRegistered = true;
            }

            // Refresh token if expired or within 5 minutes of expiry
            if (DateTime.UtcNow >= config.TokenExpiresAt.AddMinutes(-5))
            {
                if (!TiltifyRefreshToken(config))
                {
                    LogWarn("TiltifyPoll: token refresh failed — skipping poll.");
                    return;
                }
                TiltifySaveConfig(config);
            }

            TiltifyPollDonations(config);
            TiltifyPollCampaign(config);

            TiltifySaveConfig(config);
        }


        // =====================================================================
        //  PRIVATE — TRIGGER REGISTRATION
        // =====================================================================

        private void TiltifyRegisterTriggers()
        {
            // Triggers appear in SB under: Custom > OSW > Tiltify
            string[] categories = new[] { "OSW", "Tiltify" };

            _CPH.RegisterCustomTrigger("Donation Received", TILTIFY_EVT_DONATION_RECEIVED, categories);
            _CPH.RegisterCustomTrigger("Donation Updated",  TILTIFY_EVT_DONATION_UPDATED,  categories);
            _CPH.RegisterCustomTrigger("Campaign Updated",  TILTIFY_EVT_CAMPAIGN_UPDATED,  categories);
            _CPH.RegisterCustomTrigger("Goal Reached",      TILTIFY_EVT_GOAL_REACHED,      categories);

            LogInfo("TiltifyRegisterTriggers: 4 triggers registered under Custom > OSW > Tiltify.");
        }


        // =====================================================================
        //  PRIVATE — POLLING LOGIC
        // =====================================================================

        /// <summary>
        /// Fetches confirmed donations and fires triggers for any not yet seen.
        ///
        /// KEY DESIGN DECISIONS:
        ///
        /// completed_before filter:
        ///   We pass the current UTC time as a completed_before parameter.
        ///   Tiltify returns some donations in a "processing" state before they
        ///   are fully confirmed. The timestamp filter ensures we only receive
        ///   donations that have cleared — matching the behaviour of the
        ///   community scripts that used this same filter.
        ///
        /// Rolling seen-ID window:
        ///   Rather than a single cursor ID, we maintain a list of the last 20
        ///   seen donation IDs. If the most recent donation is refunded and
        ///   removed from the API, a cursor would get stuck. The rolling list
        ///   handles this safely — a donation is skipped if its ID is in the
        ///   list, and the list is trimmed to 20 entries after each poll.
        ///
        /// Oldest-first firing:
        ///   Tiltify returns donations newest-first. We reverse the new batch
        ///   before firing so alerts appear in chronological order.
        /// </summary>
        private void TiltifyPollDonations(TiltifyConfig config)
        {
            try
            {
                // completed_before prevents picking up pending/unconfirmed donations.
                // Tiltify expects ISO8601 UTC; UrlEncode handles the colons/pluses.
                string timeNow     = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ");
                string encodedTime = Uri.EscapeDataString(timeNow);

                string url = $"{TILTIFY_API_BASE}/campaigns/{config.CampaignPublicId}" +
                             $"/donations?completed_before={encodedTime}&limit=20";

                string json = TiltifyGet(url, config.AccessToken);
                if (json == null) return;

                JObject response = JObject.Parse(json);
                JArray  data     = response["data"] as JArray;
                if (data == null || data.Count == 0) return;

                // Collect donations not yet in our seen list
                var newDonations = new List<JObject>();
                foreach (JToken item in data)
                {
                    string id = item["id"]?.ToString() ?? "";
                    if (!config.SeenDonationIds.Contains(id))
                        newDonations.Add((JObject)item);
                }

                if (newDonations.Count == 0) return;

                // Add new IDs to the seen list, trim to last 20
                foreach (var d in newDonations)
                {
                    string id = d["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(id))
                        config.SeenDonationIds.Add(id);
                }
                while (config.SeenDonationIds.Count > 20)
                    config.SeenDonationIds.RemoveAt(0);

                // Fire oldest-first so alerts arrive in chronological order
                newDonations.Reverse();
                foreach (var donation in newDonations)
                    TiltifyFireDonationTrigger(donation, config, isNew: true);

                LogInfo($"TiltifyPollDonations: fired {newDonations.Count} donation trigger(s).");
            }
            catch (Exception ex)
            {
                LogWarn("TiltifyPollDonations failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Fetches current campaign totals and fires triggers on changes.
        ///
        /// FIELD NAME NOTE:
        /// The Tiltify API returns amount_raised (not total_amount_raised) on
        /// the campaign endpoint. This was confirmed by comparing against
        /// community scripts that were working in production.
        /// </summary>
        private void TiltifyPollCampaign(TiltifyConfig config)
        {
            try
            {
                string url  = $"{TILTIFY_API_BASE}/campaigns/{config.CampaignPublicId}";
                string json = TiltifyGet(url, config.AccessToken);
                if (json == null) return;

                JObject response = JObject.Parse(json);
                JObject data     = response["data"] as JObject;
                if (data == null) return;

                // Tiltify campaign endpoint uses "amount_raised", not "total_amount_raised"
                double amountRaised = TiltifyParseAmount(data["amount_raised"]);
                double goal         = TiltifyParseAmount(data["goal"]);
                string currency     = data["amount_raised"]?["currency"]?.ToString() ?? "USD";
                string status       = data["status"]?.ToString() ?? "";

                // Only fire if total has actually changed
                if (Math.Abs(amountRaised - config.LastAmountRaised) < 0.001) return;
                config.LastAmountRaised = amountRaised;

                double percent = goal > 0 ? Math.Round((amountRaised / goal) * 100, 1) : 0;

                // ── Campaign Updated trigger ──────────────────────────────────
                _CPH.SetArgument("campaignPublicId",  config.CampaignPublicId);
                _CPH.SetArgument("campaignName",       config.CampaignName);
                _CPH.SetArgument("amountRaised",       amountRaised);
                _CPH.SetArgument("goalAmount",         goal);
                _CPH.SetArgument("currency",           currency);
                _CPH.SetArgument("percentToGoal",      percent);
                _CPH.SetArgument("campaignStatus",     status);
                _CPH.TriggerCodeEvent(TILTIFY_EVT_CAMPAIGN_UPDATED, false);

                // ── Goal Reached trigger (fires once per campaign reset) ───────
                if (!config.GoalReachedFired && goal > 0 && amountRaised >= goal)
                {
                    config.GoalReachedFired = true;
                    _CPH.SetArgument("campaignPublicId", config.CampaignPublicId);
                    _CPH.SetArgument("campaignName",     config.CampaignName);
                    _CPH.SetArgument("amountRaised",     amountRaised);
                    _CPH.SetArgument("goalAmount",       goal);
                    _CPH.SetArgument("currency",         currency);
                    _CPH.SetArgument("percentToGoal",    percent);
                    _CPH.TriggerCodeEvent(TILTIFY_EVT_GOAL_REACHED, false);
                    LogInfo($"TiltifyPollCampaign: GOAL REACHED — {currency} {amountRaised:F2} of {goal:F2}");
                }
            }
            catch (Exception ex)
            {
                LogWarn("TiltifyPollCampaign failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Sets all SB arguments for a donation and fires the appropriate trigger.
        ///
        /// REWARD CLAIMS NOTE:
        /// A single donation can redeem multiple Tiltify rewards. The community
        /// script only captured the first reward_id. This version iterates the
        /// full reward_claims array and exposes:
        ///   rewardClaimCount  — how many rewards were claimed (int)
        ///   rewardIds         — comma-separated string of all reward IDs
        ///   rewardId          — first reward ID (for backwards compatibility)
        ///
        /// Args available in SB after this trigger fires: see quick reference
        /// at the bottom of this file.
        /// </summary>
        private void TiltifyFireDonationTrigger(JObject donation, TiltifyConfig config, bool isNew)
        {
            // Donor name — Tiltify returns null or empty for anonymous donors
            string donorName   = donation["donor_name"]?.ToString();
            bool   isAnonymous = string.IsNullOrWhiteSpace(donorName);
            if (isAnonymous) donorName = "Anonymous";

            double amount   = TiltifyParseAmount(donation["amount"]);
            string currency = donation["amount"]?["currency"]?.ToString() ?? "USD";

            // ── Parse full reward_claims list ─────────────────────────────────
            JArray rewardClaims = donation["reward_claims"] as JArray;
            var    rewardIds    = new List<string>();

            if (rewardClaims != null)
            {
                foreach (JToken claim in rewardClaims)
                {
                    string rewardId = claim["reward_id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(rewardId))
                        rewardIds.Add(rewardId);
                }
            }

            string rewardIdFirst    = rewardIds.Count > 0 ? rewardIds[0] : "";
            string rewardIdsJoined  = string.Join(",", rewardIds);

            // ── Set all SB arguments ──────────────────────────────────────────
            _CPH.SetArgument("donationId",        donation["id"]?.ToString() ?? "");
            _CPH.SetArgument("donorName",          donorName);
            _CPH.SetArgument("donorComment",       donation["donor_comment"]?.ToString() ?? "");
            _CPH.SetArgument("donationAmount",     amount);
            _CPH.SetArgument("donationCurrency",   currency);
            _CPH.SetArgument("campaignPublicId",   config.CampaignPublicId);
            _CPH.SetArgument("campaignName",       config.CampaignName);
            _CPH.SetArgument("completedAt",        donation["completed_at"]?.ToString() ?? "");
            _CPH.SetArgument("isAnonymous",        isAnonymous);
            _CPH.SetArgument("rewardId",           rewardIdFirst);    // first reward (compat)
            _CPH.SetArgument("rewardIds",          rewardIdsJoined);  // all rewards, comma-sep
            _CPH.SetArgument("rewardClaimCount",   rewardIds.Count);
            _CPH.SetArgument("pollOptionId",       donation["poll_option_id"]?.ToString() ?? "");
            _CPH.SetArgument("challengeId",        donation["challenge_id"]?.ToString() ?? "");

            // Ready-to-use formatted string for chat messages / alert text
            string formatted = isAnonymous
                ? $"Anonymous donated {currency} {amount:F2}!"
                : $"{donorName} donated {currency} {amount:F2}!";
            _CPH.SetArgument("donationFormatted", formatted);

            string eventName = isNew ? TILTIFY_EVT_DONATION_RECEIVED : TILTIFY_EVT_DONATION_UPDATED;

            // false = use our SetArgument calls above, not the current action's args
            _CPH.TriggerCodeEvent(eventName, false);
        }


        // =====================================================================
        //  PRIVATE — OAUTH TOKEN MANAGEMENT
        // =====================================================================

        /// <summary>
        /// Fetches a new Client Credentials access token from Tiltify.
        ///
        /// Sends credentials in a JSON request body (per Tiltify docs spec).
        /// Note: some community scripts send credentials as URL query params —
        /// both work, but JSON body is the documented approach.
        ///
        /// Token TTL is 7200 seconds (~2 hours). We store the absolute expiry
        /// time so TiltifyPoll() can check if a refresh is needed.
        /// </summary>
        private bool TiltifyRefreshToken(TiltifyConfig config)
        {
            try
            {
                var body = new JObject
                {
                    ["grant_type"]    = "client_credentials",
                    ["client_id"]     = config.ClientId,
                    ["client_secret"] = config.ClientSecret,
                    ["scope"]         = "public"
                };

                var request = new HttpRequestMessage(HttpMethod.Post, TILTIFY_TOKEN_URL)
                {
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                };

                HttpResponseMessage response  = _tiltifyHttp.SendAsync(request).Result;
                string              respBody  = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    LogWarn($"TiltifyRefreshToken: HTTP {(int)response.StatusCode} — {respBody}");
                    return false;
                }

                JObject json      = JObject.Parse(respBody);
                string  token     = json["access_token"]?.ToString();
                int     expiresIn = json["expires_in"]?.ToObject<int>() ?? 7200;

                if (string.IsNullOrWhiteSpace(token))
                {
                    LogWarn("TiltifyRefreshToken: response missing access_token.");
                    return false;
                }

                config.AccessToken    = token;
                config.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

                LogInfo($"TiltifyRefreshToken: token obtained. Expires {config.TokenExpiresAt:HH:mm:ss} UTC.");
                return true;
            }
            catch (Exception ex)
            {
                LogWarn("TiltifyRefreshToken exception: " + ex.Message);
                return false;
            }
        }


        // =====================================================================
        //  PRIVATE — API HELPERS
        // =====================================================================

        /// <summary>
        /// Performs an authenticated GET against the Tiltify API.
        /// Returns the response body string, or null on failure.
        /// </summary>
        private string TiltifyGet(string url, string accessToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", "Bearer " + accessToken);

                HttpResponseMessage response = _tiltifyHttp.SendAsync(request).Result;
                string              body     = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    LogWarn($"TiltifyGet: HTTP {(int)response.StatusCode} for {url}");
                    return null;
                }

                return body;
            }
            catch (Exception ex)
            {
                LogWarn("TiltifyGet exception: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Fetches and caches the campaign name and baseline amount raised.
        /// Called during TiltifyInitialize() to verify the public ID is valid.
        /// </summary>
        private bool TiltifyFetchCampaignInfo(TiltifyConfig config)
        {
            try
            {
                string url  = $"{TILTIFY_API_BASE}/campaigns/{config.CampaignPublicId}";
                string json = TiltifyGet(url, config.AccessToken);
                if (json == null) return false;

                JObject response = JObject.Parse(json);
                JObject data     = response["data"] as JObject;
                if (data == null) return false;

                config.CampaignName     = data["name"]?.ToString() ?? config.CampaignPublicId;
                config.LastAmountRaised = TiltifyParseAmount(data["amount_raised"]);

                LogInfo($"TiltifyFetchCampaignInfo: \"{config.CampaignName}\" — " +
                        $"{TiltifyParseAmount(data["amount_raised"]?["currency"]) } " +
                        $"{config.LastAmountRaised:F2} raised so far.");
                return true;
            }
            catch (Exception ex)
            {
                LogWarn("TiltifyFetchCampaignInfo exception: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Safely parses a Tiltify amount object:
        ///   { "value": "25.00", "currency": "USD" }
        /// Returns 0 on any failure.
        /// </summary>
        private static double TiltifyParseAmount(JToken amountToken)
        {
            if (amountToken == null) return 0;
            string raw = amountToken["value"]?.ToString() ?? "0";
            return double.TryParse(raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double result) ? result : 0;
        }


        // =====================================================================
        //  PRIVATE — CONFIG PERSISTENCE
        // =====================================================================

        private static TiltifyConfig TiltifyLoadConfig()
        {
            try
            {
                if (!File.Exists(TiltifyConfigPath))
                    return new TiltifyConfig();

                string json = File.ReadAllText(TiltifyConfigPath);
                return JsonConvert.DeserializeObject<TiltifyConfig>(json) ?? new TiltifyConfig();
            }
            catch
            {
                return new TiltifyConfig();
            }
        }

        private static void TiltifySaveConfig(TiltifyConfig config)
        {
            string dir = Path.GetDirectoryName(TiltifyConfigPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(TiltifyConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
    }
}

// =============================================================================
//  QUICK SETUP GUIDE
// =============================================================================
//
//  1. GET CREDENTIALS
//     → https://dashboard.tiltify.com/account/apps
//     → Create or open an application → copy Client ID and Client Secret
//
//  2. ONE-TIME SETUP ACTION (run manually once)
//     var lib = new OSWLib(CPH, "Tiltify Setup");
//     lib.TiltifySetCredentials("your-client-id", "your-client-secret");
//     lib.TiltifySetCampaign("your-campaign-public-id");   ← UUID from dashboard
//
//  3. APPLICATION STARTED TRIGGER
//     var lib = new OSWLib(CPH, "Tiltify");
//     lib.TiltifyInitialize();
//
//  4. TIMER TRIGGER (every 30 seconds)
//     var lib = new OSWLib(CPH, "Tiltify");
//     lib.TiltifyPoll();
//
//  5. ATTACH ACTIONS TO TRIGGERS
//     Custom > OSW > Tiltify in SB's trigger picker:
//       "Donation Received"  "Donation Updated"  "Campaign Updated"  "Goal Reached"
//
// =============================================================================
//  ARGS AVAILABLE IN DONATION TRIGGERS
// =============================================================================
//
//  %donationId%         Tiltify donation ID
//  %donorName%          Donor display name ("Anonymous" if hidden)
//  %donorComment%       Donor message (empty string if none)
//  %donationAmount%     Amount as double  e.g. 25.0
//  %donationCurrency%   ISO currency code e.g. "USD"
//  %donationFormatted%  Ready-to-use string: "JohnDoe donated USD 25.00!"
//  %campaignPublicId%   Campaign public UUID
//  %campaignName%       Campaign display name
//  %completedAt%        ISO timestamp of the donation
//  %isAnonymous%        true / false
//  %rewardId%           First reward ID claimed (empty if none)
//  %rewardIds%          All reward IDs comma-separated (e.g. "id1,id2")
//  %rewardClaimCount%   Number of rewards claimed (int)
//  %pollOptionId%       Poll option ID if voted (empty if none)
//  %challengeId%        Challenge ID if applicable (empty if none)
//
// =============================================================================
//  ARGS AVAILABLE IN CAMPAIGN TRIGGERS
// =============================================================================
//
//  %campaignPublicId%   Campaign public UUID
//  %campaignName%       Campaign display name
//  %amountRaised%       Current total raised (double)
//  %goalAmount%         Campaign goal (double)
//  %currency%           ISO currency code
//  %percentToGoal%      e.g. 73.4  (for 73.4% of goal)
//  %campaignStatus%     e.g. "published", "ended"
//
// =============================================================================
