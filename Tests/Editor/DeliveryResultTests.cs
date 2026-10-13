using System.Collections.Generic;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class DeliveryResultTests
    {
        [TestCase(200)]
        [TestCase(202)]
        public void A_request_Unity_reports_as_successful_is_delivered(long status)
        {
            Assert.AreEqual(DeliveryResult.Delivered, DeliveryResults.FromResponse(true, status));
        }

        [TestCase(400)]
        [TestCase(401)]
        [TestCase(403)]
        [TestCase(413)]
        [TestCase(422)]
        public void A_4xx_the_server_would_only_repeat_is_rejected(long status)
        {
            Assert.AreEqual(DeliveryResult.Rejected, DeliveryResults.FromResponse(false, status));
        }

        [TestCase(0)] // no response at all: a network error or a timeout
        [TestCase(408)]
        [TestCase(429)]
        [TestCase(500)]
        [TestCase(503)]
        public void A_network_error_a_5xx_a_408_or_a_429_is_a_failure_for_now(long status)
        {
            Assert.AreEqual(DeliveryResult.Failed, DeliveryResults.FromResponse(false, status));
        }

        [Test]
        public void The_public_bool_callbacks_still_mean_delivered_or_not()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 10 });
            var completed = new List<bool>();
            for (var i = 0; i < 3; i++)
            {
                queue.Push(new Dictionary<string, object> { ["n"] = i }, DeliveryTarget.CustomMetrics, completed.Add);
            }

            foreach (var result in new[] { DeliveryResult.Delivered, DeliveryResult.Rejected, DeliveryResult.Failed })
            {
                Assert.IsTrue(queue.TryDequeueWithResult(out _, out _, out var onResult));
                onResult(result);
            }

            CollectionAssert.AreEqual(new[] { true, false, false }, completed);
        }

        [Test]
        public void A_false_through_the_public_bool_callback_is_a_failure_for_now_never_a_rejection()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 10 });
            var results = new List<DeliveryResult>();
            queue.PushWithResult(new Dictionary<string, object> { ["n"] = 1 }, DeliveryTarget.CustomMetrics, results.Add);
            queue.PushWithResult(new Dictionary<string, object> { ["n"] = 2 }, DeliveryTarget.CustomMetrics, results.Add);

            Assert.IsTrue(queue.TryDequeue(out _, out _, out var first));
            first(false);
            Assert.IsTrue(queue.TryDequeue(out _, out _, out var second));
            second(true);

            CollectionAssert.AreEqual(new[] { DeliveryResult.Failed, DeliveryResult.Delivered }, results);
        }

        [Test]
        public void A_full_queue_tells_the_result_callback_failed_since_nothing_was_sent()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 1 });
            queue.Push(new Dictionary<string, object> { ["n"] = 1 });
            var results = new List<DeliveryResult>();

            Assert.IsFalse(queue.PushWithResult(new Dictionary<string, object> { ["n"] = 2 }, DeliveryTarget.CustomMetrics, results.Add));

            CollectionAssert.AreEqual(new[] { DeliveryResult.Failed }, results);
        }
    }
}
