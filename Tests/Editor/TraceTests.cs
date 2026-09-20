using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class TraceTests
    {
        private static Configuration EnabledConfiguration(Action<Configuration> configure = null)
        {
            var config = new Configuration
            {
                Dsn = "https://key@tracker.example.com/api/v1/events",
                EnvironmentName = "production",
                Release = "a1b2c3d",
                TraceCaptureThresholdSeconds = 0.01
            };
            configure?.Invoke(config);
            return config;
        }

        private static Trace NewTrace(Configuration config, List<Dictionary<string, object>> delivered) =>
            new Trace("GET /checkout", config, delivered.Add);

        private static Dictionary<string, object> SpanNamed(Dictionary<string, object> trace, string name) =>
            ((List<object>)trace["spans"]).Cast(s => (Dictionary<string, object>)s).Find(s => (string)s["name"] == name);

        [Test]
        public void ASlowTraceIsDeliveredWithNestedSpansAndTheWireShape()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);

            using (trace.StartSpan("charge", "service", new Dictionary<string, object> { ["order"] = 42 }))
            {
                trace.RecordSpan("SELECT users", "database", new DateTime(2023, 11, 14, 22, 13, 20, 123, DateTimeKind.Utc), 3);
                Thread.Sleep(30);
            }
            trace.RecordSpan("sibling", "database", DateTime.UtcNow, 1);
            trace.Finish();

            Assert.AreEqual(1, delivered.Count);
            var payload = delivered[0];
            StringAssert.IsMatch("^[0-9a-f]{32}$", (string)payload["trace_id"]);
            var root = SpanNamed(payload, "GET /checkout");
            var charge = SpanNamed(payload, "charge");
            Assert.IsNull(root["parent_span_id"]);
            Assert.AreEqual("controller", root["kind"]);
            StringAssert.IsMatch("^[0-9a-f]{16}$", (string)root["span_id"]);
            Assert.AreEqual(root["span_id"], charge["parent_span_id"]);
            Assert.AreEqual(charge["span_id"], SpanNamed(payload, "SELECT users")["parent_span_id"]);
            Assert.AreEqual(root["span_id"], SpanNamed(payload, "sibling")["parent_span_id"]);
            Assert.AreEqual("2023-11-14T22:13:20.123Z", SpanNamed(payload, "SELECT users")["started_at"]);
            Assert.AreEqual("production", charge["environment"]);
            Assert.AreEqual("a1b2c3d", charge["release"]);
            Assert.AreEqual(42, ((Dictionary<string, object>)charge["data"])["order"]);
            StringAssert.Contains("\"parent_span_id\":null", Json.Encode(root));
        }

        [Test]
        public void AnUnknownKindIsSentAsOtherSinceTheServerWouldRejectTheWholeTrace()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);
            trace.RecordSpan("q", "db", DateTime.UtcNow, 1);
            trace.RecordSpan("n", null, DateTime.UtcNow, 1);
            trace.RecordSpan("r", "database", DateTime.UtcNow, 1);
            Thread.Sleep(30);
            trace.Finish();

            Assert.AreEqual("other", SpanNamed(delivered[0], "q")["kind"]);
            Assert.AreEqual("other", SpanNamed(delivered[0], "n")["kind"]);
            Assert.AreEqual("database", SpanNamed(delivered[0], "r")["kind"]);
        }

        [Test]
        public void ADisposedTraceFinishesEvenWhenTheBlockThrowsAndTheExceptionPropagatesUnchanged()
        {
            var delivered = new List<Dictionary<string, object>>();
            var failure = new InvalidOperationException("boom");

            var thrown = Assert.Throws<InvalidOperationException>(() =>
            {
                using (var trace = NewTrace(EnabledConfiguration(), delivered))
                {
                    trace.MeasureSpan("bad", (Action)(() =>
                    {
                        Thread.Sleep(30);
                        throw failure;
                    }));
                }
            });

            Assert.AreSame(failure, thrown);
            Assert.AreEqual(1, delivered.Count);
            Assert.IsNotNull(SpanNamed(delivered[0], "bad"));
        }

        [Test]
        public void MeasureSpanReturnsTheBodysValue()
        {
            var trace = NewTrace(EnabledConfiguration(), new List<Dictionary<string, object>>());
            Assert.AreEqual(7, trace.MeasureSpan("x", () => 7));
        }

        [Test]
        public void AFastTraceSendsNothing()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(c => c.TraceCaptureThresholdSeconds = 60), delivered);
            trace.RecordSpan("q", "database", DateTime.UtcNow, 1);
            trace.Finish();

            Assert.AreEqual(0, delivered.Count);
        }

        [Test]
        public void ADisabledTraceRecordsNothingAndEveryMethodStillRunsTheBody()
        {
            var disabled = new Trace("x", EnabledConfiguration(), null);
            var ran = disabled.MeasureSpan("y", () => true);
            disabled.RecordSpan("z", "database", DateTime.UtcNow, 1);
            using (disabled.StartSpan("s")) { }
            disabled.Finish();

            Assert.IsTrue(ran);
        }

        [Test]
        public void StartTraceBeforeInitOrWithTracingOffReturnsADisabledTraceNotNull()
        {
            var trace = ForgeOpsTrackerClient.StartTrace("x");
            Assert.IsNotNull(trace);
            Assert.AreEqual(3, trace.MeasureSpan("y", () => 3));
            trace.Finish();
        }

        [Test]
        public void ASpanRecordedFromAnotherThreadParentsUnderTheRootNotTheOpenSpanOnThisOne()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);

            using (trace.StartSpan("outer"))
            {
                var thread = new Thread(() => trace.RecordSpan("background", "job", DateTime.UtcNow, 1));
                thread.Start();
                thread.Join();
                Thread.Sleep(30);
            }
            trace.Finish();

            Assert.AreEqual(SpanNamed(delivered[0], "GET /checkout")["span_id"], SpanNamed(delivered[0], "background")["parent_span_id"]);
        }

        [Test]
        public void FinishIsIdempotentAndSpansAfterItAreDropped()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);
            Thread.Sleep(30);
            trace.Finish();
            trace.Finish();
            trace.RecordSpan("late", "service", DateTime.UtcNow, 1);
            trace.Dispose();

            Assert.AreEqual(1, delivered.Count);
            Assert.IsNull(SpanNamed(delivered[0], "late"));
        }

        [Test]
        public void ATraceHoldsAtMost500SpansIncludingTheRoot()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(c => c.TraceCaptureThresholdSeconds = 0), delivered);
            for (var i = 0; i < 700; i++) trace.RecordSpan("q", "database", DateTime.UtcNow, 1);
            trace.Finish();

            Assert.AreEqual(500, ((List<object>)delivered[0]["spans"]).Count);
        }

        [Test]
        public void SpansUriSwapsTheTrailingEventsSegment()
        {
            Assert.AreEqual("https://tracker.example.com/api/v1/spans", EnabledConfiguration().SpansUri());
        }

        [Test]
        public void ADeliveredTraceEncodesAsValidJson()
        {
            var delivered = new List<Dictionary<string, object>>();
            var trace = NewTrace(EnabledConfiguration(), delivered);
            trace.RecordSpan("with \"quotes\"", "service", DateTime.UtcNow, 1);
            Thread.Sleep(30);
            trace.Finish();

            var json = Json.Encode(delivered[0]);
            Assert.IsTrue(Regex.IsMatch(json, "^\\{\"trace_id\":\"[0-9a-f]{32}\",\"spans\":\\[\\{"));
            StringAssert.Contains("with \\\"quotes\\\"", json);
        }
    }

    internal static class ListExtensions
    {
        public static List<T> Cast<T>(this List<object> source, Func<object, T> convert)
        {
            var result = new List<T>(source.Count);
            foreach (var item in source) result.Add(convert(item));
            return result;
        }
    }
}
