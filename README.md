# com.forgeops.tracker (Unity)

Unity error reporting package for a [ForgeOps](https://getforgeops.net) project.
Requires Unity 2021.3 LTS or newer.

## A note on how this SDK was built, read before using it

Unity ships as a GUI installer (Unity Hub) requiring an interactive license login, with no
scriptable, headless install path available in this environment, so there was no way to install
the Unity Editor itself here to build and run this package's real test suite against it.

To close that gap as much as honestly possible without the real Editor, this package's source was
instead checked against a hand-written stub of the exact `UnityEngine`/`UnityEngine.Networking`
API surface it uses, compiled and unit-tested for real under the plain .NET runtime available in
this environment. That caught and fixed one real, verified bug: the backtrace-parsing regex's
first draft assumed a Mono-desktop-era `Exception.StackTrace` format
(`[0x0001f] in file:line`), which a real caught exception's actual `.StackTrace` value in this
environment showed to be wrong: the real format has no bracketed offset at all
(`in file:line 42`, "line" spelled out): see `EventBuilder.cs`'s own comment on the fix. Every
pure-logic piece (`Configuration`, `PiiScrubber`, `Json`, that regex, `DeliveryQueue`'s thread
safety) was therefore genuinely exercised, not just written from documentation.

What that stub-and-test process could **not** verify, and this package's design was written from
Unity's long-stable, publicly documented behavior for instead:

- Unity's own console/log-callback stack trace format (the separate
  `Application.logMessageReceivedThreaded` path, distinct from `Exception.StackTrace` above):
  see `EventBuilder.cs`'s own doc comment.
- Whether `MonoBehaviour.Update()`, `UnityWebRequest`, coroutines, and the rest of the real engine
  runtime behave exactly as documented under an actual Editor or IL2CPP player build.
- Anything IL2CPP-specific: AOT compilation limits, whether file/line info survives a release
  build with symbols stripped, and WebGL's single-threaded execution model (this package's design
  already accounts for WebGL having no raw sockets, see "Delivery" below, but that account is
  unverified against a real WebGL build).

One more concrete gap from the same root cause: Unity generates a `.meta` file (a stable GUID plus
import settings) alongside every asset the first time it's opened in the Editor, and normally
those are committed alongside the asset itself. None exist here, since nothing in this package has
ever been opened in an Editor. Unity will generate them automatically the first time this package
is imported into a real project: expect that as a one-time, harmless diff (new `.meta` files
appearing) on first import, not a sign anything is broken.

**Before shipping this in a real project**: import the package, run its EditMode test suite (see
"Running the tests" below) inside a real Unity Editor, and smoke-test both an uncaught exception
and an explicit `CaptureException` call in at least one IL2CPP build target before relying on it in
production.

## Installation

Install it from Git through the Package Manager (Window > Package Manager > + > Add package from git URL),
or by adding it to `Packages/manifest.json` yourself:

```json
{
  "dependencies": {
    "com.forgeops.tracker": "https://github.com/Luke-Popwell/forge-ops-tracker-unity.git"
  }
}
```

To pin a release, add a tag to the URL, for example `...forge-ops-tracker-unity.git#0.7.0`. That
repository is a read-only mirror of this directory, refreshed on every release; this package is not
on a UPM registry or the Asset Store.

## Configuration

```csharp
using ForgeOpsTracker.Unity;

// Call once, as early as possible: e.g. a [RuntimeInitializeOnLoadMethod] in your own code,
// or the first line of your bootstrap scene's startup script.
ForgeOpsTrackerClient.Init(c =>
{
    c.Dsn = "https://<api_key>@your-forgeops-host/api/v1/events"; // or set FORGE_OPS_DSN
    c.EnvironmentName = "production";
});
```

`Dsn` is deliberately not exposed as an Inspector-editable field on a `ScriptableObject`: see
`Configuration.cs`'s own comment: a DSN carries a bearer credential, and a `ScriptableObject`
asset ships inside every build, including ones a player can pull apart. Set it from code, ideally
sourced from a build step or a value kept out of source control.

## What gets reported automatically, and what doesn't

**Any uncaught managed exception is reported with zero further wiring**, once `Init` has run.
Unity's own player loop wraps each frame's calls into your code (`Update`, coroutines, UI event
handlers) in a try/catch and logs whatever escapes as `LogType.Exception` rather than crashing the
player outright; this package listens for exactly that via
`Application.logMessageReceivedThreaded`. It also installs `AppDomain.UnhandledException` as a
second, belt-and-suspenders hook for a genuinely unhandled exception on a background thread your
own game code started (not a thread Unity's own player loop already protects).

**This does not cover a true native crash**: a segfault in IL2CPP-compiled code, a native
plugin, or the engine itself. There is no signal handler or platform-native crash store here;
building one would need a real per-platform native crash-reporting layer (the approach Unity's own
Cloud Diagnostics and other Unity crash-reporting SDKs take), which could not be built or verified
without real device builds in this environment. See the note at the top of this README.

For an exception you've already caught yourself and want to report explicitly (optionally with
extra context):

```csharp
try
{
    LoadSave(path);
}
catch (Exception e)
{
    ForgeOpsTrackerClient.CaptureException(e, new Dictionary<string, object>
    {
        ["save_path"] = path
    });
}
```

## Identifying users

```csharp
ForgeOpsTrackerClient.CaptureException(e, user: new Dictionary<string, object> { ["id"] = player.Id, ["email"] = player.Email });
```

Or `SetUser` to attach it to every subsequently reported error (an explicit `CaptureException`
call, or anything the automatic hooks above catch) until changed or cleared, rather than passing
it to every call by hand, e.g. right after sign-in:

```csharp
ForgeOpsTrackerClient.SetUser(new Dictionary<string, object> { ["id"] = player.Id, ["email"] = player.Email });
// on sign-out:
ForgeOpsTrackerClient.SetUser(null);
```

There's no way to automatically detect "the current player" the way a server-side web framework
with its own session/auth middleware can, so this is always manual. A game install is effectively
single-player on this device (unlike a server handling many concurrent, unrelated requests at
once), so this is a plain static field, not any kind of per-request storage. `id`/`email`/
`username` keys are all independently optional. Shows up on an issue's own detail page, and as its
own affected-users count alongside the regular event count.

## Breadcrumbs

A small, bounded trail of recent events attached to whatever gets reported next, so an issue's
detail page can show what led up to it, not just the moment it happened:

```csharp
ForgeOpsTrackerClient.AddBreadcrumb("charging card", "payment", data: new Dictionary<string, object> { ["order_id"] = orderId });
ForgeOpsTrackerClient.AddBreadcrumb("opened shop"); // category "custom", level "info"
```

Every `Debug.Log`/`Debug.LogWarning`/`Debug.LogError` is also recorded automatically (category
`"console"`, level `info`/`warning`/`error`) once `Init` has run, via the same
`Application.logMessageReceivedThreaded` hook this package already uses for uncaught exceptions.
Anything else (a scene change, a purchase, a menu screen) is one you add by hand: there's no
generic hook for it in Unity. `category`/`level` default to `"custom"`/`"info"`. Only the 30 most
recent (`Configuration.MaxBreadcrumbs`) are kept, oldest dropped first; turn all of it off with
`TrackBreadcrumbs = false`. A single log line is capped at 500 characters. `message` and `data` are
PII-scrubbed like the rest of the payload; `category`, `level`, and `timestamp` never are. Omitted
from the payload entirely when the trail is empty.

One trail shared by the whole game behind a lock, not per-thread: `logMessageReceivedThreaded`
fires on whatever thread logged, so a per-thread trail would be missing every event that happened
on a different thread than the one an error is reported from. A game install is effectively
single-player on the device (the same reasoning `SetUser`'s one shared user documents); call
`ForgeOpsTrackerClient.ClearBreadcrumbs()` to start a new logical unit of work (a new save slot,
say) with a fresh one. `AddBreadcrumb` before `Init` is recorded, not lost. An uncaught exception's
report carries the trail leading up to it, but not its own log line (that line is recorded after
the report is built, so a *later* report still shows it).

No persistence to disk: an uncaught *managed* exception runs ordinary code in the still-live
process, which builds the whole payload, breadcrumbs included; a true native crash isn't covered by
this package at all (see the note at the top), with or without breadcrumbs.

**Verification, the same caveat as the rest of this package:** the new code and its tests were
compiled and run against the hand-written `UnityEngine` stub described at the top (71 tests, 21 of
them new, all passing), not a real Editor. The tests deliberately use only real Unity API
(`Debug.Log`, `Debug.LogException`, `LogAssert.Expect`), so they should run unchanged under Unity's
own Test Runner, but the stub's `Debug.Log*` re-raising `Application.logMessageReceivedThreaded`
is a model of Unity's behavior, not a confirmation of it.

## Performance monitoring

Times whatever you wrap and reports one small aggregate per transaction (how many times it ran,
total and maximum duration) every `Configuration.PerformanceFlushIntervalSeconds` (60 by default),
for the Performance page's per-transaction table. Not one network call per timed call.

Each aggregate also carries a small latency histogram (a count per fixed latency bucket: 50, 100,
250, 500, 1000, 2500, 5000 and 10000ms, plus an overflow bucket), so ForgeOps can show an
approximate p50/p95/p99 per transaction, not just an average. Percentiles are accurate to the width
of whichever bucket a duration falls into; the SDK never stores the individual durations.

```csharp
// A scope: recorded when it is disposed, even if the block throws.
using (ForgeOpsTrackerClient.StartTransaction("Level1.Load")) { LoadLevel(); }

// Or wrap a delegate, and keep its return value:
var save = ForgeOpsTrackerClient.TimeTransaction("Save.Read", () => ReadSave(path));

// Or record a duration you measured yourself, in milliseconds:
ForgeOpsTrackerClient.RecordPerformance("Shop.Open", elapsedMs);
```

This client has no web framework integration, so **nothing is timed automatically**: you choose
what to wrap. Keep transaction names low-cardinality (`"Level1.Load"`, not one per item id): every
distinct name is its own row. Safe to call from any thread (Unity code often runs on worker
threads). Turn it off with `TrackPerformance = false`; it also does nothing when reporting isn't
enabled for the current environment.

**No thread and no timer, unlike every other client's flusher.** A `UnityWebRequest` can only be
started from Unity's main thread and needs a coroutine to drive it, so a flush can't be a call made
from a background thread. Instead a flush only builds the payload and hands it to the same delivery
queue crash reports use (tagged for the `performance_samples` endpoint, with a callback the driver
invokes once the request finishes), and the driver's `Update` decides each frame whether an interval
has elapsed. The consequence, which you should know about: **delivery happens on the driver's next
`Update`, so a flush requested as the app is suspended or quit will not be delivered**, and whatever
was tallied since the last interval tick can be lost then. Shorten `PerformanceFlushIntervalSeconds`
to lose less; `ForgeOpsTrackerClient.FlushPerformance()` queues a flush right now.

A failed delivery keeps every tally, retried once per interval (not once per frame), so the next
window just grows. What a flush delivered is *subtracted* from the tallies afterward, never the whole
set cleared: delivery takes at least a frame, so a record that lands in that window would otherwise
be silently discarded, a real bug `sdks/go` had and fixed and that `gems/forge_ops_tracker`'s
reference implementation still has. A deterministic test pins this. At most one flush is in flight
at a time.

**Verification, the same caveat as the rest of this package:** the new code and its tests (89 in
total, 18 of them new) were compiled and run against the hand-written `UnityEngine` stub described at
the top, not a real Editor. The driver's per-frame `Tick()` call and the coroutine that reports each
delivery's outcome are the parts that genuinely need one.

## Distributed tracing

One flow's own call tree (a level load, a save read, a shop opening and what it triggered), shown as
a span tree on ForgeOps. A trace is sent only when the whole flow took at least
`Configuration.TraceCaptureThresholdSeconds` (1 by default), so fast flows cost nothing on the wire.
Traces are per game; nothing is propagated across services.

```csharp
using (var trace = ForgeOpsTrackerClient.StartTrace("Level1.Load"))
{
    var save = trace.MeasureSpan("Save.Read", () => ReadSave(path), "service");
    using (trace.StartSpan("Assets.Load", "job", new Dictionary<string, object> { ["count"] = 12 })) { LoadAssets(); }

    // Something you timed yourself (kind is one of controller/service/database/redis/http/job/other):
    trace.RecordSpan("Shop.Fetch", "http", startedAtUtc, elapsedMs);
}
```

Disposing the trace finishes it, so a `using` block sends the trace even if the code inside throws.
Game code hops between Unity's main thread and worker threads or Jobs, so a `Trace` is an explicit
object you pass around or capture in a delegate, not ambient per-thread state, and it is safe to use
from any thread. Nesting is tracked per thread: a span opened by `MeasureSpan` or `StartSpan` is the
parent of any span recorded on the same thread inside it, and a span recorded from another thread
parents under the root. When tracing is off, reporting isn't enabled for the environment, or `Init`
hasn't run, `StartTrace` returns a disabled trace whose every method is a no-op (the body still runs),
so callers never null-check. `kind` outside that list is sent as `other`, since the server rejects a
whole trace over one unknown kind. A trace holds at most 500 spans.

This client has no web framework integration, so **nothing starts a trace or records a span
automatically**. Delivery is the same as everything else here: finishing a slow trace queues its
payload on the `DeliveryQueue`, and the driver sends it from Unity's main thread on a later frame (a
`UnityWebRequest` can only run there), so a trace finished as the app is suspended or quit is not
delivered. Turn the feature off with `TrackTracing = false`.

## Custom metrics and infrastructure monitoring

Two explicit calls (nothing is automatic, so there is no `TrackMetrics` flag): a business event you
name yourself, and a reading from a device or one of your own hosts.

```csharp
ForgeOpsTrackerClient.CaptureMetric("purchase");                 // value defaults to 1: a bare counter
ForgeOpsTrackerClient.CaptureMetric("iap_revenue", 4.99);        // a real magnitude; it may be negative (a refund)

ForgeOpsTrackerClient.CaptureInfrastructureMetric("frame_ms", 16.4);              // hostname defaults to ServerName
ForgeOpsTrackerClient.CaptureInfrastructureMetric("memory_mb", 812, "build-box-1");
ForgeOpsTrackerClient.FlushMetrics();                            // queue for delivery right now
```

Each capture is buffered and queued for delivery as one batch every `MetricFlushIntervalSeconds` /
`InfrastructureMetricFlushIntervalSeconds` (60 by default). As with performance monitoring, there is
no thread and no timer: the driver ticks each buffer every frame and delivery happens from its next
`Update` (a `UnityWebRequest` can only run from Unity's main thread), so a flush requested as the app
is suspended or quit is not delivered; shorten the intervals to lose less. Safe to call from any
thread. Every entry is stored as it was captured (a purchase is a row, not a running total), so a count
or sum you compute later is exact. Both are a no-op when reporting isn't enabled for the environment.

A failed delivery keeps every entry for the next flush, and an entry captured while a delivery is in
flight is kept too (the Ruby gem's own buffer loses it; a test pins this by recording between the flush
being handed to the driver and the driver reporting it delivered). Each buffer holds at most 1000
entries and drops further ones until a flush succeeds, since a plan without the feature rejects every
flush and would otherwise grow it for as long as the game runs. A NaN or infinite value is dropped at
capture: it is not valid JSON. Requires a ForgeOps plan that includes custom metrics / infrastructure
monitoring.

## `in_app` backtrace frames

A frame is marked `in_app` when its method text starts with one of
`Configuration.AppNamespacePrefixes`: e.g. `"MyGame"` for a project whose scripts are all
namespaced under `MyGame.*`. Empty by default (no frame marked `in_app`). This has to be a text
prefix check: a shipped IL2CPP build's stack trace carries no reliable assembly or file-path
identity to compare against at runtime, only whatever text survives into the trace string itself.

## Delivery

Uses `UnityWebRequest`, not `System.Net.Http.HttpClient` or a raw socket: `UnityWebRequest` is
the one HTTP client Unity documents as working identically across every platform this package
targets, including WebGL, where a build runs inside a browser sandbox with no raw socket access at
all (`UnityWebRequest` goes through the browser's own `XMLHttpRequest` there transparently;
`HttpClient` does not work on that platform). Delivery is driven by a small internal
`MonoBehaviour` (`ForgeOpsTrackerDriver`, created automatically on an invisible
`DontDestroyOnLoad` GameObject the first time `Init` runs: never place one in a scene by hand),
delivering one payload at a time from a bounded, thread-safe queue
(`Configuration.QueueSize`, default 200; a full queue drops the payload and logs, rather than
applying backpressure to the game).

## PII scrubbing

The message, backtrace, and any context you attach are scanned for likely personal data (email
addresses, formatted SSNs/credit cards, known API key/token formats, and anything under a
suspiciously-named key like `password`, `api_key`, or `ssn`) and redacted before the
payload ever leaves this process. ForgeOps itself scrubs again on arrival regardless, so this
is a second, earlier layer, not the only one. The user attached via `CaptureException`'s `user`
argument or `SetUser` above is a deliberate exception: it's never scrubbed, since redacting it
would defeat the whole point of identifying users in the first place.

To disable it:

```csharp
ForgeOpsTrackerClient.Init(c => c.ScrubPii = false);
```

## Dependencies

Zero third-party packages. HTTP delivery uses `UnityWebRequest` (part of the `com.unity.modules.unitywebrequest`
built-in module, enabled by default). JSON encoding is a small hand-rolled writer (`Json.cs`), not
Unity's built-in `JsonUtility`: `JsonUtility` cannot serialize a `Dictionary<string, object>` or
any other loosely-typed value at all (documented directly in Unity's own manual), which is exactly
the shape this client's freeform `context` field needs. Pulling in a third-party JSON package
(commonly Newtonsoft's, itself often added via Unity's own package registry) would be this
package's only dependency for a problem a ~100-line writer solves outright, so it wasn't added.

## Running the tests

This package ships EditMode tests (`Tests/Editor`, using Unity's built-in Test Framework package,
`com.unity.test-framework`) covering `Configuration`, `PiiScrubber`, `Json`, `EventBuilder`, and
`DeliveryQueue`: everything that doesn't need a live scene or a real `UnityWebRequest`. These
could not be run in this environment (see the note at the top of this README) and must be run for
real before depending on this package:

```
Window > General > Test Runner > EditMode > Run All
```

or headless from the command line:

```bash
/path/to/Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results.xml
```

There is deliberately no PlayMode test for `ForgeOpsTrackerDriver`/`Client`'s actual
`UnityWebRequest` delivery: that needs a running scene and a real local HTTP server, which was
not possible to set up and verify in this environment.
