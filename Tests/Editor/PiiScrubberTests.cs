using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class PiiScrubberTests
    {
        [Test]
        public void Scrubs_email_addresses()
        {
            Assert.AreEqual(
                "contact [EMAIL FILTERED] for help",
                PiiScrubber.ScrubString("contact user@example.com for help"));
        }

        [Test]
        public void Scrubs_ssns()
        {
            Assert.AreEqual("ssn: [SSN FILTERED]", PiiScrubber.ScrubString("ssn: 123-45-6789"));
        }

        [Test]
        public void Scrubs_credit_card_numbers()
        {
            Assert.AreEqual(
                "card [CREDIT CARD FILTERED]",
                PiiScrubber.ScrubString("card 4111-1111-1111-1111"));
        }

        [Test]
        public void Scrubs_bearer_tokens()
        {
            Assert.AreEqual(
                "Authorization: [BEARER TOKEN FILTERED]",
                PiiScrubber.ScrubString("Authorization: Bearer abc123.def456"));
        }

        [Test]
        public void Scrubs_jwts()
        {
            var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
            Assert.AreEqual("token: [JWT FILTERED]", PiiScrubber.ScrubString($"token: {jwt}"));
        }

        [Test]
        public void Scrubs_aws_keys()
        {
            Assert.AreEqual(
                "key: [AWS KEY FILTERED]",
                PiiScrubber.ScrubString("key: AKIAIOSFODNN7EXAMPLE"));
        }

        [Test]
        public void Scrubs_stripe_keys()
        {
            Assert.AreEqual(
                "[STRIPE KEY FILTERED]",
                PiiScrubber.ScrubString("sk_live_" + "4eC39HqLyjWDarjtT1zdp7dc"));
        }

        [Test]
        public void Scrubs_github_tokens()
        {
            Assert.AreEqual(
                "[GITHUB TOKEN FILTERED]",
                PiiScrubber.ScrubString("ghp_" + "16C7e42F292c6912E7710c838347Ae178B4a"));
        }

        [Test]
        public void Scrub_redacts_values_under_a_sensitive_key_in_a_dictionary()
        {
            var input = new Dictionary<string, object> { ["password"] = "hunter2" };
            var scrubbed = (Dictionary<string, object>)PiiScrubber.Scrub(input);
            Assert.AreEqual(PiiScrubber.Redacted, scrubbed["password"]);
        }

        [Test]
        public void Scrub_recurses_into_nested_dictionaries()
        {
            var input = new Dictionary<string, object>
            {
                ["user"] = new Dictionary<string, object>
                {
                    ["email"] = "ada@example.com",
                    ["api_key"] = "supersecret"
                }
            };

            var scrubbed = (Dictionary<string, object>)PiiScrubber.Scrub(input);
            var user = (Dictionary<string, object>)scrubbed["user"];

            Assert.AreEqual("[EMAIL FILTERED]", user["email"]);
            Assert.AreEqual(PiiScrubber.Redacted, user["api_key"]);
        }

        [Test]
        public void Scrub_recurses_into_lists()
        {
            var input = new List<object> { "contact user@example.com" };
            var scrubbed = (List<object>)PiiScrubber.Scrub(input);
            Assert.AreEqual("contact [EMAIL FILTERED]", scrubbed[0]);
        }

        [Test]
        public void Scrub_leaves_non_sensitive_values_untouched()
        {
            var input = new Dictionary<string, object> { ["order_id"] = 42 };
            var scrubbed = (Dictionary<string, object>)PiiScrubber.Scrub(input);
            Assert.AreEqual(42, scrubbed["order_id"]);
        }

        // Fixtures/pii_scrubbing_vectors.json, a byte-identical copy of this repo's
        // sdks/pii_scrubbing_vectors.json, generated from the server's own PiiScrubber (see
        // sdks/PII_SCRUBBING.md). Found next to this source file, as StrictPrivacyTests finds its own.
        private static string VectorsPath([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here), "Fixtures", "pii_scrubbing_vectors.json");

        // The objects in one of the vectors file's sections, as key/value string pairs, a bare true or
        // false kept as its text. A tiny purpose-built reader like StrictPrivacyTests' own, not a JSON
        // parser: the file holds only arrays of flat objects of strings and booleans, whose only
        // escapes are \t, \n, \" and \\.
        private static List<Dictionary<string, string>> Section(string name)
        {
            var text = File.ReadAllText(VectorsPath(), Encoding.UTF8);
            var start = text.IndexOf($"\"{name}\": [", StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, name);

            var entries = new List<Dictionary<string, string>>();
            var entry = new Dictionary<string, string>();
            string key = null;
            for (var i = start + name.Length + 4; i < text.Length; i++)
            {
                var c = text[i];
                if (c == ']') break;
                if (c == '}')
                {
                    entries.Add(entry);
                    entry = new Dictionary<string, string>();
                }
                if (key != null && (c == 't' || c == 'f'))
                {
                    var literal = c == 't' ? "true" : "false";
                    entry[key] = literal;
                    key = null;
                    i += literal.Length - 1;
                    continue;
                }
                if (c != '"') continue;

                var value = new StringBuilder();
                for (i++; text[i] != '"'; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                        value.Append(text[i] == 't' ? '\t' : text[i] == 'n' ? '\n' : text[i]);
                    }
                    else
                    {
                        value.Append(text[i]);
                    }
                }
                if (key == null)
                {
                    key = value.ToString();
                }
                else
                {
                    entry[key] = value.ToString();
                    key = null;
                }
            }
            Assert.IsNotEmpty(entries, name);
            return entries;
        }

        [Test]
        public void Scrubs_card_numbers_and_ibans_in_every_vector_exactly_as_the_server_does()
        {
            foreach (var vector in Section("strings"))
            {
                Assert.AreEqual(vector["scrubbed"], PiiScrubber.ScrubString(vector["input"]), vector["input"]);
            }
        }

        [Test]
        public void Redacts_bank_detail_keys_in_every_vector_exactly_as_the_server_does()
        {
            foreach (var vector in Section("keys"))
            {
                var key = vector["key"];
                var scrubbed = (Dictionary<string, object>)PiiScrubber.Scrub(new Dictionary<string, object> { [key] = "x" });
                Assert.AreEqual(vector["redacted"] == "true" ? PiiScrubber.Redacted : "x", scrubbed[key], key);
            }
        }
    }
}
