using System;
using System.Linq;
using System.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// What the allocation measurements the suite's budgets and margins are held to count,
    /// held to being exact while the collector is busy (#293).
    /// </summary>
    [TestClass]
    public class TestAllocationMeasurement
    {
        /// <summary>
        /// A read measured with a background collection started just before each repeat
        /// comes to what it allocates measured quietly: a collection running beside a
        /// measurement does not reach the count.
        /// </summary>
        /// <remarks>
        /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> is exact for what a thread
        /// allocates while no collection runs, but with a background collection running
        /// beside the work it reports kilobytes the work did not ask for, and a loaded run
        /// can have one running beside every repeat: <see cref="DpswSketch"/>'s large-form
        /// read, quietly 4,648,320 bytes, was once measured at 4,656,208, past the margin
        /// its test allows (#293). A background collection started before each repeat is
        /// that run's condition made certain. The read is that one, the longest the
        /// suite's budget samples make. The guard measures the same read the same way
        /// without the measurement's own protection, and needs it to come out high at
        /// least once, so that a runtime whose counter a background collection no longer
        /// disturbs fails here rather than passing by testing nothing. Not run in parallel,
        /// so that the collections are this test's own.
        /// </remarks>
        [TestMethod]
        [DoNotParallelize]
        public void TestAReadIsMeasuredExactlyWithABackgroundCollectionRunning()
        {
            if (GCSettings.LatencyMode == GCLatencyMode.Batch)
            {
                Assert.Inconclusive("background collection is turned off in this process, so none can run beside a measurement");
            }

            var sample = TestReadBudget.InTheLargeForm(TestReadBudget.Samples.Value[StructureId.DpswSketch]);
            object Read() => sample.Read(sample.Bytes, ReadOptions.Unlimited);
            Read();

            var quiet = TestReadBudget.Allocated(Read);
            var unprotected = Enumerable.Range(0, 5).Max(_ =>
            {
                StartABackgroundCollection();
                var before = GC.GetAllocatedBytesForCurrentThread();
                GC.KeepAlive(Read());
                return GC.GetAllocatedBytesForCurrentThread() - before;
            });
            var busy = TestReadBudget.LeastAllocated(Read, 5, StartABackgroundCollection);
            Console.WriteLine(
                $"[MEASUREMENT] {sample.Name}: quietly {quiet:N0}, unprotected with a background collection " +
                $"started first at most {unprotected:N0}, measured so {busy:N0}");

            Assert.IsGreaterThan(quiet, unprotected,
                $"{sample.Name}'s read counted {unprotected:N0} bytes at most with a background collection started " +
                $"before it and nothing to keep it out, and {quiet:N0} quietly, so the collection did not disturb " +
                "the count and this shows nothing");
            Assert.AreEqual(quiet, busy,
                $"{sample.Name}'s read was measured at {busy:N0} bytes with a background collection started before " +
                $"each repeat, and {quiet:N0} quietly: the collection reached the count");
        }

        /// <summary>
        /// Starts a background collection, after a full blocking one: started alone, one
        /// sometimes left a repeat undisturbed, measured in one repeat of ten, and after a
        /// full collection none did, in twenty.
        /// </summary>
        private static void StartABackgroundCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        }
    }
}
