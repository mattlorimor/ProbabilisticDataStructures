using System;
using System.Collections.Generic;
using System.Linq;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>What a structure's number counts, which decides the table it is listed in.</summary>
    internal enum CapacityKind
    {
        /// <summary>It is built for a number of items, and holds that many at the rate asked for.</summary>
        Sized,

        /// <summary>It grows as items are added, until something stops it.</summary>
        Grows,

        /// <summary>It keeps a bounded number of entries and sees any number of items.</summary>
        Retains,

        /// <summary>It sees any number of items in memory that its settings fix.</summary>
        Unlimited,
    }

    internal static partial class CapacityRows
    {
        /// <summary>Every row of the table, in the order the document gives them.</summary>
        internal static IReadOnlyList<CapacityRow> All { get; } =
            Bloom().Concat(Membership()).Concat(Tables()).Concat(Sketches())
                .Select(row => row with { Saves = SavesFor(row) }).ToArray();

        /// <summary>
        /// The table a row is listed in. Decided by structure, and for the one structure
        /// that has rows of two kinds, by what the row counts.
        /// </summary>
        internal static CapacityKind KindOf(CapacityRow row) => row.Id switch
        {
            StructureId.BloomFilter or StructureId.BloomFilter64 or StructureId.CountingBloomFilter
                or StructureId.DeletableBloomFilter or StructureId.PartitionedBloomFilter
                or StructureId.CuckooBloomFilter or StructureId.QuotientFilter
                or StructureId.BinaryFuseFilter or StructureId.BloomierFilter
                or StructureId.Grafite or StructureId.InvertibleBloomLookupTable => CapacityKind.Sized,
            StructureId.ScalableBloomFilter or StructureId.InfiniFilter
                or StructureId.MementoFilter => CapacityKind.Grows,
            StructureId.InverseBloomFilter or StructureId.ThetaSketch or StructureId.TupleSketch
                or StructureId.MinHashSignature or StructureId.VarOpt => CapacityKind.Retains,
            StructureId.TopK => row.Counts.StartsWith("k,", StringComparison.Ordinal)
                ? CapacityKind.Retains : CapacityKind.Unlimited,
            _ => CapacityKind.Unlimited,
        };
    }
}
