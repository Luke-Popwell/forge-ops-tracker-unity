using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class EventBuilderTests
    {
        private static Configuration NewConfiguration(Action<Configuration> configure = null)
        {
            var config = new Configuration
            {
                EnvironmentName = "production",
                Release = "1.2.3",
                ServerName = "device-abc"
            };
            configure?.Invoke(config);
            return config;
        }

        private static Exception RaiseAndCatch()
        {
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (Exception e)
            {
                return e;
            }
        }

        [Test]
        public void BuildFromException_sets_exception_class_message_and_metadata()
        {
            var config = NewConfiguration();
            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch(), new Dictionary<string, object> { ["order_id"] = 42 });

            StringAssert.Contains("InvalidOperationException", (string)payload["exception_class"]);
            Assert.AreEqual("boom", payload["message"]);
            Assert.AreEqual("production", payload["environment"]);
            Assert.AreEqual("1.2.3", payload["release"]);
            Assert.AreEqual("device-abc", payload["server_name"]);
            Assert.IsNotNull(payload["occurred_at"]);

            var context = (Dictionary<string, object>)payload["context"];
            Assert.AreEqual(42, context["order_id"]);
            Assert.AreEqual("unity", payload["sdk_name"]);
        }

        [Test]
        public void BuildFromException_parses_at_least_one_backtrace_frame()
        {
            var config = NewConfiguration();
            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch());

            var backtrace = (List<Dictionary<string, object>>)payload["backtrace"];
            Assert.Greater(backtrace.Count, 0);
            StringAssert.Contains("RaiseAndCatch", (string)backtrace[0]["method"]);
        }

        [Test]
        public void BuildFromException_marks_frames_matching_app_prefix_as_in_app()
        {
            var config = NewConfiguration(c => c.AppNamespacePrefixes.Add("ForgeOpsTracker.Unity.Tests"));
            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch());

            var backtrace = (List<Dictionary<string, object>>)payload["backtrace"];
            Assert.IsTrue(backtrace[0]["in_app"] as bool? ?? false);
        }

        [Test]
        public void BuildFromException_marks_no_frame_in_app_when_prefix_is_unset()
        {
            var config = NewConfiguration();
            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch());

            var backtrace = (List<Dictionary<string, object>>)payload["backtrace"];
            foreach (var frame in backtrace)
            {
                Assert.IsFalse(frame["in_app"] as bool? ?? true);
            }
        }

        [Test]
        public void BuildFromLogMessage_splits_condition_into_class_and_message()
        {
            var config = NewConfiguration();
            var payload = EventBuilder.BuildFromLogMessage(
                config,
                "NullReferenceException: Object reference not set to an instance of an object",
                "MyGame.Player.TakeDamage (System.Int32 amount) (at Assets/Scripts/Player.cs:42)");

            Assert.AreEqual("NullReferenceException", payload["exception_class"]);
            Assert.AreEqual("Object reference not set to an instance of an object", payload["message"]);
        }

        [Test]
        public void BuildFromLogMessage_parses_unity_style_stack_trace_lines()
        {
            var config = NewConfiguration();
            var payload = EventBuilder.BuildFromLogMessage(
                config,
                "NullReferenceException: boom",
                "MyGame.Player.TakeDamage (System.Int32 amount) (at Assets/Scripts/Player.cs:42)");

            var backtrace = (List<Dictionary<string, object>>)payload["backtrace"];
            Assert.AreEqual(1, backtrace.Count);
            StringAssert.Contains("TakeDamage", (string)backtrace[0]["method"]);
            Assert.AreEqual("Assets/Scripts/Player.cs", backtrace[0]["file"]);
            Assert.AreEqual(42, backtrace[0]["line"]);
        }

        [Test]
        public void BuildFromLogMessage_handles_a_frame_with_no_file_info()
        {
            var config = NewConfiguration();
            var payload = EventBuilder.BuildFromLogMessage(
                config,
                "System.Exception: boom",
                "MyGame.Player.TakeDamage (System.Int32 amount)");

            var backtrace = (List<Dictionary<string, object>>)payload["backtrace"];
            Assert.AreEqual(1, backtrace.Count);
            Assert.IsNull(backtrace[0]["file"]);
            Assert.IsNull(backtrace[0]["line"]);
        }

        [Test]
        public void BuildFromException_includes_the_user_when_given_one_never_scrubbed_even_though_its_an_email()
        {
            var config = NewConfiguration();
            var user = new Dictionary<string, object> { ["id"] = 42, ["email"] = "ada@example.com" };

            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch(), null, user);

            var payloadUser = (Dictionary<string, object>)payload["user"];
            Assert.AreEqual("ada@example.com", payloadUser["email"]);
        }

        [Test]
        public void BuildFromException_omits_the_user_key_entirely_when_none_was_given()
        {
            var config = NewConfiguration();

            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch());

            Assert.IsFalse(payload.ContainsKey("user"));
        }

        [Test]
        public void BuildFromLogMessage_includes_the_user_when_given_one()
        {
            var config = NewConfiguration();
            var user = new Dictionary<string, object> { ["id"] = 7 };

            var payload = EventBuilder.BuildFromLogMessage(config, "System.Exception: boom", "MyGame.Foo.Bar ()", user);

            var payloadUser = (Dictionary<string, object>)payload["user"];
            Assert.AreEqual(7, payloadUser["id"]);
        }

        [Test]
        public void Scrubs_likely_pii_out_of_message_and_context_by_default()
        {
            var config = NewConfiguration();
            var context = new Dictionary<string, object> { ["email"] = "ada@example.com" };

            var payload = EventBuilder.BuildFromLogMessage(
                config,
                "System.Exception: failed for user@example.com",
                "MyGame.Foo.Bar ()");

            Assert.AreEqual("failed for [EMAIL FILTERED]", payload["message"]);

            var directPayload = EventBuilder.BuildFromException(config, RaiseAndCatch(), context);
            var directContext = (Dictionary<string, object>)directPayload["context"];
            Assert.AreEqual("[EMAIL FILTERED]", directContext["email"]);
        }

        [Test]
        public void Leaves_payload_untouched_when_scrub_pii_is_disabled()
        {
            var config = NewConfiguration(c => c.ScrubPii = false);
            var context = new Dictionary<string, object> { ["email"] = "ada@example.com" };

            var payload = EventBuilder.BuildFromException(config, RaiseAndCatch(), context);
            var resultContext = (Dictionary<string, object>)payload["context"];

            Assert.AreEqual("ada@example.com", resultContext["email"]);
        }

        // --- Source context capture ---
        //
        // Ported from gems/forge_ops_tracker's own "source context capture" spec. Uses
        // BuildFromLogMessage (which takes a plain stack-trace *string*, exactly like Ruby's own
        // spec feeds a fabricated backtrace via instance_double) rather than a real thrown
        // exception, so each test can point a frame at a real temp file it controls, the same way
        // the Ruby spec controls Configuration#app_root and a Tempfile.

        private static string WriteTempSourceFile(int lineCount)
        {
            var path = Path.GetTempFileName();
            var lines = new string[lineCount];
            for (var i = 0; i < lineCount; i++)
            {
                lines[i] = $"line {i + 1}";
            }
            File.WriteAllLines(path, lines);
            return path;
        }

        private static Dictionary<string, object> FrameFor(string path, int line, string appPrefix = "MyGame", bool captureSourceContext = true)
        {
            var config = NewConfiguration(c =>
            {
                c.CaptureSourceContext = captureSourceContext;
                if (appPrefix != null)
                {
                    c.AppNamespacePrefixes.Add(appPrefix);
                }
            });

            var stackTrace = $"MyGame.Foo.Bar (System.Int32 amount) (at {path}:{line})";
            var payload = EventBuilder.BuildFromLogMessage(config, "System.Exception: boom", stackTrace);
            var backtrace = (List<Dictionary<string, object>>)payload["backtrace"];
            return backtrace[0];
        }

        [Test]
        public void CaptureSourceContext_defaults_to_true()
        {
            Assert.IsTrue(new Configuration().CaptureSourceContext);
        }

        [Test]
        public void SourceContext_attaches_pre_context_context_line_and_post_context_around_an_in_app_frame_by_default()
        {
            var path = WriteTempSourceFile(20);
            try
            {
                var frame = FrameFor(path, 10);

                Assert.AreEqual("line 10", frame["context_line"]);
                CollectionAssert.AreEqual(new[] { "line 5", "line 6", "line 7", "line 8", "line 9" }, (List<string>)frame["pre_context"]);
                CollectionAssert.AreEqual(new[] { "line 11", "line 12", "line 13", "line 14", "line 15" }, (List<string>)frame["post_context"]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void SourceContext_clamps_pre_context_and_post_context_at_the_start_and_end_of_the_file()
        {
            var path = WriteTempSourceFile(3);
            try
            {
                var first = FrameFor(path, 1);
                var last = FrameFor(path, 3);

                CollectionAssert.AreEqual(Array.Empty<string>(), (List<string>)first["pre_context"]);
                CollectionAssert.AreEqual(new[] { "line 2", "line 3" }, (List<string>)first["post_context"]);
                CollectionAssert.AreEqual(new[] { "line 1", "line 2" }, (List<string>)last["pre_context"]);
                CollectionAssert.AreEqual(Array.Empty<string>(), (List<string>)last["post_context"]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void SourceContext_truncates_an_individual_line_longer_than_500_characters()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, new string('x', 600));
            try
            {
                var frame = FrameFor(path, 1);

                Assert.AreEqual(new string('x', 500) + "...", frame["context_line"]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void SourceContext_never_attaches_to_a_frame_that_is_not_in_app()
        {
            var path = WriteTempSourceFile(20);
            try
            {
                var frame = FrameFor(path, 10, appPrefix: null);

                Assert.IsFalse((bool)frame["in_app"]);
                Assert.IsFalse(frame.ContainsKey("context_line"));
                Assert.IsFalse(frame.ContainsKey("pre_context"));
                Assert.IsFalse(frame.ContainsKey("post_context"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void SourceContext_is_left_off_a_frame_when_CaptureSourceContext_is_disabled()
        {
            var path = WriteTempSourceFile(20);
            try
            {
                var frame = FrameFor(path, 10, captureSourceContext: false);

                Assert.IsFalse(frame.ContainsKey("context_line"));
                Assert.IsFalse(frame.ContainsKey("pre_context"));
                Assert.IsFalse(frame.ContainsKey("post_context"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void SourceContext_leaves_a_frame_untouched_when_the_recorded_file_cannot_be_read()
        {
            var missingPath = Path.Combine(Path.GetTempPath(), $"forgeops-does-not-exist-{Guid.NewGuid():N}.cs");

            var frame = FrameFor(missingPath, 1);

            Assert.IsFalse(frame.ContainsKey("context_line"));
        }

        // --- Breadcrumbs ---

        private static Dictionary<string, object> Crumb(string message, Dictionary<string, object> data = null) => new Dictionary<string, object>
        {
            ["category"] = "custom",
            ["message"] = message,
            ["level"] = "info",
            ["timestamp"] = "2024-01-15T10:29:58Z",
            ["data"] = data ?? new Dictionary<string, object>()
        };

        [Test]
        public void BuildFromException_includes_breadcrumbs_when_given()
        {
            var crumbs = new List<Dictionary<string, object>> { Crumb("GET /orders/42") };

            var payload = EventBuilder.BuildFromException(NewConfiguration(), RaiseAndCatch(), null, null, crumbs);

            var built = (List<Dictionary<string, object>>)payload["breadcrumbs"];
            Assert.AreEqual(1, built.Count);
            Assert.AreEqual("GET /orders/42", built[0]["message"]);
        }

        [Test]
        public void BuildFromException_omits_the_breadcrumbs_key_entirely_when_none_were_given_or_the_list_is_empty()
        {
            Assert.IsFalse(EventBuilder.BuildFromException(NewConfiguration(), RaiseAndCatch()).ContainsKey("breadcrumbs"));
            Assert.IsFalse(EventBuilder.BuildFromException(NewConfiguration(), RaiseAndCatch(), null, null, new List<Dictionary<string, object>>()).ContainsKey("breadcrumbs"));
        }

        [Test]
        public void BuildFromLogMessage_includes_breadcrumbs_too()
        {
            var crumbs = new List<Dictionary<string, object>> { Crumb("before the exception") };

            var payload = EventBuilder.BuildFromLogMessage(NewConfiguration(), "InvalidOperationException: boom", "", null, crumbs);

            Assert.AreEqual("before the exception", ((List<Dictionary<string, object>>)payload["breadcrumbs"])[0]["message"]);
        }

        [Test]
        public void Breadcrumb_message_and_data_are_scrubbed_but_category_level_and_timestamp_are_not()
        {
            var crumb = Crumb("emailed alice@example.com", new Dictionary<string, object> { ["email"] = "alice@example.com", ["password"] = "hunter2" });

            var payload = EventBuilder.BuildFromException(NewConfiguration(), RaiseAndCatch(), null, null, new List<Dictionary<string, object>> { crumb });

            var scrubbed = ((List<Dictionary<string, object>>)payload["breadcrumbs"])[0];
            Assert.AreEqual("emailed [EMAIL FILTERED]", scrubbed["message"]);
            Assert.AreEqual("custom", scrubbed["category"]);
            Assert.AreEqual("info", scrubbed["level"]);
            Assert.AreEqual("2024-01-15T10:29:58Z", scrubbed["timestamp"]);
            var data = (Dictionary<string, object>)scrubbed["data"];
            Assert.AreEqual("[EMAIL FILTERED]", data["email"]);
            Assert.AreEqual("[FILTERED]", data["password"]);
        }

        [Test]
        public void Scrubbing_never_mutates_the_callers_own_breadcrumb_entries()
        {
            var crumb = Crumb("emailed alice@example.com");

            EventBuilder.BuildFromException(NewConfiguration(), RaiseAndCatch(), null, null, new List<Dictionary<string, object>> { crumb });

            Assert.AreEqual("emailed alice@example.com", crumb["message"]);
        }

        [Test]
        public void Breadcrumbs_are_left_untouched_when_ScrubPii_is_off()
        {
            var crumb = Crumb("emailed alice@example.com");

            var payload = EventBuilder.BuildFromException(NewConfiguration(c => c.ScrubPii = false), RaiseAndCatch(), null, null, new List<Dictionary<string, object>> { crumb });

            Assert.AreEqual("emailed alice@example.com", ((List<Dictionary<string, object>>)payload["breadcrumbs"])[0]["message"]);
        }

        [Test]
        public void The_breadcrumbs_payload_encodes_to_valid_json()
        {
            var crumbs = new List<Dictionary<string, object>> { Crumb("said \"hi\"", new Dictionary<string, object> { ["n"] = 1 }) };

            var json = Json.Encode(EventBuilder.BuildFromException(NewConfiguration(), RaiseAndCatch(), null, null, crumbs));

            StringAssert.Contains("\"breadcrumbs\":[{\"category\":\"custom\",\"message\":\"said \\\"hi\\\"\"", json);
        }
    }
}
