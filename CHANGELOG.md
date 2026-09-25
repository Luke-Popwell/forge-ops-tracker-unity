# Changelog

## 0.11.0 (2026-09-25)

- A `database` span can now carry the SQL it ran, such as a local SQLite query: `MeasureSpan`, `StartSpan` and `RecordSpan` take optional `statement` and `dbSystem` (such as `"sqlite"`) arguments. The statement is masked on the device (every string and number literal becomes `?`), cut at 4000 characters, and sent in the span's data as `db.statement`, with `db.system` lowercased. A `db.statement` put in `data` directly is masked the same way. Both are ignored on spans of any other kind. Like the rest of this package, verified against the hand-written `UnityEngine` stub under plain .NET, not a real Editor.

## 0.10.0 (2026-09-25)

- Change tracking: `ForgeOpsTrackerClient.RecordChange(kind, title, details, environment, service, actor, url, id, occurredAt)` records one change you made (a feature flag flipped, a remote config value changed, a new content catalog rolled out) so ForgeOps can show it next to the errors and slowdowns that followed. `kind` is one of `feature_flag`, `config`, `migration`, `dependency`, `infrastructure` or `other` (`ForgeOpsTrackerClient.ChangeKinds`); anything else is sent as `other`. The title is cut to 200 characters, `environment` defaults to `EnvironmentName`, and `occurredAt` to now. Queued like an error report and delivered from the driver's next `Update` through the new `DeliveryTarget.Changes`, so it never blocks and never throws; a failed delivery, including a 403 on a plan without change tracking, is dropped quietly. A no-op before `Init` or when reporting isn't enabled. This package sends no startup snapshot of its own.

## 0.9.0

- Trace context: follow a request from the game into your backend, using the W3C Trace Context standard (`traceparent`). `Trace.StartHttpSpan(method, url)` (for a `UnityWebRequest` in a coroutine: `span.AddHeadersTo(request)`, then `span.Finish()` once it completes) and `Trace.MeasureHttpSpan(method, url, headers => ...)` (for a blocking call) record the request as an `http` span named after its method and host and hand you the `traceparent` header whose parent id is that span's own id, so the backend's root span nests under it. `AddHeadersTo` leaves a `traceparent` the request already has alone.
- Errors captured while a trace is open now carry a top-level `trace_id`, linking them to the backend error from the same request (the two projects must be linked in ForgeOps). `CaptureException` uses the most recently started trace that hasn't finished, or the one passed as its new `trace` argument; uncaught exceptions reported automatically carry it too. `ForgeOpsTrackerClient.CurrentTraceId()` returns it, and `Trace.TraceId` is public. Errors captured with no open trace are unchanged.
- New `PropagateTraces` (default true; false stops the header but still records the span) and `TracePropagationTargets` (default null, every host; otherwise host strings matched exactly or as a subdomain on a dot boundary, and `Regex`es matched against the host).
- `StartTrace` with `TrackTracing = false` now returns a trace that records and sends no spans but still has an id for errors and the header (previously a disabled trace with no id). It still returns a disabled trace when reporting isn't enabled or before `Init`.
- Trace and span ids were already W3C shaped (32 and 16 lowercase hex characters); they now come from a cryptographic random source and are never all zeros, the one value the standard reserves as invalid.

This package has earlier tagged releases on its mirror (up to 0.8.0), but no `CHANGELOG.md` existed for it before this entry; it starts here rather than backfilling every earlier version.
