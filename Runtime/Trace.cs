using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// One trace: a tree of timed spans (a level load, a save read, a shop opening and what it
    /// triggered) that is sent to ForgeOps only when the whole thing took at least
    /// <see cref="Configuration.TraceCaptureThresholdSeconds"/> (1s), so fast flows cost nothing on
    /// the wire. Start one with <see cref="ForgeOpsTrackerClient.StartTrace"/>, never directly.
    /// Disposing it finishes it, so a <c>using</c> block sends the trace even if the code inside throws.
    ///
    /// Unlike the server SDKs, where one request owns one thread, game code hops between Unity's main
    /// thread and worker threads or Jobs, so a trace is an explicit object you pass around (or capture
    /// in a delegate) rather than ambient per-thread state, and it is safe to use from any thread.
    /// Nesting is tracked per thread: a span opened by <see cref="MeasureSpan{T}"/> or
    /// <see cref="StartSpan"/> becomes the parent of any span recorded on the same thread inside it,
    /// and a span recorded from another thread parents under the root.
    ///
    /// Kinds are the closed set the ingestion API accepts (controller, service, database, redis, http,
    /// job, other): anything else is sent as "other", since one bad kind would make the server reject
    /// the whole trace. A trace holds at most <see cref="MaxSpans"/> spans including the root.
    ///
    /// When tracing is off (or reporting isn't enabled) <see cref="ForgeOpsTrackerClient.StartTrace"/>
    /// still returns a trace, a disabled one on which every method is a cheap no-op (the body still
    /// runs), so callers never null-check.
    ///
    /// Delivery is the same as everything else in this SDK: finishing a slow trace only queues its
    /// payload on the <see cref="DeliveryQueue"/> (tagged <see cref="DeliveryTarget.Spans"/>), and
    /// <see cref="ForgeOpsTrackerDriver"/> sends it from Unity's main thread on a later frame.
    /// </summary>
    public sealed class Trace : IDisposable
    {
        public const int MaxSpans = 500;

        private static readonly HashSet<string> Kinds = new HashSet<string> { "controller", "service", "database", "redis", "http", "job", "other" };
        private static readonly object RandomLock = new object();
        private static readonly Random Random = new Random();

        private readonly string _name;
        private readonly Configuration _configuration;
        private readonly Action<Dictionary<string, object>> _deliver;
        private readonly string _traceId = RandomHex(16);
        private readonly string _rootSpanId = RandomHex(8);
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private readonly object _lock = new object();
        private readonly List<object> _spans = new List<object>();
        private readonly ThreadLocal<List<string>> _open = new ThreadLocal<List<string>>(() => new List<string>());
        private bool _finished;

        /// <param name="deliver">Null makes this a disabled trace that records nothing.</param>
        internal Trace(string name, Configuration configuration, Action<Dictionary<string, object>> deliver)
        {
            _name = name;
            _configuration = configuration;
            _deliver = deliver;
        }

        /// <summary>Times <paramref name="body"/> as a span (a child of whatever span is open on this thread, or of the root), recording it even if it throws, and returns its value.</summary>
        public T MeasureSpan<T>(string name, Func<T> body, string kind = "service", Dictionary<string, object> data = null)
        {
            using (StartSpan(name, kind, data)) return body();
        }

        /// <summary>Times <paramref name="body"/> as a span, recording it even if it throws.</summary>
        public void MeasureSpan(string name, Action body, string kind = "service", Dictionary<string, object> data = null)
        {
            using (StartSpan(name, kind, data)) body();
        }

        /// <summary>
        /// Starts a span; the returned scope records it when disposed, even if the block it wraps
        /// throws:
        ///
        ///     using (trace.StartSpan("Level1.Load", "job")) { LoadLevel(); }
        /// </summary>
        public IDisposable StartSpan(string name, string kind = "service", Dictionary<string, object> data = null)
        {
            if (_deliver == null) return NoopScope.Instance;

            var id = RandomHex(8);
            var open = _open.Value;
            var parent = open.Count > 0 ? open[open.Count - 1] : _rootSpanId;
            open.Add(id);
            return new SpanScope(this, id, parent, name, kind, data);
        }

        /// <summary>Records a span you timed yourself, under whatever is open on this thread (or the root).</summary>
        public void RecordSpan(string name, string kind, DateTime startedAtUtc, double durationMs, Dictionary<string, object> data = null)
        {
            if (_deliver == null) return;

            var open = _open.Value;
            Store(Build(RandomHex(8), open.Count > 0 ? open[open.Count - 1] : _rootSpanId, name, kind, startedAtUtc, durationMs, data));
        }

        /// <summary>
        /// Ends the trace and sends it if the root took long enough. Idempotent: a second call does
        /// nothing, and spans recorded after it are dropped. <see cref="Dispose"/> calls this.
        /// </summary>
        public void Finish()
        {
            if (_deliver == null) return;

            List<object> recorded;
            lock (_lock)
            {
                if (_finished) return;
                _finished = true;
                recorded = new List<object>(_spans);
            }

            var durationMs = _timer.Elapsed.TotalMilliseconds;
            if (durationMs < _configuration.TraceCaptureThresholdSeconds * 1000.0) return;

            var all = new List<object> { Build(_rootSpanId, null, _name, "controller", _startedAt, durationMs, null) };
            all.AddRange(recorded);
            _deliver(new Dictionary<string, object> { ["trace_id"] = _traceId, ["spans"] = all });
        }

        public void Dispose() => Finish();

        private void Store(Dictionary<string, object> span)
        {
            lock (_lock)
            {
                if (!_finished && _spans.Count < MaxSpans - 1) _spans.Add(span); // leave room for the root
            }
        }

        private Dictionary<string, object> Build(string id, string parent, string name, string kind, DateTime startedAtUtc, double durationMs, Dictionary<string, object> data) =>
            new Dictionary<string, object>
            {
                ["span_id"] = id,
                ["parent_span_id"] = parent,
                ["name"] = name,
                ["kind"] = kind != null && Kinds.Contains(kind) ? kind : "other",
                ["started_at"] = startedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["duration_ms"] = Math.Round(durationMs, 2),
                ["environment"] = _configuration.EnvironmentName,
                ["release"] = _configuration.Release,
                ["data"] = data ?? new Dictionary<string, object>()
            };

        private static string RandomHex(int bytes)
        {
            var raw = new byte[bytes];
            lock (RandomLock) Random.NextBytes(raw);
            var chars = new char[bytes * 2];
            for (var i = 0; i < bytes; i++) raw[i].ToString("x2", CultureInfo.InvariantCulture).CopyTo(0, chars, i * 2, 2);
            return new string(chars);
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new NoopScope();

            public void Dispose()
            {
            }
        }

        private sealed class SpanScope : IDisposable
        {
            private readonly Trace _trace;
            private readonly string _id;
            private readonly string _parent;
            private readonly string _name;
            private readonly string _kind;
            private readonly Dictionary<string, object> _data;
            private readonly DateTime _startedAt = DateTime.UtcNow;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private bool _disposed;

            public SpanScope(Trace trace, string id, string parent, string name, string kind, Dictionary<string, object> data)
            {
                _trace = trace;
                _id = id;
                _parent = parent;
                _name = name;
                _kind = kind;
                _data = data;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _trace._open.Value.Remove(_id);
                _trace.Store(_trace.Build(_id, _parent, _name, _kind, _startedAt, _stopwatch.Elapsed.TotalMilliseconds, _data));
            }
        }
    }
}
