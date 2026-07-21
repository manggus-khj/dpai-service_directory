using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public static class PeerContractXmlCodec
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

        public static byte[] SerializeServiceRecord(PeerServiceRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            return SerializeAndLimit(
                CreateServiceElement(record),
                PeerSyncContract.MaximumControlBodyBytes);
        }

        public static PeerServiceRecord ParseServiceRecord(byte[] body)
        {
            XElement root = ParseValidatedRoot(
                body,
                PeerSyncContract.MaximumControlBodyBytes,
                "Service",
                false);
            return ParseServiceElement(root);
        }

        // The caller must authenticate the exact raw bytes, active session,
        // peer identity and endpoint before invoking this parser.
        public static PeerPkiStateRequest ParseAuthenticatedPkiStateRequest(
            byte[] body)
        {
            XElement root = ParseValidatedRoot(
                body,
                PeerSyncContract.MaximumControlBodyBytes,
                "PkiStateRequest",
                false);
            try
            {
                return new PeerPkiStateRequest(
                    ReadCanonicalGuid(root, "InstanceId"),
                    ReadCanonicalGuid(root, "KnownIssuerInstanceId"),
                    ReadPositiveUInt64(root, "KnownPkiRevision"),
                    ReadPositiveUInt64(root, "KnownCrlNumber"));
            }
            catch (ArgumentException exception)
            {
                throw InvalidRequest(
                    "The Peer PKI state request is invalid.",
                    exception);
            }
        }

        public static byte[] SerializePkiStateRequest(
            PeerPkiStateRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var root = new XElement(
                Namespace + "PkiStateRequest",
                Element("InstanceId", FormatGuid(request.InstanceId)),
                Element(
                    "KnownIssuerInstanceId",
                    FormatGuid(request.KnownIssuerInstanceId)),
                Element(
                    "KnownPkiRevision",
                    FormatUInt64(request.KnownPkiRevision)),
                Element(
                    "KnownCrlNumber",
                    FormatUInt64(request.KnownCrlNumber)));
            return SerializeAndLimit(
                root,
                PeerSyncContract.MaximumControlBodyBytes);
        }

        public static byte[] SerializePkiStateResponse(
            PeerPkiStateResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var root = new XElement(
                Namespace + "Response",
                Element("Result", response.Result),
                Element(
                    "Code",
                    ((uint)response.Code).ToString(
                        CultureInfo.InvariantCulture)),
                Element("Message", response.Message));

            if (response.IsSuccess)
            {
                root.Add(CreatePkiStateElement(response.PkiState));
            }

            return SerializeAndLimit(
                root,
                PeerSyncContract.MaximumExchangeBodyBytes);
        }

        // The known state is required so equal-revision different content can
        // be rejected instead of silently replacing the standby high-water.
        public static PeerPkiStateResponse ParseAuthenticatedPkiStateResponse(
            byte[] body,
            PeerPkiStateRequest request,
            PeerPkiState knownState)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (knownState == null)
            {
                throw new ArgumentNullException(nameof(knownState));
            }

            EnsureKnownStateMatchesRequest(request, knownState);
            XElement root = ParseValidatedRoot(
                body,
                PeerSyncContract.MaximumExchangeBodyBytes,
                "Response",
                true);

            string result = ReadRequiredValue(root, "Result");
            PeerSyncResponseCode code = ReadResponseCode(root);
            string message = ReadRequiredValue(root, "Message");
            XElement stateElement = root.Element(Namespace + "PkiState");
            if (StringComparer.Ordinal.Equals(result, "ERROR"))
            {
                if (code == PeerSyncResponseCode.Ok || stateElement != null)
                {
                    throw InvalidRequest(
                        "A Peer PKI error response contains an invalid payload.");
                }

                return PeerPkiStateResponse.CreateParsedError(code, message);
            }

            if (!StringComparer.Ordinal.Equals(result, "OK")
                || code != PeerSyncResponseCode.Ok
                || stateElement == null)
            {
                throw InvalidRequest(
                    "A successful Peer PKI response requires one PkiState payload.");
            }

            PeerPkiState state = ParsePkiStateElement(stateElement);
            EnsureHighWater(request, knownState, state);
            return PeerPkiStateResponse.CreateSuccess(state);
        }

        private static XElement CreateServiceElement(PeerServiceRecord record)
        {
            var service = new XElement(
                Namespace + "Service",
                Element("Name", record.Name),
                Element("ProductCode", record.ProductCode),
                Element("ServiceHostName", record.ServiceHostName),
                Element(
                    "ServiceIpv4Address",
                    record.ServiceIpv4Address),
                Element(
                    "Port",
                    record.Port.ToString(CultureInfo.InvariantCulture)),
                Element(
                    "LastModifiedUtc",
                    FormatUtc(record.LastModifiedUtc)),
                Element("Deleted", FormatBoolean(record.Deleted)));
            if (record.DeletedUtc.HasValue)
            {
                service.Add(
                    Element(
                        "DeletedUtc",
                        FormatUtc(record.DeletedUtc.Value)));
            }

            service.Add(
                Element(
                    "LogicalVersion",
                    FormatUInt64(record.LogicalVersion)),
                Element(
                    "OriginInstanceId",
                    FormatGuid(record.OriginInstanceId)));
            return service;
        }

        private static PeerServiceRecord ParseServiceElement(XElement root)
        {
            try
            {
                bool deleted = ReadCanonicalBoolean(root, "Deleted");
                DateTime? deletedUtc = ReadOptionalCanonicalUtc(
                    root,
                    "DeletedUtc");
                return new PeerServiceRecord(
                    ReadRequiredValue(root, "Name"),
                    ReadRequiredValue(root, "ProductCode"),
                    ReadRequiredValue(root, "ServiceHostName"),
                    ReadRequiredValue(root, "ServiceIpv4Address"),
                    ReadCanonicalPort(root),
                    ReadCanonicalUtc(root, "LastModifiedUtc"),
                    deleted,
                    deletedUtc,
                    ReadPositiveUInt64(root, "LogicalVersion"),
                    ReadCanonicalGuid(root, "OriginInstanceId"));
            }
            catch (ArgumentException exception)
            {
                throw InvalidRequest(
                    "The Peer service record is invalid or non-canonical.",
                    exception);
            }
        }

        private static XElement CreatePkiStateElement(PeerPkiState state)
        {
            var certificates = new XElement(
                Namespace + "ActiveCertificates");
            foreach (PeerActiveCertificate certificate in
                state.ActiveCertificates)
            {
                certificates.Add(
                    new XElement(
                        Namespace + "Certificate",
                        Element(
                            "ProductCode",
                            certificate.ProductCode),
                        Element(
                            "SerialNumber",
                            certificate.SerialNumber),
                        Element(
                            "LeafSha256",
                            certificate.LeafSha256),
                        Element(
                            "NotAfterUtc",
                            FormatUtc(certificate.NotAfterUtc))));
            }

            byte[] crl = state.GetCrl();
            try
            {
                return new XElement(
                    Namespace + "PkiState",
                    Element(
                        "IssuerInstanceId",
                        FormatGuid(state.IssuerInstanceId)),
                    Element(
                        "PkiRevision",
                        FormatUInt64(state.PkiRevision)),
                    Element(
                        "CrlNumber",
                        FormatUInt64(state.CrlNumber)),
                    Element("CrlSha256", state.CrlSha256),
                    Element("Crl", Convert.ToBase64String(crl)),
                    certificates);
            }
            finally
            {
                Array.Clear(crl, 0, crl.Length);
            }
        }

        private static PeerPkiState ParsePkiStateElement(XElement element)
        {
            byte[] crl = ReadCanonicalBase64(element, "Crl", null);
            try
            {
                var certificates = new List<PeerActiveCertificate>();
                XElement activeCertificates = element.Element(
                    Namespace + "ActiveCertificates");
                foreach (XElement certificateElement in
                    activeCertificates.Elements(Namespace + "Certificate"))
                {
                    certificates.Add(
                        new PeerActiveCertificate(
                            ReadRequiredValue(
                                certificateElement,
                                "ProductCode"),
                            ReadRequiredValue(
                                certificateElement,
                                "SerialNumber"),
                            ReadRequiredValue(
                                certificateElement,
                                "LeafSha256"),
                            ReadCanonicalUtc(
                                certificateElement,
                                "NotAfterUtc")));
                }

                return new PeerPkiState(
                    ReadCanonicalGuid(element, "IssuerInstanceId"),
                    ReadPositiveUInt64(element, "PkiRevision"),
                    ReadPositiveUInt64(element, "CrlNumber"),
                    ReadRequiredValue(element, "CrlSha256"),
                    crl,
                    certificates.AsReadOnly());
            }
            catch (ArgumentException exception)
            {
                throw InvalidRequest(
                    "The Peer PKI state is invalid or non-canonical.",
                    exception);
            }
            finally
            {
                Array.Clear(crl, 0, crl.Length);
            }
        }

        private static void EnsureKnownStateMatchesRequest(
            PeerPkiStateRequest request,
            PeerPkiState knownState)
        {
            if (knownState.IssuerInstanceId
                    != request.KnownIssuerInstanceId
                || knownState.PkiRevision != request.KnownPkiRevision
                || knownState.CrlNumber != request.KnownCrlNumber)
            {
                throw new ArgumentException(
                    "The known Peer PKI state does not match the request high-water.",
                    nameof(knownState));
            }
        }

        private static void EnsureHighWater(
            PeerPkiStateRequest request,
            PeerPkiState knownState,
            PeerPkiState receivedState)
        {
            if (receivedState.IssuerInstanceId
                    != request.KnownIssuerInstanceId
                || receivedState.PkiRevision < request.KnownPkiRevision
                || receivedState.CrlNumber < request.KnownCrlNumber)
            {
                throw InvalidRequest(
                    "The Peer PKI response changes the issuer or rolls back a high-water value.");
            }

            if (receivedState.PkiRevision == request.KnownPkiRevision
                && !StatesEqual(knownState, receivedState))
            {
                throw InvalidRequest(
                    "The Peer PKI response has different content at the known revision.");
            }
        }

        private static bool StatesEqual(
            PeerPkiState left,
            PeerPkiState right)
        {
            if (left.IssuerInstanceId != right.IssuerInstanceId
                || left.PkiRevision != right.PkiRevision
                || left.CrlNumber != right.CrlNumber
                || !StringComparer.Ordinal.Equals(
                    left.CrlSha256,
                    right.CrlSha256)
                || left.ActiveCertificates.Count
                    != right.ActiveCertificates.Count)
            {
                return false;
            }

            byte[] leftCrl = left.GetCrl();
            byte[] rightCrl = right.GetCrl();
            try
            {
                if (!BytesEqual(leftCrl, rightCrl))
                {
                    return false;
                }
            }
            finally
            {
                Array.Clear(leftCrl, 0, leftCrl.Length);
                Array.Clear(rightCrl, 0, rightCrl.Length);
            }

            for (int index = 0;
                index < left.ActiveCertificates.Count;
                index++)
            {
                PeerActiveCertificate leftCertificate =
                    left.ActiveCertificates[index];
                PeerActiveCertificate rightCertificate =
                    right.ActiveCertificates[index];
                if (!StringComparer.Ordinal.Equals(
                        leftCertificate.ProductCode,
                        rightCertificate.ProductCode)
                    || !StringComparer.Ordinal.Equals(
                        leftCertificate.SerialNumber,
                        rightCertificate.SerialNumber)
                    || !StringComparer.Ordinal.Equals(
                        leftCertificate.LeafSha256,
                        rightCertificate.LeafSha256)
                    || leftCertificate.NotAfterUtc
                        != rightCertificate.NotAfterUtc)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static XElement ParseValidatedRoot(
            byte[] body,
            int maximumBodyBytes,
            string expectedRootName,
            bool countCertificates)
        {
            string xml = DecodeAndInspect(
                body,
                maximumBodyBytes,
                countCertificates);
            XmlReaderSettings settings = CreateReaderSettings(
                maximumBodyBytes);
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
                            "The Peer contract root or namespace is invalid.");
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
                throw InvalidRequest(
                    "The Peer contract body is invalid.",
                    exception);
            }
        }

        private static string DecodeAndInspect(
            byte[] body,
            int maximumBodyBytes,
            bool countCertificates)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0)
            {
                throw InvalidRequest("The Peer contract body is empty.");
            }

            if (body.Length > maximumBodyBytes)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "The Peer contract body exceeds the raw byte limit.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException exception)
            {
                throw InvalidRequest(
                    "The Peer contract body is not strict UTF-8.",
                    exception);
            }

            if (xml.Length > 0 && xml[0] == '\uFEFF')
            {
                xml = xml.Substring(1);
            }

            InspectDepthAndCertificateCount(
                xml,
                maximumBodyBytes,
                countCertificates);
            return xml;
        }

        private static void InspectDepthAndCertificateCount(
            string xml,
            int maximumCharacters,
            bool countCertificates)
        {
            XmlReaderSettings settings = CreateReaderSettings(
                maximumCharacters);
            var localNames = new string[PeerSyncContract.MaximumXmlDepth];
            var namespaces = new string[PeerSyncContract.MaximumXmlDepth];
            int certificateCount = 0;

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

                        if (reader.Depth + 1
                            > PeerSyncContract.MaximumXmlDepth)
                        {
                            throw InvalidRequest(
                                "The Peer contract body exceeds the XML depth limit.");
                        }

                        localNames[reader.Depth] = reader.LocalName;
                        namespaces[reader.Depth] = reader.NamespaceURI;
                        if (countCertificates
                            && reader.Depth == 3
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                0,
                                "Response")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                1,
                                "PkiState")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                2,
                                "ActiveCertificates")
                            && IsPeerElement(
                                localNames,
                                namespaces,
                                3,
                                "Certificate"))
                        {
                            certificateCount++;
                            if (certificateCount
                                > PeerSyncContract
                                    .MaximumActiveCertificateCount)
                            {
                                throw new PeerSyncProtocolException(
                                    PeerSyncProtocolFailure
                                        .ItemLimitExceeded,
                                    "The Peer PKI state exceeds 1,000 active certificates.");
                            }
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
                throw InvalidRequest(
                    "The Peer contract body contains invalid XML.",
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

        private static XmlSchemaSet LoadPeerSchemas()
        {
            Stream schemaStream = typeof(PeerContractXmlCodec)
                .Assembly
                .GetManifestResourceStream(SchemaResourceName);
            if (schemaStream == null)
            {
                throw new InvalidOperationException(
                    "The embedded target Peer XML schema is missing.");
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
            throw InvalidRequest(
                "The Peer contract body does not match the fixed XML schema.",
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
                    "The Peer contract body is missing a required value: "
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
                    FormatGuid(value)))
            {
                throw InvalidRequest(
                    "A Peer GUID is not canonical: " + name + ".");
            }

            return value;
        }

        private static ulong ReadPositiveUInt64(
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
                || value == 0
                || !StringComparer.Ordinal.Equals(
                    text,
                    FormatUInt64(value)))
            {
                throw InvalidRequest(
                    "A Peer high-water value is not canonical: "
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
                || value > ushort.MaxValue
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString(CultureInfo.InvariantCulture)))
            {
                throw InvalidRequest("The Peer service port is not canonical.");
            }

            return value;
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
                "A Peer boolean is not canonical: " + name + ".");
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
                    "A Peer timestamp is not canonical UTC: "
                    + name
                    + ".");
            }

            return value;
        }

        private static DateTime? ReadOptionalCanonicalUtc(
            XElement parent,
            string name)
        {
            return parent.Element(Namespace + name) == null
                ? (DateTime?)null
                : ReadCanonicalUtc(parent, name);
        }

        private static byte[] ReadCanonicalBase64(
            XElement parent,
            string name,
            int? expectedLength)
        {
            string text = ReadRequiredValue(parent, name);
            byte[] value;
            try
            {
                value = Convert.FromBase64String(text);
            }
            catch (FormatException exception)
            {
                throw InvalidRequest(
                    "A Peer base64 value is invalid: " + name + ".",
                    exception);
            }

            if (value.Length == 0
                || (expectedLength.HasValue
                    && value.Length != expectedLength.Value)
                || !StringComparer.Ordinal.Equals(
                    text,
                    Convert.ToBase64String(value)))
            {
                Array.Clear(value, 0, value.Length);
                throw InvalidRequest(
                    "A Peer base64 value is not canonical: "
                    + name
                    + ".");
            }

            return value;
        }

        private static PeerSyncResponseCode ReadResponseCode(XElement root)
        {
            string text = ReadRequiredValue(root, "Code");
            uint raw;
            if (!uint.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out raw)
                || !StringComparer.Ordinal.Equals(
                    text,
                    raw.ToString(CultureInfo.InvariantCulture))
                || !Enum.IsDefined(
                    typeof(PeerSyncResponseCode),
                    raw))
            {
                throw InvalidRequest(
                    "The Peer response code is not canonical or known.");
            }

            return (PeerSyncResponseCode)raw;
        }

        private static XElement Element(string name, string value)
        {
            return new XElement(Namespace + name, value);
        }

        private static string FormatGuid(Guid value)
        {
            return value.ToString("D").ToLowerInvariant();
        }

        private static string FormatUInt64(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatBoolean(bool value)
        {
            return value ? "true" : "false";
        }

        private static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString(
                UtcTimestampFormat,
                CultureInfo.InvariantCulture);
        }

        private static byte[] SerializeAndLimit(
            XElement root,
            int maximumBodyBytes)
        {
            byte[] body;
            try
            {
                body = StrictUtf8.GetBytes(
                    root.ToString(SaveOptions.DisableFormatting));
            }
            catch (ArgumentException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer contract model contains an invalid XML value.",
                    exception);
            }

            if (body.Length > maximumBodyBytes)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "The serialized Peer contract body exceeds the raw byte limit.");
            }

            return body;
        }

        private static PeerSyncProtocolException InvalidRequest(
            string message)
        {
            return new PeerSyncProtocolException(
                PeerSyncProtocolFailure.InvalidRequest,
                message);
        }

        private static PeerSyncProtocolException InvalidRequest(
            string message,
            Exception exception)
        {
            return new PeerSyncProtocolException(
                PeerSyncProtocolFailure.InvalidRequest,
                message,
                exception);
        }
    }
}
