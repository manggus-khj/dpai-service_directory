using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence.SerializationModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed partial class StateXmlCodec
    {
        private const string CurrentSchemaVersion = "1";
        private const string UtcTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        private const int MaximumXmlDepth = 16;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly XmlSerializer DirectorySerializer =
            new XmlSerializer(typeof(DirectoryDocument));
        private static readonly XmlSerializer PendingSerializer =
            new XmlSerializer(typeof(PendingDocument));
        private static readonly XmlSerializerNamespaces EmptyNamespaces =
            CreateEmptyNamespaces();
        private static readonly object SerializerGate = new object();

        internal byte[] SerializeDirectory(DirectorySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var document = new DirectoryDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                LogicalClock = FormatUInt64(snapshot.LogicalClock)
            };

            var records = new List<ServiceRecord>(snapshot.Records.Values);
            records.Sort(CompareRecordsByProductCode);
            foreach (ServiceRecord record in records)
            {
                document.Records.Add(ToDocument(record));
            }

            return SerializeDocument(document, DirectorySerializer, "directory.xml");
        }

        internal byte[] SerializePending(DirectorySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var document = new PendingDocument
            {
                SchemaVersion = CurrentSchemaVersion
            };

            var pending = new List<PendingRegistration>(snapshot.PendingById.Values);
            pending.Sort(ComparePendingRegistrations);
            foreach (PendingRegistration item in pending)
            {
                document.Items.Add(ToDocument(item));
            }

            return SerializeDocument(document, PendingSerializer, "pending.xml");
        }

        internal DirectorySnapshot DeserializeSnapshot(
            byte[] directoryContents,
            byte[] pendingContents)
        {
            if (directoryContents == null)
            {
                throw new ArgumentNullException(nameof(directoryContents));
            }

            if (pendingContents == null)
            {
                throw new ArgumentNullException(nameof(pendingContents));
            }

            try
            {
                string directoryXml = DecodeStrictUtf8(
                    directoryContents,
                    "directory.xml");
                string pendingXml = DecodeStrictUtf8(
                    pendingContents,
                    "pending.xml");

                StrictShapeReader.ValidateDirectory(directoryXml);
                StrictShapeReader.ValidatePending(pendingXml);

                DirectoryDocument directory = DeserializeDocument<DirectoryDocument>(
                    directoryXml,
                    DirectorySerializer,
                    "directory.xml");
                PendingDocument pending = DeserializeDocument<PendingDocument>(
                    pendingXml,
                    PendingSerializer,
                    "pending.xml");

                DirectorySnapshot snapshot = ToSnapshot(directory, pending);
                RequireCanonicalDocument(
                    directoryContents,
                    SerializeDirectory(snapshot),
                    "directory.xml");
                RequireCanonicalDocument(
                    pendingContents,
                    SerializePending(snapshot),
                    "pending.xml");
                return snapshot;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (ArgumentException exception)
            {
                throw InvalidStateXml(
                    "The persisted service directory state violates a domain invariant.",
                    exception);
            }
            catch (InvalidOperationException exception)
            {
                throw InvalidStateXml(
                    "The persisted service directory state could not be deserialized.",
                    exception);
            }
        }

        private static DirectorySnapshot ToSnapshot(
            DirectoryDocument directory,
            PendingDocument pending)
        {
            if (directory == null || pending == null)
            {
                throw InvalidStateXml("A persisted state document is missing its root.");
            }

            RequireSchemaVersion(directory.SchemaVersion, "directory.xml");
            RequireSchemaVersion(pending.SchemaVersion, "pending.xml");

            if (directory.Records == null)
            {
                throw InvalidStateXml("directory.xml is missing its Records collection.");
            }

            if (pending.Items == null)
            {
                throw InvalidStateXml("pending.xml is missing its Items collection.");
            }

            ulong logicalClock = ParseCanonicalUInt64(
                directory.LogicalClock,
                "LogicalClock");

            var records = new List<ServiceRecord>(directory.Records.Count);
            string previousProductCode = null;
            foreach (ServiceRecordDocument item in directory.Records)
            {
                ServiceRecord record = ToDomain(item, "Record");
                string productCode = record.Definition.ProductCode.Value;
                if (previousProductCode != null
                    && string.CompareOrdinal(previousProductCode, productCode) >= 0)
                {
                    throw InvalidStateXml(
                        "directory.xml records are not in canonical ProductCode order.");
                }

                previousProductCode = productCode;
                records.Add(record);
            }

            var pendingRegistrations =
                new List<PendingRegistration>(pending.Items.Count);
            string previousPendingProductCode = null;
            string previousPendingId = null;
            foreach (PendingRegistrationDocument item in pending.Items)
            {
                PendingRegistration registration = ToDomain(item);
                string productCode = registration.Requested.ProductCode.Value;
                string id = registration.Id.ToString("D");
                if (previousPendingProductCode != null)
                {
                    int productComparison = string.CompareOrdinal(
                        previousPendingProductCode,
                        productCode);
                    if (productComparison > 0
                        || (productComparison == 0
                            && string.CompareOrdinal(previousPendingId, id) >= 0))
                    {
                        throw InvalidStateXml(
                            "pending.xml items are not in canonical ProductCode and ID order.");
                    }
                }

                previousPendingProductCode = productCode;
                previousPendingId = id;
                pendingRegistrations.Add(registration);
            }

            return new DirectorySnapshot(
                records,
                pendingRegistrations,
                logicalClock);
        }

        private static ServiceDefinitionDocument ToDocument(
            ServiceDefinition definition)
        {
            return new ServiceDefinitionDocument
            {
                Name = definition.Name,
                ProductCode = definition.ProductCode.Value,
                ServerAddress = definition.ServerAddress,
                Port = definition.Port.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static ServiceRecordDocument ToDocument(ServiceRecord record)
        {
            return new ServiceRecordDocument
            {
                Definition = ToDocument(record.Definition),
                LastModifiedUtc = FormatUtc(record.LastModifiedUtc),
                Deleted = record.Deleted ? "true" : "false",
                DeletedUtc = record.DeletedUtc.HasValue
                    ? FormatUtc(record.DeletedUtc.Value)
                    : null,
                LogicalVersion = FormatUInt64(record.LogicalVersion),
                OriginInstanceId = record.OriginInstanceId.ToString("D")
            };
        }

        private static PendingRegistrationDocument ToDocument(
            PendingRegistration pending)
        {
            return new PendingRegistrationDocument
            {
                Id = pending.Id.ToString("D"),
                Type = FormatPendingType(pending.Type),
                RequestedUtc = FormatUtc(pending.RequestedUtc),
                SourceIp = pending.SourceIp,
                Requested = ToDocument(pending.Requested),
                BaseRevision = new BaseRevisionDocument
                {
                    Kind = FormatBaseRevisionKind(pending.BaseRevision.Kind),
                    Record = pending.BaseRevision.Record == null
                        ? null
                        : ToDocument(pending.BaseRevision.Record)
                }
            };
        }

        private static ServiceDefinition ToDomain(
            ServiceDefinitionDocument document,
            string context)
        {
            if (document == null)
            {
                throw InvalidStateXml(context + " is missing its service definition.");
            }

            int port = ParseCanonicalInt32(document.Port, context + ".Port");
            ServiceDefinition definition;
            ServiceDefinitionValidationError error;
            if (!ServiceDefinition.TryCreate(
                    document.Name,
                    document.ProductCode,
                    document.ServerAddress,
                    port,
                    out definition,
                    out error))
            {
                throw InvalidStateXml(
                    context + " contains an invalid service definition: "
                    + error.ToString()
                    + ".");
            }

            if (!StringComparer.Ordinal.Equals(document.Name, definition.Name)
                || !StringComparer.Ordinal.Equals(
                    document.ProductCode,
                    definition.ProductCode.Value)
                || !StringComparer.Ordinal.Equals(
                    document.ServerAddress,
                    definition.ServerAddress))
            {
                throw InvalidStateXml(
                    context + " contains a non-canonical service definition.");
            }

            return definition;
        }

        private static ServiceRecord ToDomain(
            ServiceRecordDocument document,
            string context)
        {
            if (document == null)
            {
                throw InvalidStateXml(context + " is missing.");
            }

            ServiceDefinition definition = ToDomain(
                document.Definition,
                context + ".Definition");
            DateTime lastModifiedUtc = ParseCanonicalUtc(
                document.LastModifiedUtc,
                context + ".LastModifiedUtc");
            bool deleted = ParseCanonicalBoolean(
                document.Deleted,
                context + ".Deleted");
            DateTime? deletedUtc = null;
            if (document.DeletedUtc != null)
            {
                deletedUtc = ParseCanonicalUtc(
                    document.DeletedUtc,
                    context + ".DeletedUtc");
            }

            if (deleted != deletedUtc.HasValue)
            {
                throw InvalidStateXml(
                    context + " has an inconsistent DeletedUtc value.");
            }

            ulong logicalVersion = ParseCanonicalUInt64(
                document.LogicalVersion,
                context + ".LogicalVersion");
            Guid originInstanceId = ParseCanonicalGuid(
                document.OriginInstanceId,
                context + ".OriginInstanceId");

            return new ServiceRecord(
                definition,
                lastModifiedUtc,
                deleted,
                deletedUtc,
                logicalVersion,
                originInstanceId);
        }

        private static PendingRegistration ToDomain(
            PendingRegistrationDocument document)
        {
            if (document == null)
            {
                throw InvalidStateXml("pending.xml contains a missing Pending item.");
            }

            Guid id = ParseCanonicalGuid(document.Id, "Pending.Id");
            PendingRequestType type = ParsePendingType(document.Type);
            DateTime requestedUtc = ParseCanonicalUtc(
                document.RequestedUtc,
                "Pending.RequestedUtc");
            string sourceIp = ParseCanonicalIpAddress(document.SourceIp);
            ServiceDefinition requested = ToDomain(
                document.Requested,
                "Pending.Requested");

            if (document.BaseRevision == null)
            {
                throw InvalidStateXml("Pending.BaseRevision is missing.");
            }

            BaseRevisionKind declaredKind = ParseBaseRevisionKind(
                document.BaseRevision.Kind);
            ServiceRecord baseRecord = document.BaseRevision.Record == null
                ? null
                : ToDomain(document.BaseRevision.Record, "Pending.BaseRevision.Record");
            DirectoryBaseRevision baseRevision = DirectoryBaseRevision.Capture(baseRecord);
            if (declaredKind != baseRevision.Kind)
            {
                throw InvalidStateXml(
                    "Pending.BaseRevision Kind does not match its Record.");
            }

            return new PendingRegistration(
                id,
                type,
                requestedUtc,
                sourceIp,
                requested,
                baseRevision);
        }

        private static byte[] SerializeDocument(
            object document,
            XmlSerializer serializer,
            string fileName)
        {
            try
            {
                using (var stream = new MemoryStream())
                {
                    var settings = new XmlWriterSettings
                    {
                        CheckCharacters = true,
                        CloseOutput = false,
                        Encoding = StrictUtf8,
                        Indent = true,
                        IndentChars = "  ",
                        NewLineChars = "\r\n",
                        NewLineHandling = NewLineHandling.Replace,
                        OmitXmlDeclaration = false
                    };

                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        lock (SerializerGate)
                        {
                            serializer.Serialize(writer, document, EmptyNamespaces);
                        }
                    }

                    return stream.ToArray();
                }
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    "Failed to serialize " + fileName + ".",
                    exception);
            }
        }

        private static T DeserializeDocument<T>(
            string xml,
            XmlSerializer serializer,
            string fileName)
            where T : class
        {
            var settings = CreateReaderSettings(xml.Length);
            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    object value;
                    lock (SerializerGate)
                    {
                        value = serializer.Deserialize(reader);
                    }

                    T typed = value as T;
                    if (typed == null)
                    {
                        throw InvalidStateXml(fileName + " has an invalid root type.");
                    }

                    return typed;
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw InvalidStateXml(fileName + " is not valid XML.", exception);
            }
            catch (InvalidOperationException exception)
            {
                throw InvalidStateXml(fileName + " could not be deserialized.", exception);
            }
        }

        private static string DecodeStrictUtf8(byte[] contents, string fileName)
        {
            if (contents.Length == 0)
            {
                throw InvalidStateXml(fileName + " is empty.");
            }

            if (contents.Length >= 3
                && contents[0] == 0xEF
                && contents[1] == 0xBB
                && contents[2] == 0xBF)
            {
                throw InvalidStateXml(fileName + " must not contain a UTF-8 BOM.");
            }

            try
            {
                return StrictUtf8.GetString(contents);
            }
            catch (DecoderFallbackException exception)
            {
                throw InvalidStateXml(fileName + " is not strict UTF-8.", exception);
            }
        }

        private static XmlReaderSettings CreateReaderSettings(int characterCount)
        {
            return new XmlReaderSettings
            {
                CheckCharacters = true,
                CloseInput = false,
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = Math.Max(1L, characterCount),
                ValidationType = ValidationType.None,
                XmlResolver = null
            };
        }

        private static void RequireSchemaVersion(string value, string fileName)
        {
            if (!StringComparer.Ordinal.Equals(value, CurrentSchemaVersion))
            {
                throw InvalidStateXml(
                    fileName + " must use exact SchemaVersion=\"1\".");
            }
        }

        private static void RequireCanonicalDocument(
            byte[] supplied,
            byte[] canonical,
            string fileName)
        {
            if (supplied.Length != canonical.Length)
            {
                throw InvalidStateXml(
                    fileName + " does not use the canonical v1 representation.");
            }

            for (int index = 0; index < supplied.Length; index++)
            {
                if (supplied[index] != canonical[index])
                {
                    throw InvalidStateXml(
                        fileName + " does not use the canonical v1 representation.");
                }
            }
        }

        private static int ParseCanonicalInt32(string value, string fieldName)
        {
            int parsed;
            if (value == null
                || !int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(CultureInfo.InvariantCulture)))
            {
                throw InvalidStateXml(fieldName + " is not a canonical integer.");
            }

            return parsed;
        }

        private static ulong ParseCanonicalUInt64(string value, string fieldName)
        {
            ulong parsed;
            if (value == null
                || !ulong.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || !StringComparer.Ordinal.Equals(value, FormatUInt64(parsed)))
            {
                throw InvalidStateXml(
                    fieldName + " is not a canonical unsigned integer.");
            }

            return parsed;
        }

        private static bool ParseCanonicalBoolean(string value, string fieldName)
        {
            if (StringComparer.Ordinal.Equals(value, "true"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(value, "false"))
            {
                return false;
            }

            throw InvalidStateXml(fieldName + " must be true or false.");
        }

        private static Guid ParseCanonicalGuid(string value, string fieldName)
        {
            Guid parsed;
            if (value == null
                || !Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(value, parsed.ToString("D")))
            {
                throw InvalidStateXml(
                    fieldName + " is not a canonical non-empty GUID.");
            }

            return parsed;
        }

        private static DateTime ParseCanonicalUtc(string value, string fieldName)
        {
            DateTime parsed;
            if (value == null
                || !DateTime.TryParseExact(
                    value,
                    UtcTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed)
                || parsed.Kind != DateTimeKind.Utc
                || !StringComparer.Ordinal.Equals(value, FormatUtc(parsed)))
            {
                throw InvalidStateXml(
                    fieldName + " is not a canonical UTC timestamp.");
            }

            return parsed;
        }

        private static string ParseCanonicalIpAddress(string value)
        {
            IPAddress parsed;
            if (value == null
                || !IPAddress.TryParse(value, out parsed)
                || !StringComparer.Ordinal.Equals(value, parsed.ToString()))
            {
                throw InvalidStateXml(
                    "Pending.SourceIp is not a canonical IP address literal.");
            }

            return value;
        }

        private static PendingRequestType ParsePendingType(string value)
        {
            switch (value)
            {
                case "New":
                    return PendingRequestType.New;
                case "Modify":
                    return PendingRequestType.Modify;
                default:
                    throw InvalidStateXml("Pending.Type is invalid.");
            }
        }

        private static BaseRevisionKind ParseBaseRevisionKind(string value)
        {
            switch (value)
            {
                case "Missing":
                    return BaseRevisionKind.Missing;
                case "Active":
                    return BaseRevisionKind.Active;
                case "Tombstone":
                    return BaseRevisionKind.Tombstone;
                default:
                    throw InvalidStateXml("Pending.BaseRevision Kind is invalid.");
            }
        }

        private static string FormatPendingType(PendingRequestType value)
        {
            switch (value)
            {
                case PendingRequestType.New:
                    return "New";
                case PendingRequestType.Modify:
                    return "Modify";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static string FormatBaseRevisionKind(BaseRevisionKind value)
        {
            switch (value)
            {
                case BaseRevisionKind.Missing:
                    return "Missing";
                case BaseRevisionKind.Active:
                    return "Active";
                case BaseRevisionKind.Tombstone:
                    return "Tombstone";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static string FormatUtc(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Persisted timestamps must be UTC.", nameof(value));
            }

            return value.ToString(UtcTimestampFormat, CultureInfo.InvariantCulture);
        }

        private static string FormatUInt64(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static int CompareRecordsByProductCode(
            ServiceRecord left,
            ServiceRecord right)
        {
            return string.CompareOrdinal(
                left.Definition.ProductCode.Value,
                right.Definition.ProductCode.Value);
        }

        private static int ComparePendingRegistrations(
            PendingRegistration left,
            PendingRegistration right)
        {
            int productComparison = string.CompareOrdinal(
                left.Requested.ProductCode.Value,
                right.Requested.ProductCode.Value);
            return productComparison != 0
                ? productComparison
                : string.CompareOrdinal(left.Id.ToString("D"), right.Id.ToString("D"));
        }

        private static XmlSerializerNamespaces CreateEmptyNamespaces()
        {
            var namespaces = new XmlSerializerNamespaces();
            namespaces.Add(string.Empty, string.Empty);
            return namespaces;
        }

        private static InvalidDataException InvalidStateXml(string message)
        {
            return new InvalidDataException(message);
        }

        private static InvalidDataException InvalidStateXml(
            string message,
            Exception innerException)
        {
            return new InvalidDataException(message, innerException);
        }
    }
}
