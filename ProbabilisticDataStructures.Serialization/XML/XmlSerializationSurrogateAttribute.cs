using System;

namespace ProbabilisticDataStructures.Serialization.XML
{
    [AttributeUsage(AttributeTargets.Class)]
    public class XmlSerializationSurrogateAttribute : Attribute
    {
        public Type SurrogateFor { get; }

        public XmlSerializationSurrogateAttribute(Type surrogateFor)
        {
            SurrogateFor = surrogateFor;
        }
    }
}