# Changelog

## 0.19.1 (2026-10-29)

- README only, no code changes. The opening note now says what has been tested (a real Unity 6 Editor on
  macOS, in Play mode) and what hasn't yet (player builds, IL2CPP, WebGL, the Test Runner).

## 0.19.0 (2026-10-27)

- `ForgeOpsTrackerClient.Init` now logs one warning when a DSN is set but nothing will be sent: when the
  DSN isn't an `https://` address with its API key (for example, pasted with `FORGE_OPS_DSN=` in front), or
  when the environment isn't enabled (Play mode in the Editor counts as `development`). It goes through
  `Configuration.Logger`, or `Debug.LogWarning` when there is none. Before this, both were silent.

## 0.18.0 (2026-10-27)

- Every file and folder in the package now ships its `.meta` file. Unity installs a git package into a folder
  it can't write to, and ignores any asset there without one, so before this release the package installed but
  none of it loaded. Found by installing it in a real Unity Editor.

## 0.17.0 (2026-10-23)

- PII scrubbing catches card numbers however they're grouped (bare, 4-4-4-4, Amex's 4-6-5, or next to a
  security code) when the digits have a card network's prefix and pass the Luhn check, so long ids stay
  readable; IBANs, compact or in groups of four, by their own check digits; and fields named `iban`,
  `account_number`, `routing_number` or `bank_account`. The rules are in sdks/PII_SCRUBBING.md, the same in
  every SDK.

## 0.16.0 (2026-10-22)

- Strict privacy: the message fingerprint now also replaces email addresses with `<email>` and quoted
  values (`"..."`, and `'...'` when the quotes aren't part of a word) with `<str>`, before the existing
  steps. `KeyError: 'Jane Doe'` and `KeyError: 'Bob Smith'` now group together, and no guessable value sits
  behind the hash. Errors whose messages contain quotes or emails get new fingerprints once, so they open
  new issues after upgrading.

## 0.15.1 (2026-10-22)

- The README says what strict privacy does in its opening lines.

## 0.15.0 (2026-10-20)

- Strict privacy: a span name or performance sample's transaction name that is a raw path (one with no route parameter such as `:id` or `{id}`) used to keep every segment without a digit, so `/players/jane-doe` went out as written. With `StrictPrivacy` on it is now cut to its first segment, then `/:path` if there was more: `GET /players/jane-doe/12` is sent as `GET /players/:path`. A route template (`GET /levels/:id/scores`) is sent as it is. With strict privacy off, nothing changes.
- `StrictPrivacy.PathWithoutIds` is marked `[Obsolete]` in favour of the new `StrictPrivacy.StrictPath`, which it now calls.
- Verified the same way as the rest of this package: compiled and tested under plain .NET against the hand-written `UnityEngine` stub (187 tests, all passing), not a real Editor.

## 0.14.0 (2026-10-20)

- Strict privacy, for games and apps that can't let personal or health data leave the device. New `Configuration.StrictPrivacy` (off by default) sends an error as structured facts only: no message (a `message_fingerprint` computed on the device instead, so errors still group), no breadcrumbs (none are recorded), no SQL statement, no source context, the user's `id` only, and only the `context` and `tags` keys listed in the new `Configuration.PrivacyAllowedFields`, when their value is a string, number or boolean (the built-in `platform` and `unity_version` tags included). Spans are named by method and host for HTTP calls, with ids stripped from paths, and keep only `db.statement` and `db.system` as data. Both can be set with `FORGE_OPS_STRICT_PRIVACY=true` and `FORGE_OPS_PRIVACY_ALLOWED_FIELDS=error_code,feature`; a value set in `Init` wins. With it off, nothing changes. See the README's "Strict privacy" section. A transaction name that's a raw path rather than a route has its ids stripped the same way in performance samples. Span names, outbound hosts included, are read by hand rather than with a URL parser, so an address a parser would reject still loses its path.
- Verified the same way as the rest of this package: compiled and tested under plain .NET against the hand-written `UnityEngine` stub (187 tests, 13 of them new, all passing), not a real Editor.

## 0.13.0 (2026-10-13)

- A batch the server will never accept is no longer re-sent forever. Performance samples and custom and infrastructure metrics used to keep a batch after any failed delivery and send it again, with more added, at every flush. A 4xx answer other than 408 and 429 (a wrong key, a plan without the feature, a batch the server can't read or that is too large) now drops the batch at once, since the same batch would only get the same answer. Before this, a rejected metric batch also stayed in the 1000-entry buffer, so once the buffer filled, every metric captured afterward was dropped too.
- A delivery that fails for now (a network error, a timeout, a 5xx, a 408 or a 429) still keeps the batch for the next flush, but only so long: after 5 failed flushes in a row the batch is dropped and the next one starts clean. A successful flush starts the count over. The 1000-entry cap on each metric buffer is unchanged.
- Each drop is reported once through `Configuration.Logger`, for example `[ForgeOpsTracker] dropped 12 metrics: the server rejected it` or `... delivery failed 5 times in a row`, and a rejected request is logged with its status (`delivery rejected: HTTP 403`). Nothing about delivery throws.
- No API changes: `DeliveryQueue`'s public `Push` and `TryDequeue` still pass a `bool` that is `true` only for a delivered payload, and errors, traces and changes are still sent once and never retried.

## 0.12.1 (2026-10-07)

- The package is now MIT licensed: it ships `LICENSE.txt`, and `package.json` says `MIT` where it said `UNLICENSED`. No code changes.

## 0.12.0 (2026-09-29)

- SQL masking now matches the server's newer rules, so fewer values can slip through. A backslash-escaped quote no longer ends a string early (`'o\'brien'` and `E'o\'brien'` are each one `?`), a string's type prefix goes with it (`X'DEADBEEF'`, `N'...'`, `B'...'`, `U&'...'`), and hex, binary, exponent and leading-dot numbers are masked (`0x1F`, `0b101`, `3e10`, `1.5E-3`, `.5`). A `database` span whose `dbSystem` is `mysql` or `mariadb` also has its double-quoted strings masked, since those databases read `"..."` as a string, not a name. A string cut off right after a backslash is masked to the end too. Applies to database spans and to the statement sent with a database error. Like the rest of this package, verified against the hand-written `UnityEngine` stub under plain .NET, not a real Editor.

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
