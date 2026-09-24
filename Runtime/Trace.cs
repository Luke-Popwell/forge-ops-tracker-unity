using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine.Networking;

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
    ///
    /// Ids are W3C trace context ids (https://www.w3.org/TR/trace-context/): a 32 lowercase hex
    /// <see cref="TraceId"/> and 16 lowercase hex span ids, never all zeros. That is what lets
    /// <see cref="StartHttpSpan"/> hand the trace to your backend as a <c>traceparent</c> header, and
    /// what an error captured while the trace is open carries as its <c>trace_id</c>, so ForgeOps can
    /// show the two sides of one request together. With <see cref="Configuration.TrackTracing"/> off
    /// the trace records no spans but still has its id for both of those jobs.
    /// </summary>
    public sealed class Trace : IDisposable
    {
        public const int MaxSpans = 500;

        private static readonly HashSet<string> Kinds = new HashSet<string> { "controller", "service", "database", "redis", "http", "job", "other" };

        private readonly string _name;
        private readonly Configuration _configuration;
        private readonly Action<Dictionary<string, object>> _deliver;
        private readonly bool _recording;
        private readonly Action<Trace> _onFinish;
        private readonly string _rootSpanId = TraceParent.GenerateSpanId();
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private readonly object _lock = new object();
        private readonly List<object> _spans = new List<object>();
        private readonly ThreadLocal<List<string>> _open = new ThreadLocal<List<string>>(() => new List<string>());
        private bool _finished;

        /// <param name="deliver">Null makes this a disabled trace that records nothing and has no id.</param>
        /// <param name="recording">False keeps the id (for errors and headers) but records and sends no spans.</param>
        /// <param name="onFinish">Called once, when the trace finishes.</param>
        internal Trace(string name, Configuration configuration, Action<Dictionary<string, object>> deliver, bool recording = true, Action<Trace> onFinish = null)
        {
            _name = name;
            _configuration = configuration;
            _deliver = deliver;
            _recording = deliver != null && recording;
            _onFinish = onFinish;
            TraceId = deliver != null ? TraceParent.GenerateTraceId() : null;
        }

        /// <summary>
        /// This trace's W3C trace id (32 lowercase hex characters, never all zeros), or null for the
        /// disabled trace <see cref="ForgeOpsTrackerClient.StartTrace"/> returns when reporting isn't
        /// enabled or before <see cref="ForgeOpsTrackerClient.Init"/>.
        /// </summary>
        public string TraceId { get; }

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
            if (!_recording) return NoopScope.Instance;

            var id = TraceParent.GenerateSpanId();
            var parent = CurrentParent();
            _open.Value.Add(id);
            return new SpanScope(this, id, parent, name, kind, data);
        }

        /// <summary>Records a span you timed yourself, under whatever is open on this thread (or the root).</summary>
        public void RecordSpan(string name, string kind, DateTime startedAtUtc, double durationMs, Dictionary<string, object> data = null)
        {
            if (!_recording) return;

            Store(Build(TraceParent.GenerateSpanId(), CurrentParent(), name, kind, startedAtUtc, durationMs, data));
        }

        /// <summary>
        /// Starts timing one outgoing HTTP request as an <c>http</c> span named "&lt;METHOD&gt; &lt;host&gt;"
        /// (never the path or query, which can carry ids or tokens), for a request that completes later,
        /// such as a <c>UnityWebRequest</c> in a coroutine: add <see cref="HttpSpan.Headers"/> to the
        /// request (or call <see cref="HttpSpan.AddHeadersTo"/>), then <see cref="HttpSpan.Finish"/> (or
        /// dispose it) once the request is done. The headers carry a <c>traceparent</c> whose parent id
        /// is this span's own id, so the backend's root span nests under it; they are empty when
        /// <see cref="Configuration.PropagateTraces"/> is off, the host isn't in
        /// <see cref="Configuration.TracePropagationTargets"/>, or this is a disabled trace. The span
        /// parents under whatever is open on this thread right now (or the root).
        ///
        ///     var span = trace.StartHttpSpan("POST", url);
        ///     using (var request = new UnityWebRequest(url, "POST")) {
        ///         span.AddHeadersTo(request);
        ///         yield return request.SendWebRequest();
        ///         span.Finish(new Dictionary&lt;string, object&gt; { ["status"] = (int)request.responseCode });
        ///     }
        /// </summary>
        public HttpSpan StartHttpSpan(string method, string url)
        {
            var host = HostOf(url);
            var name = $"{(method ?? "GET").ToUpperInvariant()} {host ?? "unknown"}";
            if (TraceId == null) return new HttpSpan(null, null, null, name, new Dictionary<string, string>());

            var id = TraceParent.GenerateSpanId();
            var headers = new Dictionary<string, string>();
            if (_configuration.ShouldPropagateTrace(host)) headers[TraceParent.Header] = TraceParent.Build(TraceId, id);
            return new HttpSpan(this, id, CurrentParent(), name, headers);
        }

        /// <summary>
        /// Times a blocking HTTP request made by <paramref name="body"/> as an <c>http</c> span (see
        /// <see cref="StartHttpSpan"/>), handing it the headers to add to that request, and returns its
        /// value. Recorded even if <paramref name="body"/> throws, which propagates unchanged; spans
        /// started on this thread inside it nest under it.
        ///
        ///     var json = trace.MeasureHttpSpan("GET", url, headers => Fetch(url, headers));
        /// </summary>
        public T MeasureHttpSpan<T>(string method, string url, Func<Dictionary<string, string>, T> body, Dictionary<string, object> data = null)
        {
            var span = StartHttpSpan(method, url);
            var open = span.SpanId != null ? _open.Value : null;
            open?.Add(span.SpanId);
            try
            {
                return body(span.Headers);
            }
            finally
            {
                open?.Remove(span.SpanId);
                span.Finish(data);
            }
        }

        /// <summary>Times a blocking HTTP request made by <paramref name="body"/>, recording it even if it throws.</summary>
        public void MeasureHttpSpan(string method, string url, Action<Dictionary<string, string>> body, Dictionary<string, object> data = null)
        {
            MeasureHttpSpan<object>(method, url, headers =>
            {
                body(headers);
                return null;
            }, data);
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
            _onFinish?.Invoke(this);
            if (!_recording) return;

            var durationMs = _timer.Elapsed.TotalMilliseconds;
            if (durationMs < _configuration.TraceCaptureThresholdSeconds * 1000.0) return;

            var all = new List<object> { Build(_rootSpanId, null, _name, "controller", _startedAt, durationMs, null) };
            all.AddRange(recorded);
            _deliver(new Dictionary<string, object> { ["trace_id"] = TraceId, ["spans"] = all });
        }

        public void Dispose() => Finish();

        internal void RecordHttpSpan(string id, string parent, string name, DateTime startedAtUtc, double durationMs, Dictionary<string, object> data)
        {
            if (_recording) Store(Build(id, parent, name, "http", startedAtUtc, durationMs, data));
        }

        private string CurrentParent()
        {
            var open = _open.Value;
            return open.Count > 0 ? open[open.Count - 1] : _rootSpanId;
        }

        // The host of an absolute URL, lowercased, without userinfo or port; null when there is none.
        // Hand-parsed for the same reason Configuration parses the DSN by hand: System.Uri has been
        // trimmed or inconsistent on some IL2CPP platforms.
        internal static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var schemeSplit = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeSplit < 0) return null;

            var authority = url.Substring(schemeSplit + 3);
            var end = authority.IndexOfAny(new[] { '/', '?', '#' });
            if (end >= 0) authority = authority.Substring(0, end);
            var at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);

            string host;
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                var close = authority.IndexOf(']');
                host = close > 0 ? authority.Substring(1, close - 1) : authority;
            }
            else
            {
                var colon = authority.IndexOf(':');
                host = colon >= 0 ? authority.Substring(0, colon) : authority;
            }
            return host.Length == 0 ? null : host.ToLowerInvariant();
        }

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

    /// <summary>
    /// One outgoing HTTP request started with <see cref="Trace.StartHttpSpan"/>: add
    /// <see cref="Headers"/> to the request, then call <see cref="Finish"/> (or dispose it) once the
    /// request has completed or failed. Safe to finish from any thread; only the first call counts.
    /// From a disabled trace it has no headers and records nothing.
    /// </summary>
    public sealed class HttpSpan : IDisposable
    {
        private readonly Trace _trace;
        private readonly string _parentSpanId;
        private readonly string _name;
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _finished;

        internal HttpSpan(Trace trace, string spanId, string parentSpanId, string name, Dictionary<string, string> headers)
        {
            _trace = trace;
            SpanId = spanId;
            _parentSpanId = parentSpanId;
            _name = name;
            Headers = headers;
        }

        /// <summary>This span's own id (16 lowercase hex characters), which <see cref="Traceparent"/> names as the parent; null from a disabled trace.</summary>
        public string SpanId { get; }

        /// <summary>The headers to add to the request: <c>traceparent</c>, or none when it shouldn't be sent.</summary>
        public Dictionary<string, string> Headers { get; }

        /// <summary>The <c>traceparent</c> header value, or null when none should be sent.</summary>
        public string Traceparent => Headers.TryGetValue(TraceParent.Header, out var value) ? value : null;

        /// <summary>
        /// Sets <see cref="Headers"/> on <paramref name="request"/>, leaving a <c>traceparent</c> the
        /// request already carries alone: whoever set it explicitly knows better than this SDK which
        /// trace the call belongs to.
        /// </summary>
        public void AddHeadersTo(UnityWebRequest request)
        {
            if (request == null || !string.IsNullOrEmpty(request.GetRequestHeader(TraceParent.Header))) return;
            foreach (var header in Headers) request.SetRequestHeader(header.Key, header.Value);
        }

        /// <summary>Records the span with its duration so far; <paramref name="data"/> is any small detail, a status code say.</summary>
        public void Finish(Dictionary<string, object> data = null)
        {
            if (_trace == null || Interlocked.Exchange(ref _finished, 1) == 1) return;
            _trace.RecordHttpSpan(SpanId, _parentSpanId, _name, _startedAt, _stopwatch.Elapsed.TotalMilliseconds, data);
        }

        public void Dispose() => Finish();
    }
}
