// =============================================================================
// OSWTools — Extensions/DiscordExtensions.cs
//
// Discord webhook posting via a fluent embed builder. Supports optional file
// attachments (multipart upload) so embeds can reference inline images.
//
// USAGE:
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
// Returns true if Discord accepts the post (HTTP 2xx). All exceptions are
// caught and logged; the method never throws.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;

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
    }

    // ═════════════════════════════════════════════════════════════════════════
    // OSWLib partial — adds PostDiscordWebhook() instance method
    // ═════════════════════════════════════════════════════════════════════════
    public partial class OSWLib
    {
        // Static HttpClient: one per process. Spinning up new clients per call
        // leaks sockets — this is the recommended Microsoft pattern.
        private static readonly HttpClient _discordHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC: Post a webhook with one embed and optional file attachment.
        //
        // Parameters:
        //   webhookUrl     — Discord webhook URL (full URL with token)
        //   embed          — DiscordEmbedBuilder with the embed contents
        //   attachmentPath — optional path to a file to upload alongside the embed.
        //                    If non-null and the file exists, it's attached as
        //                    "file" and the embed's image URL is set to
        //                    "attachment://<filename>" automatically.
        //
        // Returns: true if Discord returned 2xx, false otherwise.
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

            try
            {
                bool hasAttachment = !string.IsNullOrEmpty(attachmentPath)
                                  && File.Exists(attachmentPath);

                // Auto-link the embed image to the attachment filename
                if (hasAttachment && string.IsNullOrEmpty(embed.ImageUrl))
                    embed.WithImage("attachment://" + Path.GetFileName(attachmentPath));

                string payloadJson = SerializeEmbed(embed);

                using (var form = new MultipartFormDataContent())
                {
                    if (hasAttachment)
                    {
                        var img = new ByteArrayContent(File.ReadAllBytes(attachmentPath));
                        // Best-effort content type detection by extension
                        img.Headers.ContentType = new MediaTypeHeaderValue(
                            GuessImageContentType(attachmentPath));
                        form.Add(img, "file", Path.GetFileName(attachmentPath));
                    }

                    form.Add(
                        new StringContent(payloadJson, Encoding.UTF8, "application/json"),
                        "payload_json");

                    var resp = _discordHttp.PostAsync(webhookUrl, form)
                                           .GetAwaiter().GetResult();

                    if (!resp.IsSuccessStatusCode)
                    {
                        LogWarn($"[Discord] Webhook returned {(int)resp.StatusCode} {resp.StatusCode}");
                        return false;
                    }

                    LogInfo($"[Discord] Posted ({(int)resp.StatusCode})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError("[Discord] Post failed: " + ex.Message);
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Translate the builder into Discord's expected JSON shape.
        //
        // We use anonymous types because they serialize cleanly via Newtonsoft
        // and let us conditionally include sub-objects (author, image, etc.)
        // by passing null where Discord expects "field absent".
        // ─────────────────────────────────────────────────────────────────────
        private static string SerializeEmbed(DiscordEmbedBuilder e)
        {
            var fields = new List<object>();
            foreach (var f in e.Fields)
                fields.Add(new { name = f.Name, value = f.Value, inline = f.Inline });

            object author = string.IsNullOrEmpty(e.AuthorName) ? null
                : (object)new { name = e.AuthorName, icon_url = e.AuthorIconUrl, url = e.AuthorUrl };

            object image = string.IsNullOrEmpty(e.ImageUrl) ? null
                : (object)new { url = e.ImageUrl };

            object thumbnail = string.IsNullOrEmpty(e.ThumbnailUrl) ? null
                : (object)new { url = e.ThumbnailUrl };

            object footer = string.IsNullOrEmpty(e.FooterText) ? null
                : (object)new { text = e.FooterText, icon_url = e.FooterIconUrl };

            var payload = new
            {
                content = string.IsNullOrEmpty(e.Content) ? null : e.Content,
                embeds = new[]
                {
                    new
                    {
                        title       = e.Title,
                        description = e.Description,
                        url         = e.Url,
                        color       = e.Color,
                        author      = author,
                        fields      = fields.Count > 0 ? fields : null,
                        image       = image,
                        thumbnail   = thumbnail,
                        footer      = footer
                    }
                }
            };

            // NullValueHandling.Ignore drops the null sub-objects so Discord
            // doesn't reject the payload for malformed empty fields.
            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }

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
