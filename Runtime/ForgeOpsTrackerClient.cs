using System;
using System.Collections.Generic;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Public entry point for the package. Named <c>ForgeOpsTrackerClient</c>, not a bare
    /// <c>ForgeOpsTracker</c> static class sharing its enclosing namespace's name: the same
    /// reasoning sdks/dotnet's own <c>ForgeOpsTrackerSdk</c> documents for the same naming
    /// choice.
    ///
    ///     ForgeOpsTrackerClient.Init(c => {
    ///         c.Dsn = "https://&lt;api_key&gt;@getforgeops.net/api/v1/events";
    ///         c.EnvironmentName = "production";
    ///     });
    ///
    /// Call once, as early as possible: a <c>RuntimeInitializeOnLoadMethod</c> in your own game
    /// code, or the first line of your bootstrap scene's startup script.
    /// </summary>
    public static class ForgeOpsTrackerClient
    {
        // Internal, not private, only so EditMode tests can configure the client without Init (which
        // creates a GameObject Unity forbids outside play mode); see UseQueueForTesting.
        internal static readonly Configuration Config = new Configuration();
        private static DeliveryQueue _queue;

        // Every trace started and not yet finished, oldest first. A trace is an explicit object rather
        // than per-thread state (see Trace), so "the trace an error belongs to" is the most recently
        // started one still open: the same one-game, one-flow-at-a-time reasoning CurrentUser uses.
        private static readonly List<Trace> OpenTraces = new List<Trace>();
        private const int MaxOpenTraces = 32;

        // The shared breadcrumb trail: see BreadcrumbBuffer for why one shared trail, not per-thread.
        // Created alongside Config (a static readonly, one instance for the process) so a breadcrumb
        // added before Init is still recorded rather than lost. Internal, not private:
        // ForgeOpsTrackerHooks reads and appends to it too.
        internal static readonly BreadcrumbBuffer Breadcrumbs = new BreadcrumbBuffer(Config);

        // The shared performance tallies: see PerformanceFlusher. Built beside Config for the same
        // reason Breadcrumbs is, and reads the queue lazily since that only exists after Init.
        // Internal, not private: ForgeOpsTrackerDriver ticks it every frame.
        internal static readonly PerformanceFlusher Performance = new PerformanceFlusher(Config, () => _queue);
        private static ForgeOpsTrackerDriver _driver;

        // The two metric buffers: see MetricBuffer. Built beside Config for the same reason
        // Performance is; ForgeOpsTrackerDriver ticks both every frame. Internal, not private, for that.
        internal static readonly MetricBuffer CustomMetrics = new MetricBuffer(Config, () => _queue, DeliveryTarget.CustomMetrics, () => Config.MetricFlushIntervalSeconds);
        internal static readonly MetricBuffer InfrastructureMetrics = new MetricBuffer(Config, () => _queue, DeliveryTarget.InfrastructureMetrics, () => Config.InfrastructureMetricFlushIntervalSeconds);

        // The user set via SetUser, if any. A game install is effectively single-player on this
        // device (unlike a server handling many concurrent, unrelated requests at once, the
        // reason sdks/node's own equivalent needs AsyncLocalStorage instead), so this is a plain
        // static field, not any kind of per-request/per-thread storage: the same reasoning
        // sdks/swift/sdks/android's own plain static property documents. Internal, not private:
        // ForgeOpsTrackerHooks reads it too, to attach it to whatever its own automatic-capture
        // hooks report.
        internal static Dictionary<string, object> CurrentUser { get; private set; }

        public static void Init(Action<Configuration> configure)
        {
            configure?.Invoke(Config);

            _queue = new DeliveryQueue(Config);
            _driver = ForgeOpsTrackerDriver.Create(Config, _queue);
            ForgeOpsTrackerHooks.Install(Config, _queue);
        }

        /// <summary>
        /// Reports an exception you've already caught yourself (typically inside your own
        /// try/catch around a risky operation you don't want to let crash to Unity's own log
        /// pipeline, or want to attach extra context to before it's reported):
        ///
        ///     try { LoadSave(path); }
        ///     catch (Exception e) {
        ///         ForgeOpsTrackerClient.CaptureException(e, new Dictionary&lt;string, object&gt; { ["save_path"] = path });
        ///     }
        ///
        /// <paramref name="user"/> defaults to whatever <see cref="SetUser"/> last established, if
        /// anything; pass one explicitly to override that for this one report.
        ///
        /// <paramref name="trace"/> links the report to that trace: its id goes on the event as
        /// <c>trace_id</c>, so ForgeOps shows it next to a backend error from the same request (see
        /// <see cref="Trace.StartHttpSpan"/>). Null (the default) means the most recently started trace
        /// that hasn't finished yet, if any (see <see cref="CurrentTraceId"/>); pass one explicitly when
        /// several overlap. No open trace, no <c>trace_id</c>: the event is exactly what it was before.
        /// </summary>
        public static void CaptureException(Exception exception, Dictionary<string, object> context = null, Dictionary<string, object> user = null, Trace trace = null)
        {
            if (_queue == null || !Config.IsEnabled()) return;

            try
            {
                var payload = EventBuilder.BuildFromException(Config, exception, context, user ?? CurrentUser, Breadcrumbs.All(), (trace ?? CurrentTrace)?.TraceId);
                _queue.Push(payload);
            }
            catch (Exception ex)
            {
                Config.Log($"[ForgeOpsTracker] capture failed: {ex}");
            }
        }

        /// <summary>
        /// Records one breadcrumb: an entry in a small, bounded trail of recent events attached to
        /// whatever gets reported next (an explicit <see cref="CaptureException"/> call, or an
        /// uncaught exception <see cref="ForgeOpsTrackerHooks"/> reports), so an issue's detail page
        /// can show what led up to it. <paramref name="category"/>/<paramref name="level"/> default
        /// to <c>"custom"</c>/<c>"info"</c>; <paramref name="data"/> is any small dictionary of extra
        /// detail. Only the most recent <see cref="Configuration.MaxBreadcrumbs"/> (30) are kept,
        /// oldest dropped first; a no-op when <see cref="Configuration.TrackBreadcrumbs"/> is false.
        /// Safe to call from any thread.
        ///
        /// Every <c>Debug.Log</c>/<c>Debug.LogWarning</c>/<c>Debug.LogError</c> is also recorded
        /// automatically (category <c>"console"</c>) once <see cref="Init"/> has run. Anything else
        /// (a scene change, a purchase, a menu screen) is one you add by hand, wherever it's
        /// meaningful:
        ///
        ///     ForgeOpsTrackerClient.AddBreadcrumb("charging card", "payment", data: new Dictionary&lt;string, object&gt; { ["order_id"] = orderId });
        /// </summary>
        public static void AddBreadcrumb(string message, string category = "custom", string level = "info", Dictionary<string, object> data = null)
        {
            Breadcrumbs.Add(message, category, level, data);
        }

        /// <summary>
        /// Empties the breadcrumb trail. A game install is effectively single-player (one shared
        /// trail, like <see cref="SetUser"/>'s one shared user), so this is only needed to start a
        /// new logical unit of work (say, a new save slot) with a fresh trail.
        /// </summary>
        public static void ClearBreadcrumbs()
        {
            Breadcrumbs.Clear();
        }

        /// <summary>
        /// Records one timed call's duration, in milliseconds, under <paramref name="transactionName"/>:
        /// tallied in-process (count, total, max) and flushed every
        /// <see cref="Configuration.PerformanceFlushIntervalSeconds"/> (60) as one small aggregate report
        /// per transaction, for the Performance page's per-transaction table, not one network call per
        /// call. A no-op when <see cref="Configuration.TrackPerformance"/> is false or reporting isn't
        /// enabled for this environment. Safe to call from any thread.
        ///
        /// This client has no web framework integration, so nothing is timed automatically: wrap
        /// whatever you want on the Performance page yourself, with <see cref="StartTransaction"/> or
        /// <see cref="TimeTransaction{T}"/>, or call this directly with a duration you measured. Keep
        /// the name low-cardinality ("Level1.Load", not one per item id): every distinct name is its
        /// own row.
        /// </summary>
        public static void RecordPerformance(string transactionName, double durationMs)
        {
            Performance.Record(transactionName, durationMs);
        }

        /// <summary>
        /// Starts timing <paramref name="transactionName"/>; the returned scope records how long it
        /// lived when disposed, even if the block it wraps throws (a <c>using</c> block disposes on the
        /// way out of an exception too):
        ///
        ///     using (ForgeOpsTrackerClient.StartTransaction("Level1.Load")) { LoadLevel(); }
        /// </summary>
        public static IDisposable StartTransaction(string transactionName) => new TransactionTimer(transactionName, RecordPerformance);

        /// <summary>Runs <paramref name="body"/>, records how long it took under <paramref name="transactionName"/> (see <see cref="StartTransaction"/>), and returns its value.</summary>
        public static T TimeTransaction<T>(string transactionName, Func<T> body) => TimeTransaction(transactionName, RecordPerformance, body);

        /// <summary>Runs <paramref name="body"/> and records how long it took under <paramref name="transactionName"/>, even if it throws.</summary>
        public static void TimeTransaction(string transactionName, Action body) => TimeTransaction(transactionName, RecordPerformance, body);

        // The same three, recording through a caller-supplied delegate instead of the shared
        // static flusher: the shared one is only enabled once Init has configured a real DSN (and
        // Init makes a DontDestroyOnLoad GameObject, which Unity forbids outside play mode), so this
        // is how the EditMode tests exercise the timing itself against a flusher they control.
        internal static IDisposable StartTransaction(string transactionName, Action<string, double> record) => new TransactionTimer(transactionName, record);

        internal static T TimeTransaction<T>(string transactionName, Action<string, double> record, Func<T> body)
        {
            using (new TransactionTimer(transactionName, record)) return body();
        }

        internal static void TimeTransaction(string transactionName, Action<string, double> record, Action body)
        {
            using (new TransactionTimer(transactionName, record)) body();
        }

        /// <summary>
        /// Queues whatever has been tallied so far for delivery right now, instead of waiting for the
        /// next <see cref="Configuration.PerformanceFlushIntervalSeconds"/> tick. Delivery itself
        /// happens on the driver's next <c>Update</c> (a <c>UnityWebRequest</c> can only run from Unity's
        /// main thread, in a coroutine), so a flush requested as the app is suspended or quit will not
        /// be delivered: whatever was tallied since the last tick can be lost then. Shorten
        /// <c>PerformanceFlushIntervalSeconds</c> to lose less.
        /// </summary>
        public static void FlushPerformance()
        {
            Performance.Flush();
        }

        private sealed class TransactionTimer : IDisposable
        {
            private readonly string _name;
            private readonly Action<string, double> _record;
            private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            private bool _disposed;

            public TransactionTimer(string name, Action<string, double> record)
            {
                _name = name;
                _record = record;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _record(_name, _stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Custom metrics and infrastructure monitoring: two explicit calls (nothing is automatic, so
        /// there is no TrackMetrics flag). <see cref="CaptureMetric"/> records a named business event (a
        /// purchase, a level completed, anything you want to name): <paramref name="value"/> defaults to
        /// 1 for a bare counter; pass one for a real magnitude, and it may be negative (a refund).
        /// <see cref="CaptureInfrastructureMetric"/> records one reading (frame time, memory, anything
        /// else you measure) from a device or one of your own hosts; <paramref name="hostname"/> defaults
        /// to <see cref="Configuration.ServerName"/>. Both are buffered and queued for delivery as one
        /// batch every <see cref="Configuration.MetricFlushIntervalSeconds"/> /
        /// <see cref="Configuration.InfrastructureMetricFlushIntervalSeconds"/> (60), delivered from the
        /// driver's next <c>Update</c> (a <c>UnityWebRequest</c> can only run from Unity's main thread),
        /// so a flush requested as the app is suspended or quit will not be delivered. Safe to call from
        /// any thread. Every entry is stored as captured (a purchase is a row, not a running total), so a
        /// count or sum computed later is exact. Both are a no-op when reporting isn't enabled for this
        /// environment, and a NaN or infinite value is dropped. A buffer holds at most
        /// <see cref="MetricBuffer.MaxEntries"/> entries and drops further ones until a flush succeeds; a
        /// failed delivery keeps every entry, and one captured while a delivery is in flight is kept too.
        /// </summary>
        public static void CaptureMetric(string name, double value = 1.0)
        {
            if (!Config.IsEnabled()) return;

            CustomMetrics.Record(new Dictionary<string, object>
            {
                ["metric_name"] = name,
                ["value"] = value,
                ["environment"] = Config.EnvironmentName,
                ["release"] = Config.Release
            }, value);
        }

        public static void CaptureInfrastructureMetric(string name, double value, string hostname = null)
        {
            if (!Config.IsEnabled()) return;

            InfrastructureMetrics.Record(new Dictionary<string, object>
            {
                ["metric_name"] = name,
                ["value"] = value,
                ["hostname"] = hostname ?? Config.ServerName ?? ""
            }, value);
        }

        /// <summary>
        /// Queues whatever has been captured so far for delivery right now, instead of waiting for the
        /// next interval tick. Delivery itself happens on the driver's next <c>Update</c>, so a flush
        /// requested as the app is suspended or quit will not be delivered: shorten the flush intervals
        /// to lose less.
        /// </summary>
        public static void FlushMetrics()
        {
            CustomMetrics.Flush();
            InfrastructureMetrics.Flush();
        }

        /// <summary>
        /// Distributed tracing: one flow's own call tree (a level load, a save read, a shop opening and
        /// what it triggered), sent to ForgeOps only when the whole thing took at least
        /// <see cref="Configuration.TraceCaptureThresholdSeconds"/> (1s), so fast flows cost nothing on
        /// the wire. Make each request with <see cref="Trace.StartHttpSpan"/> or
        /// <see cref="Trace.MeasureHttpSpan{T}"/> and your backend continues the trace from their
        /// <c>traceparent</c> header; errors captured while a trace is open carry its id.
        ///
        ///     using (var trace = ForgeOpsTrackerClient.StartTrace("Level1.Load"))
        ///     {
        ///         var save = trace.MeasureSpan("Save.Read", () => ReadSave(path), "service");
        ///         using (trace.StartSpan("Assets.Load", "job")) { LoadAssets(); }
        ///     }
        ///
        /// Disposing the trace finishes it, even if the block throws. When reporting isn't enabled for
        /// this environment, or before <see cref="Init"/>, this returns a disabled trace whose every
        /// method is a no-op (the body still runs, and it has no <see cref="Trace.TraceId"/>), so
        /// callers never null-check. When <see cref="Configuration.TrackTracing"/> is false it returns a
        /// trace that records nothing but still has an id for errors and the <c>traceparent</c> header. This client has no web
        /// framework integration, so nothing starts a trace or records a span automatically. Finishing
        /// a slow trace queues it for delivery from the driver's next <c>Update</c> (a
        /// <c>UnityWebRequest</c> can only run from Unity's main thread), so a trace finished as the app
        /// is suspended or quit will not be delivered.
        /// </summary>
        public static Trace StartTrace(string name)
        {
            var queue = _queue;
            if (queue == null || !Config.IsEnabled()) return new Trace(name, Config, null);

            var trace = new Trace(name, Config, payload => queue.Push(payload, DeliveryTarget.Spans, null), Config.TrackTracing, finished =>
            {
                lock (OpenTraces) OpenTraces.Remove(finished);
            });
            lock (OpenTraces)
            {
                OpenTraces.Add(trace);
                // A trace that is never finished would otherwise be held forever; the oldest goes first.
                if (OpenTraces.Count > MaxOpenTraces) OpenTraces.RemoveAt(0);
            }
            return trace;
        }

        /// <summary>The most recently started trace that hasn't finished, if any: what an error captured right now belongs to.</summary>
        internal static Trace CurrentTrace
        {
            get
            {
                lock (OpenTraces) return OpenTraces.Count > 0 ? OpenTraces[OpenTraces.Count - 1] : null;
            }
        }

        /// <summary>
        /// The id of the most recently started trace that hasn't finished (32 lowercase hex
        /// characters), or null. Errors captured right now, including the uncaught exceptions
        /// <see cref="ForgeOpsTrackerHooks"/> reports, carry it as <c>trace_id</c>.
        /// </summary>
        public static string CurrentTraceId() => CurrentTrace?.TraceId;

        /// <summary>
        /// Manually attaches an affected user to whatever gets reported from here on (an explicit
        /// <see cref="CaptureException"/> call with no <c>user</c> argument, or anything
        /// <see cref="ForgeOpsTrackerHooks"/>'s automatic capture reports): there's no way to
        /// automatically detect "the current player" the way a server-side web framework with its
        /// own session/auth middleware can, so call this yourself, e.g. right after sign-in. Keys
        /// like <c>id</c>/<c>email</c>/<c>username</c> are all independently optional; call with
        /// <c>null</c> (or an empty dictionary) to clear whatever was set, e.g. on sign-out.
        /// </summary>
        public static void SetUser(Dictionary<string, object> user)
        {
            CurrentUser = (user == null || user.Count == 0) ? null : user;
        }

        /// <summary>
        /// Test-only: what <see cref="Init"/> does minus the driver and hooks (the driver needs a
        /// GameObject, which Unity forbids outside play mode), so captures and traces land on a queue the
        /// test can read.
        /// </summary>
        internal static void UseQueueForTesting(DeliveryQueue queue)
        {
            _queue = queue;
        }

        /// <summary>Test-only: resets module state between test cases.</summary>
        internal static void ResetForTesting()
        {
            _queue = null;
            lock (OpenTraces) OpenTraces.Clear();
            CurrentUser = null;
            Breadcrumbs.Clear();
            Performance.Reset();
            CustomMetrics.Reset();
            InfrastructureMetrics.Reset();
        }
    }
}
