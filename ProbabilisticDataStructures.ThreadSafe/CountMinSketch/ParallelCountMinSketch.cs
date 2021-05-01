using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProbabilisticDataStructures.ThreadSafe.CountMinSketch.Models;
using ProbabilisticDataStructures.ThreadSafe.CountMinSketch.SubStreams;

namespace ProbabilisticDataStructures.ThreadSafe.CountMinSketch
{
    /// <summary>
    /// ParallelCountMinSketch implements a Count-Min Sketch as described by BOWEN Yu, YU Zhang, and LUBING Li
    /// in "Parallelizing Count-Min Sketch Algorithm onMulti-core Processors"
    /// </summary>
    public class ParallelCountMinSketch : IDisposable
    {
        private readonly double _epsilon;
        private readonly double _delta;
        private readonly double _alpha;

        private readonly BlockingCollection<byte[]> _addCollection;
        private readonly BlockingCollection<Update> _mergeCollection;

        private List<SubStream> _subStreams;
        private MainSubStream _mainSubStream;

        public ParallelCountMinSketch(double alpha, double epsilon, double delta)
        {
            _alpha = alpha;
            _epsilon = epsilon;
            _delta = delta;

            _addCollection = new BlockingCollection<byte[]>();
            _mergeCollection = new BlockingCollection<Update>();

            CreateSubStreams();
        }

        public void Add(byte[] data)
        {
            _addCollection.Add(data);
        }

        public Task<ulong> GetCount(byte[] data)
        {
            return _mainSubStream.GetCount(data);
        }

        private void CreateSubStreams()
        {
            var workersCount = Math.Max(1, Environment.ProcessorCount - 1);

            _subStreams = new List<SubStream>(workersCount);

            for (var i = 0; i < workersCount; i++)
            {
                _subStreams.Add(new SubStream(_addCollection, _mergeCollection, _alpha, _epsilon, _delta));
            }

            _mainSubStream = new MainSubStream(_mergeCollection, _alpha, _epsilon, _delta);
        }

        public void Dispose()
        {
            _addCollection?.Dispose();
            _mergeCollection?.Dispose();
        }
    }
}