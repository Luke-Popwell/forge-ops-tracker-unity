using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// A small hand-rolled JSON writer, used only to encode this client's own event payload.
    /// Unity's built-in <c>JsonUtility</c> cannot do this job: it only serializes the public
    /// fields of a plain class/struct matching a fixed set of supported types, with no support at
    /// all for <see cref="Dictionary{TKey,TValue}"/> or loosely-typed <c>object</c> values:
    /// exactly the shape this client's freeform Context/Tags fields need (documented directly in
    /// Unity's own JsonUtility manual page, not a guess). Pulling in a third-party JSON package
    /// (Newtonsoft's, commonly added via Unity's own package registry) is the usual real-world
    /// answer to that gap, but would be this SDK's only dependency for a problem a ~60-line writer
    /// solves outright: the same "add a dependency only when the platform truly lacks the
    /// capability" philosophy every client in this repo follows (see e.g. sdks/c's own hand-rolled
    /// JSON writer, needed for the same underlying reason: no JSON support in the platform at
    /// all).
    ///
    /// Only handles what this client ever needs to write: null, bool, numbers, strings,
    /// <see cref="IDictionary"/> (string-keyed), and <see cref="IEnumerable"/>. Not a general
    /// purpose serializer: deliberately doesn't need to be one.
    /// </summary>
    internal static class Json
    {
        public static string Encode(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case bool b:
                    builder.Append(b ? "true" : "false");
                    break;
                case string s:
                    WriteString(builder, s);
                    break;
                case float f:
                    builder.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    builder.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                    WriteDictionary(builder, dict);
                    break;
                case IEnumerable list:
                    WriteArray(builder, list);
                    break;
                default:
                    // int, long, and every other numeric type: IFormattable's
                    // InvariantCulture.ToString is a valid JSON number for all of them.
                    builder.Append(System.Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void WriteDictionary(StringBuilder builder, IDictionary dict)
        {
            builder.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) builder.Append(',');
                first = false;
                WriteString(builder, entry.Key.ToString());
                builder.Append(':');
                Write(builder, entry.Value);
            }
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable list)
        {
            builder.Append('[');
            var first = true;
            foreach (var item in list)
            {
                if (!first) builder.Append(',');
                first = false;
                Write(builder, item);
            }
            builder.Append(']');
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
