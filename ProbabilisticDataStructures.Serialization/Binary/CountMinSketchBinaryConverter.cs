using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace ProbabilisticDataStructures.Serialization.Binary
{
    [BinarySerializationSurrogate(typeof(CountMinSketch))]
    public class CountMinSketchBinaryConverter : ISerializationSurrogate
    {
        public void GetObjectData(object obj, SerializationInfo info, StreamingContext context)
        {
            var countMinSketch = (CountMinSketch) obj;

            info.AddValue("width", countMinSketch.Width);
            info.AddValue("depth", countMinSketch.Depth);
            info.AddValue("delta", countMinSketch.delta);
            info.AddValue("epsilon", countMinSketch.epsilon);
            info.AddValue("count", countMinSketch.count);
            info.AddValue("hashAlgorithm", countMinSketch.HashAlgorithmName);
            info.AddValue("matrix", countMinSketch.Matrix);
        }

        public object SetObjectData(object obj, SerializationInfo info, StreamingContext context,
            ISurrogateSelector selector)
        {
            var countMinSketch = (CountMinSketch) obj;

            countMinSketch.Width = info.GetUInt32("width");
            countMinSketch.Depth = info.GetUInt32("depth");
            countMinSketch.delta = info.GetDouble("delta");
            countMinSketch.epsilon = info.GetDouble("epsilon");
            countMinSketch.count = info.GetUInt64("count");

            var hashAlgorithmName = info.GetString("hashAlgorithm");
            countMinSketch.Hash = HashAlgorithm.Create(hashAlgorithmName);
            countMinSketch.HashAlgorithmName = hashAlgorithmName;
            
            countMinSketch.Matrix = (ulong[][]) info.GetValue("matrix", typeof(ulong[][]));

            return countMinSketch;
        }
    }
}