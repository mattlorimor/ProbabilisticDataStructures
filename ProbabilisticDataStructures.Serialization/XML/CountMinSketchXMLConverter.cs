using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace ProbabilisticDataStructures.Serialization.XML
{
    [XmlRoot("CountMinSketch")]
    [XmlSerializationSurrogate(typeof(CountMinSketch))]
    public class CountMinSketchXmlConverter : IXmlSerializable
    {
        public CountMinSketch CountMinSketch;

        public XmlSchema GetSchema()
        {
            return null;
        }

        public void ReadXml(XmlReader reader)
        {
            CountMinSketch = new CountMinSketch();

            reader.MoveToContent();

            reader.ReadStartElement(); // skip <CountMinSketch>

            CountMinSketch.Width = Convert.ToUInt32(reader.ReadElementString("Width"));
            CountMinSketch.Depth = Convert.ToUInt32(reader.ReadElementString("Depth"));
            CountMinSketch.delta = Convert.ToDouble(reader.ReadElementString("Delta"), CultureInfo.InvariantCulture);
            CountMinSketch.epsilon =
                Convert.ToDouble(reader.ReadElementString("Epsilon"), CultureInfo.InvariantCulture);
            CountMinSketch.count = Convert.ToUInt64(reader.ReadElementString("Count"));

            var hashAlgorithmName = reader.ReadElementString("HashAlgorithm");
            CountMinSketch.Hash = HashAlgorithm.Create(hashAlgorithmName);
            CountMinSketch.HashAlgorithmName = hashAlgorithmName;

            reader.Read(); // skip <Matrix>

            var matrix = new List<List<ulong>>();

            while (reader.Name == "MatrixRow")
            {
                reader.ReadStartElement(); // skip <MatrixRow>

                var currentRow = new List<ulong>();
                matrix.Add(currentRow);

                while (reader.Name == "MatrixItem")
                {
                    var current = Convert.ToUInt64(reader.ReadElementString("MatrixItem"));

                    currentRow.Add(current);
                }

                reader.ReadEndElement(); // skip </MatrixRow>
            }

            CountMinSketch.Matrix = new ulong[matrix.Count][];

            for (var i = 0; i < matrix.Count; i++)
            {
                CountMinSketch.Matrix[i] = new ulong[matrix.ElementAt(i).Count];

                for (var j = 0; j < matrix.ElementAt(i).Count; j++)
                {
                    CountMinSketch.Matrix[i][j] = matrix.ElementAt(i).ElementAt(j);
                }
            }

            reader.ReadEndElement(); // skip </Matrix>
            reader.ReadEndElement(); // skip </CountMinSketch>
        }

        public void WriteXml(XmlWriter writer)
        {
            writer.WriteElementString("Width", CountMinSketch.Width.ToString());
            writer.WriteElementString("Depth", CountMinSketch.Depth.ToString());
            writer.WriteElementString("Delta", CountMinSketch.delta.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Epsilon", CountMinSketch.epsilon.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("Count", CountMinSketch.count.ToString());
            writer.WriteElementString("HashAlgorithm", CountMinSketch.HashAlgorithmName);

            writer.WriteStartElement("Matrix");

            foreach (var row in CountMinSketch.Matrix)
            {
                writer.WriteStartElement("MatrixRow");

                foreach (var el in row)
                {
                    writer.WriteElementString("MatrixItem", el.ToString());
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        public static implicit operator CountMinSketch(CountMinSketchXmlConverter converter)
        {
            return converter.CountMinSketch;
        }

        public static implicit operator CountMinSketchXmlConverter(CountMinSketch countMinSketch)
        {
            return new CountMinSketchXmlConverter() {CountMinSketch = countMinSketch};
        }
    }
}