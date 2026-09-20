using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class PerformanceFlusherTests
    {
        private static Configuration EnabledConfiguration(Action<Configuration> configure = null)
        {
            var config = new Configuration
            {
                Dsn = "https://key@tracker.example.com/api/v1/events",
                EnvironmentName = "production",
                Release = "a1b2c3d",
                PerformanceFlushIntervalSeconds = 3600
            };
            configure?.Invoke(config);
            return config;
        }

        private static (PerformanceFlusher Flusher, DeliveryQueue Queue) NewFlusher(Configuration config = null)
        {
            config = config ?? EnabledConfiguration();
            var queue = new DeliveryQueue(config);
            return (new PerformanceFlusher(config, () => queue), queue);
        }

        // Acts as the driver: takes the queued flush and reports its outcome.
        private static Dictionary<string, object> Deliver(DeliveryQueue queue, bool success, out DeliveryTarget target)
        {
            Assert.IsTrue(queue.TryDequeue(out var payload, out target, out var onComplete));
            onComplete?.Invoke(success);
            return payload;
        }

        private static Dictionary<string, object> FirstSample(Dictionary<string, object> payload) =>
            ((List<Dictionary<string, object>>)payload["samples"])[0];

        [Test]
        public void Record_buckets_by_transaction_name_with_count_sum_and_max()
        {
            var (flusher, _) = NewFlusher();

            flusher.Record("Level1.Load", 10);
            flusher.Record("Level1.Load", 30);
            flusher.Record("Shop.Open", 5);

            Assert.AreEqual((2L, 40.0, 30.0), flusher.Tally("Level1.Load").Value);
            Assert.AreEqual((1L, 5.0, 5.0), flusher.Tally("Shop.Open").Value);
        }

        [Test]
        public void Record_does_nothing_when_TrackPerformance_is_off_or_the_environment_is_not_enabled()
        {
            var (off, _) = NewFlusher(EnabledConfiguration(c => c.TrackPerformance = false));
            off.Record("x", 10);
            Assert.IsNull(off.Tally("x"));

            var (development, _) = NewFlusher(EnabledConfiguration(c => c.EnvironmentName = "development"));
            development.Record("x", 10);
            Assert.IsNull(development.Tally("x"));
        }

        [Test]
        public void Flush_queues_one_batch_for_the_performance_samples_endpoint()
        {
            var (flusher, queue) = NewFlusher();
            flusher.Record("Level1.Load", 10);
            flusher.Record("Level1.Load", 30);

            Assert.IsTrue(flusher.Flush());

            var payload = Deliver(queue, true, out var target);
            Assert.AreEqual(DeliveryTarget.PerformanceSamples, target);
            var sample = FirstSample(payload);
            Assert.AreEqual("Level1.Load", sample["transaction_name"]);
            Assert.AreEqual(2L, sample["request_count"]);
            Assert.AreEqual(40.0, sample["duration_sum_ms"]);
            Assert.AreEqual(30.0, sample["max_duration_ms"]);
            Assert.AreEqual("production", sample["environment"]);
            Assert.AreEqual("a1b2c3d", sample["release"]);
            StringAssert.EndsWith("Z", (string)sample["period_started_at"]);
            Assert.IsNull(flusher.Tally("Level1.Load"), "delivered, so emptied");
        }

        [Test]
        public void Flush_does_nothing_when_there_is_nothing_to_send_or_no_queue_exists_yet()
        {
            var (empty, queue) = NewFlusher();
            Assert.IsFalse(empty.Flush());
            Assert.AreEqual(0, queue.Count);

            var beforeInit = new PerformanceFlusher(EnabledConfiguration(), () => null);
            beforeInit.Record("x", 10);
            Assert.IsFalse(beforeInit.Flush());
            Assert.IsNotNull(beforeInit.Tally("x"), "kept for when the queue exists");
        }

        [Test]
        public void A_failed_delivery_keeps_every_bucket_so_the_next_flush_carries_more()
        {
            var (flusher, queue) = NewFlusher();
            flusher.Record("x", 10);

            flusher.Flush();
            Deliver(queue, false, out _);
            Assert.AreEqual((1L, 10.0, 10.0), flusher.Tally("x").Value);

            flusher.Record("x", 20);
            flusher.Flush();
            var payload = Deliver(queue, true, out _);

            Assert.AreEqual(2L, FirstSample(payload)["request_count"]);
            Assert.IsNull(flusher.Tally("x"));
        }

        [Test]
        public void A_record_that_lands_during_delivery_is_never_lost()
        {
            // Deterministic reproduction of the race Flush's own comment describes: delivery here
            // takes at least a frame, so the test records strictly between the flush being queued
            // and the driver reporting it delivered.
            var (flusher, queue) = NewFlusher();
            flusher.Record("x", 10);

            flusher.Flush();
            Assert.IsTrue(queue.TryDequeue(out _, out _, out var onComplete)); // the driver has it, mid-delivery

            flusher.Record("x", 25); // same transaction, mid-delivery
            flusher.Record("new", 7); // a brand-new one, mid-delivery
            onComplete(true);

            Assert.AreEqual((1L, 25.0, 25.0), flusher.Tally("x").Value);
            Assert.AreEqual((1L, 7.0, 7.0), flusher.Tally("new").Value);
        }

        [Test]
        public void A_second_flush_while_one_is_in_flight_does_nothing_rather_than_delivering_the_same_snapshot_twice()
        {
            var (flusher, queue) = NewFlusher();
            flusher.Record("x", 10);

            Assert.IsTrue(flusher.Flush());
            Assert.IsFalse(flusher.Flush());

            Assert.AreEqual(1, queue.Count);
        }

        [Test]
        public void A_full_queue_drops_the_flush_but_never_leaves_it_stuck_in_flight()
        {
            var config = EnabledConfiguration(c => c.QueueSize = 1);
            var (flusher, queue) = NewFlusher(config);
            queue.Push(new Dictionary<string, object> { ["filler"] = 1 });
            flusher.Record("x", 10);

            Assert.IsFalse(flusher.Flush());
            Assert.IsNotNull(flusher.Tally("x"), "kept: it was never delivered");

            queue.TryDequeue(out _); // room again
            Assert.IsTrue(flusher.Flush(), "and a later flush is not blocked by the dropped one");
        }

        [Test]
        public void Tick_flushes_once_the_interval_has_elapsed_and_not_before()
        {
            var (flusher, queue) = NewFlusher(EnabledConfiguration(c => c.PerformanceFlushIntervalSeconds = 1));
            flusher.Record("x", 10);

            flusher.Tick();
            Assert.AreEqual(0, queue.Count, "an interval has not elapsed yet");

            Thread.Sleep(1200);
            flusher.Tick();
            Assert.AreEqual(1, queue.Count);
        }

        [Test]
        public void Tick_after_a_failed_delivery_waits_a_full_interval_rather_than_retrying_every_frame()
        {
            var (flusher, queue) = NewFlusher(EnabledConfiguration(c => c.PerformanceFlushIntervalSeconds = 1));
            flusher.Record("x", 10);
            Thread.Sleep(1200);
            flusher.Tick();
            Deliver(queue, false, out _);

            for (var frame = 0; frame < 50; frame++) flusher.Tick();

            Assert.AreEqual(0, queue.Count);
        }

        [Test]
        public void Record_is_safe_to_call_from_many_threads_at_once()
        {
            var (flusher, _) = NewFlusher();

            System.Threading.Tasks.Parallel.For(0, 1000, i => flusher.Record("x", 1));

            Assert.AreEqual(1000L, flusher.Tally("x").Value.Count);
        }

        [Test]
        public void PerformanceSamplesUri_swaps_the_trailing_events_segment()
        {
            var config = new Configuration { Dsn = "https://key@tracker.example.com/api/v1/events" };

            Assert.AreEqual("https://tracker.example.com/api/v1/performance_samples", config.PerformanceSamplesUri());
            Assert.IsNull(new Configuration { Dsn = null }.PerformanceSamplesUri());
        }

        // The latency histogram: what the server reads a percentile from.

        private static Dictionary<string, long> HistogramOf(Dictionary<string, object> sample) =>
            (Dictionary<string, long>)sample["histogram"];

        [Test]
        public void Flush_delivers_a_latency_histogram_per_bucket_alongside_count_sum_and_max()
        {
            var (flusher, queue) = NewFlusher();
            foreach (var duration in new[] { 10.0, 40.0, 120.0, 700.0, 12000.0 }) flusher.Record("GET /posts", duration);

            flusher.Flush();
            var sample = FirstSample(Deliver(queue, true, out _));

            CollectionAssert.AreEquivalent(
                new Dictionary<string, long> { ["50"] = 2, ["250"] = 1, ["1000"] = 1, ["inf"] = 1 },
                HistogramOf(sample));
            Assert.AreEqual(5L, sample["request_count"]);
        }

        [Test]
        public void The_histogram_serializes_as_a_json_object_keyed_by_bucket_label()
        {
            var (flusher, queue) = NewFlusher();
            flusher.Record("GET /posts", 70);

            flusher.Flush();
            var json = Json.Encode(Deliver(queue, true, out _));

            StringAssert.Contains("\"histogram\":{\"100\":1}", json);
        }

        [Test]
        public void A_failed_delivery_keeps_histogram_counts_for_the_next_flush()
        {
            var (flusher, queue) = NewFlusher();
            flusher.Record("GET /posts", 10);
            flusher.Flush();
            Deliver(queue, false, out _);

            flusher.Record("GET /posts", 300);
            flusher.Flush();
            var sample = FirstSample(Deliver(queue, true, out _));

            CollectionAssert.AreEquivalent(new Dictionary<string, long> { ["50"] = 1, ["500"] = 1 }, HistogramOf(sample));
        }

        [Test]
        public void A_histogram_count_recorded_during_delivery_is_sent_on_the_next_flush()
        {
            var (flusher, queue) = NewFlusher();
            flusher.Record("GET /posts", 10);

            flusher.Flush();
            Assert.IsTrue(queue.TryDequeue(out var first, out _, out var onComplete)); // the driver has it, mid-delivery
            flusher.Record("GET /posts", 300); // same transaction, mid-delivery
            flusher.Record("GET /new", 5); // a brand-new one, mid-delivery
            onComplete(true);
            CollectionAssert.AreEquivalent(new Dictionary<string, long> { ["50"] = 1 }, HistogramOf(FirstSample(first)));

            flusher.Flush();
            var samples = (List<Dictionary<string, object>>)Deliver(queue, true, out _)["samples"];
            foreach (var sample in samples)
            {
                var expected = (string)sample["transaction_name"] == "GET /posts" ? "500" : "50";
                CollectionAssert.AreEquivalent(new Dictionary<string, long> { [expected] = 1 }, HistogramOf(sample));
            }
            Assert.AreEqual(2, samples.Count);
        }

        // --- The timing helpers, recording through a flusher the test controls ---

        [Test]
        public void StartTransaction_records_how_long_the_scope_lived_and_only_once_however_often_it_is_disposed()
        {
            var (flusher, _) = NewFlusher();

            var scope = ForgeOpsTrackerClient.StartTransaction("timed", flusher.Record);
            Thread.Sleep(30);
            scope.Dispose();
            scope.Dispose();

            var tally = flusher.Tally("timed").Value;
            Assert.AreEqual(1L, tally.Count);
            Assert.GreaterOrEqual(tally.DurationSumMs, 25.0);
        }

        [Test]
        public void TimeTransaction_returns_the_bodys_value_and_records_it()
        {
            var (flusher, _) = NewFlusher();

            var value = ForgeOpsTrackerClient.TimeTransaction("timed", flusher.Record, () => 42);

            Assert.AreEqual(42, value);
            Assert.AreEqual(1L, flusher.Tally("timed").Value.Count);
        }

        [Test]
        public void TimeTransaction_records_even_when_the_body_throws_and_lets_it_propagate()
        {
            var (flusher, _) = NewFlusher();

            Assert.Throws<InvalidOperationException>(() =>
                ForgeOpsTrackerClient.TimeTransaction("timed throws", flusher.Record, () => throw new InvalidOperationException("inside")));

            Assert.AreEqual(1L, flusher.Tally("timed throws").Value.Count);
        }

        [Test]
        public void A_using_block_over_StartTransaction_records_even_when_it_throws()
        {
            var (flusher, _) = NewFlusher();

            Assert.Throws<InvalidOperationException>(() =>
            {
                using (ForgeOpsTrackerClient.StartTransaction("timed using", flusher.Record))
                {
                    throw new InvalidOperationException("inside");
                }
            });

            Assert.AreEqual(1L, flusher.Tally("timed using").Value.Count);
        }

        // --- DeliveryQueue's own additions ---

        [Test]
        public void DeliveryQueue_preserves_target_and_callback_and_still_serves_the_original_overloads()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 10 });
            var completed = new List<bool>();
            queue.Push(new Dictionary<string, object> { ["n"] = 1 });
            queue.Push(new Dictionary<string, object> { ["n"] = 2 }, DeliveryTarget.PerformanceSamples, completed.Add);

            Assert.IsTrue(queue.TryDequeue(out var first, out var firstTarget, out var firstCallback));
            Assert.AreEqual(1, first["n"]);
            Assert.AreEqual(DeliveryTarget.Events, firstTarget);
            Assert.IsNull(firstCallback);

            Assert.IsTrue(queue.TryDequeue(out var second));
            Assert.AreEqual(2, second["n"]);
        }

        [Test]
        public void DeliveryQueue_tells_the_callback_false_immediately_when_it_drops_a_full_queue_push()
        {
            var queue = new DeliveryQueue(new Configuration { QueueSize = 1 });
            queue.Push(new Dictionary<string, object> { ["n"] = 1 });
            var completed = new List<bool>();

            var accepted = queue.Push(new Dictionary<string, object> { ["n"] = 2 }, DeliveryTarget.PerformanceSamples, completed.Add);

            Assert.IsFalse(accepted);
            CollectionAssert.AreEqual(new[] { false }, completed);
        }
    }
}
