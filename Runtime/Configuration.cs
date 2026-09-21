using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Holds a single ForgeOps DSN plus everything else the client needs to build and deliver
    /// events. Mirrors gems/forge_ops_tracker/lib/forge_ops_tracker/configuration.rb and
    /// sdks/dotnet's own Configuration: a single DSN string carries both the
    /// ingestion URL and the project's api_key: "https://&lt;api_key&gt;@host/api/v1/events".
    ///
    /// A plain C# class, not a ScriptableObject or MonoBehaviour: there is deliberately no
    /// Inspector-editable asset for this. A DSN is effectively a bearer credential (see ApiKey
    /// below); baking it into a ScriptableObject would ship it inside every build asset bundle,
    /// including ones a player could pull apart. Set it from code instead, ideally from a build
    /// step or a value not checked into source control alongside the rest of the project.
    /// </summary>
    public sealed class Configuration
    {
        public string Dsn { get; set; } = Environment.GetEnvironmentVariable("FORGE_OPS_DSN");

        public string EnvironmentName { get; set; } =
            Environment.GetEnvironmentVariable("FORGE_OPS_ENVIRONMENT")
            ?? (Debug.isDebugBuild ? "development" : "production");

        public string Release { get; set; } =
            Environment.GetEnvironmentVariable("FORGE_OPS_RELEASE") ?? Application.version;

        public string ServerName { get; set; } = SystemInfo.deviceUniqueIdentifier;

        // Used to decide whether a backtrace frame is "in_app": a frame is in-app if its method
        // name starts with one of these prefixes. Namespace prefix, not a file-path/assembly
        // check like sdks/dotnet uses: a shipped Unity build strips file paths from IL2CPP
        // stack traces entirely (see EventBuilder's own comment), so there is no reliable path or
        // assembly identity left to check against at runtime, only whatever text survived into
        // the stack trace string itself. Empty by default (no frame marked in_app); set to your
        // own top-level namespace, e.g. new HashSet&lt;string&gt; { "MyGame" }.
        public HashSet<string> AppNamespacePrefixes { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> EnabledEnvironments { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "production", "staging" };

        public int QueueSize { get; set; } = 200;

        public int TimeoutMillis { get; set; } = 5000;

        // See PiiScrubber: redacts likely-sensitive content (emails, credit cards, known API
        // key formats, anything under a suspiciously-named key) before a payload ever leaves this
        // process. ForgeOps itself scrubs again on arrival regardless, so this is a second,
        // earlier layer, not the only one, but "on" is the only sane default.
        public bool ScrubPii { get; set; } = true;

        /// <summary>
        /// Whether <see cref="EventBuilder"/> reads a few lines of source off disk around an
        /// in-app frame's culprit line and attaches them to that frame (see
        /// EventBuilder.AttachSourceContext). Defaults to true so a snippet shows up with no
        /// extra setup, but this flag by itself isn't the real protection against literal source
        /// text leaving a player's device: every ForgeOps project has its own server-side setting
        /// that durably governs whether the server will ever actually store what a client sends,
        /// regardless of what this flag happens to be set to on any particular build. Set this to
        /// false if this package should never even attempt the disk read in the first place.
        ///
        /// In practice this only ever does anything for a frame whose recorded file is a real,
        /// openable path on the machine the code is running on: see EventBuilder's own comment
        /// on why that is true under Mono (the Editor, and some non-IL2CPP platforms) but not in a
        /// shipped IL2CPP build, which strips file paths from stack traces entirely.
        /// </summary>
        public bool CaptureSourceContext { get; set; } = true;

        /// <summary>
        /// When an exception carries the SQL behind a failed database call (attached under
        /// <c>exception.Data["forge_ops_sql"]</c>, or exposed as a Statement/Sql/CommandText
        /// property by the database library), send the names of the stored procedure, table and
        /// view that SQL touched, so an issue says where to start looking. Names are identifiers,
        /// never values, which is why this defaults on. <see cref="CaptureSqlStatement"/> is the
        /// separate, opt-in step of also sending the statement itself, with every string and number
        /// replaced by "?"; off by default because even a masked statement describes your schema,
        /// and ForgeOps' own per-project setting is what durably governs whether the server stores
        /// it. See <see cref="SqlStatement"/>.
        /// </summary>
        public bool CaptureSqlObjects { get; set; } = true;

        public bool CaptureSqlStatement { get; set; }

        /// <summary>
        /// Whether <see cref="ForgeOpsTrackerClient.AddBreadcrumb"/> records anything at all, and
        /// whether the console (log message) breadcrumb source is active. On by default, matching
        /// every other client in this repo.
        /// </summary>
        public bool TrackBreadcrumbs { get; set; } = true;

        /// <summary>How many of the most recent breadcrumbs are kept, oldest dropped first. 30, matching every other client's default.</summary>
        public int MaxBreadcrumbs { get; set; } = 30;

        /// <summary>
        /// Whether <see cref="ForgeOpsTrackerClient.RecordPerformance"/> and the transaction timers
        /// time anything at all. On by default, the same "on unless you turn it off" posture error
        /// reporting itself already has. This client has no web framework integration, so nothing is
        /// timed automatically: this only gates the manual API.
        /// </summary>
        public bool TrackPerformance { get; set; } = true;

        /// <summary>How often, in seconds, the in-process tallies are flushed as one small aggregate report, rather than one network call per timed call. 60, matching gems/forge_ops_tracker's own default.</summary>
        public int PerformanceFlushIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Whether <see cref="ForgeOpsTrackerClient.StartTrace"/> starts a trace at all, and so
        /// whether spans are recorded and slow traces sent. On by default. This client has no web
        /// framework integration, so nothing starts a trace automatically: this only gates the manual API.
        /// </summary>
        public bool TrackTracing { get; set; } = true;

        /// <summary>
        /// Seconds between flushes of the buffered <see cref="ForgeOpsTrackerClient.CaptureMetric"/> /
        /// <see cref="ForgeOpsTrackerClient.CaptureInfrastructureMetric"/> entries (see <see cref="MetricBuffer"/>).
        /// There is no TrackMetrics flag the way TrackPerformance has one: these are explicit calls the
        /// host game's own code makes, not automatic instrumentation, so there is nothing to turn off
        /// that simply not calling them doesn't already do.
        /// </summary>
        public int MetricFlushIntervalSeconds { get; set; } = 60;
        public int InfrastructureMetricFlushIntervalSeconds { get; set; } = 60;

        /// <summary>A trace is only sent when its root took at least this many seconds. 1, matching every other client's default.</summary>
        public double TraceCaptureThresholdSeconds { get; set; } = 1.0;

        public Action<string> Logger { get; set; }

        public string ApiKey
        {
            get
            {
                var parsed = ParsedDsn();
                return parsed.userInfo.Length == 0 ? null : Uri.UnescapeDataString(parsed.userInfo);
            }
        }

        /// <summary>
        /// The ingestion URL with credentials stripped out (they travel as the Authorization
        /// header instead, not embedded in the request URI).
        /// </summary>
        public string IngestionUri()
        {
            var parsed = ParsedDsn();
            return parsed.hostAndPath.Length == 0 ? null : $"{parsed.scheme}://{parsed.hostAndPath}";
        }

        /// <summary>
        /// Same derivation as <see cref="IngestionUri"/>, with the trailing "/events" swapped for
        /// "/performance_samples": one DSN, two endpoints, matching the Ruby gem's own
        /// Configuration#performance_samples_uri.
        /// </summary>
        public string PerformanceSamplesUri()
        {
            var uri = IngestionUri();
            if (uri == null) return null;
            return uri.EndsWith("/events", StringComparison.Ordinal) ? uri.Substring(0, uri.Length - "/events".Length) + "/performance_samples" : uri;
        }

        /// <summary>Same derivation again, swapping the trailing "/events" for "/custom_metrics".</summary>
        public string CustomMetricsUri() => SwapEventsSuffix("/custom_metrics");

        /// <summary>Same derivation again, swapping the trailing "/events" for "/infrastructure_metrics".</summary>
        public string InfrastructureMetricsUri() => SwapEventsSuffix("/infrastructure_metrics");

        private string SwapEventsSuffix(string replacement)
        {
            var uri = IngestionUri();
            if (uri == null) return null;
            return uri.EndsWith("/events", StringComparison.Ordinal) ? uri.Substring(0, uri.Length - "/events".Length) + replacement : uri;
        }

        /// <summary>Same derivation again, swapping the trailing "/events" for "/spans".</summary>
        public string SpansUri()
        {
            var uri = IngestionUri();
            if (uri == null) return null;
            return uri.EndsWith("/events", StringComparison.Ordinal) ? uri.Substring(0, uri.Length - "/events".Length) + "/spans" : uri;
        }

        public bool IsEnabled() =>
            !string.IsNullOrEmpty(Dsn)
            && !string.IsNullOrEmpty(ApiKey)
            && EnabledEnvironments.Contains(EnvironmentName);

        public void Log(string message)
        {
            try
            {
                Logger?.Invoke(message);
            }
            catch
            {
                // A caller-supplied logger delegate must never be able to take this client down.
            }
        }

        // Hand-parses a DSN of the form "scheme://api_key@host[:port]/path" rather than relying
        // on System.Uri: IL2CPP builds on some platforms (notably consoles and older WebGL
        // toolchains) have historically shipped a trimmed or otherwise inconsistent
        // System.Uri/UriBuilder, and a DSN's shape is simple enough that a small dependency-free
        // parser sidesteps the question entirely, the same spirit as sdks/go/sdks/rust/sdks/c's
        // own dependency-free DSN parsing.
        private (string scheme, string userInfo, string hostAndPath) ParsedDsn()
        {
            if (string.IsNullOrEmpty(Dsn)) return (string.Empty, string.Empty, string.Empty);

            var schemeSplit = Dsn.IndexOf("://", StringComparison.Ordinal);
            if (schemeSplit < 0) return (string.Empty, string.Empty, string.Empty);

            var scheme = Dsn.Substring(0, schemeSplit);
            var rest = Dsn.Substring(schemeSplit + 3);

            var atSplit = rest.IndexOf('@');
            if (atSplit < 0) return (scheme, string.Empty, rest);

            var userInfo = rest.Substring(0, atSplit);
            var hostAndPath = rest.Substring(atSplit + 1);
            return hostAndPath.Length == 0 ? (string.Empty, string.Empty, string.Empty) : (scheme, userInfo, hostAndPath);
        }
    }
}
