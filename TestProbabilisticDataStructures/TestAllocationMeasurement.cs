using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        /// An attempt whose region a collection ended is not counted: the work is measured
        /// again in a region of its own, and what comes back is that count.
        /// </summary>
        /// <remarks>
        /// The first attempt induces a collection, which ends its region, and allocates more
        /// than the attempts after it, so a measurement that kept a count from a region that
        /// did not hold comes to that and not to what the work allocates quietly.
        /// </remarks>
        [TestMethod]
        public void TestAnAttemptWhoseRegionACollectionEndedIsMeasuredAgain()
        {
            var attempts = 0;
            var measured = NoGCRegion.Allocated(() =>
            {
                if (attempts++ == 0)
                {
                    GC.Collect();
                    return new byte[4096];
                }

                return new byte[64];
            });
            var quiet = NoGCRegion.Allocated(() => new byte[64]);

            Assert.IsGreaterThan(1, attempts,
                "the first attempt's region held through the collection it induced, so nothing was measured again");
            Assert.AreEqual(quiet, measured,
                $"the work was measured at {measured:N0} bytes and allocates {quiet:N0} quietly: the count was " +
                "kept from a region a collection had ended");
        }

        /// <summary>
        /// Work that allocates more than its region's room fails, saying so, rather than being
        /// counted with the collection that ended the region in it.
        /// </summary>
        /// <remarks>
        /// The work is many small arrays, eight times the room. The small object heap's room is
        /// held closely: measured, two megabytes of them ended a region of one, where a single
        /// large array of four did not, so one allocation can overshoot what is left of a
        /// budget. A region that holds, however much was allocated in it, ran no collection,
        /// and its count is exact.
        /// </remarks>
        [TestMethod]
        public void TestWorkLargerThanItsRegionFailsSayingSo()
        {
            const long room = 1 << 20;
            var refused = Assert.ThrowsExactly<AssertFailedException>(
                () => NoGCRegion.Allocated(
                    () => Enumerable.Range(0, (int)(8 * room / (16 << 10))).Select(_ => new byte[16 << 10]).ToList(),
                    room: room),
                "work allocating eight times its region's room was measured");
            StringAssert.Contains(refused.Message, $"more than the {room:N0} of the no-GC region", refused.Message);
        }

        /// <summary>
        /// A region the runtime refuses for its size fails a measurement, saying so, rather
        /// than letting it be counted without one: a room of nothing, which
        /// <see cref="GC.TryStartNoGCRegion(long)"/> refuses as out of range.
        /// </summary>
        [TestMethod]
        public void TestARegionRefusedForItsSizeFailsAMeasurementSayingSo()
        {
            var refused = Assert.ThrowsExactly<AssertFailedException>(
                () => NoGCRegion.Allocated(() => new object(), room: 0),
                "work was measured in a region of no room");
            StringAssert.Contains(refused.Message, "refused a no-GC region of 0 bytes as out of range", refused.Message);
        }

        /// <summary>
        /// A room past the largest every runner gives one region fails a measurement on every
        /// runner, saying so, and not only on the runner that refuses it. A room of 256 MiB
        /// was given here, on regions, and refused on macOS, which collects on segments, so
        /// that every measurement passed on four runners and failed on the fifth (#293).
        /// </summary>
        [TestMethod]
        public void TestARoomPastTheLargestEveryRunnerGivesFailsOnEveryRunnerSayingSo()
        {
            var refused = Assert.ThrowsExactly<AssertFailedException>(
                () => NoGCRegion.Allocated(() => new object(), room: NoGCRegion.Largest + 1),
                "work was measured in a region larger than macOS gives one");
            StringAssert.Contains(refused.Message,
                $"is more than the {NoGCRegion.Largest:N0} bytes", refused.Message);
        }

        /// <summary>
        /// The largest room is given: work is counted in a region of exactly
        /// <see cref="NoGCRegion.Largest"/>, so the bound refuses no room a runner gives. On
        /// macOS, whose collector refuses a room past it, this checks its derivation.
        /// </summary>
        [TestMethod]
        public void TestTheLargestRoomEveryRunnerGivesIsGiven()
        {
            var measured = NoGCRegion.Allocated(() => { }, room: NoGCRegion.Largest);
            Assert.AreEqual(0, measured, $"work that allocates nothing was measured at {measured:N0} bytes");
        }

        /// <summary>
        /// The room a measurement is given unless it asks for another is one every runner
        /// gives.
        /// </summary>
        [TestMethod]
        public void TestTheDefaultRoomIsOneEveryRunnerGives()
        {
            Assert.IsLessThanOrEqualTo(NoGCRegion.Largest, NoGCRegion.Room,
                $"the default room, {NoGCRegion.Room:N0} bytes, is more than the {NoGCRegion.Largest:N0} " +
                "macOS gives one region, so every measurement that takes it is refused there");
        }

        /// <summary>
        /// <see cref="NoGCRegion.Largest"/> is the bound its remarks derive from gc.cpp, worked
        /// out here from the same terms: a 64-bit workstation collector's 256 MiB segment, less
        /// one 16 KiB page for its information and two 24-byte objects for the generations'
        /// starts, over the 1.05 <c>prepare_for_no_gc_region</c> scales a room by, truncated.
        /// </summary>
        [TestMethod]
        public void TestTheLargestRoomIsTheBoundASegmentsCollectorPutsOnOneRegion()
        {
            const long segment = 256L << 20;
            const long page = 16L << 10;
            const long starts = 2 * 24;
            var bound = (long)((ulong)(segment - page - starts) / 1.05);
            Assert.AreEqual(NoGCRegion.Largest, bound, "the largest room is not the bound gc.cpp gives one region on segments");
        }

        /// <summary>
        /// A region already in progress, entered by something that does not take turns with
        /// <see cref="NoGCRegion"/>, fails a measurement, saying so, rather than letting it be
        /// counted in a region it does not hold. Not run in parallel, since while this test's
        /// region is held no other measurement can enter one.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void TestARegionAlreadyInProgressFailsAMeasurementSayingSo()
        {
            Assert.IsTrue(GC.TryStartNoGCRegion(NoGCRegion.Room), "premise: a region could be entered");
            try
            {
                var refused = Assert.ThrowsExactly<AssertFailedException>(
                    () => NoGCRegion.Allocated(() => new object()),
                    "work was measured while a region it did not enter was in progress");
                StringAssert.Contains(refused.Message, "already in progress", refused.Message);
            }
            finally
            {
                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                {
                    GC.EndNoGCRegion();
                }
            }
        }

        /// <summary>
        /// Nothing in the suite reads an allocation counter but <see cref="NoGCRegion"/>, so
        /// every allocation the suite holds code to is counted in a region. The roster is the
        /// suite's own code: every method of every type in this assembly, the lambdas and
        /// state machines the compiler makes included, whose IL calls
        /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> or
        /// <see cref="GC.GetTotalAllocatedBytes"/>.
        /// </summary>
        /// <remarks>
        /// One exemption, this class's own test above, whose guard counts a read without a
        /// region on purpose; it is checked to be found, so it fails once it is out of date,
        /// and so must the helper, so that a scan that finds nothing cannot pass.
        /// </remarks>
        [TestMethod]
        public void TestEveryAllocationTheSuiteCountsIsCountedInARegion()
        {
            var counters = typeof(GC).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name is nameof(GC.GetAllocatedBytesForCurrentThread) or nameof(GC.GetTotalAllocatedBytes))
                .ToArray();
            Assert.IsNotEmpty(counters, "no allocation counter was found on GC to look for");

            var callers = new HashSet<Type>();
            var reading = new List<string>();
            foreach (var type in typeof(NoGCRegion).Assembly.GetTypes())
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                         BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if (!Called(method).Any(counters.Contains))
                    {
                        continue;
                    }

                    var outermost = type;
                    while (outermost.DeclaringType is not null)
                    {
                        outermost = outermost.DeclaringType;
                    }

                    callers.Add(outermost);
                    if (outermost != typeof(NoGCRegion) && outermost != typeof(TestAllocationMeasurement))
                    {
                        reading.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }

            Assert.IsEmpty(reading,
                "these read an allocation counter themselves, so what they count is not counted in a no-GC region " +
                "and a background collection can reach it; measure with NoGCRegion: " + string.Join(", ", reading));
            Assert.Contains(typeof(NoGCRegion), callers,
                "NoGCRegion was not found reading the counter, so the scan finds nothing and shows nothing");
            Assert.Contains(typeof(TestAllocationMeasurement), callers,
                "the exemption for this class's guard, which counts without a region on purpose, is out of date");
        }

        /// <summary>The methods <paramref name="method"/>'s IL calls, decoded an instruction at a time.</summary>
        private static List<MethodBase> Called(MethodBase method)
        {
            var called = new List<MethodBase>();
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
            {
                return called;
            }

            for (var at = 0; at < il.Length;)
            {
                var value = (short)il[at++];
                if (value == 0xFE)
                {
                    value = unchecked((short)(0xFE00 | il[at++]));
                }

                var op = Instructions.Value[value];
                if (op.OperandType == OperandType.InlineMethod)
                {
                    try
                    {
                        called.Add(method.Module.ResolveMethod(
                            BitConverter.ToInt32(il, at),
                            method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null));
                    }
                    catch (ArgumentException)
                    {
                        // A token naming something generic over a context this one cannot give;
                        // never one of the counters, which are not generic.
                    }
                }

                at += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                    _ => 4,
                };
            }

            return called;
        }

        private static readonly Lazy<Dictionary<short, OpCode>> Instructions = new(() =>
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field => (OpCode)field.GetValue(null))
                .ToDictionary(op => op.Value));

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
