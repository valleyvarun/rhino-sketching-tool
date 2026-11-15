using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RhinoSketchingTool
{
    /// <summary>
    /// Just enough JSON to talk to Claude Code: text to and from plain objects. An object
    /// is a Dictionary of string to object, an array a List of object, and the rest are
    /// string, double, bool and null. (Written here so the plug-in needs no JSON library,
    /// whichever .NET Rhino is running on.)
    /// </summary>
    internal static class Json
    {
        static readonly Dictionary<string, object> NoMap = new Dictionary<string, object>();
        static readonly List<object> NoList = new List<object>();

        /// <summary>The value as an object, or an empty one if it is anything else.</summary>
        public static Dictionary<string, object> Map(object value) => value as Dictionary<string, object> ?? NoMap;

        public static List<object> List(object value) => value as List<object> ?? NoList;

        public static string Text(object value) => value as string ?? "";

        /// <summary>A member of an object, or null if it has none by that name.</summary>
        public static object At(this Dictionary<string, object> map, string key) =>
            map != null && map.TryGetValue(key, out object value) ? value : null;

        // ------------------------------------------------------------ reading

        /// <summary>The value the text holds, or null if it is not JSON.</summary>
        public static object Parse(string text)
        {
            try
            {
                int at = 0;
                object value = Value(text, ref at);
                Blank(text, ref at);
                return at == text.Length ? value : null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null; // the text ended in the middle of something
            }
        }

        static void Blank(string text, ref int at)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        }

        static object Value(string text, ref int at)
        {
            Blank(text, ref at);
            char first = text[at];
            if (first == '{')
            {
                var map = new Dictionary<string, object>();
                at++;
                Blank(text, ref at);
                if (text[at] == '}') { at++; return map; }
                while (true)
                {
                    Blank(text, ref at);
                    string key = String(text, ref at);
                    Blank(text, ref at);
                    if (text[at++] != ':') throw new FormatException();
                    map[key] = Value(text, ref at);
                    Blank(text, ref at);
                    char next = text[at++];
                    if (next == '}') return map;
                    if (next != ',') throw new FormatException();
                }
            }
            if (first == '[')
            {
                var list = new List<object>();
                at++;
                Blank(text, ref at);
                if (text[at] == ']') { at++; return list; }
                while (true)
                {
                    list.Add(Value(text, ref at));
                    Blank(text, ref at);
                    char next = text[at++];
                    if (next == ']') return list;
                    if (next != ',') throw new FormatException();
                }
            }
            if (first == '"') return String(text, ref at);
            if (Word(text, ref at, "true")) return true;
            if (Word(text, ref at, "false")) return false;
            if (Word(text, ref at, "null")) return null;

            int start = at;
            while (at < text.Length && "+-.eE0123456789".IndexOf(text[at]) >= 0) at++;
            if (at == start) throw new FormatException();
            return double.Parse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Word(string text, ref int at, string word)
        {
            if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0) return false;
            at += word.Length;
            return true;
        }

        static string String(string text, ref int at)
        {
            if (text[at++] != '"') throw new FormatException();
            var result = new StringBuilder();
            while (true)
            {
                char c = text[at++];
                if (c == '"') return result.ToString();
                if (c != '\\')
                {
                    result.Append(c);
                    continue;
                }
                c = text[at++];
                switch (c)
                {
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        result.Append((char)int.Parse(text.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        at += 4;
                        break;
                    default: result.Append(c); break; // " \ /
                }
            }
        }

        // ------------------------------------------------------------ writing

        public static string Write(object value)
        {
            var text = new StringBuilder();
            Write(text, value);
            return text.ToString();
        }

        static void Write(StringBuilder text, object value)
        {
            switch (value)
            {
                case null:
                    text.Append("null");
                    break;
                case string s:
                    Write(text, s);
                    break;
                case bool b:
                    text.Append(b ? "true" : "false");
                    break;
                case IDictionary<string, object> map:
                {
                    text.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, object> member in map)
                    {
                        if (!first) text.Append(',');
                        first = false;
                        Write(text, member.Key);
                        text.Append(':');
                        Write(text, member.Value);
                    }
                    text.Append('}');
                    break;
                }
                case IEnumerable<object> list:
                {
                    text.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) text.Append(',');
                        first = false;
                        Write(text, item);
                    }
                    text.Append(']');
                    break;
                }
                case IFormattable number:
                    text.Append(number.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    Write(text, value.ToString());
                    break;
            }
        }

        static void Write(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4"));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
    }
}
