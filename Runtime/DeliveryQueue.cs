using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeOpsTracker.Unity
{
    /// <summary>Which ingestion endpoint a queued payload is delivered to.</summary>
    public enum DeliveryTarget
    {
        /// <summary>The DSN's own ingestion URL: an error event.</summary>
        Events,
        /// <summary>The DSN's performance_samples endpoint: one aggregate batch (see <see cref="PerformanceFlusher"/>).</summary>
        PerformanceSamples,
        /// <summary>The DSN's spans endpoint: one finished trace (see <see cref="Trace"/>).</summary>
        Spans,
        /// <summary>The DSN's custom_metrics endpoint: a batch of individual metric captures (see <see cref="MetricBuffer"/>).</summary>
        CustomMetrics,
        /// <summary>The DSN's infrastructure_metrics endpoint: a batch of infrastructure readings.</summary>
        InfrastructureMetrics,
        /// <summary>The DSN's changes endpoint: one change (see <see cref="ForgeOpsTrackerClient.RecordChange"/>).</summary>
        Changes
    }

    /// <summary>
    /// A small thread-safe bounded FIFO of payloads waiting to be delivered. Plain C#, no
    /// UnityEngine dependency: kept separate from <see cref="ForgeOpsTrackerDriver"/> (the
    /// MonoBehaviour that actually drains it) specifically so this piece is testable in EditMode
    /// without a scene or GameObject, the same "keep Unity API surface out of anything that
    /// doesn't strictly need it" split sdks/android takes between its plain-Kotlin core and its
    /// Android-only entry points.
    ///
    /// Locked, not a lock-free queue: <see cref="Push"/> can be called from any thread (see
    /// <see cref="ForgeOpsTrackerHooks"/>'s own comment on
    /// <c>Application.logMessageReceivedThreaded</c> firing on whatever thread logged), while
    /// <see cref="TryDequeue"/> only ever runs from Unity's main thread inside
    /// <see cref="ForgeOpsTrackerDriver"/>'s Update loop, since that's the only thread it's safe
    /// to start a UnityWebRequest from.
    /// </summary>
    public sealed class DeliveryQueue
    {
        private readonly object _lock = new object();
        private sealed class Item
        {
            public Dictionary<string, object> Payload;
            public DeliveryTarget Target;
            public Action<bool> OnComplete;
        }

        private readonly Queue<Item> _queue = new Queue<Item>();
        private readonly int _maxSize;
        private readonly Configuration _configuration;

        public DeliveryQueue(Configuration configuration)
        {
            _configuration = configuration;
            _maxSize = System.Math.Max(1, configuration.QueueSize);
        }

        public int Count
        {
            get { lock (_lock) return _queue.Count; }
        }

        /// <summary>
        /// Enqueues a payload, dropping it silently (never blocking or throwing) if the queue is
        /// already full: a burst of exceptions must never apply backpressure to the host game.
        /// </summary>
        public bool Push(Dictionary<string, object> payload) => Push(payload, DeliveryTarget.Events, null);

        /// <summary>
        /// Same as <see cref="Push(Dictionary{string, object})"/>, for a payload that goes to
        /// <paramref name="target"/> and whose delivery outcome the caller wants to hear about:
        /// <paramref name="onComplete"/> is invoked by whoever delivers it (with whether it
        /// succeeded), or immediately with <c>false</c> if the queue is full and the payload is
        /// dropped, so a caller waiting on it is never left hanging.
        /// </summary>
        public bool Push(Dictionary<string, object> payload, DeliveryTarget target, Action<bool> onComplete)
        {
            lock (_lock)
            {
                if (_queue.Count < _maxSize)
                {
                    _queue.Enqueue(new Item { Payload = payload, Target = target, OnComplete = onComplete });
                    return true;
                }
            }

            _configuration.Log("[ForgeOpsTracker] delivery queue full, dropping event");
            onComplete?.Invoke(false);
            return false;
        }

        public bool TryDequeue(out Dictionary<string, object> payload) => TryDequeue(out payload, out _, out _);

        public bool TryDequeue(out Dictionary<string, object> payload, out DeliveryTarget target, out Action<bool> onComplete)
        {
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    payload = null;
                    target = DeliveryTarget.Events;
                    onComplete = null;
                    return false;
                }

                var item = _queue.Dequeue();
                payload = item.Payload;
                target = item.Target;
                onComplete = item.OnComplete;
                return true;
            }
        }

        /// <summary>Test/diagnostic helper only: production code should never need to peek.</summary>
        internal List<Dictionary<string, object>> Snapshot()
        {
            lock (_lock) return _queue.Select(item => item.Payload).ToList();
        }
    }
}
