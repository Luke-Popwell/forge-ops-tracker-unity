using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class DeliveryQueueTests
    {
        private static Dictionary<string, object> SamplePayload(int n) =>
            new Dictionary<string, object> { ["n"] = n };

        [Test]
        public void Push_then_TryDequeue_returns_the_same_payload_fifo()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 10 });
            queue.Push(SamplePayload(1));
            queue.Push(SamplePayload(2));

            Assert.IsTrue(queue.TryDequeue(out var first));
            Assert.AreEqual(1, first["n"]);

            Assert.IsTrue(queue.TryDequeue(out var second));
            Assert.AreEqual(2, second["n"]);
        }

        [Test]
        public void TryDequeue_returns_false_when_empty()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 10 });
            Assert.IsFalse(queue.TryDequeue(out _));
        }

        [Test]
        public void Push_drops_the_payload_once_the_queue_is_full()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 2 });

            Assert.IsTrue(queue.Push(SamplePayload(1)));
            Assert.IsTrue(queue.Push(SamplePayload(2)));
            Assert.IsFalse(queue.Push(SamplePayload(3)));

            Assert.AreEqual(2, queue.Count);
        }

        [Test]
        public void Push_is_safe_to_call_concurrently_from_multiple_threads()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 1000 });

            Parallel.For(0, 500, i => queue.Push(SamplePayload(i)));

            Assert.AreEqual(500, queue.Count);
        }
    }
}
