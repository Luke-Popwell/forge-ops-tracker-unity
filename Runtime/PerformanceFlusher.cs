using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// Times work in-process, bucketed by transaction name (see
    /// <see cref="ForgeOpsTrackerClient.StartTransaction"/> and friends), and periodically flushes
    /// each distinct bucket as one small aggregate report, rather than one network call per timed
    /// call. Ported from gems/forge_ops_tracker/lib/forge_ops_tracker/performance_flusher.rb and
    /// sdks/go's own port of it, including the one thing both of those learned the hard way: see
    /// <see cref="Flush"/>'s own comment on why it subtracts what it delivered instead of clearing
    /// the buckets.
    ///
    /// **No thread and no timer**, unlike every other client's flusher: a <c>UnityWebRequest</c> can
    /// only be started from Unity's main thread and needs a coroutine to drive it, so a flush is not
    /// a call this class can make from a background thread. Instead <see cref="Flush"/> only builds
    /// the payload and hands it to the same <see cref="DeliveryQueue"/> crash reports use (tagged
    /// with <see cref="DeliveryTarget.PerformanceSamples"/>, plus a callback the driver invokes once
    /// the request finishes), and <see cref="Tick"/>, called from
    /// <see cref="ForgeOpsTrackerDriver"/>'s <c>Update</c> every frame, decides when an interval has
    /// elapsed. [Record] can come from any thread (Unity code often runs on worker threads), so
    /// the buckets are lock-guarded.
    /// </summary>
    public sealed class PerformanceFlusher
    {
        private sealed class Bucket
        {
            public long Count;
            public double DurationSumMs;
            public double MaxDurationMs;
            public Dictionary<string, long> Histogram = new Dictionary<string, long>();
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();
        private readonly Configuration _configuration;
        private readonly Func<DeliveryQueue> _queue;
        private readonly Stopwatch _sinceLastAttempt = Stopwatch.StartNew();
        private DateTime _periodStartedAt = DateTime.UtcNow;
        private bool _inFlight;

        /// <param name="queue">Resolved on every flush rather than captured: the queue only exists once <see cref="ForgeOpsTrackerClient.Init"/> has run.</param>
        public PerformanceFlusher(Configuration configuration, Func<DeliveryQueue> queue)
        {
            _configuration = configuration;
            _queue = queue;
        }

        /// <summary>Does nothing when <see cref="Configuration.TrackPerformance"/> is off or reporting isn't enabled for this environment.</summary>
        public void Record(string transactionName, double durationMs)
        {
            if (!_configuration.TrackPerformance || !_configuration.IsEnabled()) return;

            lock (_lock)
            {
                if (!_buckets.TryGetValue(transactionName, out var bucket))
                {
                    bucket = new Bucket();
                    _buckets[transactionName] = bucket;
                }
                bucket.Count++;
                bucket.DurationSumMs += durationMs;
                if (durationMs > bucket.MaxDurationMs) bucket.MaxDurationMs = durationMs;
                // The distribution count/sum/max can't reconstruct: see HistogramBucketer for why the
                // server approximates a percentile from these bucket counts.
                var label = HistogramBucketer.BucketFor(durationMs);
                bucket.Histogram.TryGetValue(label, out var labelCount);
                bucket.Histogram[label] = labelCount + 1;
            }
        }

        /// <summary>
        /// Called every frame by the driver: starts a <see cref="Flush"/> once
        /// <see cref="Configuration.PerformanceFlushIntervalSeconds"/> has elapsed since the last
        /// attempt (successful or not, so a failing endpoint is retried once per interval, not once
        /// per frame). Cheap when there is nothing to do.
        /// </summary>
        public void Tick()
        {
            if (_sinceLastAttempt.Elapsed.TotalSeconds < Math.Max(1, _configuration.PerformanceFlushIntervalSeconds)) return;
            Flush();
        }

        /// <summary>
        /// Snapshots the buffered buckets and queues them for delivery as one batch (the driver
        /// delivers it on its next <c>Update</c>). A failed delivery keeps every bucket where it is,
        /// so the next flush's batch just grows instead of losing what was already tallied: there's
        /// no other copy of this data anywhere.
        ///
        /// Only exactly what this snapshot delivered is removed afterward, subtracted from whatever
        /// is in each bucket by then, never the whole set cleared outright. <see cref="Record"/> can
        /// run on another thread while delivery is in flight (and delivery here takes at least a
        /// frame), so a record for a transaction already in the snapshot, or a brand-new one, can
        /// land in the exact window between the snapshot and delivery succeeding. Clearing
        /// afterward, as if delivery had covered everything now in the set, would silently discard
        /// that data forever. This is a real bug sdks/go had and fixed, and that
        /// gems/forge_ops_tracker's reference implementation still has; see this package's own
        /// test for a deterministic reproduction.
        ///
        /// At most one flush is in flight at a time: a second call while one is pending does
        /// nothing, rather than delivering the same snapshot twice. Returns false when nothing was
        /// queued.
        /// </summary>
        public bool Flush()
        {
            _sinceLastAttempt.Restart();
            var queue = _queue();
            if (queue == null) return false;

            Dictionary<string, Bucket> snapshot;
            DateTime periodStart;
            DateTime periodEnd;
            var samples = new List<Dictionary<string, object>>();
            lock (_lock)
            {
                if (_inFlight || _buckets.Count == 0) return false;

                periodStart = _periodStartedAt;
                periodEnd = DateTime.UtcNow;
                snapshot = new Dictionary<string, Bucket>(_buckets.Count);
                foreach (var entry in _buckets)
                {
                    snapshot[entry.Key] = new Bucket
                    {
                        Count = entry.Value.Count,
                        DurationSumMs = entry.Value.DurationSumMs,
                        MaxDurationMs = entry.Value.MaxDurationMs,
                        Histogram = new Dictionary<string, long>(entry.Value.Histogram)
                    };
                }
                _inFlight = true;
            }

            foreach (var entry in snapshot)
            {
                samples.Add(new Dictionary<string, object>
                {
                    ["transaction_name"] = entry.Key,
                    ["environment"] = _configuration.EnvironmentName,
                    ["release"] = _configuration.Release,
                    ["period_started_at"] = periodStart.ToString("o", CultureInfo.InvariantCulture),
                    ["period_ended_at"] = periodEnd.ToString("o", CultureInfo.InvariantCulture),
                    ["request_count"] = entry.Value.Count,
                    ["duration_sum_ms"] = entry.Value.DurationSumMs,
                    ["max_duration_ms"] = entry.Value.MaxDurationMs,
                    ["histogram"] = new Dictionary<string, long>(entry.Value.Histogram)
                });
            }

            var payload = new Dictionary<string, object> { ["samples"] = samples };
            // Push invokes the callback itself (with false) if the queue is full, so _inFlight is
            // always cleared, whichever way this goes.
            return queue.Push(payload, DeliveryTarget.PerformanceSamples, success => Complete(snapshot, periodEnd, success));
        }

        private void Complete(Dictionary<string, Bucket> snapshot, DateTime periodEnd, bool success)
        {
            lock (_lock)
            {
                _inFlight = false;
                if (!success) return;

                foreach (var entry in snapshot)
                {
                    if (!_buckets.TryGetValue(entry.Key, out var current)) continue;

                    current.Count = Math.Max(0, current.Count - entry.Value.Count);
                    current.DurationSumMs = Math.Max(0, current.DurationSumMs - entry.Value.DurationSumMs);
                    foreach (var sent in entry.Value.Histogram)
                    {
                        current.Histogram.TryGetValue(sent.Key, out var remaining);
                        remaining -= sent.Value;
                        if (remaining > 0) current.Histogram[sent.Key] = remaining;
                        else current.Histogram.Remove(sent.Key);
                    }
                    // MaxDurationMs is deliberately left as whatever is currently on the bucket,
                    // sent or not: unlike Count/DurationSumMs, a max can't be correctly
                    // "subtracted" back out (the true max of what's left is anything at or below
                    // it, not knowable from the two numbers alone), and leaving it never
                    // overstates the next period's own max, only potentially understates how far
                    // back it was actually set.
                    if (current.Count == 0) _buckets.Remove(entry.Key);
                }
                _periodStartedAt = periodEnd;
            }
        }

        /// <summary>Test-only: (count, durationSumMs, maxDurationMs) for one transaction, or null.</summary>
        internal (long Count, double DurationSumMs, double MaxDurationMs)? Tally(string transactionName)
        {
            lock (_lock)
            {
                if (!_buckets.TryGetValue(transactionName, out var bucket)) return null;
                return (bucket.Count, bucket.DurationSumMs, bucket.MaxDurationMs);
            }
        }

        /// <summary>Test-only: drops every bucket without delivering anything.</summary>
        internal void Reset()
        {
            lock (_lock)
            {
                _buckets.Clear();
                _inFlight = false;
                _periodStartedAt = DateTime.UtcNow;
            }
            _sinceLastAttempt.Restart();
        }
    }
}
