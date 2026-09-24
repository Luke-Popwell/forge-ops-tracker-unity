using System.Globalization;
using System.Security.Cryptography;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Builds the W3C Trace Context <c>traceparent</c> header (https://www.w3.org/TR/trace-context/):
    /// <c>00-&lt;32 hex trace id&gt;-&lt;16 hex parent id&gt;-&lt;2 hex flags&gt;</c>, and the ids that
    /// go in it. Only ever written here, never read: a game is where a request starts, not a service
    /// one arrives at. Mirrors gems/forge_ops_tracker/lib/forge_ops_tracker/trace_parent.rb's
    /// <c>build</c>.
    /// </summary>
    internal static class TraceParent
    {
        public const string Header = "traceparent";

        // Always "01" (sampled): whether a trace is sent is only decided when it finishes, long after
        // the header has gone out, so "this may be recorded" is the only honest answer. The backend is
        // free to make its own decision either way.
        private const string SampledFlags = "01";

        private static readonly object RandomLock = new object();
        private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

        public static string Build(string traceId, string spanId) => $"00-{traceId}-{spanId}-{SampledFlags}";

        /// <summary>32 lowercase hex characters, never all zeros.</summary>
        public static string GenerateTraceId() => NonZeroHex(16);

        /// <summary>16 lowercase hex characters, never all zeros.</summary>
        public static string GenerateSpanId() => NonZeroHex(8);

        // The spec reserves all zeros as invalid, and a receiver discards a header carrying one, so
        // that one value in 2^64 (or 2^128) is drawn again rather than sent.
        private static string NonZeroHex(int bytes)
        {
            var raw = new byte[bytes];
            while (true)
            {
                lock (RandomLock) Random.GetBytes(raw);
                var nonZero = false;
                foreach (var b in raw) nonZero |= b != 0;
                if (!nonZero) continue;

                var chars = new char[bytes * 2];
                for (var i = 0; i < bytes; i++) raw[i].ToString("x2", CultureInfo.InvariantCulture).CopyTo(0, chars, i * 2, 2);
                return new string(chars);
            }
        }
    }
}
