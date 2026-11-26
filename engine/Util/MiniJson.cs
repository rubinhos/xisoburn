using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace xISOBurn.Util
{
    /// <summary>
    /// A small JSON reader for the engine (it must build for UWP / .NET Standard 2.0, which have no
    /// System.Text.Json). Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double, plus string, bool and null. Enough for the MusicBrainz web service.
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            int pos = 0;
            object v = Value(text, ref pos);
            Skip(text, ref pos);
            if (pos != text.Length) throw new FormatException("trailing data at " + pos);
            return v;
        }

        // ------------------------------------------------------------------ access helpers

        public static Dictionary<string, object> Obj(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static string Str(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            if (v is string) return (string)v;
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            return null;
        }

        // ------------------------------------------------------------------ parser

        private static void Skip(string s, ref int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++;
        }

        private static object Value(string s, ref int p)
        {
            Skip(s, ref p);
            if (p >= s.Length) throw new FormatException("unexpected end");
            char c = s[p];
            if (c == '{') return ObjectValue(s, ref p);
            if (c == '[') return ArrayValue(s, ref p);
            if (c == '"') return StringValue(s, ref p);
            if (Match(s, ref p, "true")) return true;
            if (Match(s, ref p, "false")) return false;
            if (Match(s, ref p, "null")) return null;
            return NumberValue(s, ref p);
        }

        private static bool Match(string s, ref int p, string word)
        {
            if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0) return false;
            p += word.Length;
            return true;
        }

        private static Dictionary<string, object> ObjectValue(string s, ref int p)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            p++; // {
            Skip(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return d; }
            while (true)
            {
                Skip(s, ref p);
                if (p >= s.Length || s[p] != '"') throw new FormatException("key expected at " + p);
                string key = StringValue(s, ref p);
                Skip(s, ref p);
                if (p >= s.Length || s[p] != ':') throw new FormatException("':' expected at " + p);
                p++;
                d[key] = Value(s, ref p);
                Skip(s, ref p);
                if (p >= s.Length) throw new FormatException("unexpected end");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return d; }
                throw new FormatException("',' or '}' expected at " + p);
            }
        }

        private static List<object> ArrayValue(string s, ref int p)
        {
            var list = new List<object>();
            p++; // [
            Skip(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return list; }
            while (true)
            {
                list.Add(Value(s, ref p));
                Skip(s, ref p);
                if (p >= s.Length) throw new FormatException("unexpected end");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return list; }
                throw new FormatException("',' or ']' expected at " + p);
            }
        }

        private static string StringValue(string s, ref int p)
        {
            var sb = new StringBuilder();
            p++; // opening quote
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) break;
                char e = s[p++];
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
                        if (p + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double NumberValue(string s, ref int p)
        {
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            if (p == start) throw new FormatException("value expected at " + p);
            return double.Parse(s.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
