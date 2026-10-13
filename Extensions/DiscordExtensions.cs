// =============================================================================
// OSWTools — Extensions/DiscordExtensions.cs
//
// Discord webhook posting via a fluent embed builder. Supports optional file
// attachments (multipart upload) so embeds can reference inline images.
//
// USAGE (unchanged — existing tools keep working exactly as before):
//   var embed = new DiscordEmbedBuilder()
//       .WithTitle("Achievement Unlocked")
//       .WithColor(0xCC0000)
//       .WithDescription("PhoenixHype reached 100 messages!")
//       .WithAuthor("PhoenixHype", profilePicUrl, "https://twitch.tv/phoenixhype")
//       .AddField("Time", DateTime.UtcNow.ToString("u"), inline: false)
//       .AddField("Tier", "1", inline: true)
//       .WithFooter("OSUPhoenix StreamWorks", iconUrl);
//
//   bool ok = Lib.PostDiscordWebhook(webhookUrl, embed, attachmentPath: screenshotPath);
//
// NEW (merged in for the multi-scheduler — see the second half of the file):
//   SendDiscordWebhook   → returns the posted message's ID, so the post can be
//                          edited or deleted later ("Moved to 8 PM", "Canceled")
//   EditDiscordWebhookMessage / DeleteDiscordWebhookMessage
//   DiscordWebhooks.Timestamp(utc) → a time every viewer sees in THEIR zone
//   .WithTimestamp(utc), .WithColor("#A257ED") on the builder
//   Discord's size limits checked BEFORE sending, so errors explain themselves
//   One short automatic retry when Discord rate-limits (429)
//
// FIXED in this merge:
//   • A field with an empty name or value made Discord reject the WHOLE post
//     (400). The Achievement System's AddField("Qualifier", Qualifier ?? "")
//     hit this for every achievement without a qualifier, so those never
//     reached Discord. Blank fields are now sent as an invisible zero-width
//     space, which Discord accepts and shows as empty.
//   • The post waited with .GetAwaiter().GetResult() but no
//     ConfigureAwait(false). Called from a WinForms button (e.g. a "Test
//     webhook" button), that can freeze the form for good. Now goes through
//     OSWHttp, which guards against it.
//   • An attachment made PostDiscordWebhook change the CALLER'S embed
//     (embed.WithImage("attachment://...")). Reusing that builder for a
//     second post without the file then showed a broken image. The
//     attachment link is now added only to the copy that gets sent.
//
// Returns: PostDiscordWebhook → true/false (never throws, as before).
//          New methods → OSWApiResult<T> (never throw; Error explains failures).
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
    // ─────────────────────────────────────────────────────────────────────────
    // Public model: a single field within an embed (the "name: value" pairs
    // shown beneath the description). Inline=true puts up to 3 per row.
    // ─────────────────────────────────────────────────────────────────────────
    public class DiscordEmbedField
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public bool Inline { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public builder: fluent API for assembling Discord embeds.
    // Mirrors Discord's webhook embed schema:
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

        // NEW — shown in the embed footer, in each viewer's own time zone
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

        // ── NEW setters ───────────────────────────────────────────────────
        /// <summary>Hex color, e.g. "#A257ED" (same result as WithColor(0xA257ED)).</summary>
        public DiscordEmbedBuilder WithColor(string hex)      { Color = DiscordWebhooks.Color(hex); return this; }
        /// <summary>Footer timestamp; Discord shows it in each viewer's own time zone.</summary>
        public DiscordEmbedBuilder WithTimestamp(DateTime utc) { TimestampUtc = utc; return this; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NEW — helpers that don't need a Streamer.bot connection.
    // ─────────────────────────────────────────────────────────────────────────
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

    // ═════════════════════════════════════════════════════════════════════════
    // OSWLib partial — Discord webhook methods
    // ═════════════════════════════════════════════════════════════════════════
    public partial class OSWLib
    {
        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC (unchanged signature): Post a webhook with one embed and
        // optional file attachment.
        //
        //   webhookUrl     — Discord webhook URL (full URL with token)
        //   embed          — DiscordEmbedBuilder with the embed contents
        //   attachmentPath — optional path to a file to upload alongside the embed.
        //                    If non-null and the file exists, it's attached as
        //                    "file" and the embed's image URL is set to
        //                    "attachment://<filename>" automatically.
        //
        // Returns: true if Discord accepted the post, false otherwise.
        // Now a thin wrapper over SendDiscordWebhook, so it shares its fixes.
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
        // NEW: the full-featured send. Returns the posted message's ID — keep
        // it to edit or delete the post later.
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

            var r = DiscordSend(HttpMethod.Post, url, () =>
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
        /// NEW: replaces the text and/or embeds of a message this webhook posted.
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

            var r = DiscordSend(OSWHttp.Patch, MessageUrl(webhookUrl, messageId, threadId),
                () => new StringContent(json, Encoding.UTF8, "application/json"));
            return r.Success ? OSWApiResult<bool>.Ok(true, r.StatusCode) : r.As<bool>();
        }

        /// <summary>NEW: deletes a message this webhook posted.</summary>
        public OSWApiResult<bool> DeleteDiscordWebhookMessage(string webhookUrl, string messageId, string threadId = null)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return OSWApiResult<bool>.Fail("No message ID given.");
            if (!DiscordWebhooks.IsWebhookUrl(webhookUrl)) return OSWApiResult<bool>.Fail("That isn't a Discord webhook URL.");
            var r = DiscordSend(HttpMethod.Delete, MessageUrl(webhookUrl, messageId, threadId), null);
            // 404 = already gone. Same end result, so not an error.
            return r.Success || r.StatusCode == 404 ? OSWApiResult<bool>.Ok(true, r.StatusCode) : r.As<bool>();
        }

        // ── Internals ─────────────────────────────────────────────────────────

        // Strip any query string the streamer pasted along with the URL.
        private static string WebhookBase(string url) => url.Split('?')[0].TrimEnd('/');

        private static string MessageUrl(string webhookUrl, string messageId, string threadId)
            => WebhookBase(webhookUrl) + "/messages/" + Uri.EscapeDataString(messageId)
               + (string.IsNullOrEmpty(threadId) ? "" : "?thread_id=" + Uri.EscapeDataString(threadId));

        private OSWApiResult<string> DiscordSend(HttpMethod method, string url, Func<HttpContent> makeBody)
        {
            // At most one retry, and only for a SHORT rate-limit wait — this
            // runs inside Streamer.bot actions, so it must never stall long.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var req = new HttpRequestMessage(method, url);
                    if (makeBody != null) req.Content = makeBody();
                    var resp = OSWHttp.Send(req);
                    int code = (int)resp.StatusCode;
                    string text = OSWHttp.ReadBody(resp);
                    if (resp.IsSuccessStatusCode) return OSWApiResult<string>.Ok(text, code);

                    if (code == 429 && attempt == 1)
                    {
                        double wait = 1;
                        try { wait = (double?)JObject.Parse(text)["retry_after"] ?? 1; } catch { }
                        if (wait <= 5) { Thread.Sleep(TimeSpan.FromSeconds(wait)); continue; }
                        return OSWApiResult<string>.Fail("Discord is rate-limiting this webhook — try again in " + Math.Ceiling(wait) + "s.", code);
                    }
                    string msg = "";
                    try { msg = (string)JObject.Parse(text)["message"] ?? ""; } catch { }
                    string hint = code == 401 || code == 404
                        ? "The webhook URL is wrong or the webhook was deleted in Discord"
                        : code == 400 ? "Discord didn't accept the message"
                        : code == 413 ? "The attachment is too large for Discord"
                        : "Discord returned an error";
                    return OSWApiResult<string>.Fail(hint + " (" + code + ")" + (msg.Length > 0 ? ": " + msg : "."), code);
                }
                catch (Exception ex)
                {
                    return OSWApiResult<string>.Fail("Couldn't reach Discord: " + ex.Message);
                }
            }
            return OSWApiResult<string>.Fail("Discord is rate-limiting this webhook — try again shortly.", 429);
        }

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
        // that shows blank. Without this, the Achievement System's
        // AddField("Qualifier", Qualifier ?? "") silently failed to post
        // every achievement that has no qualifier.
        private const string ZeroWidthSpace = "\u200B";
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
