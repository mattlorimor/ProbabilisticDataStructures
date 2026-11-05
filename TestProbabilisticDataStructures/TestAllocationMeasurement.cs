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
