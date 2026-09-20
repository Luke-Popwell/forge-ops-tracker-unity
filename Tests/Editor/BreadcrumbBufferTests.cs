using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class BreadcrumbBufferTests
    {
        private static void Add(BreadcrumbBuffer buffer, string message) => buffer.Add(message, "custom", "info", null);

        [Test]
        public void Records_entries_in_order_with_the_wire_shape()
        {
            var buffer = new BreadcrumbBuffer(new Configuration());

            Add(buffer, "first");
            buffer.Add("second", "payment", "warning", new Dictionary<string, object> { ["order_id"] = 42 });

            var all = buffer.All();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual("first", all[0]["message"]);
            Assert.AreEqual(0, ((Dictionary<string, object>)all[0]["data"]).Count);
            Assert.AreEqual("payment", all[1]["category"]);
            Assert.AreEqual("warning", all[1]["level"]);
            Assert.AreEqual(42, ((Dictionary<string, object>)all[1]["data"])["order_id"]);
            StringAssert.EndsWith("Z", (string)all[1]["timestamp"]);
        }

        [Test]
        public void Drops_the_oldest_entries_past_MaxBreadcrumbs()
        {
            var buffer = new BreadcrumbBuffer(new Configuration { MaxBreadcrumbs = 2 });

            Add(buffer, "first");
            Add(buffer, "second");
            Add(buffer, "third");

            var all = buffer.All();
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual("second", all[0]["message"]);
            Assert.AreEqual("third", all[1]["message"]);
        }

        [Test]
        public void Re_reads_MaxBreadcrumbs_on_every_add_so_a_change_after_construction_takes_effect()
        {
            var config = new Configuration();
            var buffer = new BreadcrumbBuffer(config);
            Add(buffer, "first");

            config.MaxBreadcrumbs = 0;
            Add(buffer, "second");

            Assert.AreEqual(1, buffer.All().Count);
        }

        [Test]
        public void Records_nothing_when_TrackBreadcrumbs_is_off()
        {
            var buffer = new BreadcrumbBuffer(new Configuration { TrackBreadcrumbs = false });

            Add(buffer, "nope");

            Assert.AreEqual(0, buffer.All().Count);
        }

        [Test]
        public void Clear_empties_the_trail()
        {
            var buffer = new BreadcrumbBuffer(new Configuration());
            Add(buffer, "first");

            buffer.Clear();

            Assert.AreEqual(0, buffer.All().Count);
        }

        [Test]
        public void All_returns_a_copy_never_the_live_list()
        {
            var buffer = new BreadcrumbBuffer(new Configuration());
            Add(buffer, "first");

            var snapshot = buffer.All();
            Add(buffer, "second");

            Assert.AreEqual(1, snapshot.Count);
        }

        [Test]
        public void A_very_long_message_is_truncated()
        {
            var buffer = new BreadcrumbBuffer(new Configuration());

            Add(buffer, new string('x', 2000));

            var message = (string)buffer.All()[0]["message"];
            Assert.AreEqual(503, message.Length);
            StringAssert.EndsWith("...", message);
        }

        [Test]
        public void Is_safe_to_add_to_from_many_threads_at_once()
        {
            var buffer = new BreadcrumbBuffer(new Configuration { MaxBreadcrumbs = 50 });

            Parallel.For(0, 400, i =>
            {
                Add(buffer, "crumb " + i);
                buffer.All();
            });

            Assert.AreEqual(50, buffer.All().Count, "exactly the cap, no lost or duplicated slots");
        }
    }
}
