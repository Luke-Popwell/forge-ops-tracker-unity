using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Turns a captured error into the payload shape the ingestion API expects. Ported from
    /// gems/forge_ops_tracker/lib/forge_ops_tracker/event_builder.rb: backtrace parsing there is
    /// a simple regex over a text stack trace, which is the right model here too, unlike
    /// sdks/dotnet's structured <c>System.Diagnostics.StackTrace</c> frame walk.
    ///
    /// Deliberately not built on <c>System.Diagnostics.StackTrace</c> the way sdks/dotnet is:
    /// Unity ships two very different scripting backends (Mono, used by the Editor and some
    /// platforms, and IL2CPP, used for most release builds: iOS, consoles, and Android/WebGL by
    /// default), and IL2CPP is ahead-of-time compiled with limited reflection support, where
    /// <c>StackFrame.GetFileName()</c>/<c>GetFileLineNumber()</c> are far less reliable than under
    /// Mono. What both backends give consistently is a plain text stack trace string: either
    /// <c>Exception.StackTrace</c> for an exception a caller catches and reports explicitly, or
    /// the <c>stackTrace</c> parameter Unity's own
    /// <see cref="Application.logMessageReceivedThreaded"/> callback hands over directly for an
    /// uncaught exception it already logged. Both are parsed here with one regex.
    ///
    /// This regex's <c>Exception.StackTrace</c> branch (an "in file:line N" suffix, no Unity
    /// Editor needed to check this part: it's plain .NET/Mono runtime behavior, not Unity API
    /// surface) was verified directly against a real caught exception on the .NET runtime
    /// available in this environment, and corrected a wrong first guess in the process (assuming
    /// older Mono desktop's "[offset] in file:line" shape, which is not what actually came back:
    /// see sdks/PROGRESS.md's own note on this SDK for the fix). What could NOT be verified here,
    /// for lack of any Unity Editor install, is Unity's own separate log-callback stack trace
    /// format (the "(at file:line)" branch, used only for
    /// <see cref="Application.logMessageReceivedThreaded"/>'s uncaught-exception path) and whether
    /// file/line survive at all in an IL2CPP release build with no debug symbols. That branch is
    /// written from Unity's long-stable, publicly documented console stack trace format, the same
    /// "well-known but not independently confirmed" honesty this repo's React Native client
    /// already flags for its own Hermes backtrace format: re-verify against a real device/Editor
    /// build before depending on it for anything beyond best-effort frame text.
    ///
    /// Also attaches source context (see <c>AttachSourceContext</c> below) to an in-app frame when
    /// <see cref="Configuration.CaptureSourceContext"/> allows it and the frame's file turns out to
    /// be a real, openable path: see that method's own doc comment for exactly when that is, and
    /// isn't, the case on this platform.
    /// </summary>
    public static class EventBuilder
    {
        private const int MaxFrames = 200;

        // How many lines of source to grab on either side of a frame's culprit line (see
        // AttachSourceContext), and the longest a single captured line is allowed to be before
        // getting truncated: guards against one pathological minified/generated line ballooning
        // the payload. Matches gems/forge_ops_tracker's own CONTEXT_LINES/MAX_CONTEXT_LINE_LENGTH.
        private const int ContextLines = 5;
        private const int MaxContextLineLength = 500;

        // Identifies this client to the server's auto language-detection on the project the
        // event lands in (see Project#note_sdk_platform server-side); matches this repo's own
        // sdks/unity directory name, the same convention every other language's client follows.
        private const string SdkName = "unity";

        // Matches a line like "Namespace.Class.Method (System.String value) (at Assets/Foo.cs:42)"
        // (Unity's own log stack trace format, the "file2"/"line2" branch below: still an
        // unverified assumption, see this method's own doc) or "   at Namespace.Class.Method
        // (System.String value) in /path/Foo.cs:line 42" (the "file1"/"line1" branch). That
        // second branch's exact shape: no "[0x...]" offset marker, and "line " spelled out
        // before the number: was verified directly against a real caught exception's own
        // Exception.StackTrace on the .NET runtime available in this environment (see
        // sdks/PROGRESS.md), which corrected an earlier, wrong assumption that Mono's older
        // desktop format (with a bracketed hex offset before "in") was what Unity's own
        // Mono/IL2CPP runtimes produce too. The leading "[offset] " is still accepted, optionally,
        // in case some Unity runtime configuration does emit one. Either trailing location suffix
        // is optional outright: a release build with symbols stripped commonly has neither,
        // leaving just the method signature.
        private static readonly Regex FrameLine = new Regex(
            @"^\s*(?:at\s+)?(?<method>[^\(]+\([^\)]*\))\s*" +
            @"(?:(?:\[[^\]]*\]\s*)?in\s+(?<file1>.+):line\s+(?<line1>\d+)|\(at\s+(?<file2>[^:]+):(?<line2>\d+)\))?\s*$",
            RegexOptions.Compiled);

        public static Dictionary<string, object> BuildFromException(Configuration configuration, Exception exception, Dictionary<string, object> context = null, Dictionary<string, object> user = null, List<Dictionary<string, object>> breadcrumbs = null)
        {
            var payload = Build(
                configuration,
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message ?? string.Empty,
                exception.StackTrace ?? string.Empty,
                context,
                user,
                breadcrumbs);
            return payload;
        }

        /// <summary>
        /// Builds from the raw (condition, stackTrace) pair Unity's own
        /// <see cref="Application.logMessageReceivedThreaded"/> hands to every registered
        /// callback for an uncaught exception (LogType.Exception): <c>condition</c> is
        /// "ExceptionTypeName: message" (Unity's own formatting, not this client's).
        /// <paramref name="user"/> is whatever <see cref="ForgeOpsTrackerClient.SetUser"/> last
        /// established, if anything: there's no per-request context here to read a user off of the
        /// way a server-side framework's own middleware could, only whatever the game set ambiently.
        /// </summary>
        public static Dictionary<string, object> BuildFromLogMessage(Configuration configuration, string condition, string stackTrace, Dictionary<string, object> user = null, List<Dictionary<string, object>> breadcrumbs = null)
        {
            var colonIndex = condition.IndexOf(':');
            var exceptionClass = colonIndex > 0 ? condition.Substring(0, colonIndex).Trim() : condition;
            var message = colonIndex > 0 && colonIndex + 1 < condition.Length
                ? condition.Substring(colonIndex + 1).Trim()
                : condition;

            return Build(configuration, exceptionClass, message, stackTrace ?? string.Empty, null, user, breadcrumbs);
        }

        private static Dictionary<string, object> Build(Configuration configuration, string exceptionClass, string message, string stackTraceText, Dictionary<string, object> context, Dictionary<string, object> user, List<Dictionary<string, object>> breadcrumbs)
        {
            var payload = new Dictionary<string, object>
            {
                ["exception_class"] = exceptionClass,
                ["message"] = configuration.ScrubPii ? PiiScrubber.ScrubString(message) : message,
                ["backtrace"] = BuildBacktrace(configuration, stackTraceText),
                ["occurred_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["environment"] = configuration.EnvironmentName,
                ["release"] = configuration.Release,
                ["server_name"] = configuration.ServerName,
                ["context"] = ScrubbedContext(configuration, context),
                ["tags"] = new Dictionary<string, object>
                {
                    ["platform"] = Application.platform.ToString(),
                    ["unity_version"] = Application.unityVersion
                },
                ["sdk_name"] = SdkName
            };

            // Attached last, and never passed through PiiScrubber: a structured field the host
            // game sets deliberately via ForgeOpsTrackerClient.SetUser/CaptureException's own
            // `user` argument, not free text that could accidentally spill sensitive data, the
            // same exemption exception_class/environment/release/server_name already get above.
            // Redacting it would defeat the whole point of identifying users in the first place.
            if (user != null && user.Count > 0)
            {
                payload["user"] = user;
            }

            // Omitted entirely (never sent as an empty array) when there's nothing to report.
            if (breadcrumbs != null && breadcrumbs.Count > 0)
            {
                payload["breadcrumbs"] = ScrubbedBreadcrumbs(configuration, breadcrumbs);
            }

            return payload;
        }

        // A breadcrumb's message and data are free text (a log line, whatever the game recorded);
        // its category, level, and timestamp are structured values set deliberately, so they are
        // left alone, the same split as the top-level fields above. Always returns fresh
        // dictionaries: the caller's own entries are never mutated.
        private static List<Dictionary<string, object>> ScrubbedBreadcrumbs(Configuration configuration, List<Dictionary<string, object>> breadcrumbs)
        {
            var result = new List<Dictionary<string, object>>(breadcrumbs.Count);
            foreach (var crumb in breadcrumbs)
            {
                var copy = new Dictionary<string, object>(crumb);
                if (configuration.ScrubPii)
                {
                    if (copy.TryGetValue("message", out var message) && message is string text)
                    {
                        copy["message"] = PiiScrubber.ScrubString(text);
                    }
                    if (copy.TryGetValue("data", out var data) && data != null)
                    {
                        copy["data"] = PiiScrubber.Scrub(data) ?? new Dictionary<string, object>();
                    }
                }
                result.Add(copy);
            }
            return result;
        }

        private static Dictionary<string, object> ScrubbedContext(Configuration configuration, Dictionary<string, object> context)
        {
            if (context == null) return new Dictionary<string, object>();
            if (!configuration.ScrubPii) return new Dictionary<string, object>(context);

            return (Dictionary<string, object>)PiiScrubber.Scrub(context) ?? new Dictionary<string, object>();
        }

        private static List<Dictionary<string, object>> BuildBacktrace(Configuration configuration, string stackTraceText)
        {
            var frames = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(stackTraceText)) return frames;

            foreach (var rawLine in stackTraceText.Split('\n'))
            {
                if (frames.Count >= MaxFrames) break;

                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;

                var match = FrameLine.Match(line);
                var method = match.Success ? match.Groups["method"].Value.Trim() : line.Trim();
                var file = FirstNonEmpty(match, "file1", "file2");
                var lineNumberText = FirstNonEmpty(match, "line1", "line2");
                var lineNumber = lineNumberText == null ? (int?)null : int.Parse(lineNumberText, CultureInfo.InvariantCulture);
                var inApp = IsInApp(configuration, method);

                var frame = new Dictionary<string, object>
                {
                    ["file"] = file == null ? null : (configuration.ScrubPii ? PiiScrubber.ScrubString(file) : file),
                    ["line"] = (object)lineNumber,
                    ["method"] = configuration.ScrubPii ? PiiScrubber.ScrubString(method) : method,
                    ["in_app"] = inApp
                };

                // Uses the raw, not-yet-scrubbed `file`/`lineNumber` to actually open the file:
                // scrubbing only ever rewrites the *reported* path text, never the real one this
                // process can still read from disk.
                AttachSourceContext(configuration, frame, file, lineNumber, inApp);

                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>
        /// Reads a few lines of source straight off disk around a frame's culprit line, the same
        /// way gems/forge_ops_tracker's own EventBuilder#attach_source_context does. Gated on
        /// three things: <see cref="Configuration.CaptureSourceContext"/> has to be on, the frame
        /// has to be in-app (never a third-party dependency: there'd be nothing meaningful to
        /// show, and it isn't the host project's own code), and <paramref name="file"/>/
        /// <paramref name="line"/> both have to be present at all.
        ///
        /// That third condition is what actually decides whether this ever does anything on a
        /// given platform: a caught exception's <c>Exception.StackTrace</c> under Mono (the Editor,
        /// Play mode, and some non-IL2CPP platforms) carries a real absolute path to the .cs file
        /// on the machine that compiled it, which is genuinely openable right there: verified
        /// directly against a real caught exception's own <c>.StackTrace</c> value on the .NET
        /// runtime available in this environment (see this class's own top comment). A shipped
        /// IL2CPP build with symbols stripped emits no file/line at all for most frames, so `file`
        /// is null there and this returns immediately without ever attempting a read: there is no
        /// on-device source tree to read from in that case regardless.
        ///
        /// Best-effort: any failure reading the file (missing, permission denied, an odd path from
        /// a platform this wasn't verified against) just means this one frame gets no source
        /// context, never an exception thrown back into the host game.
        /// </summary>
        private static void AttachSourceContext(Configuration configuration, Dictionary<string, object> frame, string file, int? line, bool inApp)
        {
            if (!configuration.CaptureSourceContext || !inApp || file == null || line == null)
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(file);
                var index = line.Value - 1;
                if (index < 0 || index >= lines.Length)
                {
                    return;
                }

                var from = Math.Max(index - ContextLines, 0);
                var to = Math.Min(index + ContextLines, lines.Length - 1);

                var preContext = new List<string>();
                for (var i = from; i < index; i++)
                {
                    preContext.Add(ScrubbedContextLine(configuration, lines[i]));
                }

                var postContext = new List<string>();
                for (var i = index + 1; i <= to; i++)
                {
                    postContext.Add(ScrubbedContextLine(configuration, lines[i]));
                }

                frame["context_line"] = ScrubbedContextLine(configuration, lines[index]);
                frame["pre_context"] = preContext;
                frame["post_context"] = postContext;
            }
            catch (IOException)
            {
                // File missing, moved, or otherwise unreadable: leave the frame as-is.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (ArgumentException)
            {
                // A malformed path (bad characters, etc.): nothing to read either way.
            }
            catch (NotSupportedException)
            {
            }
        }

        private static string ScrubbedContextLine(Configuration configuration, string line)
        {
            var truncated = TruncateLine(line);
            return configuration.ScrubPii ? PiiScrubber.ScrubString(truncated) : truncated;
        }

        private static string TruncateLine(string line)
        {
            return line.Length <= MaxContextLineLength ? line : line.Substring(0, MaxContextLineLength) + "...";
        }

        private static string FirstNonEmpty(Match match, string groupA, string groupB)
        {
            if (match.Groups[groupA].Success) return match.Groups[groupA].Value;
            if (match.Groups[groupB].Success) return match.Groups[groupB].Value;
            return null;
        }

        private static bool IsInApp(Configuration configuration, string method)
        {
            foreach (var prefix in configuration.AppNamespacePrefixes)
            {
                if (method.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}
