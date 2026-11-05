using System;
using System.Runtime;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Counts what work allocates on the calling thread inside a no-GC region, so that no
    /// collection, in the foreground or the background, runs while it is counted (#293).
    /// </summary>
    /// <remarks>
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> is exact for what a thread
    /// allocates while no collection runs. With a background collection running beside the
    /// work it reports kilobytes the work did not ask for, and on a loaded run one can be
    /// running beside every repeat of a measurement, so the least of several is no defence:
    /// <c>DpswSketch</c>'s large-form read, 4,648,320 bytes quietly, was measured at
    /// 4,656,208 and failed a margin of a few hundred.
    /// <para>
    /// Entering a region waits for a background collection in progress to finish, and none
    /// starts until the region ends or its room runs out: <c>StartNoGCRegion</c> collects
    /// with <c>GarbageCollect</c>'s default mode, <c>collection_blocking</c>, which waits for
    /// a background collection that is running (dotnet/runtime v10.0.14, gc.cpp
    /// <c>GCHeap::StartNoGCRegion</c> and <c>GCHeap::GarbageCollect</c>, gcimpl.h). A region
    /// is the process's, not the thread's, so measurements take turns here, and every
    /// thread's allocations count against its room. A collection that ends a region before
    /// the work is done -- another test's allocations ran past the room, or something
    /// induced one -- leaves a count nothing vouches for, so the work is measured again in a
    /// new region, after a pause that grows with each one lost. An attempt can be repeated, so
    /// the work must be repeatable: work that changes what it works on, an add that doubles a
    /// table or a write into a stream that is then read, has that rebuilt by the action run
    /// before each attempt. Work that cannot be measured in one fails, saying why: a region that
    /// cannot be entered, work that allocates more than the room, or a region ended by a
    /// collection at every attempt. Nothing is ever counted outside a region instead.
    /// </para>
    /// </remarks>
    internal static class NoGCRegion
    {
        /// <summary>
        /// The room a region is entered with unless a measurement asks for more: what all
        /// threads may allocate, on the small object heap and the large one each, before a
        /// collection ends the region.
        /// </summary>
        internal const long Room = 256L << 20;

        /// <summary>How many regions a measurement may lose to collections before it fails.</summary>
        internal const int Attempts = 50;

        private static readonly object Turn = new();

        /// <summary>
        /// What <paramref name="work"/> allocates on this thread, counted inside a no-GC
        /// region of <paramref name="room"/> bytes entered after <paramref name="first"/>,
        /// which is neither counted nor in the region.
        /// </summary>
        internal static long Allocated(Func<object> work, Action first = null, long room = Room)
        {
            lock (Turn)
            {
                for (var attempt = 1; attempt <= Attempts; attempt++)
                {
                    first?.Invoke();
                    Enter(room);

                    var collections = GC.CollectionCount(0);
                    var everywhere = GC.GetTotalAllocatedBytes();
                    var intact = false;
                    long allocated;
                    try
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        var result = work();
                        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                        GC.KeepAlive(result);
                    }
                    finally
                    {
                        intact = Leave(collections);
                    }

                    everywhere = GC.GetTotalAllocatedBytes() - everywhere;

                    if (intact)
                    {
                        return allocated;
                    }

                    if (allocated > room)
                    {
                        Assert.Fail(
                            $"the work allocated {allocated:N0} bytes, more than the {room:N0} of the no-GC region " +
                            "it was measured in, so a collection ran while it was counted. Give it more room.");
                    }

                    Console.WriteLine(
                        $"[NO-GC-REGION] attempt {attempt}: a collection ended the region before the work was done, " +
                        $"which allocated {allocated:N0} bytes while every thread allocated {everywhere:N0}; " +
                        "measuring it again in a new one");

                    // Another test allocating heavily ends regions for as long as it runs, so
                    // the attempts are spread out rather than spent inside one burst of it.
                    Thread.Sleep(Math.Min(attempt * 10, 500));
                }

                Assert.Fail(
                    $"a collection ended the no-GC region before the work was done at every one of {Attempts} " +
                    "attempts, so nothing was measured without one running. The work, or other threads, " +
                    $"allocated past the {room:N0} bytes of room, or induced a collection.");
                return 0;
            }
        }

        /// <summary>
        /// What <paramref name="work"/> allocates on this thread, counted as
        /// <see cref="Allocated(Func{object}, Action, long)"/> counts it.
        /// </summary>
        internal static long Allocated(Action work, Action first = null, long room = Room) =>
            Allocated(() =>
            {
                work();
                return null;
            }, first, room);

        private static void Enter(long room)
        {
            bool entered;
            try
            {
                entered = GC.TryStartNoGCRegion(room);
            }
            catch (ArgumentOutOfRangeException tooLarge)
            {
                Assert.Fail($"a no-GC region of {room:N0} bytes is more than this runtime allows, so nothing was measured: {tooLarge.Message}");
                throw;
            }
            catch (InvalidOperationException inProgress)
            {
                Assert.Fail($"a no-GC region was already in progress, entered by something that does not take turns here, so nothing was measured: {inProgress.Message}");
                throw;
            }

            if (!entered)
            {
                Assert.Fail($"the runtime could not commit the {room:N0} bytes a no-GC region needs, so nothing was measured");
            }
        }

        /// <summary>
        /// Ends the region <see cref="Enter"/> entered, if a collection has not ended it
        /// already, and says whether it held: no collection ran since
        /// <paramref name="collections"/> were counted.
        /// </summary>
        private static bool Leave(int collections)
        {
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                return false;
            }

            var held = GC.CollectionCount(0) == collections;
            try
            {
                GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            return held;
        }
    }
}
