using System;
using UnityEngine;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Installs the two automatic-capture hooks this client uses. Neither is a signal handler or
    /// a disk-backed crash store the way sdks/objc/sdks/swift/sdks/c/sdks/android are shaped:
    /// unlike a native crash (the process disappearing before any managed code could run), an
    /// uncaught *managed* exception in Unity does not usually kill the player outright: Unity's
    /// own player loop wraps each frame's calls into user code (Update, coroutines, UI event
    /// handlers) in a try/catch and logs whatever escapes as <c>LogType.Exception</c> rather than
    /// crashing, which is exactly the event this SDK listens for below. There is deliberately no
    /// attempt here to also catch a true native crash (a segfault in an IL2CPP-compiled method, a
    /// native plugin, or the engine itself): that would need a per-platform native crash
    /// handler (Unity's own Cloud Diagnostics / other Unity crash-reporting SDKs use exactly this
    /// approach), which could not be built or verified without a real device build in this
    /// environment; see this SDK's README for the honest statement of that gap.
    /// </summary>
    internal static class ForgeOpsTrackerHooks
    {
        private static bool _installed;
        private static Configuration _configuration;
        private static DeliveryQueue _queue;

        public static void Install(Configuration configuration, DeliveryQueue queue)
        {
            if (_installed) return;
            _installed = true;

            _configuration = configuration;
            _queue = queue;

            // logMessageReceivedThreaded (not the main-thread-only logMessageReceived) is used
            // deliberately: Unity documents this variant as firing on whatever thread actually
            // produced the log, which matters for an uncaught exception thrown from a background
            // thread the game itself started (Unity's player loop only wraps its own main-thread
            // callbacks; a raw Thread the game spawns is not automatically protected the same
            // way). DeliveryQueue.Push is thread-safe for exactly this reason.
            Application.logMessageReceivedThreaded += OnLogMessage;

            // Covers a genuinely unhandled exception that reaches the runtime's own top-level
            // handler: e.g. one thrown from a background thread with nothing observing it,
            // which on some platforms terminates the process before Unity's own log pipeline
            // above gets a chance to run at all. Belt-and-suspenders with the hook above, not a
            // replacement for it.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        /// <summary>Test-only: undoes <see cref="Install"/> so tests don't leak handlers between runs.</summary>
        internal static void Uninstall()
        {
            if (!_installed) return;
            _installed = false;

            Application.logMessageReceivedThreaded -= OnLogMessage;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            _configuration = null;
            _queue = null;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception && _configuration.IsEnabled())
            {
                try
                {
                    // Built before this exception's own log line is recorded as a breadcrumb below,
                    // so a report's trail is what led up to it, not the exception itself.
                    var payload = EventBuilder.BuildFromLogMessage(_configuration, condition, stackTrace, ForgeOpsTrackerClient.CurrentUser, ForgeOpsTrackerClient.Breadcrumbs.All());
                    _queue.Push(payload);
                }
                catch (Exception ex)
                {
                    _configuration.Log($"[ForgeOpsTracker] failed to build event from log message: {ex}");
                }
            }

            RecordConsoleBreadcrumb(condition, type);
        }

        // Every log line, exception included (so a later report still shows what was logged just
        // before it), the same console source sdks/typescript and sdks/reactnative record from
        // console.*. Never allowed to throw into Unity's own log pipeline.
        private static void RecordConsoleBreadcrumb(string condition, LogType type)
        {
            try
            {
                var level = type == LogType.Log ? "info" : type == LogType.Warning ? "warning" : "error";
                ForgeOpsTrackerClient.Breadcrumbs.Add(condition ?? string.Empty, "console", level, null);
            }
            catch (Exception ex)
            {
                _configuration.Log($"[ForgeOpsTracker] failed to record console breadcrumb: {ex}");
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            if (!_configuration.IsEnabled()) return;
            if (!(args.ExceptionObject is Exception exception)) return;

            try
            {
                var payload = EventBuilder.BuildFromException(_configuration, exception, null, ForgeOpsTrackerClient.CurrentUser, ForgeOpsTrackerClient.Breadcrumbs.All());
                _queue.Push(payload);
            }
            catch (Exception ex)
            {
                _configuration.Log($"[ForgeOpsTracker] failed to build event from unhandled exception: {ex}");
            }
        }
    }
}
