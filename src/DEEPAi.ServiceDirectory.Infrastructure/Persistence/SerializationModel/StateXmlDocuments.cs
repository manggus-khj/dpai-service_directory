using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence.SerializationModel
{
    [XmlRoot("Directory", Namespace = "", IsNullable = false)]
    public sealed class DirectoryDocument
    {
        public DirectoryDocument()
        {
            Records = new List<ServiceRecordDocument>();
        }

        [XmlAttribute("SchemaVersion")]
        public string SchemaVersion { get; set; }

        [XmlElement("LogicalClock", Order = 0)]
        public string LogicalClock { get; set; }

        [XmlArray("Records", Order = 1)]
        [XmlArrayItem("Record", IsNullable = false)]
        public List<ServiceRecordDocument> Records { get; set; }
    }

    public sealed class ServiceRecordDocument
    {
        [XmlElement("Name", Order = 0)]
        public string Name { get; set; }

        [XmlElement("ProductCode", Order = 1)]
        public string ProductCode { get; set; }

        [XmlElement("ServiceHostName", Order = 2)]
        public string ServiceHostName { get; set; }

        [XmlElement("ServiceIpv4Address", Order = 3)]
        public string ServiceIpv4Address { get; set; }

        [XmlElement("Port", Order = 4)]
        public string Port { get; set; }

        [XmlElement("LastModifiedUtc", Order = 5)]
        public string LastModifiedUtc { get; set; }

        [XmlElement("Deleted", Order = 6)]
        public string Deleted { get; set; }

        [XmlElement("DeletedUtc", Order = 7)]
        public string DeletedUtc { get; set; }

        [XmlElement("LogicalVersion", Order = 8)]
        public string LogicalVersion { get; set; }

        [XmlElement("OriginInstanceId", Order = 9)]
        public string OriginInstanceId { get; set; }
    }
}

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed partial class StateXmlCodec
    {
        private sealed class StrictShapeReader : IDisposable
        {
            private static readonly string[] NoAttributes = new string[0];
            private static readonly string[] SchemaVersionAttribute =
                { "SchemaVersion" };

            private readonly XmlReader _reader;

            private StrictShapeReader(string xml)
            {
                _reader = XmlReader.Create(
                    new StringReader(xml),
                    CreateReaderSettings(xml.Length));

                if (!_reader.Read())
                {
                    throw InvalidStateXml("The XML document is empty.");
                }

                if (_reader.NodeType == XmlNodeType.XmlDeclaration)
                {
                    ValidateDeclaration();
                    ReadNext();
                }

                SkipInterElementWhitespace();
            }

            internal static void ValidateDirectory(string xml)
            {
                try
                {
                    using (var reader = new StrictShapeReader(xml))
                    {
                        reader.ReadDirectory();
                        reader.RequireEndOfDocument();
                    }
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (XmlException exception)
                {
                    throw InvalidStateXml("directory.xml is not valid XML.", exception);
                }
            }

            public void Dispose()
            {
                _reader.Dispose();
            }

            private void ReadDirectory()
            {
                if (EnterElement("Directory", SchemaVersionAttribute))
                {
                    throw InvalidStateXml("Directory cannot be empty.");
                }

                SkipInterElementWhitespace();
                ReadScalar("LogicalClock");
                SkipInterElementWhitespace();
                ReadRecords();
                SkipInterElementWhitespace();
                LeaveElement("Directory");
            }

            private void ReadRecords()
            {
                if (EnterElement("Records", NoAttributes))
                {
                    return;
                }

                SkipInterElementWhitespace();
                while (IsElement("Record"))
                {
                    ReadRecord("Record");
                    SkipInterElementWhitespace();
                }

                LeaveElement("Records");
            }

            private void ReadRecord(string elementName)
            {
                if (EnterElement(elementName, NoAttributes))
                {
                    throw InvalidStateXml(elementName + " cannot be empty.");
                }

                SkipInterElementWhitespace();
                ReadScalar("Name");
                SkipInterElementWhitespace();
                ReadScalar("ProductCode");
                SkipInterElementWhitespace();
                ReadScalar("ServiceHostName");
                SkipInterElementWhitespace();
                ReadScalar("ServiceIpv4Address");
                SkipInterElementWhitespace();
                ReadScalar("Port");
                SkipInterElementWhitespace();
                ReadScalar("LastModifiedUtc");
                SkipInterElementWhitespace();
                ReadScalar("Deleted");
                SkipInterElementWhitespace();
                if (IsElement("DeletedUtc"))
                {
                    ReadScalar("DeletedUtc");
                    SkipInterElementWhitespace();
                }

                ReadScalar("LogicalVersion");
                SkipInterElementWhitespace();
                ReadScalar("OriginInstanceId");
                SkipInterElementWhitespace();
                LeaveElement(elementName);
            }

            private void ReadScalar(string elementName)
            {
                if (EnterElement(elementName, NoAttributes))
                {
                    return;
                }

                while (_reader.NodeType == XmlNodeType.Text
                    || _reader.NodeType == XmlNodeType.Whitespace
                    || _reader.NodeType == XmlNodeType.SignificantWhitespace)
                {
                    ReadNext();
                }

                LeaveElement(elementName);
            }

            private bool EnterElement(string elementName, string[] requiredAttributes)
            {
                RequireElement(elementName);
                ValidateAttributes(requiredAttributes);

                bool isEmpty = _reader.IsEmptyElement;
                ReadNext();
                return isEmpty;
            }

            private void LeaveElement(string elementName)
            {
                if (_reader.NodeType != XmlNodeType.EndElement
                    || _reader.Prefix.Length != 0
                    || _reader.NamespaceURI.Length != 0
                    || !StringComparer.Ordinal.Equals(_reader.LocalName, elementName))
                {
                    throw InvalidStateXml(
                        "Expected closing element " + elementName + ".");
                }

                ReadNext();
            }

            private void RequireElement(string elementName)
            {
                if (_reader.NodeType != XmlNodeType.Element
                    || _reader.Prefix.Length != 0
                    || _reader.NamespaceURI.Length != 0
                    || !StringComparer.Ordinal.Equals(_reader.LocalName, elementName))
                {
                    throw InvalidStateXml("Expected element " + elementName + ".");
                }

                if (_reader.Depth >= MaximumXmlDepth)
                {
                    throw InvalidStateXml("The persisted XML exceeds the maximum depth.");
                }
            }

            private bool IsElement(string elementName)
            {
                return _reader.NodeType == XmlNodeType.Element
                    && _reader.Prefix.Length == 0
                    && _reader.NamespaceURI.Length == 0
                    && StringComparer.Ordinal.Equals(_reader.LocalName, elementName);
            }

            private void ValidateAttributes(string[] requiredAttributes)
            {
                if (_reader.AttributeCount != requiredAttributes.Length)
                {
                    throw InvalidStateXml(
                        "Element " + _reader.LocalName + " has invalid attributes.");
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (_reader.MoveToFirstAttribute())
                {
                    do
                    {
                        if (_reader.Prefix.Length != 0
                            || _reader.NamespaceURI.Length != 0
                            || !Contains(requiredAttributes, _reader.LocalName)
                            || !seen.Add(_reader.LocalName))
                        {
                            throw InvalidStateXml(
                                "Element " + _reader.LocalName + " has an unknown or duplicate attribute.");
                        }
                    }
                    while (_reader.MoveToNextAttribute());

                    _reader.MoveToElement();
                }

                if (seen.Count != requiredAttributes.Length)
                {
                    throw InvalidStateXml(
                        "Element " + _reader.LocalName + " is missing a required attribute.");
                }
            }

            private void ValidateDeclaration()
            {
                if (_reader.AttributeCount != 2
                    || !StringComparer.Ordinal.Equals(
                        _reader.GetAttribute("version"),
                        "1.0")
                    || !StringComparer.Ordinal.Equals(
                        _reader.GetAttribute("encoding"),
                        "utf-8")
                    || _reader.GetAttribute("standalone") != null)
                {
                    throw InvalidStateXml(
                        "The XML declaration must be version 1.0 and UTF-8 without standalone.");
                }
            }

            private void SkipInterElementWhitespace()
            {
                while (_reader.NodeType == XmlNodeType.Whitespace
                    || _reader.NodeType == XmlNodeType.SignificantWhitespace)
                {
                    ReadNext();
                }
            }

            private void RequireEndOfDocument()
            {
                SkipInterElementWhitespace();
                if (!_reader.EOF)
                {
                    throw InvalidStateXml(
                        "The XML document contains content outside its root element.");
                }
            }

            private void ReadNext()
            {
                _reader.Read();
            }

            private static bool Contains(string[] values, string candidate)
            {
                for (int index = 0; index < values.Length; index++)
                {
                    if (StringComparer.Ordinal.Equals(values[index], candidate))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
