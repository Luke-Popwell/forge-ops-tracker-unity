using NUnit.Framework;

namespace ForgeOpsTracker.Unity.Tests
{
    public class HistogramBucketerTests
    {
        [Test]
        public void Returns_the_smallest_boundary_a_duration_fits_under_as_a_string()
        {
            Assert.AreEqual("50", HistogramBucketer.BucketFor(10));
            Assert.AreEqual("50", HistogramBucketer.BucketFor(50));
            Assert.AreEqual("100", HistogramBucketer.BucketFor(50.5));
            Assert.AreEqual("5000", HistogramBucketer.BucketFor(4999));
        }

        [Test]
        public void Returns_inf_for_anything_larger_than_the_largest_boundary()
        {
            Assert.AreEqual("inf", HistogramBucketer.BucketFor(10001));
            Assert.AreEqual("inf", HistogramBucketer.BucketFor(1000000));
        }

        [Test]
        public void Puts_a_duration_exactly_on_a_boundary_into_that_boundarys_own_bucket()
        {
            foreach (var boundary in HistogramBucketer.BoundariesMs)
            {
                Assert.AreEqual(boundary.ToString(), HistogramBucketer.BucketFor(boundary));
            }
        }

        [Test]
        public void Boundaries_match_the_servers_HistogramPercentile()
        {
            // app/services/histogram_percentile.rb and every other SDK must agree on this exact list.
            CollectionAssert.AreEqual(new long[] { 50, 100, 250, 500, 1000, 2500, 5000, 10000 }, HistogramBucketer.BoundariesMs);
        }
    }
}
