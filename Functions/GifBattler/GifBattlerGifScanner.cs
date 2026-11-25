// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerGifScanner.cs DLL v1.2.2
//
//  Finds a native Twitch GIF inside a Streamer.bot trigger's args,
//  without needing to know what Streamer.bot named the arg.
//
//  WHY A "SCANNER" INSTEAD OF READING ONE NAMED ARG:
//    Twitch sends a GIF as a message *fragment* (EventSub) — an object
//    like { type: "gif", text: "...", gif: { gif_id, url } }.
//    Streamer.bot turns fragments into its own "parts" objects
//    ({ type, text, gifId, url }) — that's what chat widgets read from
//    the WebSocket event's data.parts. Which C# args (if any) carry
//    those parts differs between Streamer.bot versions and isn't
//    documented. So instead of guessing a name, this looks at EVERY
//    arg value and recognises a GIF part by its SHAPE.
//
//  HOW IT RECOGNISES A GIF PART:
//    Any object that has a url AND either
//      • a gifId / gif_id property, or
//      • type == "gif"
//    Property names are matched case-insensitively, because C# objects
//    serialise as PascalCase (Url, GifId) while JSON uses camelCase
//    (url, gifId) or snake_case (gif_id). The gifId check matters: if
//    Streamer.bot stores "type" as an enum, it serialises as a NUMBER,
//    so the type == "gif" test alone would miss it.
//
//  HOW IT LOOKS INSIDE A VALUE:
//    • string that looks like JSON ([...] or {...}) → parsed, searched
//    • any other object (lists, Streamer.bot classes) → converted to a
//      JSON tree with JToken.FromObject, then searched
//    • numbers / bools / plain text → skipped (can't hold a part)
//    Depth is capped and every conversion is try/catch'd, so an odd
//    object can never crash a chat message.
// ═══════════════════════════════════════════════════════════════════
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OSWTools.Functions.GifBattler
{
    internal static class GifBattlerGifScanner
    {
        private const int MAX_DEPTH = 10;

        /// <summary>A found GIF: its URL and which arg it came from.</summary>
        internal sealed class Hit
        {
            internal string Url;
            internal string GifId;
            internal string ArgKey;
        }

        // One shared serializer: ignores reference loops (SB objects can
        // point back at their parents) and swallows per-property errors
        // (some getters throw when read out of context).
        private static readonly JsonSerializer _serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Error = (sender, e) => { e.ErrorContext.Handled = true; }
        });

        /// <summary>Returns the first GIF part found in any arg, or null.</summary>
        internal static Hit Find(IDictionary<string, object> args)
        {
            if (args == null) return null;

            foreach (var kv in args)
            {
                JToken tree = ToTree(kv.Value);
                if (tree == null) continue;

                Hit hit = Search(tree, 0);
                if (hit != null)
                {
                    hit.ArgKey = kv.Key;
                    return hit;
                }
            }

            return null;
        }

        /// <summary>
        /// Turns an arg value into a searchable JSON tree, or null if the
        /// value can't possibly contain a GIF part. Also used by the
        /// diagnostic so it can print complex args readably.
        /// </summary>
        internal static JToken ToTree(object value)
        {
            if (value == null) return null;

            // Value types (int, bool, DateTime, enums...) can't hold parts.
            if (value is ValueType) return null;

            string s = value as string;
            if (s != null)
            {
                string t = s.Trim();
                bool looksJson = (t.StartsWith("{") && t.EndsWith("}")) ||
                                 (t.StartsWith("[") && t.EndsWith("]"));
                if (!looksJson) return null;
                try { return JToken.Parse(t); }
                catch { return null; }
            }

            try { return JToken.FromObject(value, _serializer); }
            catch { return null; }
        }

        private static Hit Search(JToken token, int depth)
        {
            if (token == null || depth > MAX_DEPTH) return null;

            var obj = token as JObject;
            if (obj != null)
            {
                Hit here = AsGifPart(obj);
                if (here != null) return here;

                foreach (var prop in obj.Properties())
                {
                    Hit inner = Search(prop.Value, depth + 1);
                    if (inner != null) return inner;
                }
                return null;
            }

            var arr = token as JArray;
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    Hit inner = Search(item, depth + 1);
                    if (inner != null) return inner;
                }
            }

            return null;
        }

        /// <summary>Is this one object a GIF part? Handles all three shapes:
        ///   Streamer.bot part   { type:"gif", gifId, url }
        ///   C# object           { Type:3, GifId, Url }
        ///   raw EventSub        { type:"gif", gif:{ gif_id, url } }
        /// </summary>
        private static Hit AsGifPart(JObject obj)
        {
            // Raw EventSub fragment: the url lives one level down in "gif".
            var nested = Prop(obj, "gif") as JObject;
            if (nested != null && IsTypeGif(obj))
            {
                string nUrl = Str(Prop(nested, "url"));
                if (!string.IsNullOrEmpty(nUrl))
                    return new Hit { Url = nUrl, GifId = Str(Prop(nested, "gif_id")) ?? Str(Prop(nested, "gifId")) };
            }

            string url = Str(Prop(obj, "url"));
            if (string.IsNullOrEmpty(url)) return null;

            string gifId = Str(Prop(obj, "gifId")) ?? Str(Prop(obj, "gif_id"));
            if (!string.IsNullOrEmpty(gifId) || IsTypeGif(obj))
                return new Hit { Url = url, GifId = gifId };

            return null;
        }

        private static bool IsTypeGif(JObject obj)
        {
            string type = Str(Prop(obj, "type"));
            return type != null && type.Equals("gif", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Case-insensitive property lookup.</summary>
        private static JToken Prop(JObject obj, string name)
        {
            foreach (var p in obj.Properties())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Value;
            return null;
        }

        private static string Str(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Object || t.Type == JTokenType.Array) return null;
            string s = t.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
