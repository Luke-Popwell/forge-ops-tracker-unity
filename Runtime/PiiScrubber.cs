using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Redacts likely-sensitive content out of a payload before it ever leaves this process:
    /// the same patterns ForgeOps itself applies again on arrival (defense in depth: this layer
    /// keeps the data off the wire and out of any request logging in between; the server-side
    /// layer is what actually protects the database, and doesn't depend on every reporting build
    /// running an up-to-date version of this client). Ported from
    /// gems/forge_ops_tracker/lib/forge_ops_tracker/pii_scrubber.rb and sdks/dotnet's own
    /// PiiScrubber: the regex patterns are unmodified from the Ruby original; .NET's regex
    /// engine (used identically under both Mono and IL2CPP's regex backport) is PCRE-compatible
    /// enough that nothing needed adapting, unlike sdks/c's POSIX ERE substitution.
    ///
    /// Can be turned off via <see cref="Configuration.ScrubPii"/> for a game that already scrubs
    /// its own data before it ever reaches an exception's context, or that has its own reasons to
    /// want the raw payload. Off by default is not an option: the safe default has to be "on."
    /// </summary>
    public static class PiiScrubber
    {
        public const string Redacted = "[FILTERED]";

        private static readonly HashSet<string> SensitiveKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "password", "passwd", "pwd",
            "secret", "apisecret", "clientsecret", "secretkey",
            "token", "accesstoken", "refreshtoken", "apikey", "apitoken", "authorization", "authtoken", "bearer", "sessiontoken", "csrftoken",
            "creditcard", "cardnumber", "cardnum", "cvv", "cvv2", "cvc",
            "ssn", "socialsecuritynumber", "socialsecurity",
            "privatekey"
        };

        private static readonly (string Label, Regex Pattern)[] Patterns =
        {
            ("EMAIL", new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)),
            ("SSN", new Regex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)),
            ("CREDIT CARD", new Regex(@"\b\d{4}[ -]\d{4}[ -]\d{4}[ -]\d{1,4}\b", RegexOptions.Compiled)),
            ("BEARER TOKEN", new Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
            ("JWT", new Regex(@"\bey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
            ("AWS KEY", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
            ("STRIPE KEY", new Regex(@"\b[sr]k_(?:live|test)_[A-Za-z0-9]{10,}\b", RegexOptions.Compiled)),
            ("GITHUB TOKEN", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled))
        };

        /// <summary>
        /// Recursively scrubs a loosely-typed value graph: used for the freeform Context/Tags
        /// fields, whose shape callers control and can't be modeled as fixed C# properties the
        /// way the rest of an event can.
        /// </summary>
        public static object Scrub(object value, string key = null)
        {
            if (IsSensitiveKey(key) && value != null)
            {
                return Redacted;
            }

            switch (value)
            {
                case IDictionary<string, object> dict:
                    return dict.ToDictionary(kv => kv.Key, kv => Scrub(kv.Value, kv.Key));
                case string s:
                    return ScrubString(s);
                case IEnumerable list when !(value is string):
                    return list.Cast<object>().Select(v => Scrub(v, key)).ToList();
                default:
                    return value;
            }
        }

        public static string ScrubString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return Patterns.Aggregate(input, (scrubbed, entry) => entry.Pattern.Replace(scrubbed, $"[{entry.Label} FILTERED]"));
        }

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            var normalized = new string(key.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return SensitiveKeys.Any(normalized.Contains);
        }
    }
}
