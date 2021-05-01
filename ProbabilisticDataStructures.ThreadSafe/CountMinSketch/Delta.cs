using System.Collections.Generic;
using ProbabilisticDataStructures.ThreadSafe.CountMinSketch.Models;

namespace ProbabilisticDataStructures.ThreadSafe.CountMinSketch
{
    public class Delta
    {
        private readonly ProbabilisticDataStructures.CountMinSketch _countMinSketch;
        private readonly double _delayedUpdateCoefficient;

        public Delta(double alpha, double epsilon, double delta)
        {
            _countMinSketch = new ProbabilisticDataStructures.CountMinSketch(alpha, delta);
            _delayedUpdateCoefficient = epsilon - alpha;
        }

        public IEnumerable<Update> Add(byte[] data)
        {
            _countMinSketch.Add(data);

            for (uint i = 0; i < _countMinSketch.Depth; i++)
            {
                for (uint j = 0; j < _countMinSketch.Width; j++)
                {
                    if (_countMinSketch.Matrix[i][j] >= _delayedUpdateCoefficient * _countMinSketch.TotalCount())
                    {
                        yield return new Update(i, j, _countMinSketch.Matrix[i][j]);
                        _countMinSketch.Matrix[i][j] = 0;
                    }
                }
            }
        }
    }
}