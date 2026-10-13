using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class MetricBufferTests
    {
        private static Configuration EnabledConfiguration() => new Configuration
        {
            Dsn = "https://key@tracker.example.com/api/v1/events",
            EnvironmentName = "production",
            Release = "a1b2c3d",
            ServerName = "device-1"
        };

        private static (MetricBuffer Buffer, DeliveryQueue Queue) NewBuffer(DeliveryTarget target = DeliveryTarget.CustomMetrics, List<string> log = null)
        {
            var config = EnabledConfiguration();
            if (log != null) config.Logger = log.Add;
            var queue = new DeliveryQueue(config);
            return (new MetricBuffer(config, () => queue, target, () => 3600), queue);
        }

        // Acts as the driver: takes the queued flush and reports its outcome.
        private static Dictionary<string, object> Deliver(DeliveryQueue queue, bool success, out DeliveryTarget target)
        {
            Assert.IsTrue(queue.TryDequeue(out var payload, out target, out var onComplete));
            onComplete?.Invoke(success);
            return payload;
        }

        // The same, as the real driver does it: with the full outcome, which can say "rejected".
        private static Dictionary<string, object> Deliver(DeliveryQueue queue, DeliveryResult result)
        {
            Assert.IsTrue(queue.TryDequeueWithResult(out var payload, out _, out var onResult));
            onResult?.Invoke(result);
            return payload;
        }

        private static Dictionary<string, object> Entry(string name, double value) =>
            new Dictionary<string, object> { ["metric_name"] = name, ["value"] = value };

        private static List<string> Names(Dictionary<string, object> payload) =>
            ((List<object>)payload["metrics"]).Select(e => (string)((Dictionary<string, object>)e)["metric_name"]).ToList();

        [Test]
        public void Flush_queues_every_entry_as_one_batch_for_its_own_target_stamped_with_recorded_at()
        {
            var (buffer, queue) = NewBuffer();
            Assert.IsTrue(buffer.Record(Entry("signup", 1), 1));
            Assert.IsTrue(buffer.Record(Entry("refund", -12.5), -12.5));

            Assert.IsTrue(buffer.Flush());
            var payload = Deliver(queue, true, out var target);

            Assert.AreEqual(DeliveryTarget.CustomMetrics, target);
            Assert.AreEqual(new List<string> { "signup", "refund" }, Names(payload));
            var first = (Dictionary<string, object>)((List<object>)payload["metrics"])[0];
            StringAssert.IsMatch("^\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ$", (string)first["recorded_at"]);
            Assert.AreEqual(0, buffer.Count);
        }

        [Test]
        public void Infrastructure_readings_use_their_own_target()
        {
            var (buffer, queue) = NewBuffer(DeliveryTarget.InfrastructureMetrics);
            buffer.Record(Entry("cpu", 0.4), 0.4);
            buffer.Flush();
            Deliver(queue, true, out var target);
            Assert.AreEqual(DeliveryTarget.InfrastructureMetrics, target);
        }

        [Test]
        public void Drops_NaN_and_infinite_values()
        {
            var (buffer, _) = NewBuffer();
            Assert.IsFalse(buffer.Record(Entry("nan", double.NaN), double.NaN));
            Assert.IsFalse(buffer.Record(Entry("inf", double.PositiveInfinity), double.PositiveInfinity));
            Assert.IsTrue(buffer.Record(Entry("ok", 3), 3));
            Assert.AreEqual(1, buffer.Count);
        }

        [Test]
        public void A_failed_delivery_keeps_every_entry_so_the_next_flush_carries_more()
        {
            var (buffer, queue) = NewBuffer();
            buffer.Record(Entry("a", 1), 1);

            buffer.Flush();
            Deliver(queue, false, out _);
            Assert.AreEqual(1, buffer.Count);

            buffer.Record(Entry("b", 2), 2);
            buffer.Flush();
            var payload = Deliver(queue, true, out _);

            Assert.AreEqual(new List<string> { "a", "b" }, Names(payload));
            Assert.AreEqual(0, buffer.Count);
        }

        [Test]
        public void A_rejected_batch_is_dropped_never_sent_again_and_leaves_room_for_new_entries()
        {
            var log = new List<string>();
            var (buffer, queue) = NewBuffer(log: log);
            for (var i = 0; i < MetricBuffer.MaxEntries; i++) buffer.Record(Entry("a", 1), 1);
            Assert.IsFalse(buffer.Record(Entry("refused", 1), 1), "full");

            buffer.Flush();
            Deliver(queue, DeliveryResult.Rejected);

            Assert.AreEqual(0, buffer.Count);
            Assert.IsFalse(buffer.Flush(), "nothing left to send");
            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(1, log.Count(line => line.Contains("dropped 1000 metrics: the server rejected it")));

            Assert.IsTrue(buffer.Record(Entry("b", 2), 2), "room again");
            buffer.Flush();
            Assert.AreEqual(new List<string> { "b" }, Names(Deliver(queue, DeliveryResult.Delivered)));
        }

        [Test]
        public void The_fifth_failed_flush_in_a_row_drops_the_batch()
        {
            var log = new List<string>();
            var (buffer, queue) = NewBuffer(log: log);
            buffer.Record(Entry("a", 1), 1);

            for (var i = 0; i < 4; i++)
            {
                Assert.IsTrue(buffer.Flush());
                Deliver(queue, DeliveryResult.Failed);
            }
            Assert.AreEqual(1, buffer.Count, "four failures in: still kept");
            Assert.AreEqual(0, log.Count(line => line.Contains("dropped")));

            Assert.IsTrue(buffer.Flush());
            Deliver(queue, DeliveryResult.Failed);

            Assert.AreEqual(0, buffer.Count);
            Assert.IsFalse(buffer.Flush(), "nothing left to send");
            Assert.AreEqual(1, log.Count(line => line.Contains("dropped 1 metrics: delivery failed 5 times in a row")));
        }

        [Test]
        public void A_delivered_flush_starts_the_count_of_failed_flushes_over()
        {
            var (buffer, queue) = NewBuffer();
            buffer.Record(Entry("a", 1), 1);
            for (var i = 0; i < 4; i++)
            {
                buffer.Flush();
                Deliver(queue, DeliveryResult.Failed);
            }
            buffer.Flush();
            Deliver(queue, DeliveryResult.Delivered);

            buffer.Record(Entry("b", 2), 2);
            for (var i = 0; i < 4; i++)
            {
                buffer.Flush();
                Deliver(queue, DeliveryResult.Failed);
            }
            Assert.AreEqual(1, buffer.Count, "only four failures since the success");

            buffer.Flush();
            Deliver(queue, DeliveryResult.Failed);
            Assert.AreEqual(0, buffer.Count);
        }

        [Test]
        public void An_entry_recorded_during_delivery_is_never_lost()
        {
            var (buffer, queue) = NewBuffer();
            buffer.Record(Entry("first", 1), 1);

            buffer.Flush();
            Assert.IsTrue(queue.TryDequeue(out _, out _, out var onComplete)); // the driver has it, mid-delivery
            buffer.Record(Entry("during", 2), 2);
            onComplete(true);

            Assert.AreEqual(1, buffer.Count);
            buffer.Flush();
            Assert.AreEqual(new List<string> { "during" }, Names(Deliver(queue, true, out _)));
        }

        [Test]
        public void At_most_one_flush_is_in_flight_at_a_time()
        {
            var (buffer, queue) = NewBuffer();
            buffer.Record(Entry("a", 1), 1);

            Assert.IsTrue(buffer.Flush());
            Assert.IsFalse(buffer.Flush(), "a second flush while one is pending does nothing");
            Assert.AreEqual(1, queue.Count);
        }

        [Test]
        public void Is_capped_and_drops_further_entries_until_a_flush_succeeds()
        {
            var (buffer, _) = NewBuffer();
            var accepted = 0;
            for (var i = 0; i < MetricBuffer.MaxEntries + 50; i++)
            {
                if (buffer.Record(Entry("m", 1), 1)) accepted++;
            }
            Assert.AreEqual(MetricBuffer.MaxEntries, accepted);
        }

        [Test]
        public void Flush_does_nothing_when_there_is_nothing_to_send_or_no_queue_exists_yet()
        {
            var (empty, queue) = NewBuffer();
            Assert.IsFalse(empty.Flush());
            Assert.AreEqual(0, queue.Count);

            var beforeInit = new MetricBuffer(EnabledConfiguration(), () => null, DeliveryTarget.CustomMetrics, () => 3600);
            beforeInit.Record(Entry("x", 1), 1);
            Assert.IsFalse(beforeInit.Flush());
            Assert.AreEqual(1, beforeInit.Count, "kept for when the queue exists");
        }

        [Test]
        public void The_metric_uris_swap_the_trailing_events_segment()
        {
            var config = EnabledConfiguration();
            Assert.AreEqual("https://tracker.example.com/api/v1/custom_metrics", config.CustomMetricsUri());
            Assert.AreEqual("https://tracker.example.com/api/v1/infrastructure_metrics", config.InfrastructureMetricsUri());
        }

        [Test]
        public void A_batch_encodes_as_valid_json()
        {
            var (buffer, queue) = NewBuffer();
            buffer.Record(Entry("with \"quotes\"", 1.5), 1.5);
            buffer.Flush();
            var json = Json.Encode(Deliver(queue, true, out _));
            StringAssert.StartsWith("{\"metrics\":[{\"metric_name\":\"with \\\"quotes\\\"\",\"value\":1.5", json);
        }
    }
}
