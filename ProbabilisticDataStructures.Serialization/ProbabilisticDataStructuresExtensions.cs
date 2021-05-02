using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using ProbabilisticDataStructures.Serialization.Binary;
using ProbabilisticDataStructures.Serialization.XML;

namespace ProbabilisticDataStructures.Serialization
{
    public static class ProbabilisticDataStructuresExtensions
    {
        private static Dictionary<Type, Type> _jsonConverters = new Dictionary<Type, Type>();
        private static Dictionary<Type, Type> _xmlSurrogates = new Dictionary<Type, Type>();
        private static Dictionary<Type, Type> _binarySurrogates = new Dictionary<Type, Type>();

        static ProbabilisticDataStructuresExtensions()
        {
            var typesInAssembly = typeof(ProbabilisticDataStructuresExtensions).Assembly.GetTypes();

            FindAllJsonConverters(typesInAssembly);
            FindAllXmlSurrogates(typesInAssembly);
            FindAllBinarySurrogates(typesInAssembly);
        }

        public static string ToJson<T>(this T value)
        {
            if (!_jsonConverters.TryGetValue(value.GetType(), out var converterType))
                throw new NotSupportedException($"Json converter for {typeof(T)} does not exist!");

            var createConverterFunc = ExpressionExtensions<JsonConverter>.GetInstanceDelegate(converterType);

            return JsonSerializer.Serialize(value,
                options: new JsonSerializerOptions {Converters = {createConverterFunc()}});
        }

        public static T FromJson<T>(string value)
        {
            if (!_jsonConverters.TryGetValue(typeof(T), out var converterType))
                throw new NotSupportedException();

            var createConverterFunc = ExpressionExtensions<JsonConverter>.GetInstanceDelegate(converterType);

            return JsonSerializer.Deserialize<T>(value,
                options: new JsonSerializerOptions {Converters = {createConverterFunc()}});
        }

        public static string ToXml<T>(this T value)
        {
            if (!_xmlSurrogates.TryGetValue(value.GetType(), out var surrogateType))
                throw new NotSupportedException($"Xml surrogate for {typeof(T)} does not exist!");

            var xmlSerializer = new XmlSerializer(surrogateType);
            using (var stringWriter = new StringWriter())
            {
                var castFunc = ExpressionExtensions<T>.GetCastDelegate(surrogateType);

                xmlSerializer.Serialize(stringWriter, castFunc(value));

                return stringWriter.ToString();
            }
        }

        public static T FromXml<T>(string value)
        {
            if (!_xmlSurrogates.TryGetValue(typeof(T), out var surrogateType))
                throw new NotSupportedException($"Xml surrogate for {typeof(T)} does not exist!");

            var xmlSerializer = new XmlSerializer(surrogateType);
            var reader = new StringReader(value);

            var castFunc = ExpressionExtensions<T>.GetCastDelegate2(surrogateType);

            return castFunc(xmlSerializer.Deserialize(reader));
        }

        public static byte[] ToBinary<T>(this T value)
        {
            if (!_binarySurrogates.TryGetValue(value.GetType(), out var surrogateType))
                throw new NotSupportedException($"Binary surrogate for {typeof(T)} does not exist!");

            var surrogateSelector = new SurrogateSelector();

            var surrogateFunc = ExpressionExtensions<ISerializationSurrogate>.GetInstanceDelegate(surrogateType);

            surrogateSelector.AddSurrogate(typeof(T), new StreamingContext(StreamingContextStates.All),
                surrogateFunc());

            var formatter = new BinaryFormatter() {SurrogateSelector = surrogateSelector};

            using (var stream = new MemoryStream())
            {
                formatter.Serialize(stream, value);
                stream.Seek(0, SeekOrigin.Begin);

                return stream.ToArray();
            }
        }

        public static T FromBinary<T>(byte[] data)
        {
            if (!_binarySurrogates.TryGetValue(typeof(T), out var surrogateType))
                throw new NotSupportedException($"Binary surrogate for {typeof(T)} does not exist!");

            var surrogateSelector = new SurrogateSelector();

            var surrogateFunc = ExpressionExtensions<ISerializationSurrogate>.GetInstanceDelegate(surrogateType);

            surrogateSelector.AddSurrogate(typeof(T), new StreamingContext(StreamingContextStates.All),
                surrogateFunc());

            var formatter = new BinaryFormatter() {SurrogateSelector = surrogateSelector};

            using (var stream = new MemoryStream(data))
            {
                return (T) formatter.Deserialize(stream);
            }
        }

        private static void FindAllXmlSurrogates(IEnumerable<Type> types)
        {
            _xmlSurrogates = types
                .Where(t => t.GetCustomAttribute(typeof(XmlSerializationSurrogateAttribute)) != null)
                .ToDictionary(
                    type => type.GetCustomAttribute<XmlSerializationSurrogateAttribute>().SurrogateFor);
        }

        private static void FindAllJsonConverters(IEnumerable<Type> types)
        {
            _jsonConverters = types
                .Where(t => t.BaseType != null
                            && t.BaseType.IsGenericType
                            && t.BaseType.GetGenericTypeDefinition() == typeof(JsonConverter<>))
                .ToDictionary(x => x.BaseType.GetGenericArguments()[0]);
        }

        private static void FindAllBinarySurrogates(IEnumerable<Type> types)
        {
            _binarySurrogates = types
                .Where(t => t.GetCustomAttribute(typeof(BinarySerializationSurrogateAttribute)) != null)
                .ToDictionary(
                    type => type.GetCustomAttribute<BinarySerializationSurrogateAttribute>().SurrogateFor);
        }
    }
}