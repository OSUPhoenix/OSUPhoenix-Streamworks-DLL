// =============================================================================
// OSWTools — Extensions/DiscordExtensions.cs
//
// EVERYTHING DISCORD, IN ONE PLACE.
// (Merged from DiscordExtensions.cs + DiscordBotExtensions.cs. Every public
// class and method keeps its exact name and signature, so existing tools
// compile and behave exactly as before.)
//
// ── WHAT'S IN THIS FILE ──────────────────────────────────────────────────────
//   MODELS          DiscordEmbedField, DiscordEmbedBuilder, DiscordMember
//   HELPERS         DiscordWebhooks.Timestamp / Color / IsWebhookUrl
//   A. WEBHOOKS     PostDiscordWebhook, SendDiscordWebhook,
//                   EditDiscordWebhookMessage, DeleteDiscordWebhookMessage
//   B. BOT          SaveDiscordBotToken, HasDiscordBotToken, TestDiscordBot,
//                   GetDiscordRoleName, FindDiscordMember, GetDiscordMember,
//                   AddDiscordMemberRole, RemoveDiscordMemberRole,
//                   ListDiscordRoleMembers
//   C. PLUMBING     DiscordRequest (the ONE place requests are sent),
//                   size checks, embed → JSON, small utilities
//
// ── WEBHOOK vs BOT: which do I need? ─────────────────────────────────────────
//   WEBHOOK = a posting address for ONE channel. Can only post, edit and
//             delete its own messages. No setup beyond pasting the URL.
//   BOT     = an app signed in with a token. Needed for anything about
//             PEOPLE: finding members, giving or taking roles. Needs
//             "Manage Roles", and its role must sit ABOVE the roles it hands
//             out (Server Settings → Roles). ListDiscordRoleMembers also needs
//             "Server Members Intent" on in the Developer Portal → Bot tab.
//
//   The bot uses plain REST calls, not a live gateway connection like
//   DiscoBot: a role change is a one-off request, so there's nothing to keep
//   alive, and every call returns a clear success/failure. It can share a bot
//   token with DiscoBot; both can use the same bot at the same time.
//
// ── USAGE ────────────────────────────────────────────────────────────────────
//   Webhook (unchanged — existing tools keep working exactly as before):
//     var embed = new DiscordEmbedBuilder()
//         .WithTitle("Achievement Unlocked")
//         .WithColor(0xCC0000)
//         .WithDescription("PhoenixHype reached 100 messages!")
//         .WithAuthor("PhoenixHype", profilePicUrl, "https://twitch.tv/phoenixhype")
//         .AddField("Time", DateTime.UtcNow.ToString("u"), inline: false)
//         .AddField("Tier", "1", inline: true)
//         .WithFooter("OSUPhoenix StreamWorks", iconUrl);
//     bool ok = Lib.PostDiscordWebhook(webhookUrl, embed, attachmentPath: screenshotPath);
//
//   Bot:
//     lib.SaveDiscordBotToken(token);                        // once; stored encrypted
//     var m = lib.FindDiscordMember(guildId, "someuser");     // name, display name or ID
//     if (m.Success) lib.AddDiscordMemberRole(guildId, m.Data.Id, roleId, "Joined game");
//
// ── RETURN VALUES ────────────────────────────────────────────────────────────
//   PostDiscordWebhook → true/false (never throws, as before).
//   Everything else    → OSWApiResult<T> (never throws; Error explains failures
//                        in plain English with a "what to do" hint).
//
// ── HISTORY ──────────────────────────────────────────────────────────────────
//   Multi-scheduler merge (webhooks):
//   • SendDiscordWebhook returns the posted message's ID, so the post can be
//     edited or deleted later ("Moved to 8 PM", "Canceled").
//   • Discord's size limits are checked BEFORE sending, so errors explain themselves.
//   • A field with an empty name or value made Discord reject the WHOLE post
//     (400). The Achievement System's AddField("Qualifier", Qualifier ?? "")
//     hit this for every achievement without a qualifier. Blank fields are now
//     sent as an invisible zero-width space.
//   • The post waited with .GetAwaiter().GetResult() but no ConfigureAwait(false),
//     which could freeze a WinForms "Test webhook" button for good. Now goes
//     through OSWHttp, which guards against it.
//   • An attachment made PostDiscordWebhook change the CALLER'S embed. The
//     attachment link is now added only to the copy that gets sent.
//   Winnable Wednesday (bot): member lookup and role management added.
//   This merge: the webhook sender and the bot sender were two copies of the
//   same retry logic. They're now ONE method (DiscordRequest), so a fix to
//   rate-limit handling or error messages applies to both.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools
{
    // ═════════════════════════════════════════════════════════════════════════
    //  MODELS
    // ═════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────
    // A single field within an embed (the "name: value" pairs shown beneath
    // the description). Inline=true puts up to 3 per row.
    // ─────────────────────────────────────────────────────────────────────────
    public class DiscordEmbedField
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public bool Inline { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fluent API for assembling Discord embeds. Mirrors Discord's embed schema:
    //   https://discord.com/developers/docs/resources/channel#embed-object
    // ─────────────────────────────────────────────────────────────────────────
    public class DiscordEmbedBuilder
    {
        public string Title       { get; set; }
        public string Description { get; set; }
        public int    Color       { get; set; }
        public string Url         { get; set; }

        public string AuthorName    { get; set; }
        public string AuthorIconUrl { get; set; }
        public string AuthorUrl     { get; set; }

        public string ImageUrl     { get; set; }
        public string ThumbnailUrl { get; set; }

        public string FooterText    { get; set; }
        public string FooterIconUrl { get; set; }

        public List<DiscordEmbedField> Fields { get; private set; } = new List<DiscordEmbedField>();

        // Optional plain-text content sent alongside the embed
        public string Content { get; set; }

        // Shown in the embed footer, in each viewer's own time zone
        public DateTime? TimestampUtc { get; set; }

        // ── Fluent setters ────────────────────────────────────────────────
        public DiscordEmbedBuilder WithTitle(string t)        { Title = t; return this; }
        public DiscordEmbedBuilder WithDescription(string d)  { Description = d; return this; }
        public DiscordEmbedBuilder WithColor(int rgb)         { Color = rgb; return this; }
        public DiscordEmbedBuilder WithUrl(string u)          { Url = u; return this; }
        public DiscordEmbedBuilder WithContent(string c)      { Content = c; return this; }
        public DiscordEmbedBuilder WithImage(string url)      { ImageUrl = url; return this; }
        public DiscordEmbedBuilder WithThumbnail(string url)  { ThumbnailUrl = url; return this; }

        public DiscordEmbedBuilder WithAuthor(string name, string iconUrl = null, string url = null)
        {
            AuthorName = name; AuthorIconUrl = iconUrl; AuthorUrl = url; return this;
        }

        public DiscordEmbedBuilder WithFooter(string text, string iconUrl = null)
        {
            FooterText = text; FooterIconUrl = iconUrl; return this;
        }

        public DiscordEmbedBuilder AddField(string name, string value, bool inline = false)
        {
            Fields.Add(new DiscordEmbedField { Name = name, Value = value, Inline = inline });
            return this;
        }

        /// <summary>Hex color, e.g. "#A257ED" (same result as WithColor(0xA257ED)).</summary>
        public DiscordEmbedBuilder WithColor(string hex)      { Color = DiscordWebhooks.Color(hex); return this; }
        /// <summary>Footer timestamp; Discord shows it in each viewer's own time zone.</summary>
        public DiscordEmbedBuilder WithTimestamp(DateTime utc) { TimestampUtc = utc; return this; }
    }

    /// <summary>A member of a Discord server, as the bot helpers see it.</summary>
    public class DiscordMember
    {
        public string Id { get; set; } = "";           // the permanent numeric ID ("snowflake")
        public string Username { get; set; } = "";     // unique handle, e.g. "osuphoenix"
        public string GlobalName { get; set; } = "";   // account-wide display name (not unique)
        public string Nick { get; set; } = "";         // server nickname (not unique)
        public List<string> RoleIds { get; set; } = new List<string>();

        /// <summary>What Discord shows in this server: nickname → display name → username.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Nick)) return Nick;
                if (!string.IsNullOrEmpty(GlobalName)) return GlobalName;
                return Username;
            }
        }

        /// <summary>Text that pings this person when posted to Discord.</summary>
        public string Mention { get { return "<@" + Id + ">"; } }

        internal static DiscordMember FromJson(JToken member)
        {
            JToken user = member["user"];
            var m = new DiscordMember
            {
                Id         = (string)user?["id"] ?? "",
                Username   = (string)user?["username"] ?? "",
                GlobalName = (string)user?["global_name"] ?? "",
                Nick       = (string)member["nick"] ?? ""
            };
            var roles = member["roles"] as JArray;
            if (roles != null) m.RoleIds = roles.Select(r => (string)r).ToList();
            return m;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HELPERS — no Streamer.bot connection needed
    // ═════════════════════════════════════════════════════════════════════════
    public static class DiscordWebhooks
    {
        /// <summary>
        /// Discord's dynamic timestamp, e.g. "&lt;t:1791590400:F&gt;". Every viewer
        /// sees it in their OWN time zone and language — ideal for "next stream"
        /// posts. Styles: t "7:00 PM", T "7:00:00 PM", d "10/9/2026",
        /// D "October 9, 2026", f "October 9, 2026 7:00 PM" (default),
        /// F "Friday, October 9, 2026 7:00 PM", R "in 2 hours".
        /// </summary>
        public static string Timestamp(DateTime utc, char style = 'f')
        {
            long unix = new DateTimeOffset(DateTime.SpecifyKind(
                utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return "<t:" + unix.ToString(CultureInfo.InvariantCulture) + ":" + style + ">";
        }

        /// <summary>"#A257ED" → the number Discord wants for an embed color (0 if unreadable).</summary>
        public static int Color(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v & 0xFFFFFF : 0;
        }

        /// <summary>True if it looks like a real Discord webhook URL (catches pasting the wrong thing).</summary>
        public static bool IsWebhookUrl(string url)
            => !string.IsNullOrWhiteSpace(url)
               && (url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://ptb.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://canary.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase));
    }

    public partial class OSWLib
    {
        // ═════════════════════════════════════════════════════════════════════
        //  A. WEBHOOKS — posting messages
        // ═════════════════════════════════════════════════════════════════════

        // ─────────────────────────────────────────────────────────────────────
        // Post a webhook with one embed and optional file attachment.
        //
        //   webhookUrl     — Discord webhook URL (full URL with token)
        //   embed          — DiscordEmbedBuilder with the embed contents
        //   attachmentPath — optional path to a file to upload alongside the embed.
        //                    If non-null and the file exists, it's attached as
        //                    "file" and the embed's image URL is set to
        //                    "attachment://<filename>" automatically.
        //
        // Returns: true if Discord accepted the post, false otherwise.
        // A thin wrapper over SendDiscordWebhook, so it shares its fixes.
        // ─────────────────────────────────────────────────────────────────────
        public bool PostDiscordWebhook(
            string webhookUrl,
            DiscordEmbedBuilder embed,
            string attachmentPath = null)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                LogWarn("[Discord] No webhook URL provided.");
                return false;
            }
            if (embed == null)
            {
                LogWarn("[Discord] No embed provided.");
                return false;
            }
            var r = SendDiscordWebhook(webhookUrl, embed.Content, new[] { embed }, attachmentPath);
            if (r.Success) LogInfo("[Discord] Posted (" + r.StatusCode + ")");
            else LogWarn("[Discord] " + r.Error);
            return r.Success;
        }

        // ─────────────────────────────────────────────────────────────────────
        // The full-featured send. Returns the posted message's ID — keep it to
        // edit or delete the post later.
        //
        //   content        — plain text (optional if there are embeds)
        //   embeds         — up to 10 builders (each builder's own Content is
        //                    ignored here; use `content`)
        //   attachmentPath — optional file; linked as the FIRST embed's image if
        //                    that embed has no image of its own
        //   username / avatarUrl — override the webhook's name and picture for
        //                    this one post. avatarUrl must be an http(s) URL.
        //   threadId       — post into a thread / forum post of that channel
        // ─────────────────────────────────────────────────────────────────────
        public OSWApiResult<string> SendDiscordWebhook(string webhookUrl, string content = null,
            IEnumerable<DiscordEmbedBuilder> embeds = null, string attachmentPath = null,
            string username = null, string avatarUrl = null, string threadId = null)
        {
            var list = embeds?.Where(e => e != null).ToList() ?? new List<DiscordEmbedBuilder>();
            bool hasAttachment = !string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath);
            if (!string.IsNullOrEmpty(attachmentPath) && !hasAttachment)
                LogWarn("[Discord] Attachment not found, posting without it: " + attachmentPath);

            var check = CheckDiscordMessage(webhookUrl, content, list, avatarUrl, requireSomething: !hasAttachment);
            if (check != null) return OSWApiResult<string>.Fail(check);

            var payload = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(content)) payload["content"] = content;
            if (list.Count > 0)
            {
                // The attachment link goes on the SENT COPY only — the caller's
                // builder is left exactly as they made it.
                string firstImage = hasAttachment && string.IsNullOrEmpty(list[0].ImageUrl)
                    ? "attachment://" + Path.GetFileName(attachmentPath) : null;
                payload["embeds"] = list.Select((e, i) => EmbedToJson(e, i == 0 ? firstImage : null)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(username)) payload["username"] = username.Length > 80 ? username.Substring(0, 80) : username;
            if (!string.IsNullOrWhiteSpace(avatarUrl)) payload["avatar_url"] = avatarUrl;

            // ?wait=true makes Discord reply with the created message — and so
            // its ID. Without it Discord answers "204 No Content": nothing to keep.
            string url = WebhookBase(webhookUrl) + "?wait=true"
                       + (string.IsNullOrEmpty(threadId) ? "" : "&thread_id=" + Uri.EscapeDataString(threadId));
            string json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            var r = DiscordRequest(HttpMethod.Post, url, asBot: false, makeBody: () =>
            {
                // Built fresh per attempt: an HttpContent can only be sent once,
                // so a retry after a rate-limit needs a new one.
                var form = new MultipartFormDataContent();
                if (hasAttachment)
                {
                    var file = new ByteArrayContent(File.ReadAllBytes(attachmentPath));
                    file.Headers.ContentType = new MediaTypeHeaderValue(GuessImageContentType(attachmentPath));
                    form.Add(file, "file", Path.GetFileName(attachmentPath));
                }
                form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");
                return form;
            });
            if (!r.Success) return r;
            try { return OSWApiResult<string>.Ok((string)JObject.Parse(r.Data)["id"] ?? "", r.StatusCode); }
            catch { return OSWApiResult<string>.Ok("", r.StatusCode); }
        }

        /// <summary>
        /// Replaces the text and/or embeds of a message this webhook posted.
        /// null = leave that part as it is; "" / an empty list = remove it.
        /// </summary>
        public OSWApiResult<bool> EditDiscordWebhookMessage(string webhookUrl, string messageId, string content = null,
            IEnumerable<DiscordEmbedBuilder> embeds = null, string threadId = null)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return OSWApiResult<bool>.Fail("No message ID given.");
            var list = embeds?.Where(e => e != null).ToList();
            var check = CheckDiscordMessage(webhookUrl, content, list ?? new List<DiscordEmbedBuilder>(), null, requireSomething: false);
            if (check != null) return OSWApiResult<bool>.Fail(check);

            var payload = new Dictionary<string, object>();
            if (content != null) payload["content"] = content;
            if (list != null) payload["embeds"] = list.Select(e => EmbedToJson(e, null)).ToList();
            if (payload.Count == 0) return OSWApiResult<bool>.Fail("Nothing to change — pass new content and/or embeds.");
            string json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

            var r = DiscordRequest(OSWHttp.Patch, MessageUrl(webhookUrl, messageId, threadId), asBot: false,
                makeBody: () => new StringContent(json, Encoding.UTF8, "application/json"));
            return r.Success ? OSWApiResult<bool>.Ok(true, r.StatusCode) : r.As<bool>();
        }

        /// <summary>Deletes a message this webhook posted.</summary>
        public OSWApiResult<bool> DeleteDiscordWebhookMessage(string webhookUrl, string messageId, string threadId = null)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return OSWApiResult<bool>.Fail("No message ID given.");
            if (!DiscordWebhooks.IsWebhookUrl(webhookUrl)) return OSWApiResult<bool>.Fail("That isn't a Discord webhook URL.");
            var r = DiscordRequest(HttpMethod.Delete, MessageUrl(webhookUrl, messageId, threadId), asBot: false);
            // 404 = already gone. Same end result, so not an error.
            return r.Success || r.StatusCode == 404 ? OSWApiResult<bool>.Ok(true, r.StatusCode) : r.As<bool>();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  B. BOT — members & roles
        //  SECURITY: the bot token is stored ENCRYPTED via OSWSecretStore
        //  (Windows DPAPI), never as a plain global. Anyone with the token
        //  controls the bot.
        // ═════════════════════════════════════════════════════════════════════

        private const string DiscordApiBase = "https://discord.com/api/v10/";
        private const string DiscordBotTokenKey = "osw_DiscordBotToken";

        // Name → member ID, remembered for this Streamer.bot session.
        // Saves a search request every time the same player comes back.
        // static = shared by every OSWLib instance; lock = safe across actions.
        private static readonly Dictionary<string, string> _discordMemberCache = new Dictionary<string, string>();
        private static readonly object _discordCacheLock = new object();

        // ── Token ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Saves the bot token ENCRYPTED for this Windows account.
        /// Accepts it with or without a leading "Bot ". Empty = delete it.
        /// </summary>
        public void SaveDiscordBotToken(string token)
        {
            string t = (token ?? "").Trim();
            if (t.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(4).Trim();
            if (t.Length == 0) { OSWSecretStore.Delete(this, DiscordBotTokenKey); return; }
            OSWSecretStore.Save(this, DiscordBotTokenKey, t);
        }

        /// <summary>True if a bot token has been saved (and can be unlocked on this PC).</summary>
        public bool HasDiscordBotToken()
        {
            return !string.IsNullOrEmpty(LoadDiscordBotToken());
        }

        private string LoadDiscordBotToken()
        {
            return OSWSecretStore.Load<string>(this, DiscordBotTokenKey) ?? "";
        }

        // ── Checks ────────────────────────────────────────────────────────────

        /// <summary>
        /// Signs in as the bot and returns its username. A quick "does the
        /// token work?" check for setup screens.
        /// </summary>
        public OSWApiResult<string> TestDiscordBot()
        {
            var r = DiscordBotSend(HttpMethod.Get, "users/@me");
            if (!r.Success) return r.As<string>();
            try { return OSWApiResult<string>.Ok((string)JObject.Parse(r.Data)["username"] ?? "", r.StatusCode); }
            catch { return OSWApiResult<string>.Ok("", r.StatusCode); }
        }

        /// <summary>
        /// Returns the name of a role in a server, confirming the bot can see
        /// both. Data = role name.
        /// </summary>
        public OSWApiResult<string> GetDiscordRoleName(string guildId, string roleId)
        {
            if (!IsSnowflake(guildId)) return OSWApiResult<string>.Fail("Server (guild) ID is missing or not a number.");
            if (!IsSnowflake(roleId))  return OSWApiResult<string>.Fail("Role ID is missing or not a number.");

            var r = DiscordBotSend(HttpMethod.Get, "guilds/" + guildId + "/roles");
            if (!r.Success) return r.As<string>();
            try
            {
                foreach (JToken role in JArray.Parse(r.Data))
                    if ((string)role["id"] == roleId) return OSWApiResult<string>.Ok((string)role["name"] ?? "", r.StatusCode);
            }
            catch { }
            return OSWApiResult<string>.Fail("No role with ID " + roleId + " in that server.", 404);
        }

        // ── Find a member ─────────────────────────────────────────────────────

        /// <summary>
        /// Finds a server member from what a viewer typed as their Discord name.
        /// Accepts: a numeric user ID, a username ("osuphoenix" or "@osuphoenix"),
        /// an old "name#1234" tag, or a display name / server nickname.
        ///
        /// Matching rules (strict on purpose: the wrong match would give a
        /// stranger access to the game room):
        ///   1. Exact USERNAME match wins. Usernames are unique on Discord.
        ///   2. Otherwise an exact display-name or nickname match, but ONLY if
        ///      exactly one member has it. Two matches = ambiguous = failure.
        ///   3. Never a partial match.
        /// </summary>
        public OSWApiResult<DiscordMember> FindDiscordMember(string guildId, string discordName)
        {
            if (!IsSnowflake(guildId)) return OSWApiResult<DiscordMember>.Fail("Server (guild) ID is missing or not a number.");

            string name = (discordName ?? "").Trim().TrimStart('@');
            int hash = name.IndexOf('#');
            if (hash > 0) name = name.Substring(0, hash);   // old "name#1234" style
            if (name.Length == 0) return OSWApiResult<DiscordMember>.Fail("No Discord name given.");

            // A pasted user ID → fetch that exact member
            if (IsSnowflake(name)) return GetDiscordMember(guildId, name);

            // Remembered from earlier this session?
            string cacheKey = guildId + "|" + name.ToLowerInvariant();
            string cachedId = null;
            lock (_discordCacheLock) _discordMemberCache.TryGetValue(cacheKey, out cachedId);
            if (cachedId != null)
            {
                var cached = GetDiscordMember(guildId, cachedId);
                if (cached.Success) return cached;
                lock (_discordCacheLock) _discordMemberCache.Remove(cacheKey);   // they left the server, etc.
            }

            // Search matches the START of usernames and nicknames
            var r = DiscordBotSend(HttpMethod.Get, "guilds/" + guildId + "/members/search?limit=25&query=" + Uri.EscapeDataString(name));
            if (!r.Success) return r.As<DiscordMember>();

            List<DiscordMember> found;
            try { found = JArray.Parse(r.Data).Select(DiscordMember.FromJson).ToList(); }
            catch { return OSWApiResult<DiscordMember>.Fail("Discord sent back something unexpected while searching."); }

            DiscordMember pick = found.FirstOrDefault(m => string.Equals(m.Username, name, StringComparison.OrdinalIgnoreCase));
            if (pick == null)
            {
                var byDisplay = found.Where(m =>
                    string.Equals(m.Nick, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.GlobalName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byDisplay.Count > 1)
                    return OSWApiResult<DiscordMember>.Fail("More than one member is called \"" + name + "\" — they should register their Discord USERNAME instead.");
                pick = byDisplay.FirstOrDefault();
            }
            if (pick == null)
                return OSWApiResult<DiscordMember>.Fail("No member named \"" + name + "\" in the server (have they joined it?).", 404);

            lock (_discordCacheLock) _discordMemberCache[cacheKey] = pick.Id;
            return OSWApiResult<DiscordMember>.Ok(pick, r.StatusCode);
        }

        /// <summary>Fetches one member by their numeric user ID.</summary>
        public OSWApiResult<DiscordMember> GetDiscordMember(string guildId, string userId)
        {
            if (!IsSnowflake(guildId) || !IsSnowflake(userId))
                return OSWApiResult<DiscordMember>.Fail("Server ID or user ID is missing or not a number.");
            var r = DiscordBotSend(HttpMethod.Get, "guilds/" + guildId + "/members/" + userId);
            if (!r.Success) return r.As<DiscordMember>();
            try { return OSWApiResult<DiscordMember>.Ok(DiscordMember.FromJson(JObject.Parse(r.Data)), r.StatusCode); }
            catch { return OSWApiResult<DiscordMember>.Fail("Discord sent back something unexpected for that member."); }
        }

        // ── Roles ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Gives a member a role. Already having it counts as success (Discord
        /// treats adding twice as a no-op). "reason" shows in the server's Audit Log.
        /// </summary>
        public OSWApiResult<bool> AddDiscordMemberRole(string guildId, string userId, string roleId, string reason = null)
        {
            return ChangeMemberRole(HttpMethod.Put, guildId, userId, roleId, reason);
        }

        /// <summary>
        /// Takes a role away. Not having it counts as success. A member who has
        /// since LEFT the server also counts as success: they don't have it anymore.
        /// </summary>
        public OSWApiResult<bool> RemoveDiscordMemberRole(string guildId, string userId, string roleId, string reason = null)
        {
            var r = ChangeMemberRole(HttpMethod.Delete, guildId, userId, roleId, reason);
            if (!r.Success && r.StatusCode == 404 && r.Error.IndexOf("Unknown Member", StringComparison.OrdinalIgnoreCase) >= 0)
                return OSWApiResult<bool>.Ok(true, 404);
            return r;
        }

        private OSWApiResult<bool> ChangeMemberRole(HttpMethod method, string guildId, string userId, string roleId, string reason)
        {
            if (!IsSnowflake(guildId)) return OSWApiResult<bool>.Fail("Server (guild) ID is missing or not a number.");
            if (!IsSnowflake(userId))  return OSWApiResult<bool>.Fail("Discord user ID is missing or not a number.");
            if (!IsSnowflake(roleId))  return OSWApiResult<bool>.Fail("Role ID is missing or not a number.");

            var r = DiscordBotSend(method, "guilds/" + guildId + "/members/" + userId + "/roles/" + roleId, reason);
            return r.Success ? OSWApiResult<bool>.Ok(true, r.StatusCode) : r.As<bool>();
        }

        /// <summary>
        /// Everyone in the server who currently has a role. Data = their user IDs.
        /// Needs "Server Members Intent" ON in the Developer Portal; without it
        /// Discord refuses and Error says so. Reads up to 20,000 members.
        /// </summary>
        public OSWApiResult<List<string>> ListDiscordRoleMembers(string guildId, string roleId)
        {
            if (!IsSnowflake(guildId) || !IsSnowflake(roleId))
                return OSWApiResult<List<string>>.Fail("Server ID or role ID is missing or not a number.");

            var ids = new List<string>();
            string after = "0";
            for (int page = 0; page < 20; page++)                 // 20 × 1000 = hard stop
            {
                var r = DiscordBotSend(HttpMethod.Get, "guilds/" + guildId + "/members?limit=1000&after=" + after);
                if (!r.Success)
                {
                    if (r.StatusCode == 403)
                        return OSWApiResult<List<string>>.Fail("Discord won't list server members. Turn on \"Server Members Intent\" in the Developer Portal → Bot tab.", 403);
                    return r.As<List<string>>();
                }

                JArray batch;
                try { batch = JArray.Parse(r.Data); }
                catch { return OSWApiResult<List<string>>.Fail("Discord sent back something unexpected while listing members."); }

                foreach (JToken mem in batch)
                {
                    DiscordMember m = DiscordMember.FromJson(mem);
                    if (m.RoleIds.Contains(roleId)) ids.Add(m.Id);
                    after = m.Id;                                  // next page starts after the last ID seen
                }
                if (batch.Count < 1000) return OSWApiResult<List<string>>.Ok(ids, r.StatusCode);
            }
            return OSWApiResult<List<string>>.Ok(ids);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  C. PLUMBING
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The ONE place every Discord request in OSWTools is sent, webhook or bot.
        ///
        /// Rules for both:
        ///   • At most one retry, and only for a SHORT rate-limit wait (≤ 5s):
        ///     this runs inside Streamer.bot actions, so it must never stall long.
        ///   • Never throws. Failures come back as plain English with a
        ///     "what to do" hint picked for webhooks or bots.
        ///
        ///   asBot       — true: add "Authorization: Bot &lt;token&gt;" (fails early if
        ///                 no token is saved). false: webhook URLs carry their own key.
        ///   makeBody    — builds the request body. A FUNCTION, not a body, because
        ///                 an HttpContent can only be sent once: a retry needs a fresh one.
        ///   auditReason — bot only: shown in Server Settings → Audit Log.
        /// </summary>
        private OSWApiResult<string> DiscordRequest(HttpMethod method, string url, bool asBot,
            Func<HttpContent> makeBody = null, string auditReason = null)
        {
            string token = "";
            if (asBot)
            {
                token = LoadDiscordBotToken();
                if (token.Length == 0)
                    return OSWApiResult<string>.Fail("No Discord bot token saved yet — run the Discord setup first.", 401);
            }
            string who = asBot ? "the bot" : "this webhook";

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var req = new HttpRequestMessage(method, url);
                    if (makeBody != null) req.Content = makeBody();
                    if (asBot)
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bot " + token);
                        if (!string.IsNullOrWhiteSpace(auditReason))
                        {
                            // Must be URL-encoded; Discord allows 512 characters.
                            string reason = auditReason.Length > 400 ? auditReason.Substring(0, 400) : auditReason;
                            req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", Uri.EscapeDataString(reason));
                        }
                    }

                    var resp = OSWHttp.Send(req);
                    int code = (int)resp.StatusCode;
                    string text = OSWHttp.ReadBody(resp);
                    if (resp.IsSuccessStatusCode) return OSWApiResult<string>.Ok(text, code);

                    if (code == 429 && attempt == 1)
                    {
                        double wait = 1;
                        try { wait = (double?)JObject.Parse(text)["retry_after"] ?? 1; } catch { }
                        if (wait <= 5) { Thread.Sleep(TimeSpan.FromSeconds(wait)); continue; }
                        return OSWApiResult<string>.Fail("Discord is rate-limiting " + who + " — try again in " + Math.Ceiling(wait) + "s.", code);
                    }

                    string msg = "";
                    try { msg = (string)JObject.Parse(text)["message"] ?? ""; } catch { }
                    string hint = asBot ? BotErrorHint(code) : WebhookErrorHint(code);
                    return OSWApiResult<string>.Fail(hint + " (" + code + ")" + (msg.Length > 0 ? ": " + msg : "."), code);
                }
                catch (Exception ex)
                {
                    return OSWApiResult<string>.Fail("Couldn't reach Discord: " + ex.Message);
                }
            }
            return OSWApiResult<string>.Fail("Discord is rate-limiting " + who + " — try again shortly.", 429);
        }

        /// <summary>Bot shortcut: a path under the API base, signed with the bot token.</summary>
        private OSWApiResult<string> DiscordBotSend(HttpMethod method, string path, string auditReason = null)
        {
            return DiscordRequest(method, DiscordApiBase + path, asBot: true, auditReason: auditReason);
        }

        private static string WebhookErrorHint(int code)
        {
            switch (code)
            {
                case 401:
                case 404: return "The webhook URL is wrong or the webhook was deleted in Discord";
                case 400: return "Discord didn't accept the message";
                case 413: return "The attachment is too large for Discord";
                default:  return "Discord returned an error";
            }
        }

        private static string BotErrorHint(int code)
        {
            switch (code)
            {
                case 401: return "Discord rejected the bot token — it was reset or copied wrong. Run the Discord setup again";
                case 403: return "The bot isn't allowed to do that. Give it \"Manage Roles\", and in Server Settings → Roles drag the bot's role ABOVE the role it hands out";
                case 404: return "Discord couldn't find that (check the server ID, role ID, or that the person is still in the server)";
                default:  return "Discord returned an error";
            }
        }

        /// <summary>Discord IDs ("snowflakes") are 17–20 digit numbers.</summary>
        private static bool IsSnowflake(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s.Length >= 15 && s.Length <= 21 && s.All(char.IsDigit);
        }

        // Strip any query string the streamer pasted along with the URL.
        private static string WebhookBase(string url) => url.Split('?')[0].TrimEnd('/');

        private static string MessageUrl(string webhookUrl, string messageId, string threadId)
            => WebhookBase(webhookUrl) + "/messages/" + Uri.EscapeDataString(messageId)
               + (string.IsNullOrEmpty(threadId) ? "" : "?thread_id=" + Uri.EscapeDataString(threadId));

        // Discord's documented limits, checked BEFORE sending so the streamer
        // gets "the description is too long" rather than a bare "400 Bad Request".
        private static string CheckDiscordMessage(string webhookUrl, string content, List<DiscordEmbedBuilder> list,
            string avatarUrl, bool requireSomething)
        {
            if (!DiscordWebhooks.IsWebhookUrl(webhookUrl))
                return "That isn't a Discord webhook URL (it should start with https://discord.com/api/webhooks/).";
            if (requireSomething && string.IsNullOrWhiteSpace(content) && list.Count == 0)
                return "Nothing to post — add text and/or an embed.";
            if (content != null && content.Length > 2000) return "The message text is over Discord's 2,000-character limit.";
            if (!string.IsNullOrWhiteSpace(avatarUrl) && !avatarUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return "The avatar must be an image URL (http/https). Discord can't use a file from this PC.";
            if (list.Count > 10) return "Discord allows at most 10 embeds per message.";
            int total = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                string n = list.Count > 1 ? " (embed " + (i + 1) + ")" : "";
                if ((e.Title ?? "").Length > 256) return "The embed title is over 256 characters" + n + ".";
                if ((e.Description ?? "").Length > 4096) return "The embed description is over 4,096 characters" + n + ".";
                if ((e.FooterText ?? "").Length > 2048) return "The embed footer is over 2,048 characters" + n + ".";
                if ((e.AuthorName ?? "").Length > 256) return "The embed author name is over 256 characters" + n + ".";
                if (e.Fields.Count > 25) return "An embed can have at most 25 fields" + n + ".";
                foreach (var f in e.Fields)
                    if ((f.Name ?? "").Length > 256 || (f.Value ?? "").Length > 1024) return "An embed field is too long (name 256 / value 1,024 max)" + n + ".";
                total += (e.Title ?? "").Length + (e.Description ?? "").Length + (e.FooterText ?? "").Length + (e.AuthorName ?? "").Length
                       + e.Fields.Sum(f => (f.Name ?? "").Length + (f.Value ?? "").Length);
            }
            return total > 6000 ? "All embeds together are over Discord's 6,000-character limit." : null;
        }

        // Translate a builder into Discord's JSON shape. Only parts that are
        // set get included, so Discord never sees an empty sub-object.
        // overrideImage: used for the attachment link (see SendDiscordWebhook).
        private static Dictionary<string, object> EmbedToJson(DiscordEmbedBuilder e, string overrideImage)
        {
            var d = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(e.Title)) d["title"] = e.Title;
            if (!string.IsNullOrEmpty(e.Description)) d["description"] = e.Description;
            if (!string.IsNullOrEmpty(e.Url)) d["url"] = e.Url;
            if (e.Color != 0) d["color"] = e.Color;
            if (e.TimestampUtc.HasValue)
                d["timestamp"] = DateTime.SpecifyKind(e.TimestampUtc.Value.Kind == DateTimeKind.Local ? e.TimestampUtc.Value.ToUniversalTime() : e.TimestampUtc.Value, DateTimeKind.Utc)
                                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(e.AuthorName))
                d["author"] = new Dictionary<string, string> { ["name"] = e.AuthorName, ["icon_url"] = e.AuthorIconUrl, ["url"] = e.AuthorUrl };
            if (e.Fields.Count > 0)
                d["fields"] = e.Fields.Select(f => new Dictionary<string, object> { ["name"] = Blankable(f.Name), ["value"] = Blankable(f.Value), ["inline"] = f.Inline }).ToList();
            string image = overrideImage ?? e.ImageUrl;
            if (!string.IsNullOrEmpty(image)) d["image"] = new Dictionary<string, string> { ["url"] = image };
            if (!string.IsNullOrEmpty(e.ThumbnailUrl)) d["thumbnail"] = new Dictionary<string, string> { ["url"] = e.ThumbnailUrl };
            if (!string.IsNullOrEmpty(e.FooterText))
                d["footer"] = new Dictionary<string, string> { ["text"] = e.FooterText, ["icon_url"] = e.FooterIconUrl };
            return d;
        }

        // Discord rejects the WHOLE post if any field's name or value is empty
        // (both must be 1–1,024 characters). A zero-width space is invisible
        // but counts as a character — the standard Discord trick for a field
        // that shows blank.
        private const string ZeroWidthSpace = "​";
        private static string Blankable(string s) => string.IsNullOrWhiteSpace(s) ? ZeroWidthSpace : s;

        // Cheap MIME guess. Discord doesn't strictly require accurate types but
        // browsers rendering the embed work better when this matches.
        private static string GuessImageContentType(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp":  return "image/bmp";
                default:      return "application/octet-stream";
            }
        }
    }
}
