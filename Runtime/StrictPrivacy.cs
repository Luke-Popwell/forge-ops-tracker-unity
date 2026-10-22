using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Strict privacy (<see cref="Configuration.StrictPrivacy"/>) is for games and apps that can't let
    /// personal or health data leave the device: an event carries only structured facts (the error's
    /// type, its stack frames, release, environment, device), never free text a person could have
    /// typed. The rules, and the vectors every client's tests check against, are in this repo's
    /// sdks/STRICT_PRIVACY.md; the server applies the same rules again on arrival
    /// (app/services/strict_privacy.rb) when the project's own Strict privacy setting is on.
    /// </summary>
    public static class StrictPrivacy
    {
        private const int FingerprintLength = 32;

        // Explicit ASCII classes, so a word character is [A-Za-z0-9_] only, never a letter or digit in
        // another script, whatever .NET's own \w or \d would accept.
        private static readonly Regex Email = new Regex(
            @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // A quoted value (KeyError: 'Jane Doe') is data, not the shape of the error. Single quotes only open
        // and close at a word boundary, so a contraction (Couldn't) or the `email' style never starts one.
        private static readonly Regex DoubleQuoted = new Regex("\"[^\"\r\n]*\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SingleQuoted = new Regex(
            "(?<![A-Za-z0-9_])'[^'\r\n]*'(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HexObjectId = new Regex("0x[0-9a-fA-F]{4,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Uuid = new Regex(
            "(?<![A-Za-z0-9_])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![A-Za-z0-9_])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex("(?<![A-Za-z0-9_])[0-9]+(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HttpMethod = new Regex(
            @"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)[ \t\r\n\v\f]+([^ \t\r\n\v\f]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex Scheme = new Regex("^[a-zA-Z][a-zA-Z0-9+.-]*://", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Port = new Regex(":[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Span data that's structure, not content: the masked statement and which database it ran on.
        private static readonly string[] SpanDataKeys = { "db.statement", "db.system" };

        /// <summary>
        /// The message with emails, quoted values, hex object ids, UUIDs and standalone numbers replaced,
        /// in that order: an email address becomes "&lt;email&gt;", a double-quoted run (or a
        /// single-quoted one no ASCII word character touches) becomes "&lt;str&gt;", "0x" plus 4 or
        /// more hex digits becomes "0x", a UUID becomes "&lt;uuid&gt;" and a run of digits becomes
        /// "&lt;n&gt;", the last two only where no ASCII word character touches them.
        /// </summary>
        public static string NormalizeMessage(string message)
        {
            var normalized = Email.Replace(message ?? string.Empty, "<email>");
            normalized = DoubleQuoted.Replace(normalized, "<str>");
            normalized = SingleQuoted.Replace(normalized, "<str>");
            normalized = HexObjectId.Replace(normalized, "0x");
            normalized = Uuid.Replace(normalized, "<uuid>");
            return Number.Replace(normalized, "<n>");
        }

        /// <summary>
        /// The first 32 hex characters of the SHA-256 of <see cref="NormalizeMessage"/>'s UTF-8 bytes:
        /// lets ForgeOps group errors whose messages differ only in ids, without the message being sent.
        /// </summary>
        public static string MessageFingerprint(string message)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
            {
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(NormalizeMessage(message)));
            }
            var hex = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) hex.Append(b.ToString("x2"));
            return hex.ToString(0, FingerprintLength);
        }

        /// <summary>
        /// A request or page path under strict privacy, with no query or fragment. A route template
        /// ("/levels/:id/scores", "/orders/{id}") is kept as it is: its words are a developer's and its
        /// parameters are already placeholders. A raw path keeps only its first segment (":id" when it
        /// holds an ASCII digit or is longer than 40 characters), then "/:path" if there was more,
        /// since anything after it can name a person: "/patients/jane-doe/12" is "/patients/:path". No
        /// segments at all is "/".
        /// </summary>
        public static string StrictPath(string path)
        {
            path = path ?? string.Empty;
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);
            if (IsRouteTemplate(path)) return path;

            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return "/";

            var first = HasAsciiDigit(segments[0]) || CodePointCount(segments[0]) > 40 ? ":id" : segments[0];
            var leading = path.StartsWith("/", StringComparison.Ordinal) ? "/" : string.Empty;
            return leading + first + (segments.Length > 1 ? "/:path" : string.Empty);
        }

        /// <summary>The earlier name of <see cref="StrictPath"/>, which it now calls.</summary>
        [Obsolete("Use StrictPath.")]
        public static string PathWithoutIds(string path) => StrictPath(path);

        // Whether any segment of the path is a route parameter: ":id", "{id}", "<int:id>", "[slug]" or
        // "*path".
        private static bool IsRouteTemplate(string path)
        {
            foreach (var segment in path.Split('/'))
            {
                if ((segment.Length > 1 && segment[0] == ':') || segment.StartsWith("*", StringComparison.Ordinal)
                    || Wrapped(segment, '{', '}') || Wrapped(segment, '<', '>') || Wrapped(segment, '[', ']'))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Wrapped(string segment, char open, char close) =>
            segment.Length > 2 && segment[0] == open && segment[segment.Length - 1] == close;

        /// <summary>
        /// A span's name under strict privacy: an outbound call keeps its method and host
        /// ("GET https://api.example.com/patients/42?x=1" is "GET api.example.com"), a request named by
        /// its path goes through <see cref="StrictPath"/> ("GET /patients/42/notes" is
        /// "GET /patients/:path"), so does a bare path ("/42/edit" is "/:id/:path"), and any other name
        /// ("OrdersController#create") is kept. The host is read by hand, never with System.Uri, so an
        /// address a parser would reject still loses its path.
        /// </summary>
        public static string SpanName(string name)
        {
            if (name == null) return null;
            if (name.StartsWith("/", StringComparison.Ordinal)) return StrictPath(name);

            var match = HttpMethod.Match(name);
            if (!match.Success) return name;

            var method = match.Groups[1].Value.ToUpperInvariant();
            var target = match.Groups[2].Value;
            var host = target.StartsWith("/", StringComparison.Ordinal) ? null : HostOf(target);
            return $"{method} {host ?? StrictPath(target)}";
        }

        /// <summary>
        /// A transaction name under strict privacy: a route or a controller action is kept, but a raw
        /// path keeps only its first segment, by the same rule as <see cref="SpanName"/>. A blank name is returned as it is.
        /// </summary>
        public static string TransactionName(string name) =>
            string.IsNullOrWhiteSpace(name) ? name : SpanName(name);

        /// <summary>
        /// An error event's payload reduced to what strict privacy sends: "message" is "" and
        /// "message_fingerprint" is added, computed from the message as it would otherwise have been
        /// sent (after PII scrubbing); breadcrumbs, endpoint, SQL statement and source context are
        /// dropped; context and tags keep only <paramref name="allowedFields"/> keys with plain values
        /// (left out when nothing is left); the user keeps only its id. Modifies and returns
        /// <paramref name="payload"/>.
        /// </summary>
        public static Dictionary<string, object> Event(Dictionary<string, object> payload, ICollection<string> allowedFields)
        {
            payload["message_fingerprint"] = MessageFingerprint(payload.TryGetValue("message", out var message) ? message as string : null);
            payload["message"] = string.Empty;
            payload.Remove("breadcrumbs");
            payload.Remove("endpoint");
            payload.Remove("sql_statement");

            foreach (var key in new[] { "context", "tags" })
            {
                var kept = Allowed(payload.TryGetValue(key, out var fields) ? fields as Dictionary<string, object> : null, allowedFields);
                if (kept != null) payload[key] = kept;
                else payload.Remove(key);
            }

            var id = UserId(payload.TryGetValue("user", out var user) ? user as Dictionary<string, object> : null);
            if (id != null) payload["user"] = new Dictionary<string, object> { ["id"] = id };
            else payload.Remove("user");

            if (payload.TryGetValue("backtrace", out var backtrace) && backtrace is List<Dictionary<string, object>> frames)
            {
                var stripped = new List<Dictionary<string, object>>(frames.Count);
                foreach (var frame in frames)
                {
                    var copy = new Dictionary<string, object>(frame);
                    copy.Remove("context_line");
                    copy.Remove("pre_context");
                    copy.Remove("post_context");
                    stripped.Add(copy);
                }
                payload["backtrace"] = stripped;
            }
            return payload;
        }

        /// <summary>
        /// One finished span under strict privacy, as a new dictionary: its name per
        /// <see cref="SpanName"/>, and its data reduced to "db.statement" and "db.system" (left out when
        /// neither is there).
        /// </summary>
        public static Dictionary<string, object> Span(Dictionary<string, object> span)
        {
            var result = new Dictionary<string, object>(span);
            if (span.TryGetValue("name", out var name) && name is string text) result["name"] = SpanName(text);
            result.Remove("data");
            if (span.TryGetValue("data", out var data) && data is Dictionary<string, object> values)
            {
                var kept = new Dictionary<string, object>();
                foreach (var key in SpanDataKeys)
                {
                    if (values.TryGetValue(key, out var value)) kept[key] = value;
                }
                if (kept.Count > 0) result["data"] = kept;
            }
            return result;
        }

        // FORGE_OPS_STRICT_PRIVACY and FORGE_OPS_PRIVACY_ALLOWED_FIELDS, for Configuration's defaults.
        internal static bool EnabledFromEnvironment(string value) =>
            string.Equals((value ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);

        internal static HashSet<string> FieldsFromEnvironment(string value)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in (value ?? string.Empty).Split(','))
            {
                var trimmed = field.Trim();
                if (trimmed.Length > 0) fields.Add(trimmed);
            }
            return fields;
        }

        private static Dictionary<string, object> Allowed(Dictionary<string, object> fields, ICollection<string> allowedFields)
        {
            if (fields == null || allowedFields == null || allowedFields.Count == 0) return null;
            var kept = new Dictionary<string, object>();
            foreach (var pair in fields)
            {
                if (allowedFields.Contains(pair.Key) && IsPlainValue(pair.Value)) kept[pair.Key] = pair.Value;
            }
            return kept.Count > 0 ? kept : null;
        }

        private static bool IsPlainValue(object value) =>
            value is string || value is bool
            || value is int || value is long || value is short || value is byte || value is sbyte
            || value is uint || value is ulong || value is ushort
            || value is float || value is double || value is decimal;

        private static string UserId(Dictionary<string, object> user)
        {
            if (user == null || !user.TryGetValue("id", out var id) || id == null) return null;
            var text = Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        // An outbound call's host: whatever comes before the first "/", "?" or "#", without a scheme,
        // credentials or port. It's a host when the address had a scheme or a port, or it has a dot or
        // is "localhost"; otherwise (a relative path) null.
        private static string HostOf(string target)
        {
            var scheme = Scheme.Match(target);
            var authority = target.Substring(scheme.Success ? scheme.Length : 0);
            var end = authority.IndexOfAny(new[] { '/', '?', '#' });
            if (end >= 0) authority = authority.Substring(0, end);
            var at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);

            var port = Port.Match(authority);
            var host = port.Success ? authority.Substring(0, port.Index) : authority;
            if (host.Length == 0) return null;
            var isHost = scheme.Success || port.Success || host.IndexOf('.') >= 0
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
            return isHost ? host : null;
        }

        private static bool HasAsciiDigit(string text)
        {
            foreach (var c in text)
            {
                if (c >= '0' && c <= '9') return true;
            }
            return false;
        }

        // Characters as the server counts them (code points), not UTF-16 units.
        private static int CodePointCount(string text)
        {
            var count = 0;
            foreach (var c in text)
            {
                if (!char.IsLowSurrogate(c)) count++;
            }
            return count;
        }
    }
}
