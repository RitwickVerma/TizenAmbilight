using System;
using System.Net.Http;

namespace TizenAmbilight
{
    /// <summary>
    /// Best-effort reader for the TV's OWN local REST API, used to learn whether the panel is
    /// actually ON before the loop spends a capture call on it.
    ///
    /// Samsung TVs expose a local REST API on port 8001. From inside the app the load-bearing
    /// assumption is that <c>GET http://127.0.0.1:8001/api/v2/</c> answers with a JSON body whose
    /// <c>device.PowerState</c> is the string <c>"on"</c> or <c>"standby"</c>. THAT ASSUMPTION IS
    /// NOT VERIFIED FROM INSIDE THE APP SANDBOX HERE — it was never run on the TV. If the sandbox
    /// blocks loopback, every probe reports <see cref="Power.Unknown"/> and the caller's fallback
    /// (assume ON, keep capturing) makes the app behave exactly as it did before this file existed.
    ///
    /// This type NEVER throws. Any network error, timeout, non-2xx status or unexpected payload is
    /// returned as <see cref="Power.Unknown"/> with a short <see cref="Result.Error"/>, and the
    /// caller MUST treat Unknown as "assume the panel is ON".
    /// </summary>
    public static class PowerStateProbe
    {
        /// <summary>The local-API endpoint. The one endpoint this app reads; not invented elsewhere.</summary>
        public const string DefaultUrl = "http://127.0.0.1:8001/api/v2/";

        public enum Power
        {
            Unknown,   // could not tell — caller assumes ON
            On,
            Standby
        }

        public sealed class Result
        {
            public Power State = Power.Unknown;
            public string Raw = "";     // the raw PowerState string, or "" when it could not be read
            public string Error;        // non-null when the probe could not answer

            public string Describe()
            {
                if (Error != null) return "unknown(" + Error + ")";
                return Raw;
            }
        }

        /// <summary>
        /// One probe. A BRAND-NEW <see cref="HttpClient"/> per call with "Connection: close" —
        /// the same style the stats POST uses — so nothing is pooled and each poll is independently
        /// observable. <paramref name="timeoutSeconds"/> should stay short (2-3 s): a stalled probe
        /// must not stall the capture loop.
        /// </summary>
        public static Result Query(string url, int timeoutSeconds)
        {
            if (string.IsNullOrEmpty(url)) return new Result { Error = "empty url" };
            if (timeoutSeconds < 1) timeoutSeconds = 1;

            HttpClient c = null;
            try
            {
                c = new HttpClient();
                c.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.ConnectionClose = true;

                using (req)
                using (var resp = c.SendAsync(req, HttpCompletionOption.ResponseContentRead)
                                   .GetAwaiter().GetResult())
                {
                    string body = "";
                    try { body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""; } catch { }

                    int code = (int)resp.StatusCode;
                    if (code < 200 || code >= 300)
                        return new Result { State = Power.Unknown, Error = "HTTP " + code };

                    string val = ExtractStringValue(body, "PowerState");
                    if (val == null)
                        return new Result { State = Power.Unknown, Error = "PowerState not present in payload" };

                    if (val == "on") return new Result { State = Power.On, Raw = val };
                    if (val == "standby") return new Result { State = Power.Standby, Raw = val };

                    return new Result { State = Power.Unknown, Raw = val, Error = "unrecognised PowerState '" + val + "'" };
                }
            }
            catch (Exception e)
            {
                return new Result { State = Power.Unknown, Error = e.GetType().Name + ": " + e.Message };
            }
            finally
            {
                if (c != null) { try { c.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// Deliberately minimal: find the literal <c>"PowerState"</c> key, then the next ':' and the
        /// next quoted string after it, and return its contents. This is NOT a JSON parser — it does
        /// no nesting, no escape handling and no type checks, because the only value it has to read
        /// is an ASCII token ("on"/"standby"). Returns null when the key or value is absent.
        /// </summary>
        public static string ExtractStringValue(string json, string key)
        {
            if (json == null) return null;

            string needle = "\"" + key + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;

            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return null;

            // Skip whitespace, then REQUIRE a quote to start the value. Without this check a
            // non-string value (null, a number, a nested object) let the scan run on to the NEXT
            // quoted token - the following KEY NAME - and return it as if it were the value. For
            // {"PowerState":null,"Version":"2.0"} that returned "Version". The docstring promises
            // null when the value is absent, so this is the behaviour the caller was written against.
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;

            int open = i;
            int close = json.IndexOf('"', open + 1);
            if (close < 0) return null;

            return json.Substring(open + 1, close - open - 1);
        }
    }
}
