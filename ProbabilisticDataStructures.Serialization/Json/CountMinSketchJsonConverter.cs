using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProbabilisticDataStructures.Serialization.Json
{
    public class CountMinSketchJsonConverter : JsonConverter<CountMinSketch>
    {
        public override CountMinSketch Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var countMinSketch = new CountMinSketch();
            var parsed = JsonDocument.ParseValue(ref reader).RootElement;

            countMinSketch.Width = parsed.GetProperty("width").GetUInt32();
            countMinSketch.Depth = parsed.GetProperty("depth").GetUInt32();
            countMinSketch.delta = parsed.GetProperty("delta").GetDouble();
            countMinSketch.epsilon = parsed.GetProperty("epsilon").GetDouble();
            countMinSketch.count = parsed.GetProperty("count").GetUInt64();

            var hashAlgorithmName = parsed.GetProperty("hashAlgorithm").GetString();
            countMinSketch.Hash = HashAlgorithm.Create(hashAlgorithmName);
            countMinSketch.HashAlgorithmName = hashAlgorithmName;
            
            var rowsCount = parsed.GetProperty("Matrix").GetArrayLength();
            countMinSketch.Matrix = new ulong[rowsCount][];

            var enumerator = parsed.GetProperty("Matrix").EnumerateArray();

            var currentRow = 0;
            var currentElement = 0;

            foreach (var row in enumerator)
            {
                countMinSketch.Matrix[currentRow] = new ulong[row.GetArrayLength()];
                
                foreach (var el in row.EnumerateArray())
                {
                    countMinSketch.Matrix[currentRow][currentElement++] = el.GetUInt64();
                }

                currentRow++;
                currentElement = 0;
            }

            return countMinSketch;
        }

        public override void Write(Utf8JsonWriter writer, CountMinSketch value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WriteNumber("width", value.Width);
            writer.WriteNumber("depth", value.Depth);
            writer.WriteNumber("delta", value.delta);
            writer.WriteNumber("epsilon", value.epsilon);
            writer.WriteNumber("count", value.count);
            writer.WriteString("hashAlgorithm", value.HashAlgorithmName);

            writer.WriteStartArray("Matrix");

            foreach (var row in value.Matrix)
            {
                writer.WriteStartArray();

                foreach (var val in row)
                {
                    writer.WriteNumberValue(val);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}