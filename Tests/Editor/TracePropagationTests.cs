using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace ForgeOpsTracker.Unity.Tests
{
    // W3C trace context: ids, the traceparent header an HTTP span hands out, the span it names, the
    // two propagation options, and trace_id on errors captured while a trace is open.
    public class TracePropagationTests
    {
        private static Configuration EnabledConfiguration(Action<Configuration> configure = null)
        {
            var config = new Configuration
            {
                Dsn = "https://key@tracker.example.com/api/v1/events",
                EnvironmentName = "production",
                TraceCaptureThresholdSeconds = 0
            };
            configure?.Invoke(config);
            return config;
        }

        private static Trace NewTrace(Configuration config, List<Dictionary<string, object>> delivered, bool recording = true) =>
            new Trace("checkout", config, delivered.Add, recording);

        private static Dictionary<string, object> SpanNamed(Dictionary<string, object> trace, string name) =>
            ((List<object>)trace["spans"]).Cast(s => (Dictionary<string, object>)s).Find(s => (string)s["name"] == name);

        // The client's Configuration is one static instance; these tests set what they need on it and
        // put it back afterward.
        private string _savedDsn;
        private string _savedEnvironment;
        private bool _savedTrackTracing;

        [SetUp]
        public void SetUp()
        {
            ForgeOpsTrackerClient.ResetForTesting();
            var config = ForgeOpsTrackerClient.Config;
            _savedDsn = config.Dsn;
            _savedEnvironment = config.EnvironmentName;
            _savedTrackTracing = config.TrackTracing;
            config.Dsn = "https://key@tracker.example.com/api/v1/events";
            config.EnvironmentName = "production";
            config.TrackTracing = true;
        }

        [TearDown]
        public void TearDown()
        {
            var config = ForgeOpsTrackerClient.Config;
            config.Dsn = _savedDsn;
            config.EnvironmentName = _savedEnvironment;
            config.TrackTracing = _savedTrackTracing;
            ForgeOpsTrackerHooks.Uninstall();
            ForgeOpsTrackerClient.ResetForTesting();
        }

        [Test]
        public void Ids_are_W3C_lowercase_hex_and_never_all_zeros()
        {
            for (var i = 0; i < 200; i++)
            {
                StringAssert.IsMatch("^(?!0{32}$)[0-9a-f]{32}$", TraceParent.GenerateTraceId());
                StringAssert.IsMatch("^(?!0{16}$)[0-9a-f]{16}$", TraceParent.GenerateSpanId());
            }
            StringAssert.IsMatch("^[0-9a-f]{32}$", NewTrace(EnabledConfiguration(), new List<Dictionary<string, object>>()).TraceId);
            Assert.IsNull(new Trace("off", EnabledConfiguration(), null).TraceId);
        }

        [Test]
        public void StartHttpSpan_hands_out_a_traceparent_naming_the_http_span_it_records()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);

            var span = trace.StartHttpSpan("post", "https://user@API.example.com:8443/orders/42?token=x");
            Assert.AreEqual($"00-{trace.TraceId}-{span.SpanId}-01", span.Headers["traceparent"]);
            Assert.AreEqual(span.Headers["traceparent"], span.Traceparent);
            span.Finish(new Dictionary<string, object> { ["status"] = 201 });
            span.Finish();
            span.Dispose();
            trace.Finish();

            var root = SpanNamed(delivered[0], "checkout");
            var http = SpanNamed(delivered[0], "POST api.example.com");
            Assert.AreEqual(2, ((List<object>)delivered[0]["spans"]).Count, "only the first Finish records anything");
            Assert.AreEqual(span.SpanId, http["span_id"]);
            Assert.AreEqual(root["span_id"], http["parent_span_id"]);
            Assert.AreEqual("http", http["kind"]);
            Assert.AreEqual(201, ((Dictionary<string, object>)http["data"])["status"]);
            Assert.AreEqual(trace.TraceId, delivered[0]["trace_id"]);
        }

        [Test]
        public void An_http_span_parents_under_the_span_open_on_this_thread_and_spans_inside_MeasureHttpSpan_nest_under_it()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);
            Dictionary<string, string> sent = null;

            using (trace.StartSpan("Shop.Open"))
            {
                var result = trace.MeasureHttpSpan("GET", "https://api.example.com/items", headers =>
                {
                    sent = headers;
                    trace.RecordSpan("decode", "service", DateTime.UtcNow, 1);
                    return 7;
                }, new Dictionary<string, object> { ["status"] = 200 });
                Assert.AreEqual(7, result);
            }
            trace.Finish();

            var http = SpanNamed(delivered[0], "GET api.example.com");
            Assert.AreEqual(SpanNamed(delivered[0], "Shop.Open")["span_id"], http["parent_span_id"]);
            Assert.AreEqual(http["span_id"], SpanNamed(delivered[0], "decode")["parent_span_id"]);
            Assert.AreEqual($"00-{trace.TraceId}-{http["span_id"]}-01", sent["traceparent"]);
        }

        [Test]
        public void MeasureHttpSpan_records_even_when_the_body_throws_and_rethrows_unchanged()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);

            Assert.Throws<InvalidOperationException>(() =>
                trace.MeasureHttpSpan("GET", "https://api.example.com/", headers => throw new InvalidOperationException("offline")));
            trace.RecordSpan("after", "service", DateTime.UtcNow, 1);
            trace.Finish();

            Assert.IsNotNull(SpanNamed(delivered[0], "GET api.example.com"));
            Assert.AreEqual(SpanNamed(delivered[0], "checkout")["span_id"], SpanNamed(delivered[0], "after")["parent_span_id"]);
        }

        [Test]
        public void AddHeadersTo_sets_the_header_but_leaves_a_traceparent_the_request_already_has()
        {
            var trace = NewTrace(EnabledConfiguration(), new List<Dictionary<string, object>>());
            var span = trace.StartHttpSpan("POST", "https://api.example.com/orders");

            var plain = new UnityWebRequest("https://api.example.com/orders", "POST");
            span.AddHeadersTo(plain);
            Assert.AreEqual(span.Traceparent, plain.GetRequestHeader("traceparent"));

            var own = new UnityWebRequest("https://api.example.com/orders", "POST");
            own.SetRequestHeader("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
            span.AddHeadersTo(own);
            Assert.AreEqual("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", own.GetRequestHeader("traceparent"));
        }

        [Test]
        public void PropagateTraces_off_or_an_untargeted_host_gets_no_header_but_the_span_is_still_recorded()
        {
            var delivered = new List<Dictionary<string, object>>();
            var config = EnabledConfiguration(c => c.TracePropagationTargets = new List<object> { "example.com" });
            var trace = NewTrace(config, delivered);

            Assert.IsNotNull(trace.StartHttpSpan("GET", "https://api.example.com/").Traceparent);
            var thirdParty = trace.StartHttpSpan("GET", "https://third-party.test/");
            Assert.IsEmpty(thirdParty.Headers);
            Assert.IsNull(thirdParty.Traceparent);
            thirdParty.Finish();
            config.PropagateTraces = false;
            var off = trace.StartHttpSpan("GET", "https://api.example.com/");
            Assert.IsEmpty(off.Headers);
            off.Finish();
            trace.Finish();

            Assert.IsNotNull(SpanNamed(delivered[0], "GET third-party.test"));
            Assert.IsNotNull(SpanNamed(delivered[0], "GET api.example.com"));
        }

        [Test]
        public void A_disabled_trace_hands_out_no_headers_and_records_nothing()
        {
            var trace = new Trace("off", EnabledConfiguration(), null);

            var span = trace.StartHttpSpan("GET", "https://api.example.com/");
            Assert.IsNull(span.SpanId);
            Assert.IsEmpty(span.Headers);
            span.Finish();
            Assert.AreEqual(3, trace.MeasureHttpSpan("GET", "https://api.example.com/", headers => headers.Count + 3));
            trace.Finish();
        }

        [Test]
        public void A_trace_that_is_not_recording_still_has_an_id_and_headers_but_sends_nothing()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered, recording: false);

            StringAssert.StartsWith($"00-{trace.TraceId}-", trace.StartHttpSpan("GET", "https://api.example.com/").Traceparent);
            trace.MeasureSpan("work", () => { });
            trace.Finish();

            Assert.IsEmpty(delivered);
        }

        [Test]
        public void HostOf_reads_the_host_of_an_absolute_url()
        {
            Assert.AreEqual("api.example.com", Trace.HostOf("https://API.example.com/x?y=1"));
            Assert.AreEqual("api.example.com", Trace.HostOf("http://user:pw@api.example.com:8080"));
            Assert.AreEqual("::1", Trace.HostOf("http://[::1]:3000/"));
            Assert.AreEqual("example.com", Trace.HostOf("https://example.com#frag"));
            Assert.IsNull(Trace.HostOf("/relative/path"));
            Assert.IsNull(Trace.HostOf(null));

            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(c => c.TracePropagationTargets = new List<object> { "example.com" }), delivered);
            var span = trace.StartHttpSpan(null, "not a url");
            Assert.IsEmpty(span.Headers, "no host only gets the header when every host does");
            span.Finish();
            trace.Finish();
            Assert.IsNotNull(SpanNamed(delivered[0], "GET unknown"));
        }

        [Test]
        public void Propagation_defaults_to_every_host()
        {
            var config = new Configuration();
            Assert.IsTrue(config.PropagateTraces);
            Assert.IsNull(config.TracePropagationTargets);
            Assert.IsTrue(config.ShouldPropagateTrace("anything.test"));
            Assert.IsTrue(config.ShouldPropagateTrace(null));
        }

        [Test]
        public void A_host_target_matches_exactly_or_as_a_subdomain_on_a_dot_boundary_ignoring_case_and_a_leading_dot()
        {
            var config = new Configuration { TracePropagationTargets = new List<object> { "Example.com", ".internal.test" } };
            Assert.IsTrue(config.ShouldPropagateTrace("example.com"));
            Assert.IsTrue(config.ShouldPropagateTrace("api.EXAMPLE.com"));
            Assert.IsFalse(config.ShouldPropagateTrace("badexample.com"));
            Assert.IsFalse(config.ShouldPropagateTrace("example.com.evil.test"));
            Assert.IsTrue(config.ShouldPropagateTrace("db.internal.test"));
            Assert.IsFalse(config.ShouldPropagateTrace(null), "no host only gets the header when every host does");
            Assert.IsFalse(config.ShouldPropagateTrace(""));
        }

        [Test]
        public void A_Regex_target_is_searched_for_in_the_lowercased_host_and_anything_else_matches_nothing()
        {
            var config = new Configuration { TracePropagationTargets = new List<object> { new Regex(@"^api\d+\.corp$"), 42 } };
            Assert.IsTrue(config.ShouldPropagateTrace("API7.corp"));
            Assert.IsFalse(config.ShouldPropagateTrace("api.corp"));
            Assert.IsFalse(config.ShouldPropagateTrace("42"));

            config.TracePropagationTargets = new List<object>();
            Assert.IsFalse(config.ShouldPropagateTrace("example.com"));
            config.TracePropagationTargets = null;
            config.PropagateTraces = false;
            Assert.IsFalse(config.ShouldPropagateTrace("example.com"));
        }

        [Test]
        public void The_event_builder_adds_trace_id_unscrubbed_and_leaves_it_out_when_null()
        {
            var config = EnabledConfiguration();
            const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

            Assert.AreEqual(traceId, EventBuilder.BuildFromException(config, new InvalidOperationException("x"), traceId: traceId)["trace_id"]);
            Assert.AreEqual(traceId, EventBuilder.BuildFromLogMessage(config, "InvalidOperationException: x", "", traceId: traceId)["trace_id"]);
            Assert.IsFalse(EventBuilder.BuildFromException(config, new InvalidOperationException("x")).ContainsKey("trace_id"));
            Assert.IsFalse(EventBuilder.BuildFromLogMessage(config, "InvalidOperationException: x", "").ContainsKey("trace_id"));
        }

        [Test]
        public void CaptureException_carries_the_open_trace_or_an_explicit_one_and_none_once_every_trace_finished()
        {
            var queue = new DeliveryQueue(ForgeOpsTrackerClient.Config);
            ForgeOpsTrackerClient.UseQueueForTesting(queue);

            var first = ForgeOpsTrackerClient.StartTrace("first");
            var second = ForgeOpsTrackerClient.StartTrace("second");
            Assert.AreEqual(second.TraceId, ForgeOpsTrackerClient.CurrentTraceId());
            ForgeOpsTrackerClient.CaptureException(new InvalidOperationException("latest"));
            ForgeOpsTrackerClient.CaptureException(new InvalidOperationException("explicit"), trace: first);
            second.Finish();
            first.Finish();
            ForgeOpsTrackerClient.CaptureException(new InvalidOperationException("none"));

            var events = queue.Snapshot().FindAll(p => p.ContainsKey("exception_class"));
            Assert.AreEqual(second.TraceId, events.Find(e => (string)e["message"] == "latest")["trace_id"]);
            Assert.AreEqual(first.TraceId, events.Find(e => (string)e["message"] == "explicit")["trace_id"]);
            Assert.IsFalse(events.Find(e => (string)e["message"] == "none").ContainsKey("trace_id"));
            Assert.IsNull(ForgeOpsTrackerClient.CurrentTraceId());
        }

        [Test]
        public void With_TrackTracing_off_StartTrace_still_gives_errors_an_id_but_sends_no_spans()
        {
            var queue = new DeliveryQueue(ForgeOpsTrackerClient.Config);
            ForgeOpsTrackerClient.UseQueueForTesting(queue);
            ForgeOpsTrackerClient.Config.TrackTracing = false;

            using (var trace = ForgeOpsTrackerClient.StartTrace("checkout"))
            {
                StringAssert.IsMatch("^[0-9a-f]{32}$", trace.TraceId);
                ForgeOpsTrackerClient.CaptureException(new InvalidOperationException("boom"));
                Assert.AreEqual(trace.TraceId, queue.Snapshot()[0]["trace_id"]);
            }

            Assert.AreEqual(1, queue.Count, "the error, and no trace");
        }

        [Test]
        public void With_reporting_disabled_StartTrace_returns_a_disabled_trace_that_is_never_current()
        {
            ForgeOpsTrackerClient.UseQueueForTesting(new DeliveryQueue(ForgeOpsTrackerClient.Config));
            ForgeOpsTrackerClient.Config.EnvironmentName = "development";

            var trace = ForgeOpsTrackerClient.StartTrace("checkout");
            Assert.IsNull(trace.TraceId);
            Assert.IsNull(ForgeOpsTrackerClient.CurrentTraceId());
        }

        [Test]
        public void An_uncaught_exception_reported_by_the_hooks_carries_the_open_trace_id()
        {
            var queue = new DeliveryQueue(ForgeOpsTrackerClient.Config);
            ForgeOpsTrackerClient.UseQueueForTesting(queue);
            var trace = ForgeOpsTrackerClient.StartTrace("Level1.Load");
            ForgeOpsTrackerHooks.Install(ForgeOpsTrackerClient.Config, queue);
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: boom");

            Debug.LogException(new InvalidOperationException("boom"));

            Assert.AreEqual(trace.TraceId, queue.Snapshot().Find(p => p.ContainsKey("exception_class"))["trace_id"]);
            trace.Finish();
        }
    }
}
