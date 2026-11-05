using System;
using System.Collections.Generic;
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
    /// before each attempt. Work that cannot be measured in one fails, saying why: a room past
    /// what every runner gives one region, a region that cannot be entered, work that allocates
    /// more than the room, or a region ended by a collection at every attempt. Nothing is ever
    /// counted outside a region instead.
    /// </para>
    /// </remarks>
    internal static class NoGCRegion
    {
        /// <summary>
        /// The room a region is entered with unless a measurement asks for another: what all
        /// threads may allocate, on the small object heap and the large one each, before a
        /// collection ends the region. 240 MiB, under <see cref="Largest"/>.
        /// </summary>
        /// <remarks>
        /// As much as every runner gives, rather than what the work needs, since most of a
        /// region's room is spent by other threads. In full runs of the suite, one at each
        /// room, the regions lost to a collection rose from 22 at 256 MiB to 48 at 128 MiB and
        /// 177 at 16 MiB, and from 64 MiB down the largest work no longer fitted: a Grafite
        /// build of 116,533,176 bytes, which CapacityClaims measures.
        /// </remarks>
        internal const long Room = 240L << 20;

        /// <summary>
        /// The largest room every runner the suite runs on gives one region: what a 64-bit
        /// workstation collector on segments, the one macOS runs, allows on 16 KiB pages.
        /// </summary>
        /// <remarks>
        /// macOS builds of the runtime collect on segments rather than regions: gcpriv.h
        /// defines <c>USE_REGIONS</c> only for a 64-bit host that is not <c>__APPLE__</c> or
        /// <c>__sun</c>, and not for a standalone collector (dotnet/runtime v10.0.14). On
        /// segments, <c>gc_heap::prepare_for_no_gc_region</c> (gc.cpp) refuses as too large a
        /// room past <c>(soh_segment_size - segment_info_size - eph_gen_starts_size) / 1.05</c>.
        /// A workstation collector's segment is <c>INITIAL_ALLOC</c>, 256 MiB on a 64-bit host
        /// whatever its processors or memory, unless a hard limit or <c>GCSegmentSize</c> sets
        /// another (<c>get_valid_segment_size</c>); the segment's own information takes one OS
        /// page, 16 KiB on Apple silicon; and the generations' starts take two of the smallest
        /// object, 24 bytes. Measured with <c>libclrgc.so</c>, the standalone collector, which
        /// is on segments, on a Linux host with 4 KiB pages, the largest room accepted was
        /// 255,648,868 bytes with 3 processors and with 32, as the same sum gives there. On
        /// regions, which Linux and Windows run, a room of 1 TiB was not refused as too large.
        /// </remarks>
        internal const long Largest = 255_637_165;

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

        /// <summary>
        /// What each of <paramref name="works"/> allocates on this thread, counted one after
        /// another inside one no-GC region, each as
        /// <see cref="Allocated(Func{object}, Action, long)"/> counts one, so that a sweep of
        /// many small measurements pays for entering a region once rather than once each.
        /// </summary>
        /// <remarks>
        /// Entering a region collects first, which is most of what a small measurement costs.
        /// The works share the region's room, and a collection that ends it before the last is
        /// counted loses them all: they are measured again in a new one, as one work is.
        /// </remarks>
        internal static long[] AllocatedEach(IReadOnlyList<Func<object>> works, long room = Room)
        {
            var counts = new long[works.Count];
            var results = new object[works.Count];
            lock (Turn)
            {
                for (var attempt = 1; attempt <= Attempts; attempt++)
                {
                    Enter(room);

                    var collections = GC.CollectionCount(0);
                    var intact = false;
                    long total = 0;
                    try
                    {
                        for (var i = 0; i < works.Count; i++)
                        {
                            var before = GC.GetAllocatedBytesForCurrentThread();
                            results[i] = works[i]();
                            counts[i] = GC.GetAllocatedBytesForCurrentThread() - before;
                            total += counts[i];
                        }
                    }
                    finally
                    {
                        intact = Leave(collections);
                    }

                    GC.KeepAlive(results);
                    Array.Clear(results);
                    if (intact)
                    {
                        return counts;
                    }

                    if (total > room)
                    {
                        Assert.Fail(
                            $"the works allocated {total:N0} bytes, more than the {room:N0} of the no-GC region " +
                            "they were measured in, so a collection ran while they were counted. Measure fewer at once.");
                    }

                    Console.WriteLine(
                        $"[NO-GC-REGION] attempt {attempt}: a collection ended the region before {works.Count} works " +
                        "were done; measuring them again in a new one");
                    Thread.Sleep(Math.Min(attempt * 10, 500));
                }

                Assert.Fail(
                    $"a collection ended the no-GC region before {works.Count} works were done at every one of " +
                    $"{Attempts} attempts, so nothing was measured without one running.");
                return counts;
            }
        }

        private static void Enter(long room)
        {
            // Refused here on every runner, where macOS alone would refuse it and the others
            // would measure it.
            if (room > Largest)
            {
                Assert.Fail(
                    $"a no-GC region of {room:N0} bytes is more than the {Largest:N0} bytes macOS's collector, on " +
                    "segments rather than regions, gives one, so it is refused on every runner rather than " +
                    "measured on some. Give the work less room, or measure less of it at once.");
            }

            bool entered;
            try
            {
                entered = GC.TryStartNoGCRegion(room);
            }
            catch (ArgumentOutOfRangeException outOfRange)
            {
                // Nothing or less, or more than the runtime gives one region.
                Assert.Fail($"the runtime refused a no-GC region of {room:N0} bytes as out of range, so nothing was measured: {outOfRange.Message}");
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
        /// Ends the region <see cref="Enter"/> entered, whatever became of it, and says whether
        /// it held: still in force, no collection since <paramref name="collections"/> were
        /// counted, and ended as one in which none ran.
        /// </summary>
        /// <remarks>
        /// Ended even when the latency mode says a collection already ended it. In a full run of
        /// the suite a region lost that way was still recorded as started, and the next attempt
        /// was refused as one already in progress, three tests' worth; ending a region clears
        /// that record whatever its state (gc.cpp <c>gc_heap::end_no_gc_region</c>, which also
        /// reports, as an exception here, a collection that ran while it was recorded).
        /// </remarks>
        private static bool Leave(int collections)
        {
            var held = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion && GC.CollectionCount(0) == collections;
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
