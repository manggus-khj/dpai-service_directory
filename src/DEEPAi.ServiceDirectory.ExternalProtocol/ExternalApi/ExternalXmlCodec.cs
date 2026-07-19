using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DEEPAi.ServiceDirectory.Domain;

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
                "RegistrationRequest");
            EnsureNoRequestAttributes(root);

            ServiceDefinition definition;
            ServiceDefinitionValidationError validationError;
            if (!ServiceDefinition.TryCreate(
                    ReadRequiredValue(root, "Name"),
                    ReadRequiredValue(root, "ProductCode"),
                    ReadRequiredValue(root, "ServerAddress"),
                    ReadRequiredPort(root),
                    out definition,
                    out validationError))
            {
                throw new ExternalProtocolException(
                    "The external registration request contains an invalid service definition: "
                    + validationError
                    + ".");
            }

            return new ExternalRegistrationRequest(definition);
        }

        public static byte[] SerializeHealthResponse(ExternalResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                && response.PayloadKind != ExternalResponsePayloadKind.Health)
            {
                throw new ArgumentException(
                    "A successful health response requires a health payload.",
                    nameof(response));
            }

            return SerializeResponse(response);
        }

        public static byte[] SerializeServiceResponse(ExternalResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                && response.PayloadKind != ExternalResponsePayloadKind.Service)
            {
                throw new ArgumentException(
                    "A successful service lookup response requires a service payload.",
                    nameof(response));
            }

            return SerializeResponse(response);
        }

        public static byte[] SerializeRegistrationResponse(
            ExternalResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.IsSuccess
                && response.PayloadKind != ExternalResponsePayloadKind.Registration)
            {
                throw new ArgumentException(
                    "A successful registration response requires a registration payload.",
                    nameof(response));
            }

            return SerializeResponse(response);
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

            return SerializeResponse(response);
        }

        private static byte[] SerializeResponse(ExternalResponse response)
        {
            var root = new XElement(
                Namespace + "Response",
                new XElement(Namespace + "Result", response.Result),
                new XElement(
                    Namespace + "Code",
                    response.NumericCode.ToString(CultureInfo.InvariantCulture)),
                new XElement(Namespace + "Message", response.Message));

            if (response.PayloadKind == ExternalResponsePayloadKind.Health)
            {
                root.Add(
                    new XElement(
                        Namespace + "UtcNow",
                        FormatUtc(response.UtcNow.Value)));
            }
            else if (response.PayloadKind == ExternalResponsePayloadKind.Service)
            {
                root.Add(CreateServiceElement(response.Service));
            }
            else if (response.PayloadKind ==
                ExternalResponsePayloadKind.Registration)
            {
                root.Add(
                    new XElement(
                        Namespace + "Status",
                        GetRegistrationStatusText(
                            response.RegistrationStatus.Value)));
                if (response.PendingId.HasValue)
                {
                    root.Add(
                        new XElement(
                            Namespace + "PendingId",
                            response.PendingId.Value
                                .ToString("D")
                                .ToLowerInvariant()));
                }
            }

            byte[] body = StrictUtf8.GetBytes(
                root.ToString(SaveOptions.DisableFormatting));
            if (body.Length > ExternalApiContract.MaximumBodyBytes)
            {
                throw new ExternalProtocolException(
                    "The external response exceeds the XML body limit.");
            }

            LoadSchemaValidatedRoot(body, "Response");
            return body;
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
                    Namespace + "ServerAddress",
                    service.ServerAddress),
                new XElement(
                    Namespace + "Port",
                    service.Port.ToString(CultureInfo.InvariantCulture)),
                new XElement(
                    Namespace + "LastModifiedUtc",
                    FormatUtc(service.LastModifiedUtc)));
        }

        private static XElement LoadSchemaValidatedRoot(
            byte[] body,
            string expectedRootName)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0
                || body.Length > ExternalApiContract.MaximumBodyBytes)
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
            if (root == null
                || root.Name != Namespace + expectedRootName)
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

        private static void EnsureNoRequestAttributes(XElement root)
        {
            foreach (XElement element in root.DescendantsAndSelf())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (!attribute.IsNamespaceDeclaration)
                    {
                        throw new ExternalProtocolException(
                            "External registration requests must not contain attributes.");
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
                    "The external registration port is invalid.");
            }

            return port;
        }

        private static string GetRegistrationStatusText(
            ExternalRegistrationStatus status)
        {
            switch (status)
            {
                case ExternalRegistrationStatus.PendingNew:
                    return "PENDING_NEW";
                case ExternalRegistrationStatus.PendingModify:
                    return "PENDING_MODIFY";
                case ExternalRegistrationStatus.PendingExists:
                    return "PENDING_EXISTS";
                case ExternalRegistrationStatus.AlreadyRegistered:
                    return "ALREADY_REGISTERED";
                default:
                    throw new ExternalProtocolException(
                        "The external registration status is invalid.");
            }
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
