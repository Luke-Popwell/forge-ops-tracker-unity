using System.Collections;
using UnityEngine;

namespace ForgeOpsTracker.Unity
{
    /// <summary>
    /// The one MonoBehaviour this SDK needs: exists purely to give <see cref="DeliveryQueue"/>
    /// a live Update loop to drain from and something to run <see cref="Client.DeliverCoroutine"/>
    /// on, since neither a coroutine nor <c>UnityWebRequest</c> can run without an active
    /// component driving them. Created lazily on an invisible, <c>DontDestroyOnLoad</c> GameObject
    /// the first time <see cref="ForgeOpsTrackerClient.Init"/> runs: never placed in a scene by
    /// hand.
    ///
    /// Delivers one payload at a time, matching every other SDK in this repo's own queue
    /// behavior: <see cref="Update"/> only starts a new delivery once the previous one's
    /// coroutine has actually finished, rather than firing every queued item's
    /// <c>UnityWebRequest</c> concurrently.
    /// </summary>
    internal sealed class ForgeOpsTrackerDriver : MonoBehaviour
    {
        private DeliveryQueue _queue;
        private Configuration _configuration;
        private bool _delivering;

        public static ForgeOpsTrackerDriver Create(Configuration configuration, DeliveryQueue queue)
        {
            var go = new GameObject("ForgeOpsTrackerDriver") { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(go);

            var driver = go.AddComponent<ForgeOpsTrackerDriver>();
            driver._configuration = configuration;
            driver._queue = queue;
            return driver;
        }

        private void Update()
        {
            // Decides whether a performance flush interval has elapsed and, if so, queues one: the
            // PerformanceFlusher has no thread or timer of its own (see its own comment).
            ForgeOpsTrackerClient.Performance.Tick();
            ForgeOpsTrackerClient.CustomMetrics.Tick();
            ForgeOpsTrackerClient.InfrastructureMetrics.Tick();

            if (_delivering) return;
            if (!_queue.TryDequeue(out var payload, out var target, out var onComplete)) return;

            _delivering = true;
            StartCoroutine(DeliverAndContinue(payload, target, onComplete));
        }

        private IEnumerator DeliverAndContinue(System.Collections.Generic.Dictionary<string, object> payload, DeliveryTarget target, System.Action<bool> onComplete)
        {
            yield return Client.DeliverCoroutine(_configuration, payload, success => onComplete?.Invoke(success), target);
            _delivering = false;
        }
    }
}
