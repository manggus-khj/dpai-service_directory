using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public static class ExternalXmlCodec
    {
        private const string SchemaResourceName =
            "DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi.external.xsd";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XNamespace Namespace =
            ExternalApiContract.XmlNamespace;
        private static readonly XmlSchemaSet ExternalSchemas =
            LoadExternalSchemas();

        public static ExternalRegistrationRequest ParseRegistrationRequest(
            byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "RegistrationRequest",
                ExternalApiContract.MaximumCertificateRequestBodyBytes);
            EnsureNoRequestAttributes(root);

            try
            {
                return new ExternalRegistrationRequest(
                    ReadCanonicalGuid(root, "RegistrationRequestId"),
                    ReadRequiredValue(root, "Name"),
                    ReadRequiredValue(root, "ProductCode"),
                    ReadRequiredValue(root, "ServiceHostName"),
                    ReadRequiredValue(root, "ServiceIpv4Address"),
                    ReadRequiredPort(root),
                    ReadCanonicalBase64(
                        root,
                        "CertificateSigningRequest",
                        0,
                        ExternalApiContract.MaximumCertificateSigningRequestBytes));
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    || exception is FormatException
                    || exception is OverflowException)
            {
                throw new ExternalProtocolException(
                    "The external registration request contains an invalid value.",
                    exception);
            }
        }

        public static ExternalCertificateRenewalRequest
            ParseCertificateRenewalRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "CertificateRenewalRequest",
                ExternalApiContract.MaximumCertificateRequestBodyBytes);
            EnsureNoRequestAttributes(root);

            try
            {
                return new ExternalCertificateRenewalRequest(
                    ReadCanonicalGuid(root, "RenewalRequestId"),
                    ReadRequiredValue(root, "ProductCode"),
                    ReadRequiredValue(root, "CurrentSerialNumber"),
                    ReadProofTimestamp(root, "TimestampUtc"),
                    ReadCanonicalBase64(
                        root,
                        "Nonce",
                        ExternalApiContract.RenewalNonceBytes,
                        0),
                    ReadRequiredValue(root, "Name"),
                    ReadRequiredValue(root, "ServiceHostName"),
                    ReadRequiredValue(root, "ServiceIpv4Address"),
                    ReadRequiredPort(root),
                    ReadCanonicalBase64(
                        root,
                        "CertificateSigningRequest",
                        0,
                        ExternalApiContract.MaximumCertificateSigningRequestBytes),
                    ReadCanonicalBase64(
                        root,
                        "ServiceIdentitySha256",
                        ExternalApiContract.Sha256Bytes,
                        0),
                    ReadCanonicalBase64(
                        root,
                        "ProofSignature",
                        0,
                        ExternalApiContract.MaximumProofSignatureBytes));
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    || exception is FormatException
                    || exception is OverflowException)
            {
                throw new ExternalProtocolException(
                    "The external certificate renewal request contains an invalid value.",
                    exception);
            }
        }

        public static byte[] SerializeTrustInfoResponse(
            ExternalResponse response)
        {
            return SerializeExpectedResponse(
                response,
                ExternalResponsePayloadKind.TrustInfo,
                ExternalApiContract.MaximumCaResponseBytes,
                "A successful CA response requires a trust information payload.");
        }

        public static byte[] SerializeHealthResponse(ExternalResponse response)
        {
            return SerializeExpectedResponse(
                response,
                ExternalResponsePayloadKind.Health,
                ExternalApiContract.MaximumBodyBytes,
                "A successful health response requires a health payload.");
        }

        public static byte[] SerializeServiceResponse(ExternalResponse response)
        {
            return SerializeExpectedResponse(
                response,
                ExternalResponsePayloadKind.Service,
                ExternalApiContract.MaximumBodyBytes,
                "A successful service lookup response requires a service payload.");
        }

        public static byte[] SerializeRegistrationResponse(
            ExternalResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                && (response.PayloadKind !=
                    ExternalResponsePayloadKind.CertificateIssuance
                    || !response.RegistrationRequestId.HasValue))
            {
                throw new ArgumentException(
                    "A successful registration response requires a registration certificate payload.",
                    nameof(response));
            }

            return SerializeResponse(
                response,
                ExternalApiContract.MaximumBodyBytes);
        }

        public static byte[] SerializeCertificateRenewalResponse(
            ExternalResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                && (response.PayloadKind !=
                    ExternalResponsePayloadKind.CertificateIssuance
                    || !response.RenewalRequestId.HasValue))
            {
                throw new ArgumentException(
                    "A successful renewal response requires a renewal certificate payload.",
                    nameof(response));
            }

            return SerializeResponse(
                response,
                ExternalApiContract.MaximumBodyBytes);
        }

        public static byte[] SerializeErrorResponse(ExternalResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                || response.PayloadKind != ExternalResponsePayloadKind.None)
            {
                throw new ArgumentException(
                    "An error serializer requires an error response without a payload.",
                    nameof(response));
            }

            return SerializeResponse(
                response,
                ExternalApiContract.MaximumBodyBytes);
        }

        private static byte[] SerializeExpectedResponse(
            ExternalResponse response,
            ExternalResponsePayloadKind expectedPayloadKind,
            int maximumBodyBytes,
            string errorMessage)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                && response.PayloadKind != expectedPayloadKind)
            {
                throw new ArgumentException(errorMessage, nameof(response));
            }

            return SerializeResponse(response, maximumBodyBytes);
        }

        private static byte[] SerializeResponse(
            ExternalResponse response,
            int maximumBodyBytes)
        {
            var root = new XElement(
                Namespace + "Response",
                new XElement(Namespace + "Result", response.Result),
                new XElement(
                    Namespace + "Code",
                    response.NumericCode.ToString(CultureInfo.InvariantCulture)),
                new XElement(Namespace + "Message", response.Message));

            switch (response.PayloadKind)
            {
                case ExternalResponsePayloadKind.None:
                    break;
                case ExternalResponsePayloadKind.TrustInfo:
                    root.Add(CreateTrustInfoElement(response.TrustInfo));
                    break;
                case ExternalResponsePayloadKind.Health:
                    root.Add(
                        new XElement(
                            Namespace + "UtcNow",
                            FormatUtc(response.UtcNow.Value)));
                    break;
                case ExternalResponsePayloadKind.Service:
                    root.Add(CreateServiceElement(response.Service));
                    break;
                case ExternalResponsePayloadKind.CertificateIssuance:
                    AddCertificateIssuancePayload(root, response);
                    break;
                default:
                    throw new ExternalProtocolException(
                        "The external response payload kind is invalid.");
            }

            if (response.TrustBundle != null)
            {
                root.Add(
                    new XElement(
                        Namespace + "Extensions",
                        CreateTrustBundleElement(response.TrustBundle)));
            }

            byte[] body = StrictUtf8.GetBytes(
                root.ToString(SaveOptions.DisableFormatting));
            if (body.Length > maximumBodyBytes)
            {
                throw new ExternalProtocolException(
                    "The external response exceeds its XML body limit.");
            }

            LoadSchemaValidatedRoot(body, "Response", maximumBodyBytes);
            return body;
        }

        private static XElement CreateTrustInfoElement(
            ExternalTrustInfo trustInfo)
        {
            if (trustInfo == null)
            {
                throw new ExternalProtocolException(
                    "The external CA response payload is missing.");
            }

            return new XElement(
                Namespace + "TrustInfo",
                new XElement(
                    Namespace + "SiteId",
                    FormatGuid(trustInfo.SiteId)),
                new XElement(
                    Namespace + "CaCertificate",
                    Convert.ToBase64String(trustInfo.CaCertificate)),
                new XElement(
                    Namespace + "CaSpkiSha256",
                    Convert.ToBase64String(trustInfo.CaSpkiSha256)),
                new XElement(Namespace + "CrlUri", trustInfo.CrlUri));
        }

        private static XElement CreateServiceElement(ExternalServiceItem service)
        {
            if (service == null)
            {
                throw new ExternalProtocolException(
                    "The external service response payload is missing.");
            }

            return new XElement(
                Namespace + "Service",
                new XElement(Namespace + "Name", service.Name),
                new XElement(Namespace + "ProductCode", service.ProductCode),
                new XElement(
                    Namespace + "ServiceHostName",
                    service.ServiceHostName),
                new XElement(
                    Namespace + "ServiceIpv4Address",
                    service.ServiceIpv4Address),
                new XElement(
                    Namespace + "Port",
                    service.Port.ToString(CultureInfo.InvariantCulture)),
                new XElement(
                    Namespace + "LastModifiedUtc",
                    FormatUtc(service.LastModifiedUtc)));
        }

        private static XElement CreateTrustBundleElement(
            ExternalTrustBundle trustBundle)
        {
            var element = new XElement(
                Namespace + "TrustBundle",
                new XElement(
                    Namespace + "SiteId",
                    FormatGuid(trustBundle.SiteId)),
                new XElement(
                    Namespace + "TrustRevision",
                    trustBundle.TrustRevision.ToString(
                        CultureInfo.InvariantCulture)));
            if (trustBundle.RotationId.HasValue)
            {
                element.Add(new XElement(
                    Namespace + "RotationId",
                    FormatGuid(trustBundle.RotationId.Value)));
            }

            element.Add(new XElement(
                Namespace + "Phase",
                FormatRotationPhase(trustBundle.Phase)));
            AddOptionalUtc(
                element,
                "PublishedUtc",
                trustBundle.PublishedUtc);
            AddOptionalUtc(
                element,
                "ActivationNotBeforeUtc",
                trustBundle.ActivationNotBeforeUtc);
            AddOptionalUtc(
                element,
                "ActivatedUtc",
                trustBundle.ActivatedUtc);
            AddOptionalUtc(
                element,
                "RetirementNotBeforeUtc",
                trustBundle.RetirementNotBeforeUtc);
            foreach (ExternalTrustAuthority authority
                in trustBundle.Authorities)
            {
                element.Add(new XElement(
                    Namespace + "Authority",
                    new XElement(
                        Namespace + "Role",
                        FormatAuthorityRole(authority.Role)),
                    new XElement(
                        Namespace + "CaSerialNumber",
                        authority.CaSerialNumber),
                    new XElement(
                        Namespace + "CaCertificate",
                        Convert.ToBase64String(authority.CaCertificate)),
                    new XElement(
                        Namespace + "CaSpkiSha256",
                        Convert.ToBase64String(authority.CaSpkiSha256)),
                    new XElement(
                        Namespace + "CrlUri",
                        authority.CrlUri),
                    new XElement(
                        Namespace + "NotBeforeUtc",
                        FormatUtc(authority.NotBeforeUtc)),
                    new XElement(
                        Namespace + "NotAfterUtc",
                        FormatUtc(authority.NotAfterUtc))));
            }

            return element;
        }

        private static void AddOptionalUtc(
            XElement parent,
            string name,
            DateTime? value)
        {
            if (value.HasValue)
            {
                parent.Add(new XElement(
                    Namespace + name,
                    FormatUtc(value.Value)));
            }
        }

        private static string FormatRotationPhase(
            ExternalCaRotationPhase phase)
        {
            switch (phase)
            {
                case ExternalCaRotationPhase.Stable:
                    return "STABLE";
                case ExternalCaRotationPhase.Published:
                    return "PUBLISHED";
                case ExternalCaRotationPhase.Activated:
                    return "ACTIVATED";
                default:
                    throw new ExternalProtocolException(
                        "The CA rotation phase is invalid.");
            }
        }

        private static string FormatAuthorityRole(
            ExternalTrustAuthorityRole role)
        {
            switch (role)
            {
                case ExternalTrustAuthorityRole.Current:
                    return "CURRENT";
                case ExternalTrustAuthorityRole.Next:
                    return "NEXT";
                case ExternalTrustAuthorityRole.Retiring:
                    return "RETIRING";
                default:
                    throw new ExternalProtocolException(
                        "The trust authority role is invalid.");
            }
        }

        private static XElement CreateCertificateElement(
            ExternalIssuedCertificate certificate)
        {
            if (certificate == null)
            {
                throw new ExternalProtocolException(
                    "The external certificate response payload is missing.");
            }

            return new XElement(
                Namespace + "Certificate",
                new XElement(
                    Namespace + "LeafCertificate",
                    Convert.ToBase64String(certificate.LeafCertificate)),
                new XElement(
                    Namespace + "IssuerCertificate",
                    Convert.ToBase64String(certificate.IssuerCertificate)),
                new XElement(
                    Namespace + "SerialNumber",
                    certificate.SerialNumber),
                new XElement(
                    Namespace + "NotBeforeUtc",
                    FormatUtc(certificate.NotBeforeUtc)),
                new XElement(
                    Namespace + "NotAfterUtc",
                    FormatUtc(certificate.NotAfterUtc)),
                new XElement(Namespace + "CrlUri", certificate.CrlUri));
        }

        private static void AddCertificateIssuancePayload(
            XElement root,
            ExternalResponse response)
        {
            root.Add(
                new XElement(
                    Namespace + "Status",
                    GetCertificateIssuanceStatusText(
                        response.IssuanceStatus.Value)));
            if (response.RegistrationRequestId.HasValue)
            {
                root.Add(
                    new XElement(
                        Namespace + "RegistrationRequestId",
                        FormatGuid(response.RegistrationRequestId.Value)));
            }
            else if (response.RenewalRequestId.HasValue)
            {
                root.Add(
                    new XElement(
                        Namespace + "RenewalRequestId",
                        FormatGuid(response.RenewalRequestId.Value)));
            }
            else
            {
                throw new ExternalProtocolException(
                    "The external certificate response request ID is missing.");
            }

            root.Add(CreateServiceElement(response.Service));
            root.Add(CreateCertificateElement(response.Certificate));
        }

        private static XElement LoadSchemaValidatedRoot(
            byte[] body,
            string expectedRootName,
            int maximumBodyBytes)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0 || body.Length > maximumBodyBytes)
            {
                throw new ExternalProtocolException(
                    "The external XML body size is invalid.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ExternalProtocolException(
                    "The external XML body is not strict UTF-8.",
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
                Schemas = ExternalSchemas,
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
            catch (ExternalProtocolException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is XmlException
                    || exception is XmlSchemaException)
            {
                throw new ExternalProtocolException(
                    "The external XML body is invalid.",
                    exception);
            }

            XElement root = document.Root;
            if (root == null || root.Name != Namespace + expectedRootName)
            {
                throw new ExternalProtocolException(
                    "The external XML root or namespace is invalid.");
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
                            && reader.Depth + 1 >
                                ExternalApiContract.MaximumXmlDepth)
                        {
                            throw new ExternalProtocolException(
                                "The external XML body exceeds the depth limit.");
                        }
                    }
                }
            }
            catch (ExternalProtocolException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw new ExternalProtocolException(
                    "The external XML body is invalid.",
                    exception);
            }
        }

        private static XmlSchemaSet LoadExternalSchemas()
        {
            Stream schemaStream = typeof(ExternalXmlCodec)
                .Assembly
                .GetManifestResourceStream(SchemaResourceName);
            if (schemaStream == null)
            {
                throw new InvalidOperationException(
                    "The embedded External XML schema is missing.");
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
                schemas.Add(ExternalApiContract.XmlNamespace, reader);
            }

            schemas.Compile();
            return schemas;
        }

        private static void OnSchemaValidation(
            object sender,
            ValidationEventArgs eventArgs)
        {
            throw new ExternalProtocolException(
                "The external XML body does not match the fixed schema.",
                eventArgs.Exception);
        }

        private static string ReadRequiredValue(XElement parent, string name)
        {
            XElement element = parent.Element(Namespace + name);
            if (element == null)
            {
                throw new ExternalProtocolException(
                    "The external XML body is missing a required value.");
            }

            return element.Value;
        }

        private static Guid ReadCanonicalGuid(XElement parent, string name)
        {
            string value = ReadRequiredValue(parent, name);
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(value, FormatGuid(parsed)))
            {
                throw new ExternalProtocolException(
                    "The external request ID is not canonical.");
            }

            return parsed;
        }

        private static byte[] ReadCanonicalBase64(
            XElement parent,
            string name,
            int exactLength,
            int maximumLength)
        {
            string value = ReadRequiredValue(parent, name);
            byte[] decoded = Convert.FromBase64String(value);
            if (decoded.Length == 0
                || !StringComparer.Ordinal.Equals(
                    value,
                    Convert.ToBase64String(decoded))
                || (exactLength > 0 && decoded.Length != exactLength)
                || (maximumLength > 0 && decoded.Length > maximumLength))
            {
                throw new ExternalProtocolException(
                    "The external binary value is not canonical or has an invalid length.");
            }

            return decoded;
        }

        private static DateTime ReadProofTimestamp(
            XElement parent,
            string name)
        {
            string value = ReadRequiredValue(parent, name);
            DateTime parsed;
            if (!DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out parsed)
                || parsed.Kind != DateTimeKind.Utc
                || !StringComparer.Ordinal.Equals(
                    value,
                    FormatProofUtc(parsed)))
            {
                throw new ExternalProtocolException(
                    "The external proof timestamp is invalid.");
            }

            return parsed;
        }

        private static void EnsureNoRequestAttributes(XElement root)
        {
            foreach (XElement element in root.DescendantsAndSelf())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (!attribute.IsNamespaceDeclaration)
                    {
                        throw new ExternalProtocolException(
                            "External requests must not contain attributes.");
                    }
                }
            }
        }

        private static int ReadRequiredPort(XElement parent)
        {
            string value = ReadRequiredValue(parent, "Port");
            int port;
            if (!int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out port)
                || port < 1
                || port > 65535)
            {
                throw new ExternalProtocolException(
                    "The external service port is invalid.");
            }

            return port;
        }

        private static string GetCertificateIssuanceStatusText(
            ExternalCertificateIssuanceStatus status)
        {
            switch (status)
            {
                case ExternalCertificateIssuanceStatus.Registered:
                    return "REGISTERED";
                case ExternalCertificateIssuanceStatus.Reregistered:
                    return "REREGISTERED";
                case ExternalCertificateIssuanceStatus.Replayed:
                    return "REPLAYED";
                case ExternalCertificateIssuanceStatus.Renewed:
                    return "RENEWED";
                default:
                    throw new ExternalProtocolException(
                        "The external certificate issuance status is invalid.");
            }
        }

        private static string FormatGuid(Guid value)
        {
            return value.ToString("D").ToLowerInvariant();
        }

        private static string FormatProofUtc(DateTime value)
        {
            return value.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);
        }

        private static string FormatUtc(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ExternalProtocolException(
                    "External API timestamps must use DateTimeKind.Utc.");
            }

            return value.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture);
        }
    }
}
