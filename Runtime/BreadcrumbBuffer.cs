using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// A bounded, in-order trail of what happened right before an error, shared by the whole
    /// game (see <see cref="ForgeOpsTrackerClient.AddBreadcrumb"/>). One shared trail behind a
    /// lock, not per-thread: <c>Application.logMessageReceivedThreaded</c> (the source this
    /// package records console breadcrumbs from, and the one it builds automatic reports from) fires
    /// on whatever thread logged, so a per-thread trail would be missing every event that happened
    /// on a different thread than the one an error is reported from. A game install is effectively
    /// single-player on the device, the same reasoning <see cref="ForgeOpsTrackerClient.SetUser"/>'s
    /// one shared user already documents. Ported in shape from sdks/android's own
    /// <c>BreadcrumbBuffer</c> (also a shared, locked trail, for the same reason).
    ///
    /// Entries are plain <c>Dictionary&lt;string, object&gt;</c> in the wire shape, matching
    /// <see cref="EventBuilder"/>'s own convention (everything it builds is one), not a typed class.
    /// </summary>
    public sealed class BreadcrumbBuffer
    {
        // A single Debug.Log(hugeString) shouldn't make one breadcrumb dominate an event's payload
        // size, the same bound sdks/typescript's and sdks/reactnative's own breadcrumbs.ts apply.
        internal const int MaxMessageLength = 500;

        private readonly object _lock = new object();
        private readonly List<Dictionary<string, object>> _entries = new List<Dictionary<string, object>>();
        private readonly Configuration _configuration;

        public BreadcrumbBuffer(Configuration configuration)
        {
            _configuration = configuration;
        }

        public void Add(string message, string category, string level, Dictionary<string, object> data)
        {
            // Read fresh on every add, not captured once at construction: a change made after
            // Init should take effect on whatever's added next.
            var maxSize = Math.Max(_configuration.MaxBreadcrumbs, 0);
            if (!_configuration.TrackBreadcrumbs || maxSize == 0) return;

            var entry = new Dictionary<string, object>
            {
                ["category"] = category,
                ["message"] = message.Length > MaxMessageLength ? message.Substring(0, MaxMessageLength) + "..." : message,
                ["level"] = level,
                ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["data"] = data ?? new Dictionary<string, object>()
            };

            lock (_lock)
            {
                _entries.Add(entry);
                while (_entries.Count > maxSize)
                {
                    _entries.RemoveAt(0);
                }
            }
        }

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }

        /// <summary>A copy, not the live list: never hand out mutable internal state.</summary>
        public List<Dictionary<string, object>> All()
        {
            lock (_lock) return new List<Dictionary<string, object>>(_entries);
        }
    }
}
