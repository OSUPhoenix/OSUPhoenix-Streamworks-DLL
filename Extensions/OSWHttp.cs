////OSWHttp.cs//
// =============================================================================
// NEW FILE — Extensions\OSWHttp.cs
// Shared plumbing for every module that talks to a web API (Twitch, Google,
// Discord, and whatever comes next). Needs a reference to System.Net.Http.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;

namespace OSWTools
{
    /// <summary>
    /// What every web-API call in OSWTools returns, instead of throwing.
    /// Check Success first. On failure, Error says what went wrong in plain
    /// English, and StatusCode is the HTTP code (0 = never reached the server).
    /// </summary>
    public class OSWApiResult<T>
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Error { get; set; } = "";
        public T Data { get; set; }

        public static OSWApiResult<T> Ok(T data, int code = 200)
            => new OSWApiResult<T> { Success = true, StatusCode = code, Data = data };
        public static OSWApiResult<T> Fail(string error, int code = 0)
            => new OSWApiResult<T> { Success = false, StatusCode = code, Error = error ?? "" };

        /// <summary>Carry a failure over to a different result type.</summary>
        public OSWApiResult<TOther> As<TOther>()
            => OSWApiResult<TOther>.Fail(Error, StatusCode);
    }

    /// <summary>
    /// The one HttpClient for the whole Streamer.bot process, plus a
    /// synchronous Send. Internal: modules use it, scripts never need to.
    /// </summary>
    internal static class OSWHttp
    {
        // ONE HttpClient, never one per request. Each HttpClient owns its
        // own pool of network connections; creating and disposing one per
        // call leaves sockets stuck in TIME_WAIT and can run out of them.
        // (Settable only so tests can swap in a fake server.)
        internal static HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Streamer.bot runs C# actions synchronously, so we have to wait for
        // the answer somewhere. ConfigureAwait(false) means "don't hop back
        // to the calling thread to finish" — that hop is what deadlocks a
        // UI thread that's blocked waiting in .GetResult(). The Timeout
        // above guarantees a call can never hang forever.
        internal static HttpResponseMessage Send(HttpRequestMessage req)
            => Client.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult();

        internal static string ReadBody(HttpResponseMessage resp)
            => resp.Content == null ? "" : resp.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        /// <summary>POST an HTML-form style body (what OAuth token endpoints expect).</summary>
        internal static HttpResponseMessage PostForm(string url, IEnumerable<KeyValuePair<string, string>> fields)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
            return Send(req);
        }

        internal static readonly HttpMethod Patch = new HttpMethod("PATCH");

        /// <summary>
        /// Cryptographically random, URL-safe text. Used for OAuth "state"
        /// values and PKCE verifiers — anything an attacker must not guess,
        /// which is why it's RandomNumberGenerator and not Random.
        /// </summary>
        internal static string RandomUrlSafe(int byteCount = 32)
        {
            var bytes = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        internal static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
