# Probabilistic Data Structures for C<span>#</span> [![Build status](https://ci.appveyor.com/api/projects/status/s9gwqptvy0jbjsmp?svg=true)](https://ci.appveyor.com/project/mattlorimor/probabilisticdatastructures)

This is a C# port of [Tyler Treat's](https://github.com/tylertreat) work in the [BoomFilters](https://github.com/tylertreat/BoomFilters) golang project. His writing on probabilistic data structures and other computing-related activities can be found here: http://bravenewgeek.com/.

If you're on this page, you probably already know a bit about probabilistic data structures and why you might want to use them. To keep this README smaller, I'll remove some of the exposition Tyler does and keep this closer to a "How to Use" document. I would refer you to [his project's README](https://github.com/tylertreat/BoomFilters/blob/master/README.md) if you are trying to get all the information you possibly can.

The descriptions for each filter were lifted directly from the BoomFilters' README.

## Included Structures
* [Count-Min Sketch](https://github.com/mattlorimor/ProbabilisticDataStructures#count-min-sketch)
* [Counting Bloom filter](https://github.com/mattlorimor/ProbabilisticDataStructures#counting-bloom-filter)
* [Cuckoo filter](https://github.com/mattlorimor/ProbabilisticDataStructures#cuckoo-filter)
* Deletable Bloom filter
* [HyperLogLog](https://github.com/mattlorimor/ProbabilisticDataStructures#hyperloglog)
* [Inverse Bloom filter](https://github.com/mattlorimor/ProbabilisticDataStructures#inverse-bloom-filter)
* [MinHash](https://github.com/mattlorimor/ProbabilisticDataStructures#minhash)
* PartitionedBloomFilter
* [Scalable Bloom filter](https://github.com/mattlorimor/ProbabilisticDataStructures#scalable-bloom-filter)
* [Stable Bloom filter](https://github.com/mattlorimor/ProbabilisticDataStructures#stable-bloom-filter)
* [TopK](https://github.com/mattlorimor/ProbabilisticDataStructures#top-k)

## Releases 

For now: https://github.com/mattlorimor/ProbabilisticDataStructures/releases

Future: NuGet

## Contributions
Pull-requests are welcome, but submitting an issue is probably the best place to start if you have complex critiques or suggestions.

## Stable Bloom Filter

This is an implementation of Stable Bloom Filters as described by Deng and Rafiei in [Approximately Detecting Duplicates for Streaming Data using Stable Bloom Filters](http://webdocs.cs.ualberta.ca/~drafiei/papers/DupDet06Sigmod.pdf).

A Stable Bloom Filter (SBF) continuously evicts stale information so that it has room for more recent elements. Like traditional Bloom filters, an SBF has a non-zero probability of false positives, which is controlled by several parameters. Unlike the classic Bloom filter, an SBF has a tight upper bound on the rate of false positives while introducing a non-zero rate of false negatives. The false-positive rate of a classic Bloom filter eventually reaches 1, after which all queries result in a false positive. The stable-point property of an SBF means the false-positive rate asymptotically approaches a configurable fixed constant. A classic Bloom filter is actually a special case of SBF where the eviction rate is zero and the cell size is one, so this provides support for them as well (in addition to bitset-based Bloom filters).

Stable Bloom Filters are useful for cases where the size of the data set isn't known a priori and memory is bounded. For example, an SBF can be used to deduplicate events from an unbounded event stream with a specified upper bound on false positives and minimal false negatives.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
            byte[] B_BYTES = Encoding.ASCII.GetBytes("b");

            var sbf = StableBloomFilter.NewDefaultStableBloomFilter(10000, 0.01);
            Console.WriteLine(string.Format("stable point: {0}", sbf.StablePoint()));

            sbf.Add(A_BYTES);
            if (sbf.Test(A_BYTES))
            {
                Console.WriteLine("contains a");
            }

            if (!sbf.TestAndAdd(B_BYTES))
            {
                Console.WriteLine("doesn't contain b");
            }

            if (sbf.Test(B_BYTES))
            {
                Console.WriteLine("now it contains b!");
            }
        }
    }
}
```

## Scalable Bloom Filter

This is an implementation of a Scalable Bloom Filter as described by Almeida, Baquero, Preguica, and Hutchison in [Scalable Bloom Filters](http://gsd.di.uminho.pt/members/cbm/ps/dbloom.pdf).

A Scalable Bloom Filter (SBF) dynamically adapts to the size of the data set while enforcing a tight upper bound on the rate of false positives and a false-negative probability of zero. This works by adding Bloom filters with geometrically decreasing false-positive rates as filters become full. A tightening ratio, r, controls the filter growth. The compounded probability over the whole series converges to a target value, even accounting for an infinite series.

Scalable Bloom Filters are useful for cases where the size of the data set isn't known a priori and memory constraints aren't of particular concern. For situations where memory is bounded, consider using Inverse or Stable Bloom Filters.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
            byte[] B_BYTES = Encoding.ASCII.GetBytes("b");

            var sbf = ScalableBloomFilter.NewDefaultScalableBloomFilter(0.01);

            sbf.Add(A_BYTES);
            if (sbf.Test(A_BYTES))
            {
                Console.WriteLine("contains a");
            }

            if (!sbf.TestAndAdd(B_BYTES))
            {
                Console.WriteLine("doesn't contain b");
            }

            if (sbf.Test(B_BYTES))
            {
                Console.WriteLine("now it contains b!");
            }
        }
    }
}
```

## Inverse Bloom Filter

An Inverse Bloom Filter, or "the opposite of a Bloom filter", is a concurrent, probabilistic data structure used to test whether an item has been observed or not. This implementation, [originally described and written by Jeff Hodges](http://www.somethingsimilar.com/2012/05/21/the-opposite-of-a-bloom-filter/), replaces the use of MD5 hashing with a non-cryptographic FNV-1 function.

The Inverse Bloom Filter may report a false negative but can never report a false positive. That is, it may report that an item has not been seen when it actually has, but it will never report an item as seen which it hasn't come across. This behaves in a similar manner to a fixed-size hashmap which does not handle conflicts.

This structure is particularly well-suited to streams in which duplicates are relatively close together. It uses a CAS-style approach, which makes it thread-safe.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
            byte[] B_BYTES = Encoding.ASCII.GetBytes("b");

            var ibf = new InverseBloomFilter(10000);

            ibf.Add(A_BYTES);
            if (ibf.Test(A_BYTES))
            {
                Console.WriteLine("contains a");
            }

            if (!ibf.TestAndAdd(B_BYTES))
            {
                Console.WriteLine("doesn't contain b");
            }

            if (ibf.Test(B_BYTES))
            {
                Console.WriteLine("now it contains b!");
            }
        }
    }
}
```

## Counting Bloom Filter

This is an implementation of a Counting Bloom Filter as described by Fan, Cao, Almeida, and Broder in [Summary Cache: A Scalable Wide-Area Web Cache Sharing Protocol](http://pages.cs.wisc.edu/~jussara/papers/00ton.pdf).

A Counting Bloom Filter (CBF) provides a way to remove elements by using an array of n-bit buckets. When an element is added, the respective buckets are incremented. To remove an element, the respective buckets are decremented. A query checks that each of the respective buckets are non-zero. Because CBFs allow elements to be removed, they introduce a non-zero probability of false negatives in addition to the possibility of false positives.

Counting Bloom Filters are useful for cases where elements are both added and removed from the data set. Since they use n-bit buckets, CBFs use roughly n-times more memory than traditional Bloom filters.

See Deletable Bloom Filter for an alternative which avoids false negatives.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
            byte[] B_BYTES = Encoding.ASCII.GetBytes("b");

            var cbf = CountingBloomFilter.NewDefaultCountingBloomFilter(1000, 0.01);

            cbf.Add(A_BYTES);
            if (cbf.Test(A_BYTES))
            {
                Console.WriteLine("contains a");
            }

            if (!cbf.TestAndAdd(B_BYTES))
            {
                Console.WriteLine("doesn't contain b");
            }

            if (cbf.TestAndRemove(B_BYTES))
            {
                Console.WriteLine("removed b");
            }
        }
    }
}
```

## Cuckoo Filter

This is an implementation of a Cuckoo Filter as described by Andersen, Kaminsky, and Mitzenmacher in [Cuckoo Filter: Practically Better Than Bloom](http://www.pdl.cmu.edu/PDL-FTP/FS/cuckoo-conext2014.pdf). The Cuckoo Filter is similar to the Counting Bloom Filter in that it supports adding and removing elements, but it does so in a way that doesn't significantly degrade space and performance.

It works by using a cuckoo hashing scheme for inserting items. Instead of storing the elements themselves, it stores their fingerprints which also allows for item removal without false negatives (if you don't attempt to remove an item not contained in the filter).

For applications that store many items and target moderately low false-positive rates, cuckoo filters have lower space overhead than space-optimized Bloom filters.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
            byte[] B_BYTES = Encoding.ASCII.GetBytes("b");

            var cf = new CuckooBloomFilter(1000, 0.01);

            cf.Add(A_BYTES);
            if (cf.Test(A_BYTES))
            {
                Console.WriteLine("contains a");
            }

            if (!cf.TestAndAdd(B_BYTES).WasAlreadyAMember)
            {
                Console.WriteLine("doesn't contain b");
            }

            if (cf.TestAndRemove(B_BYTES))
            {
                Console.WriteLine("removed b");
            }
        }
    }
}
```

## Classic Bloom Filter

A classic Bloom filter is a special case of a Stable Bloom Filter whose eviction rate is zero and cell size is one. We call this special case an Unstable Bloom Filter. Because cells require more memory overhead, this package also provides two bitset-based Bloom filter variations. The first variation is the traditional implementation consisting of a single bit array. The second implementation is a partitioned approach which uniformly distributes the probability of false positives across all elements.

Bloom filters have a limited capacity, depending on the configured size. Once all bits are set, the probability of a false positive is 1. However, traditional Bloom filters cannot return a false negative.

A Bloom filter is ideal for cases where the data set is known a priori because the false-positive rate can be configured by the size and number of hash functions.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] A_BYTES = Encoding.ASCII.GetBytes("a");
            byte[] B_BYTES = Encoding.ASCII.GetBytes("b");

            var bf = new BloomFilter(1000, 0.01);

            bf.Add(A_BYTES);
            if (bf.Test(A_BYTES))
            {
                Console.WriteLine("contains a");
            }

            if (!bf.TestAndAdd(B_BYTES))
            {
                Console.WriteLine("doesn't contain b");
            }

            if (bf.Test(B_BYTES))
            {
                Console.WriteLine("now it contains b!");
            }
        }
    }
}
```

## Count-Min Sketch

This is an implementation of a Count-Min Sketch as described by Cormode and Muthukrishnan in [An Improved Data Stream Summary: The Count-Min Sketch and its Applications](http://dimacs.rutgers.edu/~graham/pubs/papers/cm-full.pdf).

A Count-Min Sketch (CMS) is a probabilistic data structure which approximates the frequency of events in a data stream. Unlike a hash map, a CMS uses sub-linear space at the expense of a configurable error factor. Similar to Counting Bloom filters, items are hashed to a series of buckets, which increment a counter. The frequency of an item is estimated by taking the minimum of each of the item's respective counter values.

Count-Min Sketches are useful for counting the frequency of events in massive data sets or unbounded streams online. In these situations, storing the entire data set or allocating counters for every event in memory is impractical. It may be possible for offline processing, but real-time processing requires fast, space-efficient solutions like the CMS. For approximating set cardinality, refer to the HyperLogLog.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] ALICE_BYTES = Encoding.ASCII.GetBytes("alice");
            byte[] BOB_BYTES = Encoding.ASCII.GetBytes("bob");
            byte[] FRANK_BYTES = Encoding.ASCII.GetBytes("frank");

            var cms = new CountMinSketch(0.001, 0.99);

            cms.Add(ALICE_BYTES).Add(BOB_BYTES).Add(BOB_BYTES).Add(FRANK_BYTES);
            Console.WriteLine(string.Format("frequency of alice: {0}", cms.Count(ALICE_BYTES)));
            Console.WriteLine(string.Format("frequency of bob: {0}", cms.Count(BOB_BYTES)));
            Console.WriteLine(string.Format("frequency of frank: {0}", cms.Count(FRANK_BYTES)));
        }
    }
}
```

## Top-K

Top-K uses a Count-Min Sketch and min-heap to track the top-k most frequent elements in a stream.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] ALICE_BYTES = Encoding.ASCII.GetBytes("alice");
            byte[] BOB_BYTES = Encoding.ASCII.GetBytes("bob");
            byte[] FRANK_BYTES = Encoding.ASCII.GetBytes("frank");
            byte[] TYLER_BYTES = Encoding.ASCII.GetBytes("tyler");
            byte[] FRED_BYTES = Encoding.ASCII.GetBytes("fred");
            byte[] JAMES_BYTES = Encoding.ASCII.GetBytes("james");
            byte[] SARA_BYTES = Encoding.ASCII.GetBytes("sara");
            byte[] BILL_BYTES = Encoding.ASCII.GetBytes("bill");

            var topK = new TopK(0.001, 0.99, 5);

            topK.Add(BOB_BYTES).Add(BOB_BYTES).Add(BOB_BYTES);
            topK.Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES).Add(TYLER_BYTES);
            topK.Add(FRED_BYTES);
            topK.Add(ALICE_BYTES).Add(ALICE_BYTES).Add(ALICE_BYTES).Add(ALICE_BYTES);
            topK.Add(JAMES_BYTES);
            topK.Add(FRED_BYTES);
            topK.Add(SARA_BYTES).Add(SARA_BYTES);
            topK.Add(BILL_BYTES);

            foreach (var element in topK.Elements())
            {
                Console.WriteLine(string.Format("element: {0}, frequency: {1}", Encoding.ASCII.GetString(element.Data), element.Freq));
            }
        }
    }
}
```

## HyperLogLog

This is an implementation of HyperLogLog as described by Flajolet, Fusy, Gandouet, and Meunier in [HyperLogLog: the analysis of a near-optimal cardinality estimation algorithm](http://algo.inria.fr/flajolet/Publications/FlFuGaMe07.pdf).

HyperLogLog is a probabilistic algorithm which approximates the number of distinct elements in a multiset. It works by hashing values and calculating the maximum number of leading zeros in the binary representation of each hash. If the maximum number of leading zeros is n, the estimated number of distinct elements in the set is 2^n. To minimize variance, the multiset is split into a configurable number of registers, the maximum number of leading zeros is calculated in the numbers in each register, and a harmonic mean is used to combine the estimates.

For large or unbounded data sets, calculating the exact cardinality is impractical. HyperLogLog uses a fraction of the memory while providing an accurate approximation.

This implementation was [originally written by Eric Lesh](https://github.com/eclesh/hyperloglog). Some small changes and additions have been made, including a way to construct a HyperLogLog optimized for a particular relative accuracy and adding FNV hashing. For counting element frequency, refer to the Count-Min Sketch.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            byte[] ALICE_BYTES = Encoding.ASCII.GetBytes("alice");
            byte[] BOB_BYTES = Encoding.ASCII.GetBytes("bob");
            byte[] FRANK_BYTES = Encoding.ASCII.GetBytes("frank");

            var hll = HyperLogLog.NewDefaultHyperLogLog(0.1);

            hll.Add(ALICE_BYTES).Add(BOB_BYTES).Add(BOB_BYTES).Add(FRANK_BYTES);
            Console.WriteLine(string.Format("count: {0}", hll.Count()));
        }
    }
}
```

## MinHash

This is a variation of the technique for estimating similarity between two sets as presented by Broder in [On the resemblance and containment of documents](http://gatekeeper.dec.com/ftp/pub/dec/SRC/publications/broder/positano-final-wpnums.pdf).

MinHash is a probabilistic algorithm which can be used to cluster or compare documents by splitting the corpus into a bag of words. MinHash returns the approximated similarity ratio of the two bags. The similarity is less accurate for very small bags of words.

### Usage

```C#
using System.Encoding;
using ProbabilisticDataStructures;

namespace FilterExample
{
    class Example
    {
        static void Main()
        {
            var bag1 = new List<string>{
                "bill",
                "alice",
                "frank",
                "bob",
                "sara",
                "tyler",
                "james"
            };

            var bag2 = new List<string>{
                "bill",
                "alice",
                "frank",
                "bob",
                "sara"
            };

            Console.WriteLine(string.Format("similarity: {0}", MinHash.Similarity(bag1.ToArray(), bag2.ToArray())));
        }
    }
}
```
