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
    /// checked could ask for gigabytes. The budget bounds that. A reader works out the
    /// whole of what it will allocate, from what the header and the payload claim, before
    /// allocating any of it, and refuses a payload that would take more than the budget:
    /// an <see cref="InvalidDataException"/> whose inner exception is a
    /// <see cref="ReadBudgetExceededException"/> naming that whole, which a budget of the
    /// same reads.
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
        /// How many bytes the whole read takes: the payload, everything its sizes say the
        /// structure is built from, and any structure nested inside it. A budget of this
        /// many reads the payload, if it is well formed.
        /// </summary>
        /// <remarks>
        /// Worked out from the payload's header and its size fields before anything they
        /// claim is allocated, so it is the same whatever budget the read was refused
        /// under. A payload that ends early, or is malformed before its sizes have all
        /// been read, is priced as far as it can be, and under a budget that covers that
        /// it is refused for what is wrong with it.
        /// </remarks>
        public long RequiredBytes { get; }

        /// <summary>The budget the read was given, in bytes.</summary>
        public long MemoryBudget { get; }

        /// <summary>
        /// The message. It names the total once, as the literal in the call that reads the
        /// payload, its digits grouped the way C# allows, so that the number a person reads
        /// is the one they paste.
        /// </summary>
        private static string Describe(string structure, long required, long budget)
        {
            var allows = budget.ToString("N0", CultureInfo.InvariantCulture);
            var literal = required.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '_');
            return $"Reading this {structure} takes more memory than the read budget of " +
                $"{allows} bytes allows. The budget stops a small payload from asking for a " +
                "large allocation. To read a payload you trust that is this large, pass a " +
                "budget that covers the whole read, worked out from the payload before " +
                $"anything was allocated: {structure}.ReadFrom(stream, new ReadOptions({literal})), " +
                "or ReadOptions.Unlimited.";
        }
    }

    /// <summary>
    /// The allowance one read draws on: priced whole before the read allocates anything a
    /// payload claims, then charged before each of those allocations.
    /// </summary>
    /// <remarks>
    /// A read happens in two passes over the same payload. The first prices it: each
    /// structure's <c>Price</c> walks the payload's fields in the order its reader reads
    /// them, adding up what the reader is going to allocate, through
    /// <see cref="Price(long)"/>, and allocating nothing itself. <see cref="Settle"/>
    /// then compares the whole with the limit, once, so that a refusal names one total
    /// and a budget of that total loads the payload. The second pass is the reader, which
    /// charges each allocation before making it, through <see cref="Charge(long)"/>,
    /// against that price.
    /// <para>
    /// The price and the charges are the same sums, made by the same helpers, so a read
    /// spends exactly what it was priced at; the read-budget tests hold the two equal. A
    /// charge past the price is a price that missed something. The read still goes no
    /// further than the limit allows: the charge is then held to the limit as charges
    /// were before there was a price, so a defect in a price can make a refusal name too
    /// little, but never let a read past its budget.
    /// </para>
    /// <para>
    /// Both are upper bounds on what the read allocates, in bytes as the runtime counts
    /// them: an array's elements and its header, and a small fixed amount for each
    /// object that is not an array. They are what the read-budget tests hold the
    /// measured allocation to, so a price that is too small fails a test rather than
    /// letting a read past its budget.
    /// </para>
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

        /// <summary>
        /// What a reader is charged for an object that is not an array, where it makes it: the
        /// general bound, <see cref="SmallObject"/>, or <paramref name="measured"/>, what the
        /// object was measured at on a 64-bit runtime, where that is more (#346).
        /// </summary>
        internal static long Object(long measured) => Math.Max(measured, SmallObject);

        // Named only in a refusal. An enum's names are kept in a cache the runtime holds
        // weakly, and naming the structure as every budget was made built them again after
        // any collection that had reclaimed them, so what a read allocated depended on when
        // the last collection ran (#287).
        private readonly StructureId structure;

        internal ReadBudget(long limit, StructureId structure)
        {
            this.Limit = limit;
            this.structure = structure;
        }

        /// <summary>
        /// The budget a read of <paramref name="structure"/> draws on: the shared one if
        /// it is nested inside another read, a fresh one otherwise.
        /// </summary>
        internal static ReadBudget For(ReadOptions options, StructureId structure) =>
            options.Shared ?? new ReadBudget(options.MemoryBudget, structure);

        /// <summary>The most this read may allocate.</summary>
        internal long Limit { get; }

        /// <summary>
        /// What the whole read is priced at, once it has been settled; while it is being
        /// priced, the price so far.
        /// </summary>
        internal long Charged { get; private set; }

        /// <summary>What the read has charged against its price since it was settled.</summary>
        internal long Spent { get; private set; }

        /// <summary>
        /// Whether the read has been priced and admitted, so that what it reads next --
        /// a structure nested in it included -- draws on that price rather than adding to
        /// it.
        /// </summary>
        internal bool IsSettled { get; private set; }

        /// <summary>
        /// Adds <paramref name="bytes"/> to the price of the read, while it is worked out.
        /// Never refuses: the whole is compared with the limit once it is known.
        /// </summary>
        internal void Price(long bytes)
        {
            this.Charged = Sum(this.Charged, bytes);
        }

        /// <summary>
        /// Admits the read at its price, or refuses it, naming the whole of it, if that
        /// is more than the limit.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The price is more than the limit; the inner exception is a
        /// <see cref="ReadBudgetExceededException"/> naming both.
        /// </exception>
        internal void Settle()
        {
            if (this.Charged > this.Limit)
            {
                throw this.Refusal(this.Charged);
            }

            this.IsSettled = true;
        }

        /// <summary>
        /// Draws <paramref name="bytes"/> on the read's price, before the allocation it
        /// pays for.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The read has been charged past its price, and past the limit; the inner
        /// exception is a <see cref="ReadBudgetExceededException"/>. Only a price that
        /// missed something gets here.
        /// </exception>
        internal void Charge(long bytes)
        {
            this.Spent = Sum(this.Spent, bytes);
            if (this.Spent > this.Charged)
            {
                if (this.Spent > this.Limit)
                {
                    throw this.Refusal(this.Spent);
                }

                this.Charged = this.Spent;
            }
        }

        /// <summary>The refusal of a read that takes <paramref name="required"/> bytes.</summary>
        internal InvalidDataException Refusal(long required)
        {
            var why = new ReadBudgetExceededException(this.structure.ToString(), required, this.Limit);
            return new InvalidDataException(why.Message, why);
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
        /// <remarks>
        /// A span rather than an array of parameters, so that charging allocates nothing
        /// itself. As an array it cost 48 bytes a call, which a read that charges once per
        /// nested sketch paid hundreds of times over, uncharged.
        /// </remarks>
        internal static long Sum(params ReadOnlySpan<long> costs)
        {
            var total = 0L;
            foreach (var cost in costs)
            {
                total = cost > long.MaxValue - total ? long.MaxValue : total + cost;
            }
            return total;
        }

        /// <summary>
        /// What a <see cref="System.Collections.Generic.List{T}"/> grown by adding to it,
        /// from empty, to <paramref name="count"/> elements of
        /// <paramref name="elementSize"/> bytes allocates: the list and every array it
        /// outgrew on the way.
        /// </summary>
        internal static long ListOf(ulong count, int elementSize)
        {
            // A list starts empty, takes four on its first add, and doubles each time it
            // is full, so filling it to count allocates every capacity from four up to
            // the first at or above it. Summed exactly; the doublings are few.
            var total = SmallObject;
            if (count == 0)
            {
                return total;
            }

            for (ulong capacity = 4; ; capacity *= 2)
            {
                total = Sum(total, ArrayOf(capacity, elementSize));
                if (capacity >= count || total == long.MaxValue)
                {
                    return total;
                }
            }
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
