using System;

namespace ProbabilisticDataStructures.Serialization.Binary
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BinarySerializationSurrogateAttribute : Attribute
    {
        public Type SurrogateFor { get; }

        public BinarySerializationSurrogateAttribute(Type surrogateFor)
        {
            SurrogateFor = surrogateFor;
        }
    }
}