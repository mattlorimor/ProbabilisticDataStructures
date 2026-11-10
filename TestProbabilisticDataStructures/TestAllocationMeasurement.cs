using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Threading;
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
        /// suite's budget samples make. Not run in parallel, so that the collections are
        /// this test's own.
        /// <para>
        /// The guard counts the same read the same way without the measurement's protection,
        /// and needs a count to come out high, so that a runtime whose counter a background
        /// collection no longer disturbs fails here rather than passing by testing nothing. A
        /// collection disturbs only a read that the end of its marking falls in, so the guard
        /// reads on until one does, as <see cref="CountUntilDisturbed"/> says (#357). It also
        /// needs the read counted that way after the same blocking collections, with no
        /// background one, to come out no higher than quietly, so that a count high for some
        /// other reason cannot pass for a disturbance.
        /// </para>
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
            var disturbed = CountUntilDisturbed(() => CountedWithoutARegion(Read), quiet, StartABackgroundCollection);
            CollectFully();
            var undisturbed = CountedWithoutARegion(Read);
            var busy = TestReadBudget.LeastAllocated(Read, 5, () => StartABackgroundCollection());
            Console.WriteLine(
                $"[MEASUREMENT] {sample.Name}: quietly {quiet:N0}; without a region, {undisturbed:N0} with no " +
                $"background collection, and {disturbed.Counted:N0} at read {disturbed.Reads}, in collection " +
                $"{disturbed.Collections}, with them running; measured so {busy:N0}");

            Assert.IsGreaterThan(quiet, disturbed.Counted,
                $"{sample.Name}'s read counted {disturbed.Counted:N0} bytes at most in {disturbed.Reads} reads, with " +
                $"{disturbed.Collections} background collections running beside them and nothing to keep them out, " +
                $"and {quiet:N0} quietly, so no collection disturbed the count and this shows nothing");
            Assert.IsLessThanOrEqualTo(quiet, undisturbed,
                $"{sample.Name}'s read counted {undisturbed:N0} bytes with nothing to keep a collection out and no " +
                $"background collection running, more than the {quiet:N0} it allocates quietly, so a count comes out " +
                "high without a collection disturbing it and the guard shows nothing");
            Assert.AreEqual(quiet, busy,
                $"{sample.Name}'s read was measured at {busy:N0} bytes with a background collection started before " +
                $"each repeat, and {quiet:N0} quietly: the collection reached the count");
        }

        /// <summary>
        /// The guard reads on while a collection runs and starts another only once it has
        /// ended, and the first count above quiet is the one it returns.
        /// </summary>
        [TestMethod]
        public void TestTheGuardReadsOnThroughACollectionAndStartsAnotherOnlyOnceItHasEnded()
        {
            // The first collection ends after its second read, undisturbed; the second is
            // disturbed at its second read.
            var guard = new ScriptedGuard(counts: [100, 100, 100, 108], readsPerCollection: [2, 10]);

            var disturbed = CountUntilDisturbed(guard.Count, 100, guard.Start, reads: 10);

            Assert.AreEqual((108L, 4, 2), disturbed, "the guard did not read on through each collection to the count above quiet");
            Assert.AreEqual(2, guard.Started, "the guard did not start a collection once, and once more after the first ended");
        }

        /// <summary>
        /// A guard no collection ever disturbs counts every read it may and no more, and
        /// returns the most it counted, so that the test fails saying how far it went.
        /// </summary>
        [TestMethod]
        public void TestAGuardNothingDisturbsCountsEveryReadItMayAndReturnsTheMost()
        {
            // The most is neither the first count, the last nor the least.
            var guard = new ScriptedGuard(counts: [76, 92, 100, 88, 96, 84, 90], readsPerCollection: [3, 3, 3]);

            var undisturbed = CountUntilDisturbed(guard.Count, 100, guard.Start, reads: 7);

            Assert.AreEqual((100L, 7, 3), undisturbed,
                "the guard did not count all seven reads, starting a collection every third, and return the most");
            Assert.AreEqual(7, guard.Counted, "the guard counted reads past the most it may");
        }

        /// <summary>
        /// The guard reads as far as the longest wait for a disturbance measured: twelve
        /// reads, over two collections.
        /// </summary>
        /// <remarks>
        /// Measured with 1.6 GB of small objects added to the live heap, where marking took 38
        /// to 50 ms against a read of about 18. Of 90 waits, 30 with one framework running and
        /// 60 with both at once, every one took three reads or more and the longest twelve;
        /// with 800 MB added, all of 30 took two, and with 400 MB or none, one (#357). Which
        /// read the first collection ended at was not recorded, so the script has it run
        /// through all twelve. See <see cref="GuardReads"/>.
        /// </remarks>
        [TestMethod]
        public void TestTheGuardReadsAsFarAsTheLongestWaitForADisturbanceMeasured()
        {
            var guard = new ScriptedGuard(
                counts: [100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 104], readsPerCollection: [12]);

            var disturbed = CountUntilDisturbed(guard.Count, 100, guard.Start);

            Assert.AreEqual((104L, 12, 1), disturbed,
                $"the guard gives up after {GuardReads} reads, short of the twelve a disturbance has been measured to take");
        }

        /// <summary>
        /// A background collection the guard starts is taken for ended once it has ended and
        /// not before, though one it started before is still running when it starts.
        /// </summary>
        /// <remarks>
        /// The scripted tests above stand in for the collections; this one holds the hook the
        /// guard runs. The second collection is started straight after the first, whose
        /// marking is still under way, so the second's blocking collections wait for the
        /// first to end. The wait for the second to end is bounded so that a hook that never
        /// tells fails rather than hanging.
        /// </remarks>
        [TestMethod]
        [DoNotParallelize]
        public void TestABackgroundCollectionIsTakenForEndedOnceItHasEndedAndNotBefore()
        {
            if (GCSettings.LatencyMode == GCLatencyMode.Batch)
            {
                Assert.Inconclusive("background collection is turned off in this process, so none can run");
            }

            var first = StartABackgroundCollection();
            var second = StartABackgroundCollection();

            Assert.IsTrue(first(),
                "the first collection was not taken for ended, though the second's blocking collections waited for it");
            Assert.IsFalse(second(),
                "the second collection was taken for ended as soon as it began: the first's end was taken for it");

            var waited = Stopwatch.StartNew();
            while (!second() && waited.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(1);
            }

            Assert.IsTrue(second(), $"the second collection was not taken for ended in {waited.Elapsed.TotalSeconds:N0} s");
        }

        /// <summary>
        /// The most reads the guard counts without a region, waiting for a background
        /// collection to disturb one, before it fails.
        /// </summary>
        /// <remarks>
        /// About four times the longest wait measured, twelve reads (see
        /// <see cref="TestTheGuardReadsAsFarAsTheLongestWaitForADisturbanceMeasured"/>), so that
        /// a collection that ends without disturbing a read, as 2 of the 92 started with 1.6 GB
        /// added did, does not bring the guard near failing. Counted in a region instead, where
        /// nothing can disturb it, the read took the guard to its fiftieth and failing in about
        /// two seconds, run alone.
        /// </remarks>
        internal const int GuardReads = 50;

        /// <summary>
        /// Counts reads with <paramref name="count"/> while background collections run, starting
        /// one with <paramref name="start"/> and another only once that one has ended, until a
        /// count comes out above <paramref name="quiet"/> or <paramref name="reads"/> have been
        /// counted. Returns that count, or the most counted if none came out above, with the
        /// reads and collections it took.
        /// </summary>
        /// <remarks>
        /// A background collection disturbs a thread's count once, at the pause that ends its
        /// marking. There, with the runtime suspended, it voids every thread's allocation
        /// context, setting its pointer and limit to zero without taking what the context had
        /// left unused off the bytes the thread is counted as allocating, where a blocking
        /// collection takes it off; and <see cref="GC.GetAllocatedBytesForCurrentThread"/>
        /// subtracts only what a context still holds unused. So a count is high by what the
        /// read's context had left when the pause came, if the pause came during the read, and
        /// not otherwise: dotnet/runtime v10.0.14, gc.cpp, <c>gc_heap::background_mark_phase</c>
        /// ends calling <c>repair_allocation_contexts (FALSE)</c>, whose <c>void_allocation</c>
        /// leaves <c>alloc_bytes</c> alone, while <c>fix_allocation_context</c> subtracts the
        /// unused part; comutilnative.cpp, <c>GCInterface::GetAllocatedBytesForCurrentThread</c>.
        /// v11.0.0 has the same, in background.cpp and allocation.cpp. Read 2026-11-10.
        /// <para>
        /// Measured that way, with the pause timed by the runtime's own events, 1,671 of the
        /// 1,680 reads it fell in came out high, by 16 to 8,192 bytes, and none of the 320 that
        /// ended before it or started after it. The guard first counted one read per
        /// collection, started straight after it, which catches the pause only when marking
        /// takes less time than the read. In the whole suite, marking took 2 to 30 ms against
        /// a read of about 11 to 21, and in the runs where it took longest, 12 to 19 reads in
        /// 100 came out exact; with 800 MB of small objects added to the live heap, 59 in 60
        /// ended before the pause, and all five of a test's repeats can miss it (#357).
        /// Reading on while the collection runs brings its pause into a read, whatever its
        /// marking takes. A collection can still end without disturbing one, when its pause
        /// falls between two reads or a read's context is empty, and the guard then starts
        /// another.
        /// </para>
        /// </remarks>
        internal static (long Counted, int Reads, int Collections) CountUntilDisturbed(
            Func<long> count, long quiet, Func<Func<bool>> start, int reads = GuardReads)
        {
            long most = 0;
            var collections = 0;
            Func<bool> ended = null;
            for (var read = 1; read <= reads; read++)
            {
                if (ended is null || ended())
                {
                    ended = start();
                    collections++;
                }

                var counted = count();
                if (counted > quiet)
                {
                    return (counted, read, collections);
                }

                most = Math.Max(most, counted);
            }

            return (most, reads, collections);
        }

        /// <summary>
        /// What <paramref name="read"/> allocates on this thread, counted with nothing to keep
        /// a collection out: the guard's count, which only this class takes.
        /// </summary>
        private static long CountedWithoutARegion(Func<object> read)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(read());
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// Counts and collections played from a script, for holding
        /// <see cref="CountUntilDisturbed"/> to what it does with them.
        /// </summary>
        private sealed class ScriptedGuard(long[] counts, int[] readsPerCollection)
        {
            private int readsInThisCollection;

            public int Started { get; private set; }

            public int Counted { get; private set; }

            public long Count()
            {
                readsInThisCollection++;
                return counts[Counted++];
            }

            public Func<bool> Start()
            {
                var runsFor = readsPerCollection[Started++];
                readsInThisCollection = 0;
                return () => readsInThisCollection >= runsFor;
            }
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
        /// macOS under .NET 10, whose collector refuses a room past it, this checks its
        /// derivation; under .NET 11, where macOS collects on regions, it checks the bound still
        /// holds there.
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
                "macOS gives one region under .NET 10, so every measurement that takes it is refused there");
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
        /// Starts a background collection, after a full blocking one, and returns whether it
        /// has ended yet: started alone, one sometimes left a repeat undisturbed, measured in
        /// one repeat of ten, and after a full collection none did, in twenty.
        /// </summary>
        /// <remarks>
        /// The last background collection to end is noted after the blocking ones, which wait
        /// for one still running: noted before them, a collection still running when this is
        /// called would end in them and be taken for this one ending as soon as it began.
        /// </remarks>
        private static Func<bool> StartABackgroundCollection()
        {
            CollectFully();
            var ended = GC.GetGCMemoryInfo(GCKind.Background).Index;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
            return () => GC.GetGCMemoryInfo(GCKind.Background).Index != ended;
        }

        /// <summary>A full blocking collection, with the finalizers it queued run and collected after it.</summary>
        private static void CollectFully()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
