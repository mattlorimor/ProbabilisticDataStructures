using ProbabilisticDataStructures.ThreadSafe.CountMinSketch.Models;

namespace ProbabilisticDataStructures.ThreadSafe.CountMinSketch
{
    public class CountMinSketchExtended : ProbabilisticDataStructures.CountMinSketch
    {
        private readonly double _delayedUpdateCoefficient;

        public CountMinSketchExtended(double alpha, double epsilon, double delta)
            : base(alpha, delta)
        {
            _delayedUpdateCoefficient = epsilon - alpha;
        }

        public void Update(Update update)
        {
            Matrix[update.R][update.U] += update.Delta;
        }

        public new ulong Count(byte[] data) => (ulong) (base.Count(data) + _delayedUpdateCoefficient * TotalCount());
    }
}