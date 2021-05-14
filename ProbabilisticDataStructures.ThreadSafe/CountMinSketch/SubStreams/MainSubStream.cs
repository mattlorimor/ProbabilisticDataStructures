using System.Collections.Concurrent;
using System.Threading.Tasks;
using Nito.AsyncEx;
using ProbabilisticDataStructures.ThreadSafe.CountMinSketch.Models;

namespace ProbabilisticDataStructures.ThreadSafe.CountMinSketch.SubStreams
{
    public class MainSubStream
    {
        private readonly BlockingCollection<Update> _updates;
        private readonly CountMinSketchExtended _countMinSketch;
        private readonly AsyncReaderWriterLock _lock;

        public MainSubStream(BlockingCollection<Update> updates, double alpha, double epsilon, double delta)
        {
            _lock = new AsyncReaderWriterLock();
            _updates = updates;
            _countMinSketch = new CountMinSketchExtended(alpha, epsilon, delta);

            Task.Run(ListenToUpdates);
        }

        public async Task<ulong> GetCount(byte[] data)
        {
            using (await _lock.ReaderLockAsync().ConfigureAwait(false))
            {
                return _countMinSketch.Count(data);
            }
        }

        private async Task ListenToUpdates()
        {
            foreach (var update in _updates.GetConsumingEnumerable())
            {
                using (await _lock.WriterLockAsync())
                {
                    _countMinSketch.Update(update);
                }
            }
        }
    }
}