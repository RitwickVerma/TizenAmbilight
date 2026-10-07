using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace TizenAmbilight
{
    /// <summary>
    /// Minimal JSON writer plus one-shot HTTP POST — deliberately dependency-free, so the app
    /// cannot fail on a missing NuGet package. Handles null, string, bool, the integer and
    /// floating types, IDictionary&lt;string,object&gt;, IEnumerable and byte[] (as hex).
    /// </summary>
    public static class Json
    {
        public static string ToJson(object o)
        {
            var sb = new StringBuilder(1024);
            Write(sb, o);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }

            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is bool) { sb.Append(((bool)o) ? "true" : "false"); return; }
            if (o is byte[]) { WriteString(sb, Hex((byte[])o)); return; }

            if (o is sbyte || o is byte || o is short || o is ushort ||
                o is int || o is uint || o is long || o is ulong)
            {
                sb.Append(Convert.ToInt64(o).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (o is float || o is double || o is decimal)
            {
                double d = Convert.ToDouble(o);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            IDictionary<string, object> map = o as IDictionary<string, object>;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            if (o is IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, o.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>POST a JSON body over a BRAND-NEW HttpClient with "Connection: close", read
        /// the response to completion, dispose everything, then return. Nothing is pooled or kept
        /// alive, which is what makes each report independently observable. Returns null on
        /// success or a short error string.</summary>
        public static string Post(string url, string jsonBody, int timeoutSeconds)
        {
            HttpClient c = null;
            try
            {
                c = new HttpClient();
                c.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.ConnectionClose = true;
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonBody));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                req.Content = content;

                using (req)
                using (var resp = c.SendAsync(req, HttpCompletionOption.ResponseContentRead)
                                   .GetAwaiter().GetResult())
                {
                    try { resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
                    int code = (int)resp.StatusCode;
                    if (code >= 200 && code < 300) return null;
                    return "HTTP " + code;
                }
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (c != null) { try { c.Dispose(); } catch { } }
            }
        }
    }
}
