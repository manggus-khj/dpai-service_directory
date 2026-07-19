using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public static partial class PeerSyncXmlCodec
    {
        private const string SchemaResourceName =
            "DEEPAi.ServiceDirectory.InternalProtocol.Peer.peer.xsd";
        private const string UtcTimestampFormat =
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XNamespace Namespace =
            PeerSyncContract.XmlNamespace;
        private static readonly XmlSchemaSet PeerSchemas =
            LoadPeerSchemas();

        // The caller must authenticate the exact raw body bytes, validate the
        // endpoint, peer identity, key epoch and session, and apply rate limits
        // before calling this parser. This codec performs no trust decision.
        // A returned batch must still pass cross-batch staging validation before
        // any merge, logical-clock update, persistence or snapshot publication.
        public static PeerPushExchangeRequest ParseAuthenticatedPushRequest(
            byte[] body)
        {
            string xml = DecodeAndInspectAuthenticatedBody(body);
            XElement root = LoadSchemaValidatedRoot(
                xml,
                body.Length,
                "Exchange");
            EnsurePushExchangeShape(root);
            EnsureOnlyContractAttributes(root);

            ParsedSyncData parsed = ParseSyncData(
                root.Element(Namespace + "SyncData"));
            return parsed.CreatePushRequest();
        }

        private static ParsedSyncData ParseSyncData(XElement syncData)
        {
            Guid instanceId = ReadCanonicalGuid(syncData, "InstanceId");
            Guid snapshotId = ReadCanonicalGuid(syncData, "SnapshotId");
            ulong logicalClock = ReadCanonicalUInt64(
                syncData,
                "LogicalClock");
            uint batchIndex = ReadCanonicalUInt32(syncData, "BatchIndex");
            ulong totalCount = ReadCanonicalUInt64(syncData, "TotalCount");
            bool isLastBatch = ReadCanonicalBoolean(
                syncData,
                "IsLastBatch");

            XElement itemsElement = syncData.Element(Namespace + "Items");
            var items = new List<PeerSyncServiceItem>();
            string previousProductCode = null;
            foreach (XElement serviceElement in
                itemsElement.Elements(Namespace + "Service"))
            {
                PeerSyncServiceItem item = ParseService(
                    serviceElement,
                    logicalClock);
                if (previousProductCode != null
                    && string.CompareOrdinal(
                        previousProductCode,
                        item.ProductCode) >= 0)
                {
                    throw InvalidRequest(
                        "Peer sync ProductCode values must be strictly Ordinal ascending within a batch.");
                }

                previousProductCode = item.ProductCode;
                items.Add(item);
            }

            return new ParsedSyncData(
                instanceId,
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                isLastBatch,
                items.AsReadOnly());
        }

        private static PeerSyncServiceItem ParseService(
            XElement serviceElement,
            ulong logicalClock)
        {
            string rawName = ReadRequiredValue(serviceElement, "Name");
            string rawProductCode = ReadRequiredValue(
                serviceElement,
                "ProductCode");
            string rawServerAddress = ReadRequiredValue(
                serviceElement,
                "ServerAddress");
            int port = ReadCanonicalPort(serviceElement);

            ServiceDefinition definition;
            ServiceDefinitionValidationError validationError;
            if (!ServiceDefinition.TryCreate(
                    rawName,
                    rawProductCode,
                    rawServerAddress,
                    port,
                    out definition,
                    out validationError)
                || !StringComparer.Ordinal.Equals(
                    rawProductCode,
                    definition.ProductCode.Value)
                || !StringComparer.Ordinal.Equals(
                    rawName,
                    definition.Name)
                || !StringComparer.Ordinal.Equals(
                    rawServerAddress,
                    definition.ServerAddress))
            {
                throw InvalidRequest(
                    "A Peer sync service definition is invalid: "
                    + validationError
                    + ".");
            }

            DateTime lastModifiedUtc = ReadCanonicalUtc(
                serviceElement,
                "LastModifiedUtc");
            bool deleted = ReadCanonicalBoolean(serviceElement, "Deleted");
            DateTime? deletedUtc = ReadOptionalCanonicalUtc(
                serviceElement,
                "DeletedUtc");
            if (deleted != deletedUtc.HasValue)
            {
                throw InvalidRequest(
                    "Deleted and DeletedUtc must describe the same Peer sync state.");
            }

            ulong logicalVersion = ReadCanonicalUInt64(
                serviceElement,
                "LogicalVersion");
            if (logicalVersion == 0 || logicalVersion > logicalClock)
            {
                throw InvalidRequest(
                    "Peer sync LogicalVersion must be positive and no greater than LogicalClock.");
            }

            Guid originInstanceId = ReadCanonicalGuid(
                serviceElement,
                "OriginInstanceId");

            return new PeerSyncServiceItem(
                definition.Name,
                definition.ProductCode.Value,
                definition.ServerAddress,
                definition.Port,
                lastModifiedUtc,
                deleted,
                deletedUtc,
                logicalVersion,
                originInstanceId);
        }

        private static string DecodeAndInspectAuthenticatedBody(byte[] body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0)
            {
                throw InvalidRequest("The Peer sync body is empty.");
            }

            if (body.Length > PeerSyncContract.MaximumExchangeBodyBytes)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "The Peer sync body exceeds the raw byte limit.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer sync body is not strict UTF-8.",
                    exception);
            }

            if (xml.Length > 0 && xml[0] == '\uFEFF')
            {
                xml = xml.Substring(1);
            }

            InspectDepthAndCountItems(xml, body.Length);
            return xml;
        }

        private static void InspectDepthAndCountItems(
            string xml,
            int maximumCharacters)
        {
            var settings = CreateReaderSettings(maximumCharacters);
            var localNames = new string[PeerSyncContract.MaximumXmlDepth];
            var namespaces = new string[PeerSyncContract.MaximumXmlDepth];
            int itemCount = 0;

            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(
                    textReader,
                    settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (reader.Depth + 1 >
                            PeerSyncContract.MaximumXmlDepth)
                        {
                            throw InvalidRequest(
                                "The Peer sync body exceeds the XML depth limit.");
                        }

                        localNames[reader.Depth] = reader.LocalName;
                        namespaces[reader.Depth] = reader.NamespaceURI;
                        if (reader.Depth == 3
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                0,
                                "Exchange")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                1,
                                "SyncData")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                2,
                                "Items")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                3,
                                "Service"))
                        {
                            itemCount++;
                        }
                        else if (reader.Depth == 4
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                0,
                                "Response")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                1,
                                "Exchange")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                2,
                                "SyncData")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                3,
                                "Items")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                4,
                                "Service"))
                        {
                            itemCount++;
                        }

                        if (itemCount >
                            PeerSyncContract.MaximumBatchItemCount)
                        {
                            throw new PeerSyncProtocolException(
                                PeerSyncProtocolFailure.ItemLimitExceeded,
                                "The Peer sync batch exceeds 1,000 Service items.");
                        }
                    }
                }
            }
            catch (PeerSyncProtocolException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer sync body contains invalid XML.",
                    exception);
            }
        }

        private static bool IsPeerElement(
            string[] localNames,
            string[] namespaces,
            int depth,
            string expectedLocalName)
        {
            return StringComparer.Ordinal.Equals(
                    localNames[depth],
                    expectedLocalName)
                && StringComparer.Ordinal.Equals(
                    namespaces[depth],
                    PeerSyncContract.XmlNamespace);
        }

        private static XElement LoadSchemaValidatedRoot(
            string xml,
            int maximumCharacters,
            string expectedRootName)
        {
            XmlReaderSettings settings = CreateReaderSettings(
                maximumCharacters);
            settings.Schemas = PeerSchemas;
            settings.ValidationType = ValidationType.Schema;
            settings.ValidationEventHandler += OnSchemaValidation;

            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(
                    textReader,
                    settings))
                {
                    XDocument document = XDocument.Load(
                        reader,
                        LoadOptions.None);
                    XElement root = document.Root;
                    if (root == null
                        || root.Name != Namespace + expectedRootName)
                    {
                        throw InvalidRequest(
                            "The Peer sync root or namespace is invalid.");
                    }

                    return root;
                }
            }
            catch (PeerSyncProtocolException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is XmlException
                    || exception is XmlSchemaException)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer sync body is invalid.",
                    exception);
            }
        }

        private static XmlReaderSettings CreateReaderSettings(
            int maximumCharacters)
        {
            return new XmlReaderSettings
            {
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = Math.Max(1, maximumCharacters),
                ValidationType = ValidationType.None,
                XmlResolver = null
            };
        }

        private static void EnsurePushExchangeShape(XElement root)
        {
            XAttribute mode = root.Attribute("Mode");
            if (mode == null
                || !StringComparer.Ordinal.Equals(mode.Value, "Push"))
            {
                throw InvalidRequest(
                    "This Peer sync parser accepts only Mode=Push requests.");
            }

            int childCount = 0;
            XElement onlyChild = null;
            foreach (XElement child in root.Elements())
            {
                childCount++;
                onlyChild = child;
            }

            if (childCount != 1
                || onlyChild.Name != Namespace + "SyncData")
            {
                throw InvalidRequest(
                    "Mode=Push must contain exactly one SyncData child.");
            }
        }

        private static void EnsureOnlyContractAttributes(XElement root)
        {
            foreach (XElement element in root.DescendantsAndSelf())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    if (element.Name == Namespace + "Exchange"
                        && attribute.Name == "Mode")
                    {
                        continue;
                    }

                    throw InvalidRequest(
                        "The Peer sync request contains an unknown attribute.");
                }
            }
        }

        private static XmlSchemaSet LoadPeerSchemas()
        {
            Stream schemaStream = typeof(PeerSyncXmlCodec)
                .Assembly
                .GetManifestResourceStream(SchemaResourceName);
            if (schemaStream == null)
            {
                throw new InvalidOperationException(
                    "The embedded Peer XML schema is missing.");
            }

            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            var schemas = new XmlSchemaSet
            {
                XmlResolver = null
            };
            using (schemaStream)
            using (XmlReader reader = XmlReader.Create(
                schemaStream,
                readerSettings))
            {
                schemas.Add(PeerSyncContract.XmlNamespace, reader);
            }

            schemas.Compile();
            return schemas;
        }

        private static void OnSchemaValidation(
            object sender,
            ValidationEventArgs eventArgs)
        {
            throw new PeerSyncProtocolException(
                PeerSyncProtocolFailure.InvalidRequest,
                "The Peer sync body does not match the fixed XML schema.",
                eventArgs.Exception);
        }

        private static string ReadRequiredValue(
            XElement parent,
            string name)
        {
            XElement element = parent.Element(Namespace + name);
            if (element == null)
            {
                throw InvalidRequest(
                    "The Peer sync body is missing a required value: "
                    + name
                    + ".");
            }

            return element.Value;
        }

        private static Guid ReadCanonicalGuid(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            Guid value;
            if (!Guid.TryParseExact(text, "D", out value)
                || value == Guid.Empty
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString("D").ToLowerInvariant()))
            {
                throw InvalidRequest(
                    "A Peer sync GUID is not canonical: " + name + ".");
            }

            return value;
        }

        private static DateTime ReadCanonicalUtc(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            DateTime value;
            if (!DateTime.TryParseExact(
                    text,
                    UtcTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out value)
                || value.Kind != DateTimeKind.Utc
                || !StringComparer.Ordinal.Equals(text, FormatUtc(value)))
            {
                throw InvalidRequest(
                    "A Peer sync timestamp is not canonical UTC: "
                    + name
                    + ".");
            }

            return value;
        }

        private static DateTime? ReadOptionalCanonicalUtc(
            XElement parent,
            string name)
        {
            XElement element = parent.Element(Namespace + name);
            return element == null
                ? (DateTime?)null
                : ReadCanonicalUtc(parent, name);
        }

        private static string FormatUtc(DateTime value)
        {
            return value.ToString(
                UtcTimestampFormat,
                CultureInfo.InvariantCulture);
        }

        private static bool ReadCanonicalBoolean(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            if (StringComparer.Ordinal.Equals(text, "true"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "false"))
            {
                return false;
            }

            throw InvalidRequest(
                "A Peer sync boolean is not canonical: " + name + ".");
        }

        private static ulong ReadCanonicalUInt64(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            ulong value;
            if (!ulong.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString(CultureInfo.InvariantCulture)))
            {
                throw InvalidRequest(
                    "A Peer sync unsigned integer is not canonical: "
                    + name
                    + ".");
            }

            return value;
        }

        private static uint ReadCanonicalUInt32(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            uint value;
            if (!uint.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString(CultureInfo.InvariantCulture)))
            {
                throw InvalidRequest(
                    "A Peer sync unsigned integer is not canonical: "
                    + name
                    + ".");
            }

            return value;
        }

        private static int ReadCanonicalPort(XElement parent)
        {
            string text = ReadRequiredValue(parent, "Port");
            int value;
            if (!int.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                || value < 1
                || value > 65535
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString(CultureInfo.InvariantCulture)))
            {
                throw InvalidRequest(
                    "The Peer sync Port is not canonical or in range.");
            }

            return value;
        }

        private sealed class ParsedSyncData
        {
            public ParsedSyncData(
                Guid instanceId,
                Guid snapshotId,
                ulong logicalClock,
                uint batchIndex,
                ulong totalCount,
                bool isLastBatch,
                IReadOnlyList<PeerSyncServiceItem> items)
            {
                InstanceId = instanceId;
                SnapshotId = snapshotId;
                LogicalClock = logicalClock;
                BatchIndex = batchIndex;
                TotalCount = totalCount;
                IsLastBatch = isLastBatch;
                Items = items;
            }

            public Guid InstanceId { get; }

            public Guid SnapshotId { get; }

            public ulong LogicalClock { get; }

            public uint BatchIndex { get; }

            public ulong TotalCount { get; }

            public bool IsLastBatch { get; }

            public IReadOnlyList<PeerSyncServiceItem> Items { get; }

            public PeerPushExchangeRequest CreatePushRequest()
            {
                return new PeerPushExchangeRequest(
                    InstanceId,
                    SnapshotId,
                    LogicalClock,
                    BatchIndex,
                    TotalCount,
                    IsLastBatch,
                    Items);
            }

            public PeerPullExchangeBatch CreatePullBatch()
            {
                return new PeerPullExchangeBatch(
                    InstanceId,
                    SnapshotId,
                    LogicalClock,
                    BatchIndex,
                    TotalCount,
                    IsLastBatch,
                    Items);
            }
        }

        private static PeerSyncProtocolException InvalidRequest(
            string message)
        {
            return new PeerSyncProtocolException(
                PeerSyncProtocolFailure.InvalidRequest,
                message);
        }
    }
}
