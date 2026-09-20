using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ForgeOpsTracker.Unity.Tests
{
    // Init() itself is deliberately never called here: it creates a DontDestroyOnLoad GameObject,
    // which Unity forbids outside play mode, and none of what these tests cover needs it.
    public class ForgeOpsTrackerBreadcrumbTests
    {
        private static List<string> Messages() =>
            ForgeOpsTrackerClient.Breadcrumbs.All().Select(b => (string)b["message"]).ToList();

        [SetUp]
        public void SetUp() => ForgeOpsTrackerClient.ResetForTesting();

        [TearDown]
        public void TearDown()
        {
            ForgeOpsTrackerHooks.Uninstall();
            ForgeOpsTrackerClient.ResetForTesting();
        }

        [Test]
        public void AddBreadcrumb_defaults_to_the_custom_category_and_info_level()
        {
            ForgeOpsTrackerClient.AddBreadcrumb("something happened");

            var crumb = ForgeOpsTrackerClient.Breadcrumbs.All().Single();
            Assert.AreEqual("custom", crumb["category"]);
            Assert.AreEqual("info", crumb["level"]);
        }

        [Test]
        public void AddBreadcrumb_works_before_Init_rather_than_losing_the_breadcrumb()
        {
            ForgeOpsTrackerClient.AddBreadcrumb("too early to matter");

            CollectionAssert.AreEqual(new[] { "too early to matter" }, Messages());
        }

        [Test]
        public void ClearBreadcrumbs_empties_the_trail()
        {
            ForgeOpsTrackerClient.AddBreadcrumb("first");

            ForgeOpsTrackerClient.ClearBreadcrumbs();

            Assert.AreEqual(0, ForgeOpsTrackerClient.Breadcrumbs.All().Count);
        }

        [Test]
        public void ResetForTesting_clears_the_breadcrumb_trail()
        {
            ForgeOpsTrackerClient.AddBreadcrumb("leftover");

            ForgeOpsTrackerClient.ResetForTesting();

            Assert.AreEqual(0, ForgeOpsTrackerClient.Breadcrumbs.All().Count);
        }

        // --- The automatic console source, via ForgeOpsTrackerHooks ---

        private static (Configuration, DeliveryQueue) InstallHooks()
        {
            var config = new Configuration { Dsn = "https://key@tracker.example.com/api/v1/events", EnvironmentName = "production" };
            var queue = new DeliveryQueue(config);
            ForgeOpsTrackerHooks.Install(config, queue);
            return (config, queue);
        }

        [Test]
        public void Every_log_line_is_recorded_as_a_console_breadcrumb_with_its_level()
        {
            InstallHooks();
            LogAssert.Expect(LogType.Error, "an error line");

            Debug.Log("an info line");
            Debug.LogWarning("a warning line");
            Debug.LogError("an error line");

            var crumbs = ForgeOpsTrackerClient.Breadcrumbs.All();
            var byMessage = crumbs.ToDictionary(c => (string)c["message"], c => c);
            Assert.AreEqual("info", byMessage["an info line"]["level"]);
            Assert.AreEqual("warning", byMessage["a warning line"]["level"]);
            Assert.AreEqual("error", byMessage["an error line"]["level"]);
            Assert.AreEqual("console", byMessage["an info line"]["category"]);
        }

        [Test]
        public void An_uncaught_exceptions_report_carries_the_trail_that_led_up_to_it_but_not_its_own_log_line()
        {
            var (_, queue) = InstallHooks();
            var exception = new InvalidOperationException("boom");
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: boom");

            Debug.Log("about to fail");
            Debug.LogException(exception);

            Assert.IsTrue(queue.TryDequeue(out var payload));
            var crumbs = (List<Dictionary<string, object>>)payload["breadcrumbs"];
            CollectionAssert.Contains(crumbs.Select(c => (string)c["message"]).ToList(), "about to fail");
            CollectionAssert.DoesNotContain(crumbs.Select(c => (string)c["message"]).ToList(), "InvalidOperationException: boom");
            // ...but a later report does see it, since every log line, exceptions included, is recorded.
            CollectionAssert.Contains(Messages(), "InvalidOperationException: boom");
        }
    }
}
