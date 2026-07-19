using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static class AdminXmlCodec
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly XNamespace Namespace = AdminApiContract.XmlNamespace;
        private static readonly XmlSchemaSet AdminSchemas = LoadAdminSchemas();

        public static AdminResponse<AdminPage<AdminServiceItem>> ParseServicesResponse(
            byte[] body)
        {
            return ParseResponse(
                body,
                "Services",
                (root, services) =>
                {
                    var items = new List<AdminServiceItem>();
                    foreach (XElement service in services.Elements(Namespace + "Service"))
                    {
                        AdminServiceDefinition definition = ParseServiceDefinition(service);
                        DateTime lastModifiedUtc = ReadRequiredUtc(
                            service,
                            "LastModifiedUtc");
                        bool deleted = ReadRequiredBoolean(service, "Deleted");
                        DateTime? deletedUtc = ReadOptionalUtc(service, "DeletedUtc");
                        items.Add(
                            new AdminServiceItem(
                                definition,
                                lastModifiedUtc,
                                deleted,
                                deletedUtc));
                    }

                    return new AdminPage<AdminServiceItem>(
                        items.AsReadOnly(),
                        ReadRequiredNonNegativeInt(root, "TotalCount"),
                        ReadOptionalString(root, "NextCursor"));
                });
        }

        public static AdminResponse<AdminPage<AdminPendingItem>> ParsePendingResponse(
            byte[] body)
        {
            return ParseResponse(
                body,
                "PendingItems",
                (root, pendingItems) =>
                {
                    var items = new List<AdminPendingItem>();
                    foreach (XElement pending in pendingItems.Elements(Namespace + "PendingItem"))
                    {
                        Guid id = ReadRequiredGuid(pending, "Id");
                        string typeText = ReadRequiredString(pending, "Type");
                        AdminPendingRequestType type;
                        if (StringComparer.Ordinal.Equals(typeText, "New"))
                        {
                            type = AdminPendingRequestType.New;
                        }
                        else if (StringComparer.Ordinal.Equals(typeText, "Modify"))
                        {
                            type = AdminPendingRequestType.Modify;
                        }
                        else
                        {
                            throw new AdminProtocolException(
                                "Pending Type must be New or Modify.");
                        }

                        XElement requestedElement = ReadRequiredElement(
                            pending,
                            "Requested");
                        XElement currentElement = ReadOptionalElement(pending, "Current");
                        items.Add(
                            new AdminPendingItem(
                                id,
                                type,
                                ReadRequiredUtc(pending, "RequestedUtc"),
                                ReadRequiredString(pending, "SourceIP"),
                                ParseServiceDefinition(requestedElement),
                                currentElement == null
                                    ? null
                                    : ParseServiceDefinition(currentElement)));
                    }

                    return new AdminPage<AdminPendingItem>(
                        items.AsReadOnly(),
                        ReadRequiredNonNegativeInt(root, "TotalCount"),
                        ReadOptionalString(root, "NextCursor"));
                });
        }

        public static AdminResponse<AdminSyncStatus> ParseSyncResponse(byte[] body)
        {
            return ParseResponse(
                body,
                "SyncStatus",
                (root, sync) => new AdminSyncStatus(
                    ReadRequiredBoolean(sync, "Enabled"),
                    ParsePairingState(ReadRequiredString(sync, "PairingState")),
                    ReadOptionalString(sync, "PeerEndpoint"),
                    ReadOptionalGuid(sync, "PeerInstanceId"),
                    ReadOptionalUInt64(sync, "KeyEpoch"),
                    ReadOptionalUtc(sync, "LastSyncUtc"),
                    ReadRequiredString(sync, "LastResult"),
                    ReadOptionalInt64(sync, "ClockSkewSeconds"),
                    ReadOptionalGuid(sync, "PairingId"),
                    ReadOptionalString(sync, "Sas"),
                    ReadOptionalUtc(sync, "PairingExpiresUtc"),
                    ReadOptionalInt(sync, "PairingRemainingSeconds"),
                    ReadOptionalBoolean(sync, "LocalConfirmed"),
                    ReadOptionalBoolean(sync, "RemoteConfirmed"),
                    ReadOptionalUtc(sync, "CommitExpiresUtc"),
                    ReadOptionalBoolean(sync, "LocalCommitConfirmed"),
                    ReadOptionalBoolean(sync, "RemoteCommitConfirmed"),
                    ParsePeerNotificationOperation(
                        ReadRequiredString(sync, "LastPeerNotificationOperation")),
                    ParsePeerNotificationResult(
                        ReadRequiredString(sync, "LastPeerNotificationResult")),
                    ReadOptionalUtc(sync, "LastPeerNotificationUtc")));
        }

        public static AdminResponse<AdminSyncDisableResult> ParseSyncDisableResponse(
            byte[] body)
        {
            return ParseResponse(
                body,
                "SyncDisableResult",
                (root, disable) => new AdminSyncDisableResult(
                    ParsePairingState(
                        ReadRequiredString(disable, "LocalPairingState")),
                    ParsePeerNotificationOperation(
                        ReadRequiredString(disable, "PeerNotificationOperation")),
                    ParsePeerNotificationResult(
                        ReadRequiredString(disable, "PeerNotificationResult")),
                    ReadRequiredUtc(disable, "PeerNotificationUtc")));
        }

        public static AdminResponse<AdminLoggingSettings> ParseLoggingResponse(
            byte[] body)
        {
            return ParseResponse(
                body,
                "LoggingSettings",
                (root, logging) => new AdminLoggingSettings(
                    ReadRequiredInt(logging, "LogRetentionDays")));
        }

        public static AdminResponse<AdminUnit> ParseUnitResponse(byte[] body)
        {
            XElement root = LoadEnvelopeRoot(body);
            EnvelopeHeader header = ParseEnvelopeHeader(root);
            EnsureUnitEnvelopeShape(root);
            return new AdminResponse<AdminUnit>(
                header.Result,
                header.Code,
                header.Message,
                header.IsSuccess ? AdminUnit.Value : null);
        }

        public static byte[] SerializeEnableSync(string peerEndpoint, bool rePair)
        {
            string canonicalEndpoint = AdminPeerEndpoint.Normalize(peerEndpoint);

            return Serialize(
                new XElement(
                    Namespace + "EnableSync",
                    new XElement(Namespace + "PeerEndpoint", canonicalEndpoint),
                    new XElement(
                        Namespace + "RePair",
                        rePair ? "true" : "false")));
        }

        public static byte[] SerializePairingConfirmation(Guid pairingId)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException("Pairing ID cannot be empty.", nameof(pairingId));
            }

            return Serialize(
                new XElement(
                    Namespace + "PairingConfirmation",
                    new XElement(
                        Namespace + "PairingId",
                        pairingId.ToString("D")),
                    new XElement(Namespace + "Confirmed", "true")));
        }

        public static byte[] SerializeDisableSync(bool forgetPeer)
        {
            return Serialize(
                new XElement(
                    Namespace + "DisableSync",
                    new XElement(
                        Namespace + "ForgetPeer",
                        forgetPeer ? "true" : "false")));
        }

        public static byte[] SerializePairingCancellation(Guid pairingId)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException("Pairing ID cannot be empty.", nameof(pairingId));
            }

            return Serialize(
                new XElement(
                    Namespace + "PairingCancellation",
                    new XElement(
                        Namespace + "PairingId",
                        pairingId.ToString("D"))));
        }

        public static byte[] SerializeLoggingSettings(int logRetentionDays)
        {
            if (logRetentionDays < AdminApiContract.MinimumLogRetentionDays
                || logRetentionDays > AdminApiContract.MaximumLogRetentionDays)
            {
                throw new ArgumentOutOfRangeException(nameof(logRetentionDays));
            }

            return Serialize(
                new XElement(
                    Namespace + "LoggingSettings",
                    new XElement(
                        Namespace + "LogRetentionDays",
                        logRetentionDays.ToString(CultureInfo.InvariantCulture))));
        }

        private static AdminResponse<T> ParseResponse<T>(
            byte[] body,
            string payloadName,
            Func<XElement, XElement, T> payloadParser)
            where T : class
        {
            XElement root = LoadEnvelopeRoot(body);
            EnvelopeHeader header = ParseEnvelopeHeader(root);
            if (!header.IsSuccess)
            {
                return new AdminResponse<T>(
                    header.Result,
                    header.Code,
                    header.Message,
                    null);
            }

            XElement payload = ReadRequiredElement(root, payloadName);
            T parsedPayload;
            try
            {
                parsedPayload = payloadParser(root, payload);
            }
            catch (AdminProtocolException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    || exception is FormatException
                    || exception is OverflowException)
            {
                throw new AdminProtocolException(
                    "The Admin response payload is invalid.",
                    exception);
            }

            if (parsedPayload == null)
            {
                throw new AdminProtocolException(
                    "A successful Admin response payload cannot be empty.");
            }

            return new AdminResponse<T>(
                header.Result,
                header.Code,
                header.Message,
                parsedPayload);
        }

        private static XElement LoadEnvelopeRoot(byte[] body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0 || body.Length > AdminApiContract.MaximumBodyBytes)
            {
                throw new AdminProtocolException(
                    "The Admin response body size is invalid.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException exception)
            {
                throw new AdminProtocolException(
                    "The Admin response is not strict UTF-8.",
                    exception);
            }

            if (xml.Length > 0 && xml[0] == '\uFEFF')
            {
                xml = xml.Substring(1);
            }

            ValidateXmlDepth(xml, body.Length);

            var settings = new XmlReaderSettings
            {
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = Math.Max(1, body.Length),
                Schemas = AdminSchemas,
                ValidationType = ValidationType.Schema,
                XmlResolver = null
            };
            settings.ValidationEventHandler += OnSchemaValidation;

            XDocument document;
            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    document = XDocument.Load(reader, LoadOptions.None);
                }
            }
            catch (XmlException exception)
            {
                throw new AdminProtocolException(
                    "The Admin response contains invalid XML.",
                    exception);
            }

            XElement root = document.Root;
            if (root == null || root.Name != Namespace + "Response")
            {
                throw new AdminProtocolException(
                    "The Admin response root or namespace is invalid.");
            }

            return root;
        }

        private static void ValidateXmlDepth(string xml, int maximumCharacters)
        {
            var settings = new XmlReaderSettings
            {
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = Math.Max(1, maximumCharacters),
                ValidationType = ValidationType.None,
                XmlResolver = null
            };

            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element
                            && reader.Depth + 1 > AdminApiContract.MaximumXmlDepth)
                        {
                            throw new AdminProtocolException(
                                "The Admin response exceeds the XML depth limit.");
                        }
                    }
                }
            }
            catch (XmlException exception)
            {
                throw new AdminProtocolException(
                    "The Admin response contains invalid XML.",
                    exception);
            }
        }

        private static void EnsureUnitEnvelopeShape(XElement root)
        {
            foreach (XElement child in root.Elements())
            {
                if (child.Name == Namespace + "Result"
                    || child.Name == Namespace + "Code"
                    || child.Name == Namespace + "Message"
                    || child.Name == Namespace + "Extensions")
                {
                    continue;
                }

                throw new AdminProtocolException(
                    "The Admin response contains an unexpected payload.");
            }
        }

        private static XmlSchemaSet LoadAdminSchemas()
        {
            const string resourceName =
                "DEEPAi.ServiceDirectory.InternalProtocol.Admin.admin.xsd";
            Stream schemaStream = typeof(AdminXmlCodec)
                .Assembly
                .GetManifestResourceStream(resourceName);
            if (schemaStream == null)
            {
                throw new InvalidOperationException(
                    "The embedded Admin XML schema is missing.");
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
            using (XmlReader reader = XmlReader.Create(schemaStream, readerSettings))
            {
                schemas.Add(AdminApiContract.XmlNamespace, reader);
            }

            schemas.Compile();
            return schemas;
        }

        private static void OnSchemaValidation(
            object sender,
            ValidationEventArgs eventArgs)
        {
            throw new AdminProtocolException(
                "The Admin response does not match the fixed XML schema.",
                eventArgs.Exception);
        }

        private static EnvelopeHeader ParseEnvelopeHeader(XElement root)
        {
            string result = ReadRequiredString(root, "Result");
            int code = ReadRequiredNonNegativeInt(root, "Code");
            string message = ReadOptionalString(root, "Message") ?? string.Empty;
            bool resultIsOk = StringComparer.Ordinal.Equals(result, "OK");
            if ((code == 0) != resultIsOk)
            {
                throw new AdminProtocolException(
                    "The Admin response Result and Code are inconsistent.");
            }

            return new EnvelopeHeader(result, code, message, resultIsOk);
        }

        private static AdminServiceDefinition ParseServiceDefinition(
            XElement element)
        {
            string name = ReadRequiredString(element, "Name");
            string productCode = ReadRequiredString(element, "ProductCode");
            string serverAddress = ReadRequiredString(
                element,
                "ServerAddress");
            int port = ReadRequiredInt(element, "Port");

            ServiceDefinition definition;
            ServiceDefinitionValidationError validationError;
            if (!ServiceDefinition.TryCreate(
                    name,
                    productCode,
                    serverAddress,
                    port,
                    out definition,
                    out validationError))
            {
                throw new AdminProtocolException(
                    "The Admin service definition is invalid: "
                    + validationError
                    + ".");
            }

            if (!StringComparer.Ordinal.Equals(name, definition.Name)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    definition.ProductCode.Value)
                || !StringComparer.Ordinal.Equals(
                    serverAddress,
                    definition.ServerAddress))
            {
                throw new AdminProtocolException(
                    "The Admin service definition is not canonical.");
            }

            return new AdminServiceDefinition(
                definition.Name,
                definition.ProductCode.Value,
                definition.ServerAddress,
                definition.Port);
        }

        private static byte[] Serialize(XElement root)
        {
            string xml = root.ToString(SaveOptions.DisableFormatting);
            byte[] bytes = StrictUtf8.GetBytes(xml);
            if (bytes.Length > AdminApiContract.MaximumBodyBytes)
            {
                throw new ArgumentException("The Admin request exceeds the body limit.");
            }

            return bytes;
        }

        private static XElement ReadRequiredElement(XElement parent, string name)
        {
            XElement element = ReadOptionalElement(parent, name);
            if (element == null)
            {
                throw new AdminProtocolException(
                    "Required Admin XML element is missing: " + name + ".");
            }

            return element;
        }

        private static XElement ReadOptionalElement(XElement parent, string name)
        {
            XElement found = null;
            foreach (XElement element in parent.Elements(Namespace + name))
            {
                if (found != null)
                {
                    throw new AdminProtocolException(
                        "Admin XML element appears more than once: " + name + ".");
                }

                found = element;
            }

            return found;
        }

        private static string ReadRequiredString(XElement parent, string name)
        {
            string value = ReadOptionalString(parent, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new AdminProtocolException(
                    "Required Admin XML value is missing: " + name + ".");
            }

            return value;
        }

        private static string ReadOptionalString(XElement parent, string name)
        {
            XElement element = ReadOptionalElement(parent, name);
            return element == null ? null : element.Value;
        }

        private static int ReadRequiredInt(XElement parent, string name)
        {
            string text = ReadRequiredString(parent, name);
            int value;
            if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value))
            {
                throw new AdminProtocolException(
                    "Admin XML integer is invalid: " + name + ".");
            }

            return value;
        }

        private static int ReadRequiredNonNegativeInt(XElement parent, string name)
        {
            int value = ReadRequiredInt(parent, name);
            if (value < 0)
            {
                throw new AdminProtocolException(
                    "Admin XML integer cannot be negative: " + name + ".");
            }

            return value;
        }

        private static int? ReadOptionalInt(XElement parent, string name)
        {
            string text = ReadOptionalString(parent, name);
            if (text == null)
            {
                return null;
            }

            int value;
            if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value))
            {
                throw new AdminProtocolException(
                    "Admin XML integer is invalid: " + name + ".");
            }

            return value;
        }

        private static long? ReadOptionalInt64(XElement parent, string name)
        {
            string text = ReadOptionalString(parent, name);
            if (text == null)
            {
                return null;
            }

            long value;
            if (!long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value))
            {
                throw new AdminProtocolException(
                    "Admin XML 64-bit integer is invalid: " + name + ".");
            }

            return value;
        }

        private static ulong? ReadOptionalUInt64(XElement parent, string name)
        {
            string text = ReadOptionalString(parent, name);
            if (text == null)
            {
                return null;
            }

            ulong value;
            if (!ulong.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value))
            {
                throw new AdminProtocolException(
                    "Admin XML unsigned integer is invalid: " + name + ".");
            }

            return value;
        }

        private static bool ReadRequiredBoolean(XElement parent, string name)
        {
            string text = ReadRequiredString(parent, name);
            if (StringComparer.Ordinal.Equals(text, "true"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "false"))
            {
                return false;
            }

            throw new AdminProtocolException(
                "Admin XML boolean is invalid: " + name + ".");
        }

        private static bool? ReadOptionalBoolean(XElement parent, string name)
        {
            string text = ReadOptionalString(parent, name);
            if (text == null)
            {
                return null;
            }

            if (StringComparer.Ordinal.Equals(text, "true"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "false"))
            {
                return false;
            }

            throw new AdminProtocolException(
                "Admin XML boolean is invalid: " + name + ".");
        }

        private static AdminPairingState ParsePairingState(string value)
        {
            switch (value)
            {
                case "Unpaired":
                    return AdminPairingState.Unpaired;
                case "PairingWindowOpen":
                    return AdminPairingState.PairingWindowOpen;
                case "Negotiating":
                    return AdminPairingState.Negotiating;
                case "SasPending":
                    return AdminPairingState.SasPending;
                case "BothConfirmed":
                    return AdminPairingState.BothConfirmed;
                case "PairedPendingCommit":
                    return AdminPairingState.PairedPendingCommit;
                case "PairedDisabled":
                    return AdminPairingState.PairedDisabled;
                case "Enabled":
                    return AdminPairingState.Enabled;
                default:
                    throw new AdminProtocolException(
                        "Admin PairingState is invalid.");
            }
        }

        private static AdminPeerNotificationOperation ParsePeerNotificationOperation(
            string value)
        {
            switch (value)
            {
                case "NONE":
                    return AdminPeerNotificationOperation.None;
                case "RELEASE":
                    return AdminPeerNotificationOperation.Release;
                case "REVOKE":
                    return AdminPeerNotificationOperation.Revoke;
                default:
                    throw new AdminProtocolException(
                        "Admin peer notification operation is invalid.");
            }
        }

        private static AdminPeerNotificationResult ParsePeerNotificationResult(
            string value)
        {
            switch (value)
            {
                case "NOT_RUN":
                    return AdminPeerNotificationResult.NotRun;
                case "CONFIRMED":
                    return AdminPeerNotificationResult.Confirmed;
                case "UNCONFIRMED":
                    return AdminPeerNotificationResult.Unconfirmed;
                case "NOT_REQUIRED":
                    return AdminPeerNotificationResult.NotRequired;
                default:
                    throw new AdminProtocolException(
                        "Admin peer notification result is invalid.");
            }
        }

        private static Guid ReadRequiredGuid(XElement parent, string name)
        {
            Guid? value = ReadOptionalGuid(parent, name);
            if (!value.HasValue || value.Value == Guid.Empty)
            {
                throw new AdminProtocolException(
                    "Admin XML GUID is invalid: " + name + ".");
            }

            return value.Value;
        }

        private static Guid? ReadOptionalGuid(XElement parent, string name)
        {
            string text = ReadOptionalString(parent, name);
            if (text == null)
            {
                return null;
            }

            Guid value;
            if (!Guid.TryParseExact(text, "D", out value) || value == Guid.Empty)
            {
                throw new AdminProtocolException(
                    "Admin XML GUID is invalid: " + name + ".");
            }

            return value;
        }

        private static DateTime ReadRequiredUtc(XElement parent, string name)
        {
            DateTime? value = ReadOptionalUtc(parent, name);
            if (!value.HasValue)
            {
                throw new AdminProtocolException(
                    "Admin XML UTC time is missing: " + name + ".");
            }

            return value.Value;
        }

        private static DateTime? ReadOptionalUtc(XElement parent, string name)
        {
            string text = ReadOptionalString(parent, name);
            if (text == null)
            {
                return null;
            }

            DateTimeOffset parsed;
            if (!text.EndsWith("Z", StringComparison.Ordinal)
                || !DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out parsed)
                || parsed.Offset != TimeSpan.Zero)
            {
                throw new AdminProtocolException(
                    "Admin XML time must be UTC ISO 8601: " + name + ".");
            }

            return parsed.UtcDateTime;
        }

        private sealed class EnvelopeHeader
        {
            public EnvelopeHeader(
                string result,
                int code,
                string message,
                bool isSuccess)
            {
                Result = result;
                Code = code;
                Message = message;
                IsSuccess = isSuccess;
            }

            public string Result { get; }

            public int Code { get; }

            public string Message { get; }

            public bool IsSuccess { get; }
        }
    }
}
