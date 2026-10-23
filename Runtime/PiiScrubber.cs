using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
            "iban", "accountnumber", "routingnumber", "bankaccount",
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

        // Card numbers in any other shape, and IBANs: see sdks/PII_SCRUBBING.md, which every SDK
        // follows, and Tests/Editor/Fixtures/pii_scrubbing_vectors.json, which pins the results.
        // Word characters and digits are ASCII only, so they are spelled out as [A-Za-z0-9_] and
        // [0-9] rather than left to .NET's Unicode \b, \w and \d.
        private static readonly Regex DigitRun =
            new Regex(@"(?<![A-Za-z0-9_])[0-9](?:[ -]?[0-9])*(?![A-Za-z0-9_])", RegexOptions.Compiled);
        private static readonly Regex CardPrefixes =
            new Regex(@"^(?:4|5[1-5]|2(?:22[1-9]|2[3-9][0-9]|[3-6][0-9][0-9]|7[01][0-9]|720)|3[47]|6011|64[4-9]|65|35(?:2[89]|[3-8][0-9])|36|30[0-5]|62)", RegexOptions.Compiled);
        private static readonly Regex Iban =
            new Regex(@"(?<![A-Za-z0-9_])[A-Z]{2}[0-9]{2}(?:[A-Z0-9]{11,30}|(?: [A-Z0-9]{4}){2,7}(?: [A-Z0-9]{1,4})?)(?![A-Za-z0-9_])", RegexOptions.Compiled);

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
            // IBANs first: a spaced IBAN's middle groups look like a formatted card number.
            var result = Iban.Replace(input, match => IsIban(match.Value) ? "[IBAN FILTERED]" : match.Value);
            result = Patterns.Aggregate(result, (scrubbed, entry) => entry.Pattern.Replace(scrubbed, $"[{entry.Label} FILTERED]"));
            return DigitRun.Replace(result, match => ScrubCardNumbersIn(match.Value));
        }

        /// <summary>
        /// From each group of the run on, the longest stretch of whole groups that is a card
        /// number is replaced, so a card followed by its CVV, two cards in a row, and Amex's 4-6-5
        /// are all found.
        /// </summary>
        private static string ScrubCardNumbersIn(string run)
        {
            var digits = new List<string>();
            var separators = new List<string>();
            var start = 0;
            for (var at = 0; at < run.Length; at++)
            {
                if (run[at] == ' ' || run[at] == '-')
                {
                    digits.Add(run.Substring(start, at - start));
                    separators.Add(run[at].ToString());
                    start = at + 1;
                }
            }
            digits.Add(run.Substring(start));
            separators.Add("");

            var output = new StringBuilder();
            var i = 0;
            while (i < digits.Count)
            {
                var j = -1;
                for (var last = digits.Count - 1; last >= i; last--)
                {
                    if (IsCardNumber(string.Concat(digits.GetRange(i, last - i + 1))))
                    {
                        j = last;
                        break;
                    }
                }

                if (j >= 0)
                {
                    output.Append("[CREDIT CARD FILTERED]").Append(separators[j]);
                    i = j + 1;
                }
                else
                {
                    output.Append(digits[i]).Append(separators[i]);
                    i++;
                }
            }
            return output.ToString();
        }

        private static bool IsCardNumber(string digits) =>
            digits.Length >= 13 && digits.Length <= 19 && CardPrefixes.IsMatch(digits) && IsLuhn(digits);

        private static bool IsLuhn(string digits)
        {
            var sum = 0;
            for (var index = 0; index < digits.Length; index++)
            {
                var value = digits[digits.Length - 1 - index] - '0';
                if (index % 2 == 1)
                {
                    value *= 2;
                    if (value > 9) value -= 9;
                }
                sum += value;
            }
            return sum % 10 == 0;
        }

        /// <summary>
        /// The IBAN check, a digit at a time: the first four characters moved to the end, letters
        /// as two digits (A is 10), and the remainder by 97 is 1.
        /// </summary>
        private static bool IsIban(string candidate)
        {
            var compact = candidate.Replace(" ", "");
            if (compact.Length < 15 || compact.Length > 34) return false;

            var rearranged = compact.Substring(4) + compact.Substring(0, 4);
            var remainder = 0;
            foreach (var c in rearranged)
            {
                var number = c >= 'A' && c <= 'Z' ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString();
                foreach (var digit in number)
                {
                    remainder = (remainder * 10 + (digit - '0')) % 97;
                }
            }
            return remainder == 1;
        }

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            var normalized = new string(key.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return SensitiveKeys.Any(normalized.Contains);
        }
    }
}
