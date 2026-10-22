using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TizenAmbilight
{
    /// <summary>
    /// The user-editable settings file, <c>config.json</c>, bundled into the TPK's <c>res/</c>
    /// directory at build time (see the csproj and <c>config.example.json</c>).
    ///
    /// Why a bundled file: a headless service app has no settings screen, and the Tizen
    /// <c>Preference</c> store can only be written by the app itself, so before this file existed the
    /// only way to point the app at a strip was to edit the compiled-in defaults. A JSON file next to
    /// the csproj is something anyone building from source can edit, and it never enters git.
    ///
    /// Keys are the preference names documented in the README (<c>wledIp</c>, <c>ledCount</c>, ...).
    /// The file must be one flat JSON object; values may be strings, numbers or booleans. Keys starting
    /// with <c>_</c> are ignored, so <c>"_comment": "..."</c> works as a comment.
    ///
    /// No Tizen dependency, so the host test harness compiles and exercises it directly.
    /// </summary>
    public static class ConfigFile
    {
        static Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Where the values came from, for the startup log and telemetry: a path, "none",
        /// or "error: ..." when a file existed but could not be parsed.</summary>
        public static string Source = "none";

        /// <summary>Load the first existing file from <paramref name="paths"/>. A missing file is not
        /// an error (every key falls back to its default); a malformed one is reported in
        /// <see cref="Source"/> and ignored as a whole rather than half-applied. Never throws.</summary>
        public static void Load(params string[] paths)
        {
            _values = new Dictionary<string, object>(StringComparer.Ordinal);
            Source = "none";
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    if (!System.IO.File.Exists(p)) continue;
                    _values = Parse(System.IO.File.ReadAllText(p, Encoding.UTF8));
                    Source = p;
                }
                catch (Exception e)
                {
                    _values = new Dictionary<string, object>(StringComparer.Ordinal);
                    Source = "error: " + p + ": " + e.Message;
                }
                return;
            }
        }

        /// <summary>Replace the loaded values directly (test harness).</summary>
        public static void Set(Dictionary<string, object> values)
        {
            _values = values ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public static int Count { get { return _values.Count; } }

        public static bool TryGetString(string key, out string value)
        {
            value = null;
            object o;
            if (!_values.TryGetValue(key, out o) || o == null) return false;
            if (o is string) { value = (string)o; return true; }
            if (o is double) { value = ((double)o).ToString("R", CultureInfo.InvariantCulture); return true; }
            if (o is bool) { value = (bool)o ? "true" : "false"; return true; }
            return false;
        }

        /// <summary>Numbers, booleans (true = 1) and numeric strings all read as a number, so
        /// <c>"hdr": true</c>, <c>"hdr": 1</c> and <c>"hdr": "1"</c> mean the same thing.
        /// NaN and infinities are refused.</summary>
        public static bool TryGetDouble(string key, out double value)
        {
            value = 0;
            object o;
            if (!_values.TryGetValue(key, out o) || o == null) return false;
            if (o is double) value = (double)o;
            else if (o is bool) value = (bool)o ? 1 : 0;
            else if (o is string)
            {
                if (!double.TryParse((string)o, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return false;
            }
            else return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // ================================================================ parser

        /// <summary>Parse one flat JSON object. Nested objects and arrays are rejected: no setting
        /// takes one, and accepting them would let a typo'd structure load "successfully" with
        /// nothing applied. Throws <see cref="FormatException"/> with a character offset.</summary>
        public static Dictionary<string, object> Parse(string text)
        {
            if (text == null) throw new FormatException("empty file");
            var p = new Parser(text);
            var result = p.ReadObject();
            p.SkipWs();
            if (!p.AtEnd) throw p.Error("trailing content after the object");
            return result;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s)
            {
                _s = s;
                // Tolerate a UTF-8 byte-order mark, which several editors write by default.
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;
            }

            public bool AtEnd { get { return _i >= _s.Length; } }

            public FormatException Error(string what)
            {
                return new FormatException(what + " at offset " + _i);
            }

            public void SkipWs()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
            }

            void Expect(char c)
            {
                SkipWs();
                if (_i >= _s.Length || _s[_i] != c) throw Error("expected '" + c + "'");
                _i++;
            }

            public Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                SkipWs();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    SkipWs();
                    string key = ReadString();
                    Expect(':');
                    object v = ReadValue();
                    if (!key.StartsWith("_", StringComparison.Ordinal)) d[key] = v;
                    SkipWs();
                    if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                    Expect('}');
                    return d;
                }
            }

            object ReadValue()
            {
                SkipWs();
                if (_i >= _s.Length) throw Error("unexpected end of file");
                char c = _s[_i];
                if (c == '"') return ReadString();
                if (c == '{' || c == '[') throw Error("nested objects and arrays are not supported");
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;
                return ReadNumber();
            }

            bool Match(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
                _i += word.Length;
                return true;
            }

            double ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0) _i++;
                double d;
                if (_i == start ||
                    !double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out d))
                {
                    _i = start;
                    throw Error("invalid value");
                }
                return d;
            }

            string ReadString()
            {
                if (_i >= _s.Length || _s[_i] != '"') throw Error("expected a string");
                _i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw Error("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) throw Error("unterminated escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("short \\u escape");
                            int cp;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber,
                                              CultureInfo.InvariantCulture, out cp))
                                throw Error("bad \\u escape");
                            sb.Append((char)cp);
                            _i += 4;
                            break;
                        default: throw Error("bad escape '\\" + e + "'");
                    }
                }
            }
        }
    }
}
