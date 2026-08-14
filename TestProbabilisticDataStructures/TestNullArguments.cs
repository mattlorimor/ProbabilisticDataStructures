using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Every public entry point that takes data rejects null.
    /// <para>
    /// This is the .NET convention, and it matters more than usual here. Without the
    /// check, a null array converts to an empty span and hashes to the same value as
    /// <see cref="Array.Empty{T}"/>, so a caller's null bug would not surface at the
    /// call site -- it would quietly insert a phantom element and produce a wrong
    /// answer later, somewhere else.
    /// </para>
    /// </summary>
    [TestClass]
    public class TestNullArguments
    {
        [TestMethod]
        public void TestFiltersRejectNullData()
        {
            var bloom = new BloomFilter(100, 0.01);
            Assert.Throws<ArgumentNullException>(() => bloom.Add(null!));
            Assert.Throws<ArgumentNullException>(() => bloom.Test(null!));
            Assert.Throws<ArgumentNullException>(() => bloom.TestAndAdd(null!));

            var bloom64 = new BloomFilter64(100, 0.01);
            Assert.Throws<ArgumentNullException>(() => bloom64.Add(null!));

            var counting = CountingBloomFilter.NewDefaultCountingBloomFilter(100, 0.01);
            Assert.Throws<ArgumentNullException>(() => counting.Add(null!));
            Assert.Throws<ArgumentNullException>(() => counting.TestAndRemove(null!));

            var partitioned = new PartitionedBloomFilter(100, 0.01);
            Assert.Throws<ArgumentNullException>(() => partitioned.Add(null!));

            var scalable = ScalableBloomFilter.NewDefaultScalableBloomFilter(0.01);
            Assert.Throws<ArgumentNullException>(() => scalable.Add(null!));

            var stable = StableBloomFilter.NewDefaultStableBloomFilter(100, 0.01);
            Assert.Throws<ArgumentNullException>(() => stable.Add(null!));

            var deletable = new DeletableBloomFilter(100, 10, 0.01);
            Assert.Throws<ArgumentNullException>(() => deletable.Add(null!));
            Assert.Throws<ArgumentNullException>(() => deletable.TestAndRemove(null!));

            var inverse = new InverseBloomFilter(100);
            Assert.Throws<ArgumentNullException>(() => inverse.Add(null!));

            var cuckoo = new CuckooBloomFilter(100, 0.01);
            Assert.Throws<ArgumentNullException>(() => cuckoo.Add(null!));
            Assert.Throws<ArgumentNullException>(() => cuckoo.TestAndRemove(null!));

            var cms = new CountMinSketch(0.001, 0.99);
            Assert.Throws<ArgumentNullException>(() => cms.Add(null!));

            var hll = HyperLogLog.NewDefaultHyperLogLog(0.01);
            Assert.Throws<ArgumentNullException>(() => hll.Add(null!));

            var topK = new TopK(0.001, 0.99, 3);
            Assert.Throws<ArgumentNullException>(() => topK.Add(null!));
        }

        /// <summary>
        /// Empty input is legitimate and distinct from null: an empty array is a
        /// value the caller meant to store, and it round-trips.
        /// </summary>
        [TestMethod]
        public void TestEmptyDataIsAcceptedAndDistinctFromNull()
        {
            var f = new BloomFilter(100, 0.01);
            var empty = Array.Empty<byte>();

            Assert.IsFalse(f.Test(empty));
            f.Add(empty);
            Assert.IsTrue(f.Test(empty), "an empty array is a storable value");

            Assert.Throws<ArgumentNullException>(() => f.Test(null!),
                "null is rejected even though empty input is accepted");
        }
    }
}
