# Changelog

## 0.9.0

- Trace context: follow a request from the game into your backend, using the W3C Trace Context standard (`traceparent`). `Trace.StartHttpSpan(method, url)` (for a `UnityWebRequest` in a coroutine: `span.AddHeadersTo(request)`, then `span.Finish()` once it completes) and `Trace.MeasureHttpSpan(method, url, headers => ...)` (for a blocking call) record the request as an `http` span named after its method and host and hand you the `traceparent` header whose parent id is that span's own id, so the backend's root span nests under it. `AddHeadersTo` leaves a `traceparent` the request already has alone.
- Errors captured while a trace is open now carry a top-level `trace_id`, linking them to the backend error from the same request (the two projects must be linked in ForgeOps). `CaptureException` uses the most recently started trace that hasn't finished, or the one passed as its new `trace` argument; uncaught exceptions reported automatically carry it too. `ForgeOpsTrackerClient.CurrentTraceId()` returns it, and `Trace.TraceId` is public. Errors captured with no open trace are unchanged.
- New `PropagateTraces` (default true; false stops the header but still records the span) and `TracePropagationTargets` (default null, every host; otherwise host strings matched exactly or as a subdomain on a dot boundary, and `Regex`es matched against the host).
- `StartTrace` with `TrackTracing = false` now returns a trace that records and sends no spans but still has an id for errors and the header (previously a disabled trace with no id). It still returns a disabled trace when reporting isn't enabled or before `Init`.
- Trace and span ids were already W3C shaped (32 and 16 lowercase hex characters); they now come from a cryptographic random source and are never all zeros, the one value the standard reserves as invalid.

This package has earlier tagged releases on its mirror (up to 0.8.0), but no `CHANGELOG.md` existed for it before this entry; it starts here rather than backfilling every earlier version.
