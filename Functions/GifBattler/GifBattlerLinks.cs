// ═══════════════════════════════════════════════════════════════════
//  OSWTools — Functions/GifBattler/GifBattlerLinks.cs        DLL v1.4.0
//
//  Turns the link a viewer pastes into the link we can actually show.
//
//  THE PROBLEM THIS SOLVES (especially on phones):
//    In the Giphy app or website, "Copy Link" gives a PAGE link:
//        https://giphy.com/gifs/funny-dance-party-joSNxeswxuc74Juo8X
//    A web page is not a picture, so the overlay could not show it — the
//    old rule was "paste the direct GIF link, not the page". On a phone
//    that is very hard to do. Now both work.
//
//  HOW IT WORKS:
//    Every Giphy GIF has an ID, and the ID is the LAST piece of its page
//    link (the part after the final dash). The picture itself always lives at
//        https://media.giphy.com/media/<ID>/giphy.gif
//    so we pull the ID out of the page link and build that address.
//    No internet lookup is needed — it is just string work, so it is fast
//    and cannot hang.
//
//  WHAT IS ACCEPTED:
//    • https://giphy.com/gifs/<words>-<ID>        page link (web + app)
//    • https://giphy.com/gifs/<ID>                page link with no words
//    • https://giphy.com/stickers/<words>-<ID>    sticker page link
//    • https://giphy.com/embed/<ID>               embed link
//    • direct links (media.giphy.com, i.giphy.com, mediaN.giphy.com) are
//      passed through UNCHANGED, exactly as before
//    • links on any other host are passed through unchanged too, so a
//      streamer who adds another site to AllowedDomains still works
//
//  WHAT IS REFUSED (returns null):
//    • giphy.com pages that are not a single GIF: clips (video), search
//      pages, channel pages, or a link where no ID can be found
//    • gph.is short links are NOT converted here — a short link only
//      reveals its target after asking the internet, which this file
//      deliberately never does.
//
//  HOW CAN WE TELL AN ID FROM A WORD?
//    A page link ends "...-<ID>", but a link with no ID could end in an
//    ordinary word ("giphy.com/gifs/celebration"). Real IDs are 8-32
//    letters/digits and, because they are random, essentially always
//    contain a digit or a capital letter. A plain lowercase word does
//    not, so it is refused instead of producing a link that goes nowhere.
// ═══════════════════════════════════════════════════════════════════
using System;

namespace OSWTools.Functions.GifBattler
{
    internal static class GifBattlerLinks
    {
        internal const int MinIdLength = 8;
        internal const int MaxIdLength = 32;

        /// <summary>
        /// Returns the link to show for what the viewer pasted, or null when
        /// it is not something we can show (the caller then ignores it).
        /// The result still has to pass the AllowedDomains check.
        /// </summary>
        internal static string Resolve(string pasted)
        {
            string text = (pasted ?? "").Trim();

            // Some apps wrap a pasted link in < > — take them off.
            if (text.Length > 2 && text[0] == '<' && text[text.Length - 1] == '>')
                text = text.Substring(1, text.Length - 2).Trim();

            if (text.Length == 0) return null;

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);

            // Anything that is not a giphy.com PAGE keeps working exactly as before.
            if (host != "giphy.com") return text;

            string[] seg = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 2) return null;

            string kind = seg[0].ToLowerInvariant();
            string idPart;
            if (kind == "gifs" || kind == "stickers") idPart = seg[seg.Length - 1];   // ".../words-ID"
            else if (kind == "embed")                 idPart = seg[1];                // ".../embed/ID"
            else return null;                                                        // clips, search, channels...

            string id = ExtractId(idPart);
            return id == null ? null : MediaUrl(id);
        }

        /// <summary>The always-available picture address for a Giphy ID.</summary>
        internal static string MediaUrl(string id)
        {
            return "https://media.giphy.com/media/" + id + "/giphy.gif";
        }

        /// <summary>
        /// "funny-dance-party-joSNxeswxuc74Juo8X" -> "joSNxeswxuc74Juo8X".
        /// Returns null if what is left does not look like a Giphy ID.
        /// </summary>
        internal static string ExtractId(string lastSegment)
        {
            if (string.IsNullOrEmpty(lastSegment)) return null;

            int dash = lastSegment.LastIndexOf('-');
            string id = dash >= 0 ? lastSegment.Substring(dash + 1) : lastSegment;

            if (id.Length < MinIdLength || id.Length > MaxIdLength) return null;

            bool mixed = false;                       // has a digit or a capital letter
            foreach (char c in id)
            {
                bool digit = c >= '0' && c <= '9';
                bool upper = c >= 'A' && c <= 'Z';
                bool lower = c >= 'a' && c <= 'z';
                if (!digit && !upper && !lower) return null;   // letters and digits only
                if (digit || upper) mixed = true;
            }
            return mixed ? id : null;
        }
    }
}
