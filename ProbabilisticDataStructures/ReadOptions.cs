using System;
using System.Globalization;
using System.IO;

namespace ProbabilisticDataStructures
{
    /// <summary>
    /// How a structure is read back from a stream: for now, the most memory the read may
    /// allocate.
    /// </summary>
    /// <remarks>
    /// A payload says how large the structure it holds is, and a reader has to allocate
    /// that structure before it can fill it in. A structure is often far larger than its
    /// payload -- a cuckoo filter writes only its occupied entries, a quotient filter's
    /// header names a table of any size -- so a few hundred bytes from a file nobody
    /// checked could ask for gigabytes. The budget bounds that. A reader works out what
    /// it will allocate, from what the header and the payload claim, before allocating
    /// it, and refuses a payload that would take more than the budget: an
    /// <see cref="InvalidDataException"/> whose inner exception is a
    /// <see cref="ReadBudgetExceededException"/>.
    /// <para>
    /// The budget covers the whole read, under one allowance: the payload's own bytes,
    /// the copies made of runs within it, the structure, and any structure nested inside
    /// it. It is a bound on what the read allocates, not on what the structure holds
    /// afterwards, so reading a structure costs roughly its payload as well as itself.
    /// </para>
    /// <para>
    /// <see cref="Default"/> is what every <c>ReadFrom</c> without options uses. Pass a
    /// larger budget to read a larger structure you trust, or <see cref="Unlimited"/>
    /// to read without one.
    /// </para>
    /// </remarks>
    public sealed class ReadOptions
    {
        /// <summary>
        /// The budget <see cref="Default"/> allows: one gibibyte.
        /// </summary>
        /// <remarks>
        /// Enough for any structure up to about half a gigabyte, since a read holds the
        /// payload while it builds the structure; small enough that a file which is not
        /// what it claims cannot take a machine's memory with it. Larger structures read
        /// with a budget of their own.
        /// </remarks>
        public const long DefaultMemoryBudget = 1L << 30;

        /// <summary>
        /// A read may allocate up to <see cref="DefaultMemoryBudget"/> bytes. Every
        /// <c>ReadFrom</c> without options reads this way.
        /// </summary>
        public static ReadOptions Default { get; } = new ReadOptions(DefaultMemoryBudget);

        /// <summary>
        /// No budget: a read allocates whatever its payload says the structure needs.
        /// For payloads you wrote yourself or otherwise trust.
        /// </summary>
        public static ReadOptions Unlimited { get; } = new ReadOptions(long.MaxValue);

        /// <summary>
        /// Options allowing a read to allocate up to the given number of bytes.
        /// </summary>
        /// <param name="memoryBudget">
        /// The most memory, in bytes, reading one payload may allocate.
        /// <see cref="long.MaxValue"/> is no limit, as <see cref="Unlimited"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="memoryBudget"/> is negative.
        /// </exception>
        public ReadOptions(long memoryBudget)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(memoryBudget);
            this.MemoryBudget = memoryBudget;
        }

        /// <summary>
        /// The options a nested structure is read under: the same allowance, already
        /// partly spent by the structure around it.
        /// </summary>
        internal ReadOptions(ReadBudget shared)
        {
            this.MemoryBudget = shared.Limit;
            this.Shared = shared;
        }

        /// <summary>
        /// The most memory, in bytes, reading one payload may allocate.
        /// </summary>
        public long MemoryBudget { get; }

        /// <summary>Whether these options set no limit at all.</summary>
        public bool IsUnlimited => this.MemoryBudget == long.MaxValue;

        /// <summary>The budget a nested read draws on, or null for a fresh read.</summary>
        internal ReadBudget? Shared { get; }
    }

    /// <summary>
    /// Why a read was refused when the payload would take more memory to read than the
    /// read's budget allows: the <see cref="Exception.InnerException"/> of the
    /// <see cref="InvalidDataException"/> a reader throws.
    /// </summary>
    /// <remarks>
    /// The refusal is raised before the allocation it would have needed, not after.
    /// What a caller catches is an <see cref="InvalidDataException"/>, as for every
    /// other payload a reader will not read, so code that already handles those goes on
    /// working; <see cref="InvalidDataException"/> is sealed, so this cannot be a kind
    /// of one. Code that wants to tell a large payload from a corrupt one looks for this
    /// as the inner exception:
    /// <code>
    /// catch (InvalidDataException e) when (e.InnerException is ReadBudgetExceededException budget)
    /// </code>
    /// It says nothing about whether the payload is well formed, only that it is larger
    /// than the budget.
    /// </remarks>
    public sealed class ReadBudgetExceededException : Exception
    {
        /// <summary>Creates the exception with a default message.</summary>
        public ReadBudgetExceededException()
            : base("Reading the payload would allocate more than the read budget allows.")
        {
        }

        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What was refused.</param>
        public ReadBudgetExceededException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a message and the exception behind it.</summary>
        /// <param name="message">What was refused.</param>
        /// <param name="innerException">The exception that caused this one.</param>
        public ReadBudgetExceededException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        internal ReadBudgetExceededException(string structure, long requiredBytes, long memoryBudget)
            : base(Describe(structure, requiredBytes, memoryBudget))
        {
            this.RequiredBytes = requiredBytes;
            this.MemoryBudget = memoryBudget;
        }

        /// <summary>
        /// How many bytes the read had been asked for when it was refused. The read needs
        /// at least this many, and may need more: it stops at the first allocation the
        /// budget does not cover.
        /// </summary>
        public long RequiredBytes { get; }

        /// <summary>The budget the read was given, in bytes.</summary>
        public long MemoryBudget { get; }

        private static string Describe(string structure, long required, long budget)
        {
            var needs = required.ToString("N0", CultureInfo.InvariantCulture);
            var allows = budget.ToString("N0", CultureInfo.InvariantCulture);
            return $"Reading this {structure} needs at least {needs} bytes, and the read " +
                $"budget is {allows}. The budget stops a small payload from asking for a " +
                "large allocation. To read a payload you trust that is this large, pass a " +
                $"larger budget: {structure}.ReadFrom(stream, new ReadOptions({required})), " +
                "or ReadOptions.Unlimited.";
        }
    }

    /// <summary>
    /// The allowance one read draws on, charged before each allocation a payload's claims
    /// drive.
    /// </summary>
    /// <remarks>
    /// The charges are upper bounds on what the read allocates, in bytes as the runtime
    /// counts them: an array's elements and its header, and a small fixed amount for
    /// each object that is not an array. They are what the read-budget tests hold the
    /// measured allocation to, so a charge that is too small fails a test rather than
    /// letting a read past its budget.
    /// </remarks>
    internal sealed class ReadBudget
    {
        /// <summary>
        /// What an array costs beyond its elements on a 64-bit runtime: the object header,
        /// the method table pointer and the length.
        /// </summary>
        internal const long ArrayHeader = 24;

        /// <summary>
        /// A generous bound on what a small object that is not an array costs: header,
        /// method table, and a handful of fields.
        /// </summary>
        internal const long SmallObject = 64;

        private readonly string structure;

        internal ReadBudget(long limit, StructureId structure)
        {
            this.Limit = limit;
            this.structure = structure.ToString();
        }

        /// <summary>
        /// The budget a read of <paramref name="structure"/> draws on: the shared one if
        /// it is nested inside another read, a fresh one otherwise.
        /// </summary>
        internal static ReadBudget For(ReadOptions options, StructureId structure) =>
            options.Shared ?? new ReadBudget(options.MemoryBudget, structure);

        /// <summary>The most this read may allocate.</summary>
        internal long Limit { get; }

        /// <summary>What this read has been charged so far.</summary>
        internal long Charged { get; private set; }

        /// <summary>
        /// Takes <paramref name="bytes"/> from the allowance, or refuses the payload if
        /// that would pass the limit. Called before the allocation it pays for.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The allowance is spent; the inner exception is a
        /// <see cref="ReadBudgetExceededException"/> saying by how much.
        /// </exception>
        internal void Charge(long bytes)
        {
            var total = bytes > long.MaxValue - this.Charged ? long.MaxValue : this.Charged + bytes;
            if (total > this.Limit)
            {
                var why = new ReadBudgetExceededException(this.structure, total, this.Limit);
                throw new InvalidDataException(why.Message, why);
            }

            this.Charged = total;
        }

        /// <summary>
        /// What an array of <paramref name="count"/> elements of
        /// <paramref name="elementSize"/> bytes costs, rounded up to the runtime's
        /// eight-byte alignment, and saturating rather than wrapping.
        /// </summary>
        internal static long ArrayOf(ulong count, int elementSize)
        {
            if (count > (ulong)(long.MaxValue - ArrayHeader - 7) / (ulong)elementSize)
            {
                return long.MaxValue;
            }

            var bytes = (long)count * elementSize + ArrayHeader;
            return (bytes + 7) & ~7L;
        }

        /// <summary>
        /// Sums costs, saturating rather than wrapping, so that a product of claimed sizes
        /// too large for a long is refused rather than read as a small one.
        /// </summary>
        internal static long Sum(params long[] costs)
        {
            var total = 0L;
            foreach (var cost in costs)
            {
                total = cost > long.MaxValue - total ? long.MaxValue : total + cost;
            }
            return total;
        }

        /// <summary><paramref name="count"/> times <paramref name="each"/>, saturating.</summary>
        internal static long Times(ulong count, long each)
        {
            if (count == 0 || each == 0)
            {
                return 0;
            }
            return count > (ulong)(long.MaxValue / each) ? long.MaxValue : (long)count * each;
        }
    }
}
