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

    [XmlRoot("PendingRegistrations", Namespace = "", IsNullable = false)]
    public sealed class PendingDocument
    {
        public PendingDocument()
        {
            Items = new List<PendingRegistrationDocument>();
        }

        [XmlAttribute("SchemaVersion")]
        public string SchemaVersion { get; set; }

        [XmlArray("Items", Order = 0)]
        [XmlArrayItem("Pending", IsNullable = false)]
        public List<PendingRegistrationDocument> Items { get; set; }
    }

    public sealed class ServiceDefinitionDocument
    {
        [XmlElement("Name", Order = 0)]
        public string Name { get; set; }

        [XmlElement("ProductCode", Order = 1)]
        public string ProductCode { get; set; }

        [XmlElement("ServerAddress", Order = 2)]
        public string ServerAddress { get; set; }

        [XmlElement("Port", Order = 3)]
        public string Port { get; set; }
    }

    public sealed class ServiceRecordDocument
    {
        [XmlElement("Definition", Order = 0)]
        public ServiceDefinitionDocument Definition { get; set; }

        [XmlElement("LastModifiedUtc", Order = 1)]
        public string LastModifiedUtc { get; set; }

        [XmlElement("Deleted", Order = 2)]
        public string Deleted { get; set; }

        [XmlElement("DeletedUtc", Order = 3)]
        public string DeletedUtc { get; set; }

        [XmlElement("LogicalVersion", Order = 4)]
        public string LogicalVersion { get; set; }

        [XmlElement("OriginInstanceId", Order = 5)]
        public string OriginInstanceId { get; set; }
    }

    public sealed class PendingRegistrationDocument
    {
        [XmlElement("Id", Order = 0)]
        public string Id { get; set; }

        [XmlElement("Type", Order = 1)]
        public string Type { get; set; }

        [XmlElement("RequestedUtc", Order = 2)]
        public string RequestedUtc { get; set; }

        [XmlElement("SourceIp", Order = 3)]
        public string SourceIp { get; set; }

        [XmlElement("Requested", Order = 4)]
        public ServiceDefinitionDocument Requested { get; set; }

        [XmlElement("BaseRevision", Order = 5)]
        public BaseRevisionDocument BaseRevision { get; set; }
    }

    public sealed class BaseRevisionDocument
    {
        [XmlAttribute("Kind")]
        public string Kind { get; set; }

        [XmlElement("Record", Order = 0)]
        public ServiceRecordDocument Record { get; set; }
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
            private static readonly string[] KindAttribute = { "Kind" };

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

            internal static void ValidatePending(string xml)
            {
                try
                {
                    using (var reader = new StrictShapeReader(xml))
                    {
                        reader.ReadPendingDocument();
                        reader.RequireEndOfDocument();
                    }
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (XmlException exception)
                {
                    throw InvalidStateXml("pending.xml is not valid XML.", exception);
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

            private void ReadPendingDocument()
            {
                if (EnterElement("PendingRegistrations", SchemaVersionAttribute))
                {
                    throw InvalidStateXml("PendingRegistrations cannot be empty.");
                }

                SkipInterElementWhitespace();
                ReadPendingItems();
                SkipInterElementWhitespace();
                LeaveElement("PendingRegistrations");
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
                ReadDefinition("Definition");
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

            private void ReadDefinition(string elementName)
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
                ReadScalar("ServerAddress");
                SkipInterElementWhitespace();
                ReadScalar("Port");
                SkipInterElementWhitespace();
                LeaveElement(elementName);
            }

            private void ReadPendingItems()
            {
                if (EnterElement("Items", NoAttributes))
                {
                    return;
                }

                int itemCount = 0;
                SkipInterElementWhitespace();
                while (IsElement("Pending"))
                {
                    itemCount++;
                    if (itemCount
                        > DEEPAi.ServiceDirectory.Domain.DirectorySnapshot.PendingRegistrationLimit)
                    {
                        throw InvalidStateXml(
                            "pending.xml exceeds the supported item limit.");
                    }

                    ReadPendingItem();
                    SkipInterElementWhitespace();
                }

                LeaveElement("Items");
            }

            private void ReadPendingItem()
            {
                if (EnterElement("Pending", NoAttributes))
                {
                    throw InvalidStateXml("Pending cannot be empty.");
                }

                SkipInterElementWhitespace();
                ReadScalar("Id");
                SkipInterElementWhitespace();
                ReadScalar("Type");
                SkipInterElementWhitespace();
                ReadScalar("RequestedUtc");
                SkipInterElementWhitespace();
                ReadScalar("SourceIp");
                SkipInterElementWhitespace();
                ReadDefinition("Requested");
                SkipInterElementWhitespace();
                ReadBaseRevision();
                SkipInterElementWhitespace();
                LeaveElement("Pending");
            }

            private void ReadBaseRevision()
            {
                if (EnterElement("BaseRevision", KindAttribute))
                {
                    return;
                }

                SkipInterElementWhitespace();
                if (IsElement("Record"))
                {
                    ReadRecord("Record");
                    SkipInterElementWhitespace();
                }

                LeaveElement("BaseRevision");
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
