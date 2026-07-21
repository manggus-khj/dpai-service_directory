using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        internal const int MaximumDocumentBytes = 16 * 1024 * 1024;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly XmlSerializer DirectorySerializer =
            new XmlSerializer(typeof(DirectoryDocument));
        private static readonly XmlSerializerNamespaces EmptyNamespaces =
            CreateEmptyNamespaces();
        private static readonly object SerializerGate = new object();

        internal byte[] SerializeDirectory(DirectorySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (snapshot.PendingCount != 0)
            {
                throw new InvalidOperationException(
                    "The target v1 directory state cannot persist pending registrations.");
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

        internal DirectorySnapshot DeserializeSnapshot(
            byte[] directoryContents)
        {
            if (directoryContents == null)
            {
                throw new ArgumentNullException(nameof(directoryContents));
            }

            try
            {
                string directoryXml = DecodeStrictUtf8(
                    directoryContents,
                    "directory.xml");

                StrictShapeReader.ValidateDirectory(directoryXml);

                DirectoryDocument directory = DeserializeDocument<DirectoryDocument>(
                    directoryXml,
                    DirectorySerializer,
                    "directory.xml");

                DirectorySnapshot snapshot = ToSnapshot(directory);
                RequireCanonicalDocument(
                    directoryContents,
                    SerializeDirectory(snapshot),
                    "directory.xml");
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
            DirectoryDocument directory)
        {
            if (directory == null)
            {
                throw InvalidStateXml("A persisted state document is missing its root.");
            }

            RequireSchemaVersion(directory.SchemaVersion, "directory.xml");

            if (directory.Records == null)
            {
                throw InvalidStateXml("directory.xml is missing its Records collection.");
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

            return new DirectorySnapshot(
                records,
                new PendingRegistration[0],
                logicalClock);
        }

        private static ServiceRecordDocument ToDocument(ServiceRecord record)
        {
            return new ServiceRecordDocument
            {
                Name = record.Definition.Name,
                ProductCode = record.Definition.ProductCode.Value,
                ServiceHostName = record.Definition.ServiceHostName,
                ServiceIpv4Address = record.Definition.ServiceIpv4Address,
                Port = record.Definition.Port.ToString(
                    CultureInfo.InvariantCulture),
                LastModifiedUtc = FormatUtc(record.LastModifiedUtc),
                Deleted = record.Deleted ? "true" : "false",
                DeletedUtc = record.DeletedUtc.HasValue
                    ? FormatUtc(record.DeletedUtc.Value)
                    : null,
                LogicalVersion = FormatUInt64(record.LogicalVersion),
                OriginInstanceId = record.OriginInstanceId.ToString("D")
            };
        }

        private static ServiceDefinition ToServiceDefinition(
            ServiceRecordDocument document,
            string context)
        {
            if (document == null)
            {
                throw InvalidStateXml(context + " is missing its service definition.");
            }

            int port = ParseCanonicalInt32(document.Port, context + ".Port");
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError identityError;
            if (!ServiceEndpointIdentity.TryCreate(
                    document.ServiceHostName,
                    document.ServiceIpv4Address,
                    out identity,
                    out identityError))
            {
                throw InvalidStateXml(
                    context + " contains an invalid service identity: "
                    + identityError
                    + ".");
            }

            ServiceDefinition definition;
            ServiceDefinitionValidationError error;
            if (!ServiceDefinition.TryCreate(
                    document.Name,
                    document.ProductCode,
                    identity,
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
                    document.ServiceHostName,
                    definition.ServiceHostName)
                || !StringComparer.Ordinal.Equals(
                    document.ServiceIpv4Address,
                    definition.ServiceIpv4Address))
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

            ServiceDefinition definition = ToServiceDefinition(
                document,
                context);
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

                    byte[] contents = EnsureFinalCrLf(stream.ToArray());
                    if (contents.Length > MaximumDocumentBytes)
                    {
                        throw InvalidStateXml(
                            fileName + " exceeds the 16 MiB size limit.");
                    }

                    return contents;
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
            if (contents.Length == 0
                || contents.Length > MaximumDocumentBytes)
            {
                throw InvalidStateXml(
                    fileName + " is empty or exceeds the size limit.");
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

        private static XmlSerializerNamespaces CreateEmptyNamespaces()
        {
            var namespaces = new XmlSerializerNamespaces();
            namespaces.Add(string.Empty, string.Empty);
            return namespaces;
        }

        private static byte[] EnsureFinalCrLf(byte[] contents)
        {
            if (contents.Length >= 2
                && contents[contents.Length - 2] == (byte)'\r'
                && contents[contents.Length - 1] == (byte)'\n')
            {
                return contents;
            }

            var canonical = new byte[contents.Length + 2];
            Buffer.BlockCopy(contents, 0, canonical, 0, contents.Length);
            canonical[canonical.Length - 2] = (byte)'\r';
            canonical[canonical.Length - 1] = (byte)'\n';
            return canonical;
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
