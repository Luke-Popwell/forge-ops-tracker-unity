using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class StrictPrivacyTests
    {
        // Fixtures/strict_privacy_vectors.json, a byte-identical copy of this repo's
        // sdks/strict_privacy_vectors.json, generated from the server's own implementation. Found next
        // to this source file, which works the same in the Editor and in a plain .NET test run.
        private static string VectorsPath([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here), "Fixtures", "strict_privacy_vectors.json");

        // The objects in one of the vectors file's sections, as key/value string pairs. A tiny
        // purpose-built reader, not a JSON parser (this package has none): the file holds only arrays
        // of flat objects of strings, whose only escapes are \t, \n, \" and \\.
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

        private static Configuration StrictConfiguration(Action<Configuration> configure = null)
        {
            var config = new Configuration
            {
                EnvironmentName = "production",
                Release = "1.2.3",
                ServerName = "device-abc",
                CaptureSqlStatement = true,
                StrictPrivacy = true,
                PrivacyAllowedFields = new HashSet<string> { "error_code", "feature", "nested", "platform" }
            };
            config.AppNamespacePrefixes.Add("MyGame");
            configure?.Invoke(config);
            return config;
        }

        private static Exception RaiseAndCatch()
        {
            try
            {
                var e = new InvalidOperationException("Couldn't find Invoice with id=8821 for jane@example.com");
                e.Data["forge_ops_sql"] = "SELECT * FROM invoices WHERE id = 8821";
                throw e;
            }
            catch (Exception e)
            {
                return e;
            }
        }

        private static Dictionary<string, object> Build(Configuration config)
        {
            var context = new Dictionary<string, object>
            {
                ["error_code"] = "E42",
                ["feature"] = 7,
                ["nested"] = new Dictionary<string, object> { ["a"] = 1 },
                ["patient"] = "Jane Doe"
            };
            var user = new Dictionary<string, object> { ["id"] = 42, ["email"] = "jane@example.com", ["username"] = "jane" };
            var crumbs = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["category"] = "custom", ["message"] = "opened Jane's chart", ["level"] = "info", ["timestamp"] = "t", ["data"] = new Dictionary<string, object>() }
            };
            return EventBuilder.BuildFromException(config, RaiseAndCatch(), context, user, crumbs, "4bf92f3577b34da6a3ce929d0e0e4736");
        }

        [Test]
        public void EveryFingerprintVectorNormalizesAndHashesExactly()
        {
            foreach (var vector in Section("fingerprint"))
            {
                Assert.AreEqual(vector["normalized"], StrictPrivacy.NormalizeMessage(vector["message"]), vector["message"]);
                Assert.AreEqual(vector["fingerprint"], StrictPrivacy.MessageFingerprint(vector["message"]), vector["message"]);
            }
        }

        [Test]
        public void EverySpanNameVector()
        {
            foreach (var vector in Section("span_name"))
            {
                Assert.AreEqual(vector["strict"], StrictPrivacy.SpanName(vector["name"]), vector["name"]);
            }
        }

        [Test]
        public void EveryPathVector()
        {
            foreach (var vector in Section("path"))
            {
                Assert.AreEqual(vector["strict"], StrictPrivacy.PathWithoutIds(vector["path"]), vector["path"]);
            }
        }

        [Test]
        public void TransactionNamesLoseRawPathIdsAndKeepRoutes()
        {
            var cases = new Dictionary<string, string>
            {
                ["GET /patients/42/notes"] = "GET /patients/:id/notes",
                ["GET /levels/{id}"] = "GET /levels/{id}",
                ["OrdersController#create"] = "OrdersController#create",
                ["/patients/jane-doe-1961"] = "/patients/:id",
                ["MyGame.LoadLevel"] = "MyGame.LoadLevel",
                [""] = ""
            };
            foreach (var pair in cases)
            {
                Assert.AreEqual(pair.Value, StrictPrivacy.TransactionName(pair.Key), pair.Key);
            }
            Assert.IsNull(StrictPrivacy.TransactionName(null));
        }

        [Test]
        public void PerformanceSamplesReduceARawPathTransactionName()
        {
            foreach (var strict in new[] { true, false })
            {
                var config = StrictConfiguration(c =>
                {
                    c.StrictPrivacy = strict;
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                });
                var flusher = new PerformanceFlusher(config, () => null);
                flusher.Record("GET /patients/42/notes", 10);
                flusher.Record("GET /levels/{id}", 10);

                Assert.AreEqual(strict, flusher.Tally("GET /patients/:id/notes").HasValue, $"strict={strict}");
                Assert.AreEqual(!strict, flusher.Tally("GET /patients/42/notes").HasValue, $"strict={strict}");
                Assert.IsTrue(flusher.Tally("GET /levels/{id}").HasValue);
            }
        }

        [Test]
        public void TheOptionsComeFromTheEnvironmentAndDefaultToOff()
        {
            Assert.IsTrue(StrictPrivacy.EnabledFromEnvironment(" TRUE "));
            Assert.IsFalse(StrictPrivacy.EnabledFromEnvironment(null));
            CollectionAssert.AreEquivalent(new[] { "error_code", "feature" }, StrictPrivacy.FieldsFromEnvironment("error_code, feature,,"));
            CollectionAssert.IsEmpty(StrictPrivacy.FieldsFromEnvironment(null));

            if (Environment.GetEnvironmentVariable("FORGE_OPS_STRICT_PRIVACY") == null)
            {
                Assert.IsFalse(new Configuration().StrictPrivacy);
                CollectionAssert.IsEmpty(new Configuration().PrivacyAllowedFields);
            }
        }

        [Test]
        public void AStrictEventCarriesNoFreeText()
        {
            var payload = Build(StrictConfiguration());

            Assert.AreEqual(string.Empty, payload["message"]);
            // Of the message as it would otherwise have been sent: after PII scrubbing.
            Assert.AreEqual(StrictPrivacy.MessageFingerprint(PiiScrubber.ScrubString(RaiseAndCatch().Message)), payload["message_fingerprint"]);
            StringAssert.IsMatch("^[0-9a-f]{32}$", (string)payload["message_fingerprint"]);
            foreach (var key in new[] { "breadcrumbs", "endpoint", "sql_statement" })
            {
                Assert.IsFalse(payload.ContainsKey(key), key);
            }
            CollectionAssert.AreEquivalent(new Dictionary<string, object> { ["error_code"] = "E42", ["feature"] = 7 }, (Dictionary<string, object>)payload["context"]);
            var tags = (Dictionary<string, object>)payload["tags"];
            CollectionAssert.AreEquivalent(new[] { "platform" }, tags.Keys);
            CollectionAssert.AreEquivalent(new Dictionary<string, object> { ["id"] = "42" }, (Dictionary<string, object>)payload["user"]);
            Assert.IsTrue(payload.ContainsKey("sql_objects"));
            Assert.AreEqual("4bf92f3577b34da6a3ce929d0e0e4736", payload["trace_id"]);
            StringAssert.Contains("InvalidOperationException", (string)payload["exception_class"]);

            var json = Json.Encode(payload);
            foreach (var text in new[] { "Jane", "jane", "8821", "context_line", "pre_context", "post_context" })
            {
                StringAssert.DoesNotContain(text, json);
            }
        }

        [Test]
        public void ContextTagsAndUserAreLeftOutWhenNothingIsLeft()
        {
            var payload = Build(StrictConfiguration(c => c.PrivacyAllowedFields = new HashSet<string>()));
            Assert.IsFalse(payload.ContainsKey("context"));
            Assert.IsFalse(payload.ContainsKey("tags"));

            var noId = StrictPrivacy.Event(new Dictionary<string, object> { ["message"] = "boom", ["user"] = new Dictionary<string, object> { ["email"] = "jane@example.com" } }, null);
            Assert.IsFalse(noId.ContainsKey("user"));
            Assert.AreEqual("81f52337ebb4cb1669bb802c708807dd", noId["message_fingerprint"]);
        }

        private static string WriteTempSourceFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"forgeops_strict_{Guid.NewGuid():N}.cs");
            File.WriteAllLines(path, new[] { "line 1", "var patient = \"Jane\";", "line 3" });
            return path;
        }

        [Test]
        public void SourceContextIsNeverReadUnderStrictPrivacy()
        {
            var path = WriteTempSourceFile();
            try
            {
                var stackTrace = $"MyGame.Foo.Bar (System.Int32 amount) (at {path}:2)";
                var strict = EventBuilder.BuildFromLogMessage(StrictConfiguration(), "System.Exception: boom", stackTrace);
                var frame = ((List<Dictionary<string, object>>)strict["backtrace"])[0];
                Assert.IsTrue((bool)frame["in_app"]);
                Assert.IsFalse(frame.ContainsKey("context_line"));
                Assert.IsFalse(frame.ContainsKey("pre_context"));
                Assert.IsFalse(frame.ContainsKey("post_context"));

                var off = EventBuilder.BuildFromLogMessage(StrictConfiguration(c => c.StrictPrivacy = false), "System.Exception: boom", stackTrace);
                Assert.AreEqual("var patient = \"Jane\";", ((List<Dictionary<string, object>>)off["backtrace"])[0]["context_line"]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void StrictPrivacyOffLeavesEventsAsBefore()
        {
            var payload = Build(StrictConfiguration(c => c.StrictPrivacy = false));

            Assert.AreEqual(PiiScrubber.ScrubString(RaiseAndCatch().Message), payload["message"]);
            Assert.IsFalse(payload.ContainsKey("message_fingerprint"));
            Assert.AreEqual("SELECT * FROM invoices WHERE id = ?", payload["sql_statement"]);
            Assert.AreEqual(1, ((List<Dictionary<string, object>>)payload["breadcrumbs"]).Count);
            Assert.AreEqual(4, ((Dictionary<string, object>)payload["context"]).Count);
            CollectionAssert.AreEquivalent(new[] { "platform", "unity_version" }, ((Dictionary<string, object>)payload["tags"]).Keys);
            Assert.AreEqual(3, ((Dictionary<string, object>)payload["user"]).Count);
        }

        [Test]
        public void NoBreadcrumbsAreRecordedUnderStrictPrivacy()
        {
            var buffer = new BreadcrumbBuffer(StrictConfiguration());
            buffer.Add("opened Jane's chart", "custom", "info", null);
            Assert.AreEqual(0, buffer.All().Count);

            var off = new BreadcrumbBuffer(StrictConfiguration(c => c.StrictPrivacy = false));
            off.Add("opened the chart", "custom", "info", null);
            Assert.AreEqual(1, off.All().Count);
        }

        private static Dictionary<string, Dictionary<string, object>> RecordSpans(Configuration config)
        {
            config.Dsn = "https://key@tracker.example.com/api/v1/events";
            config.TraceCaptureThresholdSeconds = 0;
            var delivered = new List<Dictionary<string, object>>();
            var trace = new Trace("GET /patients/42/notes", config, delivered.Add);

            using (var span = trace.StartHttpSpan("GET", "https://api.example.com/patients/42?x=1"))
            {
                span.Finish(new Dictionary<string, object> { ["status"] = 200, ["url"] = "https://api.example.com/patients/42" });
            }
            trace.RecordSpan("load patient", "database", DateTime.UtcNow, 1, new Dictionary<string, object> { ["rows"] = 1 }, "SELECT * FROM patients WHERE name = 'Jane'", "postgresql");
            trace.Finish();

            Assert.AreEqual(1, delivered.Count);
            var byName = new Dictionary<string, Dictionary<string, object>>();
            foreach (var span in (List<object>)delivered[0]["spans"])
            {
                var values = (Dictionary<string, object>)span;
                byName[(string)values["name"]] = values;
            }
            return byName;
        }

        [Test]
        public void StrictSpansKeepOnlyMethodHostAndStatementData()
        {
            var spans = RecordSpans(StrictConfiguration());

            Assert.IsFalse(spans["GET api.example.com"].ContainsKey("data"));
            Assert.IsFalse(spans["GET /patients/:id/notes"].ContainsKey("data"));
            CollectionAssert.AreEquivalent(
                new Dictionary<string, object> { ["db.statement"] = "SELECT * FROM patients WHERE name = ?", ["db.system"] = "postgresql" },
                (Dictionary<string, object>)spans["load patient"]["data"]);
        }

        [Test]
        public void SpansAreUnchangedWithStrictPrivacyOff()
        {
            var spans = RecordSpans(StrictConfiguration(c => c.StrictPrivacy = false));

            Assert.AreEqual(200, ((Dictionary<string, object>)spans["GET api.example.com"]["data"])["status"]);
            Assert.AreEqual(0, ((Dictionary<string, object>)spans["GET /patients/42/notes"]["data"]).Count);
            Assert.AreEqual(1, ((Dictionary<string, object>)spans["load patient"]["data"])["rows"]);
        }
    }
}
