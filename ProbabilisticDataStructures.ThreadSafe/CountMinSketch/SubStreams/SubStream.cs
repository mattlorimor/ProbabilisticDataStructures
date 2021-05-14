using System.Collections.Concurrent;
using System.Threading;
using ProbabilisticDataStructures.ThreadSafe.CountMinSketch.Models;

namespace ProbabilisticDataStructures.ThreadSafe.CountMinSketch.SubStreams
{
    public class SubStream
    {
        private readonly BlockingCollection<byte[]> _addCollection;
        private readonly BlockingCollection<Update> _mergeCollection;
        private readonly CountMinSketchExtended _countMinSketch;
        private readonly Delta _delta;

        public SubStream(
            BlockingCollection<byte[]> addCollection,
            BlockingCollection<Update> mergeCollection,
            double alpha,
            double epsilon,
            double delta)
        {
            _addCollection = addCollection;
            _mergeCollection = mergeCollection;

            _countMinSketch = new CountMinSketchExtended(alpha, epsilon, delta);
            _delta = new Delta(alpha, epsilon, delta);

            var thread = new Thread(ListenToUpdates) {IsBackground = true};

            thread.Start();
        }

        private void ListenToUpdates()
        {
            foreach (var newItem in _addCollection.GetConsumingEnumerable())
            {
                _countMinSketch.Add(newItem);
                var updates = _delta.Add(newItem);

                foreach (var update in updates)
                {
                    _mergeCollection.Add(update);
                }
            }
        }
    }
}