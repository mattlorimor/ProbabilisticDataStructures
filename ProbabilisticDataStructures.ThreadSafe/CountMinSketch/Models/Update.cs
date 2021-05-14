namespace ProbabilisticDataStructures.ThreadSafe.CountMinSketch.Models
{
    public class Update
    {
        public uint R { get; }
        public uint U { get; }
        public ulong Delta { get; }

        public Update(uint r, uint u, ulong delta)
        {
            R = r;
            U = u;
            Delta = delta;
        }
    }
}