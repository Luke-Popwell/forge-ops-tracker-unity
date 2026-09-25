using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    // RecordChange: the payload it queues for the changes endpoint, kind validation, and that it
    // never throws into the game.
    public class RecordChangeTests
    {
        // The client's Configuration is one static instance; these tests set what they need on it and
        // put it back afterward.
        private string _savedDsn;
        private string _savedEnvironment;
        private DeliveryQueue _queue;

        [SetUp]
        public void SetUp()
        {
            ForgeOpsTrackerClient.ResetForTesting();
            var config = ForgeOpsTrackerClient.Config;
            _savedDsn = config.Dsn;
            _savedEnvironment = config.EnvironmentName;
            config.Dsn = "https://key@tracker.example.com/api/v1/events";
            config.EnvironmentName = "production";
            _queue = new DeliveryQueue(config);
            ForgeOpsTrackerClient.UseQueueForTesting(_queue);
        }

        [TearDown]
        public void TearDown()
        {
            var config = ForgeOpsTrackerClient.Config;
            config.Dsn = _savedDsn;
            config.EnvironmentName = _savedEnvironment;
            ForgeOpsTrackerClient.ResetForTesting();
        }

        private Dictionary<string, object> Dequeue(out DeliveryTarget target)
        {
            Assert.IsTrue(_queue.TryDequeue(out var payload, out target, out _));
            return payload;
        }

        [Test]
        public void Queues_the_documented_shape_for_the_changes_endpoint()
        {
            var details = new Dictionary<string, object> { ["flag"] = "new_shop", ["to"] = true };
            ForgeOpsTrackerClient.RecordChange("feature_flag", "Enabled new shop", details, service: "game", actor: "luke",
                url: "https://example.com/flags/1", id: "change-1", occurredAt: new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc));

            var payload = Dequeue(out var target);
            Assert.AreEqual(DeliveryTarget.Changes, target);
            Assert.AreEqual("feature_flag", payload["kind"]);
            Assert.AreEqual("Enabled new shop", payload["title"]);
            Assert.AreEqual("production", payload["environment"]);
            Assert.AreEqual("2026-09-25T12:00:00.0000000Z", payload["occurred_at"]);
            Assert.AreSame(details, payload["details"]);
            Assert.AreEqual("game", payload["service"]);
            Assert.AreEqual("luke", payload["actor"]);
            Assert.AreEqual("https://example.com/flags/1", payload["url"]);
            Assert.AreEqual("change-1", payload["id"]);
            Assert.AreEqual("https://tracker.example.com/api/v1/changes", ForgeOpsTrackerClient.Config.ChangesUri());
        }

        [Test]
        public void Defaults_environment_and_occurred_at_and_leaves_unset_keys_out()
        {
            ForgeOpsTrackerClient.RecordChange("config", "Raised the frame cap");

            var payload = Dequeue(out _);
            CollectionAssert.AreEquivalent(new[] { "kind", "title", "environment", "occurred_at" }, payload.Keys);
            Assert.AreEqual("production", payload["environment"]);
            StringAssert.EndsWith("Z", (string)payload["occurred_at"]);
        }

        [Test]
        public void An_unknown_kind_is_sent_as_other_and_every_known_kind_is_kept()
        {
            ForgeOpsTrackerClient.RecordChange("deploy", "Unknown kind");
            ForgeOpsTrackerClient.RecordChange(null, "No kind");
            Assert.AreEqual("other", Dequeue(out _)["kind"]);
            Assert.AreEqual("other", Dequeue(out _)["kind"]);

            foreach (var kind in ForgeOpsTrackerClient.ChangeKinds)
            {
                ForgeOpsTrackerClient.RecordChange(kind, "Kind " + kind);
                Assert.AreEqual(kind, Dequeue(out _)["kind"]);
            }
            Assert.AreEqual(6, ForgeOpsTrackerClient.ChangeKinds.Count);
        }

        [Test]
        public void A_long_title_is_cut_to_200_characters_and_a_blank_one_queues_nothing()
        {
            ForgeOpsTrackerClient.RecordChange("other", new string('x', 250));
            ForgeOpsTrackerClient.RecordChange("other", "   ");
            ForgeOpsTrackerClient.RecordChange("other", null);

            Assert.AreEqual(new string('x', 200), Dequeue(out _)["title"]);
            Assert.AreEqual(0, _queue.Count);
        }

        [Test]
        public void Is_a_no_op_when_reporting_is_not_enabled_or_before_Init()
        {
            ForgeOpsTrackerClient.Config.EnvironmentName = "development";
            ForgeOpsTrackerClient.RecordChange("config", "Ignored");
            Assert.AreEqual(0, _queue.Count);

            ForgeOpsTrackerClient.Config.EnvironmentName = "production";
            ForgeOpsTrackerClient.ResetForTesting();
            Assert.DoesNotThrow(() => ForgeOpsTrackerClient.RecordChange("config", "Before Init"));
            Assert.AreEqual(0, _queue.Count);
        }

        [Test]
        public void Never_throws_when_the_queue_is_full()
        {
            for (var i = 0; i < ForgeOpsTrackerClient.Config.QueueSize; i++) ForgeOpsTrackerClient.RecordChange("config", "Fill " + i);

            Assert.DoesNotThrow(() => ForgeOpsTrackerClient.RecordChange("config", "One too many"));
            Assert.AreEqual(ForgeOpsTrackerClient.Config.QueueSize, _queue.Count);
        }
    }
}
