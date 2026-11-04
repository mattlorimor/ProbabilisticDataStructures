using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProbabilisticDataStructures;

namespace TestProbabilisticDataStructures
{
    /// <summary>
    /// Two structures here make random choices as part of what they are: the stable
    /// filter decrements randomly chosen cells to make room, and the cuckoo filter
    /// evicts a randomly chosen entry when both of an item's buckets are full. Both can
    /// now be seeded, which is what makes their behavior assertable at all rather than
    /// only describable.
    /// </summary>
    [TestClass]
    public class TestSeededRandomness
    {
        private static byte[] Key(string s) => Encoding.ASCII.GetBytes(s);

        private static string Fingerprint<T>(T structure) where T : IBinaryPersistable<T>
        {
            return Convert.ToHexString(
                System.IO.Hashing.XxHash3.Hash(structure.ToByteArray()));
        }

        /// <summary>
        /// An <c>int</c> seed as the filters took it before their seeds were 64-bit: widened
        /// with sign extension, the conversion the CHANGELOG gives a caller with a negative
        /// one. The helpers here keep <c>int</c> rows, and so their pinned sequences, by
        /// passing each through it.
        /// </summary>
        private static ulong? AsTheIntSeedWas(int? seed) =>
            seed is null ? null : unchecked((ulong)seed.Value);

        private static StableBloomFilter FilledStable(int? seed)
        {
            var f = new StableBloomFilter(1000, 2, 0.01, seed: AsTheIntSeedWas(seed));
            Feed(f);

            return f;
        }

        /// <summary>
        /// The stream FilledStable puts through a filter, kept apart so a test can put
        /// the same one through a filter again -- a reset one, for instance.
        /// </summary>
        private static void Feed(StableBloomFilter f)
        {
            for (int i = 0; i < 3000; i++)
            {
                f.Add(Key($"w{i}"));
            }
        }

        /// <summary>
        /// Enough items that both of some item's buckets fill and the eviction path
        /// runs. A load near the stated capacity never reaches the random choice at all,
        /// so this goes well past it: 100 items sizes the filter at 32 buckets of 4, and
        /// the evictions start once those 128 entries begin to collide.
        /// </summary>
        private static CuckooBloomFilter FilledCuckoo(int? seed)
        {
            var f = new CuckooBloomFilter(100, 0.01, seed: AsTheIntSeedWas(seed));
            FeedCuckoo(f);

            return f;
        }

        /// <summary>
        /// The stream FilledCuckoo puts through a filter, kept apart so a test can put
        /// the same one through a filter again -- a reset one, for instance.
        /// </summary>
        private static void FeedCuckoo(CuckooBloomFilter f)
        {
            for (int i = 0; i < 3000; i++)
            {
                f.Add(Key($"w{i}"));
            }
        }

        [TestMethod]
        public void TestTheSameSeedGivesTheSameStableFilter()
        {
            Assert.AreEqual(Fingerprint(FilledStable(7)), Fingerprint(FilledStable(7)));
        }

        [TestMethod]
        public void TestADifferentSeedGivesADifferentStableFilter()
        {
            Assert.AreNotEqual(Fingerprint(FilledStable(7)), Fingerprint(FilledStable(8)),
                "the seed made no difference, so the random decrements are not being seeded");
        }

        [TestMethod]
        public void TestTheSameSeedGivesTheSameCuckooFilter()
        {
            Assert.AreEqual(Fingerprint(FilledCuckoo(7)), Fingerprint(FilledCuckoo(7)));
        }

        [TestMethod]
        public void TestADifferentSeedGivesADifferentCuckooFilter()
        {
            Assert.AreNotEqual(Fingerprint(FilledCuckoo(7)), Fingerprint(FilledCuckoo(8)),
                "the seed made no difference, so the eviction choice is not being seeded");
        }

        /// <summary>
        /// A seed is the generator's whole 64-bit starting state, as it is for every other
        /// seeded structure (#235): one past <see cref="int.MaxValue"/> is taken as given,
        /// and the same one gives the same filter. The payload says where the generator
        /// began, so that is checked to be the seed itself. The filter is also checked to
        /// differ from one seeded with what the seed would have been as an <c>int</c> of
        /// its low 32 bits, sign-extended, which is what the generator got before.
        /// </summary>
        [TestMethod]
        [DataRow(0x8000_0000UL)]
        [DataRow(0x1_0000_0007UL)]
        [DataRow(0xFFFF_FFFF_0000_0000UL)]
        public void TestASeedPastIntMaxValueSeedsTheWholeGenerator(ulong seed)
        {
            var asAnInt = unchecked((ulong)(int)(uint)seed);
            Assert.AreNotEqual(seed, asAnInt, "the row does not tell a 64-bit seed from an int");

            StableBloomFilter Stable(ulong s)
            {
                var f = new StableBloomFilter(1000, 2, 0.01, seed: s);
                Feed(f);
                return f;
            }

            Assert.AreEqual(seed,
                GeneratorPositions(new StableBloomFilter(1000, 2, 0.01, seed: seed).ToByteArray()).Began,
                "the stable filter's generator did not begin at the seed");
            Assert.AreEqual(Fingerprint(Stable(seed)), Fingerprint(Stable(seed)),
                "the same seed gave two different stable filters");
            Assert.AreNotEqual(Fingerprint(Stable(asAnInt)), Fingerprint(Stable(seed)),
                "the stable filter's seed lost its high bits");

            CuckooBloomFilter Cuckoo(ulong s)
            {
                var f = new CuckooBloomFilter(100, 0.01, seed: s);
                FeedCuckoo(f);
                return f;
            }

            Assert.AreEqual(seed,
                GeneratorPositions(new CuckooBloomFilter(100, 0.01, seed: seed).ToByteArray()).Began,
                "the cuckoo filter's generator did not begin at the seed");
            Assert.AreEqual(Fingerprint(Cuckoo(seed)), Fingerprint(Cuckoo(seed)),
                "the same seed gave two different cuckoo filters");
            Assert.AreNotEqual(Fingerprint(Cuckoo(asAnInt)), Fingerprint(Cuckoo(seed)),
                "the cuckoo filter's seed lost its high bits");
        }

        /// <summary>
        /// Leaving the seed out still gives an unpredictable filter, so seeding is a
        /// thing a caller asks for rather than something they now have by default.
        /// </summary>
        [TestMethod]
        public void TestOmittingTheSeedLeavesTheFilterUnpredictable()
        {
            var fingerprints = Enumerable.Range(0, 4)
                .Select(_ => Fingerprint(FilledStable(null)))
                .Distinct()
                .Count();

            Assert.IsGreaterThan(1, fingerprints,
                "four unseeded filters over 3000 adds came out identical");
        }

        /// <summary>
        /// A seeded filter still behaves like the filter it is. Seeding fixes which
        /// cells are decremented, not whether the decay happens.
        /// </summary>
        [TestMethod]
        public void TestSeedingDoesNotChangeWhatTheFilterDoes()
        {
            var seeded = FilledStable(7);
            var unseeded = FilledStable(null);

            // The stable filter forgets: after 3000 adds into 1000 cells, the earliest
            // items are gone from both, and the most recent are in both.
            Assert.IsFalse(seeded.Test(Key("w0")), "seeded filter should have forgotten w0");
            Assert.IsFalse(unseeded.Test(Key("w0")), "unseeded filter should have forgotten w0");
            Assert.IsTrue(seeded.Test(Key("w2999")), "seeded filter should hold the newest item");
            Assert.IsTrue(unseeded.Test(Key("w2999")), "unseeded filter should hold the newest item");

            // Their stable points agree, being a function of the parameters rather than
            // of the choices made along the way.
            Assert.AreEqual(unseeded.StablePoint(), seeded.StablePoint());
        }

        /// <summary>
        /// And a seeded cuckoo filter keeps its guarantee: everything it accepted is
        /// still findable, however the evictions fell out.
        /// </summary>
        [TestMethod]
        public void TestASeededCuckooFilterStillFindsWhatItAccepted()
        {
            var f = new CuckooBloomFilter(100, 0.01, seed: 7);

            var accepted = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 3000; i++)
            {
                if (f.Add(Key($"w{i}")))
                {
                    accepted.Add($"w{i}");
                }
            }

            Assert.IsGreaterThan(0, accepted.Count);
            foreach (var word in accepted)
            {
                Assert.IsTrue(f.Test(Key(word)), $"{word} was accepted and cannot be found");
            }
        }
        /// <summary>
        /// A filter that is written out and read back resumes the draw sequence it was
        /// partway through, rather than starting it over.
        /// <para>
        /// Storing the seed alone would not do this. The bits come back correct either
        /// way, but a filter re-seeded on read sits at the start of the sequence while
        /// the original sits wherever its adds left it, so the two answer differently
        /// from then on. What makes that worth guarding rather than tolerating is the
        /// case persistence exists for: a filter checkpointed on a schedule would replay
        /// the same first draws after every load, and the stable filter's bound on its
        /// false positive rate assumes its decay is spread across cells rather than
        /// aimed at the same ones after every restart.
        /// </para>
        /// <para>
        /// Asserted against a filter that was never written out, because two filters
        /// restored from the same payload agree under either design -- that comparison
        /// is the one this defect passes.
        /// </para>
        /// </summary>
        [TestMethod]
        public void TestARestoredStableFilterResumesItsDrawSequence()
        {
            var original = FilledStable(seed: 42);
            var restored = Persistence.FromByteArray<StableBloomFilter>(original.ToByteArray());

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restore differed before either filter was used again");

            for (int i = 3000; i < 9000; i++)
            {
                var key = Key($"w{i}");
                original.Add(key);
                restored.Add(key);
            }

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restored filter decayed different cells from the original");
        }

        [TestMethod]
        public void TestARestoredCuckooFilterResumesItsDrawSequence()
        {
            var original = FilledCuckoo(seed: 42);
            var restored = Persistence.FromByteArray<CuckooBloomFilter>(original.ToByteArray());

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restore differed before either filter was used again");

            for (int i = 3000; i < 9000; i++)
            {
                var key = Key($"w{i}");
                original.Add(key);
                restored.Add(key);
            }

            Assert.AreEqual(Fingerprint(original), Fingerprint(restored),
                "the restored filter evicted different entries from the original");
        }

        /// <summary>
        /// The case above, run the way it would actually arise: a long-lived filter
        /// checkpointed repeatedly rather than round-tripped once. A filter re-seeded on
        /// each read would draw the same numbers after every one of these loads.
        /// </summary>
        [TestMethod]
        public void TestCheckpointingRepeatedlyDoesNotReplayTheSameDraws()
        {
            var uninterrupted = FilledStable(seed: 42);
            var checkpointed = FilledStable(seed: 42);

            for (int round = 0; round < 20; round++)
            {
                // Save and reload between every batch, as a long-running process would.
                checkpointed = Persistence.FromByteArray<StableBloomFilter>(
                    checkpointed.ToByteArray());

                for (int i = 0; i < 500; i++)
                {
                    var key = Key($"r{round}-{i}");
                    uninterrupted.Add(key);
                    checkpointed.Add(key);
                }
            }

            Assert.AreEqual(Fingerprint(uninterrupted), Fingerprint(checkpointed),
                "twenty checkpoints left the filter somewhere the uninterrupted one never went");
        }

        /// <summary>
        /// A payload written before 6.0.0 carries no generator state, and is read rather
        /// than refused: every cell is exactly right, and only the sequence of cells the
        /// filter will decay next is unrecoverable, because it was never written down.
        /// </summary>
        [TestMethod]
        public void TestAPayloadWithoutAStoredGeneratorStateStillReads()
        {
            var v1 = ReadFixture("stablebloomfilter-v1.bin");
            Assert.AreEqual(1, v1[4] | (v1[5] << 8), "fixture is no longer a version 1 payload");

            var f = Persistence.FromByteArray<StableBloomFilter>(v1);
            Assert.AreEqual(1000u, f.Cells());

            // And it keeps working, which is the part that needs a generator at all.
            for (int i = 0; i < 2000; i++)
            {
                f.Add(Key($"later{i}"));
            }
        }

        // ------------------------------------------------------------------ Reset

        /// <summary>
        /// Where a stable or cuckoo filter's payload says its generator is now, and where
        /// it began: the last two u64 fields, ahead of the CRC, in the layout FORMAT.md
        /// freezes.
        /// Read from the bytes rather than from a member that exists only for the tests,
        /// so what is asserted is what a saved filter would carry.
        /// </summary>
        private static (ulong Now, ulong Began) GeneratorPositions(byte[] frame)
        {
            var end = frame.Length - 4;
            return (BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(end - 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(end - 8)));
        }

        /// <summary>
        /// A new filter of the shape FilledStable builds: what a reset one has to equal.
        /// </summary>
        private static byte[] NewStable(int seed) =>
            new StableBloomFilter(1000, 2, 0.01, seed: AsTheIntSeedWas(seed)).ToByteArray();

        private static void AddMore(StableBloomFilter f, string prefix, int count)
        {
            for (int i = 0; i < count; i++)
            {
                f.Add(Key($"{prefix}{i}"));
            }
        }

        /// <summary>
        /// Reset restores the original state, and the generator is part of it: a reset
        /// filter is a new one, byte for byte, for any seed. Zero and a negative seed
        /// are rows of their own because the seed is widened to the generator's 64 bits,
        /// and the state a seed becomes is the state Reset must return to.
        /// </summary>
        [TestMethod]
        [DataRow(7)]
        [DataRow(0)]
        [DataRow(-1)]
        public void TestAResetStableFilterIsANewOneByteForByte(int seed)
        {
            var fresh = NewStable(seed);
            var f = FilledStable(seed);

            // Without this, a filter whose adds never moved the generator would pass
            // by doing nothing at all.
            Assert.AreNotEqual(
                GeneratorPositions(fresh).Now, GeneratorPositions(f.ToByteArray()).Now,
                "the adds left the generator where it began, so a reset has nothing to undo");

            Assert.AreSame(f, f.Reset());

            CollectionAssert.AreEqual(fresh, f.ToByteArray(),
                "a reset filter differs from a new one built with the same seed");
        }

        /// <summary>
        /// What the restored generator is for, stated as behavior: the stream that took a
        /// filter to a state takes its reset self to the same state, because the reset
        /// filter makes the same random choices a second time.
        /// </summary>
        [TestMethod]
        public void TestAResetStableFilterRepeatsItsRandomSequence()
        {
            var f = FilledStable(seed: 7);
            var firstPass = f.ToByteArray();

            f.Reset();
            Feed(f);

            CollectionAssert.AreEqual(firstPass, f.ToByteArray(),
                "the same stream took the reset filter somewhere else, so it did not " +
                "repeat the random choices it made the first time");
        }

        /// <summary>
        /// The same property for a filter that was saved and loaded after use, where the
        /// original state can only come from the payload: the generator's current
        /// position is the wrong thing to write for it, and so is a reseed on read.
        /// </summary>
        [TestMethod]
        [DataRow(7)]
        [DataRow(-1)]
        public void TestAResetStableFilterThatWasSavedAndLoadedIsANewOneByteForByte(int seed)
        {
            var fresh = NewStable(seed);

            var loaded = Persistence.FromByteArray<StableBloomFilter>(
                FilledStable(seed).ToByteArray());
            loaded.Reset();
            CollectionAssert.AreEqual(fresh, loaded.ToByteArray(),
                "a filter used, saved, loaded and reset differs from a new one");

            // Saved again and again with more use between, as a checkpointed filter is.
            // Each write has to carry the origin forward rather than the position the
            // generator had reached when it wrote.
            var checkpointed = FilledStable(seed);
            for (int round = 0; round < 5; round++)
            {
                checkpointed = Persistence.FromByteArray<StableBloomFilter>(
                    checkpointed.ToByteArray());
                AddMore(checkpointed, $"r{round}-", 200);
            }

            checkpointed.Reset();
            CollectionAssert.AreEqual(fresh, checkpointed.ToByteArray(),
                "a filter checkpointed repeatedly and then reset differs from a new one");
        }

        /// <summary>
        /// And the other order: a filter reset and then saved still knows where it began,
        /// so a second reset after more use returns to the start as well.
        /// </summary>
        [TestMethod]
        public void TestAStableFilterResetBeforeBeingSavedStillKnowsWhereItBegan()
        {
            var f = FilledStable(seed: 7);
            f.Reset();

            var restored = Persistence.FromByteArray<StableBloomFilter>(f.ToByteArray());
            CollectionAssert.AreEqual(NewStable(7), restored.ToByteArray());

            Feed(restored);
            restored.Reset();
            CollectionAssert.AreEqual(NewStable(7), restored.ToByteArray(),
                "the loaded filter was reset to somewhere other than where it began");
        }

        /// <summary>
        /// An unseeded filter has no seed to restore, and a constant would be wrong in a
        /// different way: its original state is whatever unpredictable one it was built
        /// with, and Reset returns it there, not to a state any other filter shares.
        /// </summary>
        [TestMethod]
        public void TestAResetUnseededStableFilterReturnsToWhereItBegan()
        {
            var f = new StableBloomFilter(1000, 2, 0.01);
            var began = f.ToByteArray();
            var (now, origin) = GeneratorPositions(began);
            Assert.AreEqual(origin, now, "a new filter's generator is where it began");

            Feed(f);
            var used = f.ToByteArray();
            Assert.AreNotEqual(now, GeneratorPositions(used).Now,
                "the adds left the generator where it began, so a reset has nothing to undo");

            f.Reset();
            CollectionAssert.AreEqual(began, f.ToByteArray(),
                "a reset unseeded filter is not back where it was built");

            var restored = Persistence.FromByteArray<StableBloomFilter>(used);
            restored.Reset();
            CollectionAssert.AreEqual(began, restored.ToByteArray(),
                "an unseeded filter that was saved and loaded is not back where it was built");
        }

        /// <summary>
        /// The unstable constructor builds its filter without a seed and the generator
        /// starts at zero, whose draws go unused because nothing is decayed. They are
        /// still stored, so a reset one has to return to that state as well.
        /// </summary>
        [TestMethod]
        public void TestAResetUnstableFilterIsANewOneByteForByte()
        {
            var fresh = StableBloomFilter.NewUnstableBloomFilter(200, 0.01).ToByteArray();
            var f = StableBloomFilter.NewUnstableBloomFilter(200, 0.01);
            AddMore(f, "u", 300);
            Assert.AreNotEqual(
                GeneratorPositions(fresh).Now, GeneratorPositions(f.ToByteArray()).Now,
                "the adds left the generator where it began, so a reset has nothing to undo");

            var restored = Persistence.FromByteArray<StableBloomFilter>(f.ToByteArray());

            f.Reset();
            CollectionAssert.AreEqual(fresh, f.ToByteArray());

            restored.Reset();
            CollectionAssert.AreEqual(fresh, restored.ToByteArray());
        }

        /// <summary>
        /// A payload from before the original state was recorded has none to give back.
        /// The filter takes the position it was read at as its beginning, so Reset
        /// returns it there: emptied, with the generator where the payload put it. That
        /// is the earliest position the payload tells us about, and unlike a guess at a
        /// seed it is one the stored bytes support.
        /// </summary>
        [TestMethod]
        public void TestResetOnAFilterReadFromAVersionTwoPayloadReturnsToWhereItWasRead()
        {
            var stored = ReadFixture("stablebloomfilter-v2.bin");
            Assert.AreEqual(2, stored[4] | (stored[5] << 8), "fixture is no longer a version 2 payload");
            var readAt = BinaryPrimitives.ReadUInt64LittleEndian(stored.AsSpan(stored.Length - 12));

            var f = Persistence.FromByteArray<StableBloomFilter>(stored);
            Assert.AreEqual((readAt, readAt), GeneratorPositions(f.ToByteArray()),
                "a filter read from a version 2 payload should begin where it was read");

            AddMore(f, "later", 500);
            Assert.AreNotEqual(readAt, GeneratorPositions(f.ToByteArray()).Now,
                "the adds left the generator where it was read, so a reset has nothing to undo");

            f.Reset();
            var reset = f.ToByteArray();
            Assert.AreEqual((readAt, readAt), GeneratorPositions(reset));

            // Emptied too: everything before the two generator fields and the CRC is
            // what an empty filter of this shape writes.
            var empty = NewStable(7);
            CollectionAssert.AreEqual(empty[..^20], reset[..^20]);
        }

        /// <summary>
        /// The same for a version 1 payload, which recorded no generator state at all and
        /// so was given an unpredictable one on read. That one is where it begins.
        /// </summary>
        [TestMethod]
        public void TestResetOnAFilterReadFromAVersionOnePayloadReturnsToWhereItWasRead()
        {
            var f = Persistence.FromByteArray<StableBloomFilter>(
                ReadFixture("stablebloomfilter-v1.bin"));
            var (readAt, began) = GeneratorPositions(f.ToByteArray());
            Assert.AreEqual(readAt, began,
                "a filter read from a version 1 payload should begin where it was read");

            AddMore(f, "later", 500);
            Assert.AreNotEqual(readAt, GeneratorPositions(f.ToByteArray()).Now,
                "the adds left the generator where it was read, so a reset has nothing to undo");

            f.Reset();
            var reset = f.ToByteArray();
            Assert.AreEqual((readAt, readAt), GeneratorPositions(reset));
            CollectionAssert.AreEqual(NewStable(7)[..^20], reset[..^20]);
        }

        /// <summary>
        /// Saving a filter read from an old payload must not change where it resets to.
        /// The write has to record the position it began at, not the one it has reached
        /// since -- otherwise a save and a load would move the target of every later Reset.
        /// </summary>
        [TestMethod]
        public void TestSavingAFilterReadFromAnOldPayloadDoesNotMoveItsReset()
        {
            foreach (var fixture in new[] { "stablebloomfilter-v1.bin", "stablebloomfilter-v2.bin" })
            {
                var f = Persistence.FromByteArray<StableBloomFilter>(ReadFixture(fixture));
                AddMore(f, "later", 500);

                var saved = f.ToByteArray();
                var reloaded = Persistence.FromByteArray<StableBloomFilter>(saved);

                f.Reset();
                reloaded.Reset();

                CollectionAssert.AreEqual(f.ToByteArray(), reloaded.ToByteArray(),
                    $"{fixture}: saving and loading moved where the filter resets to");
            }
        }

        // --------------------------------------------------------- Reset, cuckoo

        /// <summary>
        /// A new filter of the shape FilledCuckoo builds: what a reset one has to equal.
        /// </summary>
        private static byte[] NewCuckoo(int seed) =>
            new CuckooBloomFilter(100, 0.01, seed: AsTheIntSeedWas(seed)).ToByteArray();

        private static void AddMore(CuckooBloomFilter f, string prefix, int count)
        {
            for (int i = 0; i < count; i++)
            {
                f.Add(Key($"{prefix}{i}"));
            }
        }

        /// <summary>
        /// Adds until the filter has refused a few inserts, which is the point at which
        /// its generator has certainly moved: the generator is drawn from only when both
        /// of an item's buckets are full, and a refusal has spent a whole eviction budget
        /// of draws. A fixed count of adds would depend on how oversized the filter in
        /// hand is -- the oldest stored payloads are far larger than their capacity.
        /// </summary>
        private static void Saturate(CuckooBloomFilter f, string prefix)
        {
            var refused = 0;
            for (int i = 0; i < 200_000 && refused < 3; i++)
            {
                if (!f.Add(Key($"{prefix}{i}")))
                {
                    refused++;
                }
            }

            Assert.AreEqual(3, refused, "the filter never filled, so its generator was never drawn");
        }

        /// <summary>
        /// The six u32 fields that open a cuckoo payload: buckets, entries per bucket,
        /// the fingerprint width, the count, the capacity and the number of occupied
        /// entries. Read from the frame rather than from the filter, as the stored
        /// bytes would be.
        /// </summary>
        private static uint[] CuckooHeader(byte[] frame) =>
            Enumerable.Range(0, 6)
                .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(14 + 4 * i)))
                .ToArray();

        /// <summary>
        /// Everything about a cuckoo filter's shape is unchanged by a reset, and the
        /// count and the occupied entries are gone.
        /// </summary>
        private static void AssertEmptiedKeepingShape(byte[] before, byte[] after, string fixture)
        {
            var was = CuckooHeader(before);
            CollectionAssert.AreEqual(
                new[] { was[0], was[1], was[2], 0u, was[4], 0u }, CuckooHeader(after),
                $"{fixture}: Reset must empty the filter and leave its shape alone");
        }

        /// <summary>
        /// The payload says where the generator is and where it began, in that order,
        /// and a new filter has both at the seed -- widened to 64 bits, so that a
        /// negative seed becomes all the high bits set and not a small number.
        /// </summary>
        [TestMethod]
        [DataRow(7, 7UL)]
        [DataRow(0, 0UL)]
        [DataRow(-1, ulong.MaxValue)]
        public void TestACuckooPayloadEndsWithWhereTheGeneratorIsAndWhereItBegan(int seed, ulong began)
        {
            var fresh = NewCuckoo(seed);
            Assert.AreEqual(6, fresh[4] | (fresh[5] << 8), "a cuckoo filter should write version 6");
            Assert.AreEqual((began, began), GeneratorPositions(fresh));

            var used = FilledCuckoo(seed).ToByteArray();
            var (now, origin) = GeneratorPositions(used);
            Assert.AreEqual(began, origin, "the adds moved where the generator began");
            Assert.AreNotEqual(began, now, "the adds left the generator where it began");
        }

        /// <summary>
        /// Reset restores the original state, and the generator is part of it: a reset
        /// filter is a new one, byte for byte, for any seed. Zero and a negative seed
        /// are rows of their own because the seed is widened to the generator's 64 bits,
        /// and the state a seed becomes is the state Reset must return to.
        /// </summary>
        [TestMethod]
        [DataRow(7)]
        [DataRow(0)]
        [DataRow(-1)]
        public void TestAResetCuckooFilterIsANewOneByteForByte(int seed)
        {
            var fresh = NewCuckoo(seed);
            var f = FilledCuckoo(seed);

            // Without this, a filter whose adds never drew from the generator would pass
            // by doing nothing at all.
            Assert.AreNotEqual(
                GeneratorPositions(fresh).Now, GeneratorPositions(f.ToByteArray()).Now,
                "the adds left the generator where it began, so a reset has nothing to undo");
            Assert.IsGreaterThan(0u, f.Count());

            Assert.AreSame(f, f.Reset());

            Assert.AreEqual(0u, f.Count());
            CollectionAssert.AreEqual(fresh, f.ToByteArray(),
                "a reset filter differs from a new one built with the same seed");
        }

        /// <summary>
        /// What the restored generator is for, stated as behavior: the stream that took a
        /// filter to a state takes its reset self to the same state, because the reset
        /// filter makes the same eviction choices a second time.
        /// </summary>
        [TestMethod]
        public void TestAResetCuckooFilterRepeatsItsRandomSequence()
        {
            var f = FilledCuckoo(seed: 7);
            var firstPass = f.ToByteArray();

            f.Reset();
            FeedCuckoo(f);

            CollectionAssert.AreEqual(firstPass, f.ToByteArray(),
                "the same stream took the reset filter somewhere else, so it did not " +
                "repeat the eviction choices it made the first time");
        }

        /// <summary>
        /// The same property for a filter that was saved and loaded after use, where the
        /// original state can only come from the payload: the generator's current
        /// position is the wrong thing to write for it, and so is a reseed on read.
        /// </summary>
        [TestMethod]
        [DataRow(7)]
        [DataRow(-1)]
        public void TestAResetCuckooFilterThatWasSavedAndLoadedIsANewOneByteForByte(int seed)
        {
            var fresh = NewCuckoo(seed);

            var loaded = Persistence.FromByteArray<CuckooBloomFilter>(
                FilledCuckoo(seed).ToByteArray());
            loaded.Reset();
            CollectionAssert.AreEqual(fresh, loaded.ToByteArray(),
                "a filter used, saved, loaded and reset differs from a new one");

            // Saved again and again with more use between, as a checkpointed filter is.
            // Each write has to carry the origin forward rather than the position the
            // generator had reached when it wrote.
            var checkpointed = FilledCuckoo(seed);
            for (int round = 0; round < 5; round++)
            {
                checkpointed = Persistence.FromByteArray<CuckooBloomFilter>(
                    checkpointed.ToByteArray());
                AddMore(checkpointed, $"r{round}-", 200);
            }

            checkpointed.Reset();
            CollectionAssert.AreEqual(fresh, checkpointed.ToByteArray(),
                "a filter checkpointed repeatedly and then reset differs from a new one");
        }

        /// <summary>
        /// And the other order: a filter reset and then saved still knows where it began,
        /// so a second reset after more use returns to the start as well.
        /// </summary>
        [TestMethod]
        public void TestACuckooFilterResetBeforeBeingSavedStillKnowsWhereItBegan()
        {
            var f = FilledCuckoo(seed: 7);
            f.Reset();

            var restored = Persistence.FromByteArray<CuckooBloomFilter>(f.ToByteArray());
            CollectionAssert.AreEqual(NewCuckoo(7), restored.ToByteArray());

            FeedCuckoo(restored);
            restored.Reset();
            CollectionAssert.AreEqual(NewCuckoo(7), restored.ToByteArray(),
                "the loaded filter was reset to somewhere other than where it began");
        }

        /// <summary>
        /// An unseeded filter has no seed to restore, and a constant would be wrong in a
        /// different way: its original state is whatever unpredictable one it was built
        /// with, and Reset returns it there, not to a state any other filter shares.
        /// </summary>
        [TestMethod]
        public void TestAResetUnseededCuckooFilterReturnsToWhereItBegan()
        {
            var f = new CuckooBloomFilter(100, 0.01);
            var began = f.ToByteArray();
            var (now, origin) = GeneratorPositions(began);
            Assert.AreEqual(origin, now, "a new filter's generator is where it began");

            FeedCuckoo(f);
            var used = f.ToByteArray();
            Assert.AreNotEqual(now, GeneratorPositions(used).Now,
                "the adds left the generator where it began, so a reset has nothing to undo");

            f.Reset();
            CollectionAssert.AreEqual(began, f.ToByteArray(),
                "a reset unseeded filter is not back where it was built");

            var restored = Persistence.FromByteArray<CuckooBloomFilter>(used);
            restored.Reset();
            CollectionAssert.AreEqual(began, restored.ToByteArray(),
                "an unseeded filter that was saved and loaded is not back where it was built");
        }

        /// <summary>
        /// A payload from before the original state was recorded has none to give back.
        /// The filter takes the position it was read at as its beginning, so Reset
        /// returns it there: emptied, with the generator where the payload put it. That
        /// is the earliest position the payload tells us about, and unlike a guess at a
        /// seed it is one the stored bytes support. Version 1 holds no position at all,
        /// so the one it was given on reading, which is unpredictable, is its beginning.
        /// </summary>
        [TestMethod]
        [DataRow("cuckoobloomfilter-v1.bin", 1)]
        [DataRow("cuckoobloomfilter-v2.bin", 2)]
        [DataRow("cuckoobloomfilter-v3.bin", 3)]
        public void TestResetOnACuckooFilterReadFromAnOldPayloadReturnsToWhereItWasRead(
            string fixture, int version)
        {
            var stored = ReadFixture(fixture);
            Assert.AreEqual(version, stored[4] | (stored[5] << 8),
                $"{fixture} is no longer a version {version} payload");

            var f = Persistence.FromByteArray<CuckooBloomFilter>(stored);
            var asRead = f.ToByteArray();
            var (readAt, began) = GeneratorPositions(asRead);
            Assert.AreEqual(readAt, began,
                $"a filter read from a version {version} payload should begin where it was read");
            if (version >= 2)
            {
                // The positions the payload held are the ones the filter carries, and
                // not a fresh draw.
                Assert.AreEqual(
                    BinaryPrimitives.ReadUInt64LittleEndian(stored.AsSpan(stored.Length - 12)),
                    readAt, "the generator was not read as stored");
            }

            Saturate(f, "later");
            Assert.AreNotEqual(readAt, GeneratorPositions(f.ToByteArray()).Now,
                "the adds left the generator where it was read, so a reset has nothing to undo");

            f.Reset();
            var reset = f.ToByteArray();
            Assert.AreEqual((readAt, readAt), GeneratorPositions(reset));
            AssertEmptiedKeepingShape(asRead, reset, fixture);
        }

        /// <summary>
        /// Saving a filter read from an old payload must not change where it resets to.
        /// The write has to record the position it began at, not the one it has reached
        /// since -- otherwise a save and a load would move the target of every later Reset.
        /// </summary>
        [TestMethod]
        public void TestSavingACuckooFilterReadFromAnOldPayloadDoesNotMoveItsReset()
        {
            foreach (var fixture in new[]
            {
                "cuckoobloomfilter-v1.bin", "cuckoobloomfilter-v2.bin", "cuckoobloomfilter-v3.bin",
            })
            {
                var f = Persistence.FromByteArray<CuckooBloomFilter>(ReadFixture(fixture));
                Saturate(f, "later");

                var saved = f.ToByteArray();
                var reloaded = Persistence.FromByteArray<CuckooBloomFilter>(saved);

                f.Reset();
                reloaded.Reset();

                CollectionAssert.AreEqual(f.ToByteArray(), reloaded.ToByteArray(),
                    $"{fixture}: saving and loading moved where the filter resets to");
            }
        }

        private static byte[] ReadFixture(string name)
        {
            using var stream = typeof(TestSeededRandomness).Assembly.GetManifestResourceStream(
                $"TestProbabilisticDataStructures.fixtures.{name}")
                ?? throw new InvalidOperationException($"fixture {name} is not embedded");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

    }
}
