using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Collects individual CaptureMetric/CaptureInfrastructureMetric calls and periodically queues
    /// them for delivery as one batch, rather than one network call per capture. Unlike
    /// <see cref="PerformanceFlusher"/> this keeps a list of individually meaningful entries instead of
    /// summing them into buckets: a customer's own purchase or level completion is exactly the kind of
    /// thing they will want a genuinely accurate count/sum of later, so the server stores one row per
    /// entry as-is. Ported from gems/forge_ops_tracker's metric_buffer.rb and
    /// infrastructure_metric_buffer.rb, which are the same class twice; here it is one class
    /// instantiated twice, told which <see cref="DeliveryTarget"/> and flush interval to use.
    ///
    /// Three deliberate differences from the Ruby buffers:
    ///
    /// - A flush snapshots the first N entries and, on success, removes exactly those N, instead of
    ///   resetting the whole list, so an entry recorded while the request is in flight (delivery here
    ///   takes at least a frame) is kept for the next flush rather than lost.
    /// - The buffer is capped at <see cref="MaxEntries"/>, and once full further entries are dropped
    ///   until a flush succeeds: a plan without the feature answers 403 on every flush, and an
    ///   uncapped buffer would then grow for as long as the game runs. Dropping the newest rather than
    ///   the oldest keeps the entries a flush is delivering at the front of the list, which is what
    ///   makes removing exactly them afterward exact.
    /// - A NaN or infinite value is dropped at record time: <see cref="Json"/> would write it as
    ///   "NaN"/"Infinity", which is not valid JSON, and one bad entry would make the server reject the
    ///   whole batch behind it.
    ///
    /// **No thread and no timer**, like <see cref="PerformanceFlusher"/>: a flush only builds the
    /// payload and hands it to the same <see cref="DeliveryQueue"/> crash reports use, and
    /// <see cref="Tick"/>, called from <see cref="ForgeOpsTrackerDriver"/>'s Update every frame,
    /// decides when an interval has elapsed. <see cref="Record"/> can come from any thread, so the
    /// list is lock-guarded.
    /// </summary>
    public sealed class MetricBuffer
    {
        public const int MaxEntries = 1000;

        private readonly object _lock = new object();
        private readonly List<Dictionary<string, object>> _entries = new List<Dictionary<string, object>>();
        private readonly Configuration _configuration;
        private readonly Func<DeliveryQueue> _queue;
        private readonly DeliveryTarget _target;
        private readonly Func<int> _intervalSeconds;
        private readonly Stopwatch _sinceLastAttempt = Stopwatch.StartNew();
        private bool _inFlight;

        /// <param name="queue">Resolved on every flush rather than captured: the queue only exists once <see cref="ForgeOpsTrackerClient.Init"/> has run.</param>
        public MetricBuffer(Configuration configuration, Func<DeliveryQueue> queue, DeliveryTarget target, Func<int> intervalSeconds)
        {
            _configuration = configuration;
            _queue = queue;
            _target = target;
            _intervalSeconds = intervalSeconds;
        }

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>Adds one entry (everything but recorded_at, which is stamped here); returns whether it was kept.</summary>
        public bool Record(Dictionary<string, object> entry, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                _configuration.Log("[ForgeOpsTracker] dropped a metric with a non-finite value");
                return false;
            }

            lock (_lock)
            {
                if (_entries.Count >= MaxEntries)
                {
                    _configuration.Log("[ForgeOpsTracker] metric buffer full, dropping a metric");
                    return false;
                }

                entry["recorded_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                _entries.Add(entry);
                return true;
            }
        }

        /// <summary>
        /// Called every frame by the driver: starts a <see cref="Flush"/> once the interval has elapsed
        /// since the last attempt (successful or not, so a failing endpoint is retried once per
        /// interval, not once per frame). Cheap when there is nothing to do.
        /// </summary>
        public void Tick()
        {
            if (_sinceLastAttempt.Elapsed.TotalSeconds < Math.Max(1, _intervalSeconds())) return;
            Flush();
        }

        /// <summary>
        /// Snapshots the buffered entries and queues them for delivery as one batch (the driver
        /// delivers it on its next Update). A failed delivery keeps every entry, so the next flush's
        /// batch just grows. At most one flush is in flight at a time: a second call while one is
        /// pending does nothing, rather than delivering the same snapshot twice. Returns false when
        /// nothing was queued.
        /// </summary>
        public bool Flush()
        {
            _sinceLastAttempt.Restart();
            var queue = _queue();
            if (queue == null) return false;

            List<object> snapshot;
            lock (_lock)
            {
                if (_inFlight || _entries.Count == 0) return false;

                snapshot = new List<object>(_entries);
                _inFlight = true;
            }

            var count = snapshot.Count;
            var payload = new Dictionary<string, object> { ["metrics"] = snapshot };
            // Push invokes the callback itself (with false) if the queue is full, so _inFlight is
            // always cleared, whichever way this goes.
            return queue.Push(payload, _target, success => Complete(count, success));
        }

        private void Complete(int count, bool success)
        {
            lock (_lock)
            {
                _inFlight = false;
                if (!success) return;

                // Exactly the entries just delivered: anything recorded while the request was in
                // flight sits after them and stays for the next flush.
                _entries.RemoveRange(0, Math.Min(count, _entries.Count));
            }
        }

        /// <summary>Test-only: drops every entry without delivering anything.</summary>
        internal void Reset()
        {
            lock (_lock)
            {
                _entries.Clear();
                _inFlight = false;
            }
            _sinceLastAttempt.Restart();
        }
    }
}
