using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class ConfigurationTests
    {
        [Test]
        public void ApiKey_extracts_userinfo_from_dsn()
        {
            var config = new Configuration { Dsn = "https://mykey@host.example.com/api/v1/events" };
            Assert.AreEqual("mykey", config.ApiKey);
        }

        [Test]
        public void ApiKey_is_null_when_dsn_has_no_userinfo()
        {
            var config = new Configuration { Dsn = "https://host.example.com/api/v1/events" };
            Assert.IsNull(config.ApiKey);
        }

        [Test]
        public void ApiKey_is_null_when_dsn_is_unset()
        {
            var config = new Configuration { Dsn = null };
            Assert.IsNull(config.ApiKey);
        }

        [Test]
        public void ApiKey_percent_decodes_the_userinfo()
        {
            var config = new Configuration { Dsn = "https://my%40key@host.example.com/events" };
            Assert.AreEqual("my@key", config.ApiKey);
        }

        [Test]
        public void IngestionUri_strips_credentials()
        {
            var config = new Configuration { Dsn = "https://mykey@host.example.com/api/v1/events" };
            Assert.AreEqual("https://host.example.com/api/v1/events", config.IngestionUri());
        }

        [Test]
        public void IngestionUri_is_null_when_dsn_is_malformed()
        {
            var config = new Configuration { Dsn = "not-a-dsn" };
            Assert.IsNull(config.IngestionUri());
        }

        [Test]
        public void IsEnabled_requires_a_valid_dsn_and_an_enabled_environment()
        {
            var config = new Configuration
            {
                Dsn = "https://mykey@host.example.com/events",
                EnvironmentName = "production"
            };
            Assert.IsTrue(config.IsEnabled());
        }

        [Test]
        public void IsEnabled_is_false_for_an_environment_not_in_the_enabled_set()
        {
            var config = new Configuration
            {
                Dsn = "https://mykey@host.example.com/events",
                EnvironmentName = "development"
            };
            Assert.IsFalse(config.IsEnabled());
        }

        [Test]
        public void IsEnabled_is_false_with_no_dsn()
        {
            var config = new Configuration { Dsn = null, EnvironmentName = "production" };
            Assert.IsFalse(config.IsEnabled());
        }

        [Test]
        public void Log_never_throws_even_if_the_caller_supplied_logger_throws()
        {
            var config = new Configuration { Logger = _ => throw new System.Exception("boom") };
            Assert.DoesNotThrow(() => config.Log("hello"));
        }
    }
}
