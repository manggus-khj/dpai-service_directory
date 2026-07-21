using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static class AdminRegistrationModeXmlCodec
    {
        private const string SchemaResourceName =
            "DEEPAi.ServiceDirectory.InternalProtocol.Admin.admin.xsd";
        private const string UtcTimestampFormat =
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XNamespace Namespace =
            AdminApiContract.XmlNamespace;
        private static readonly XmlSchemaSet AdminSchemas =
            LoadAdminSchemas();

        public static byte[] SerializeRegistrationModeResponse(
            AdminServerRegistrationModeResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(CreateRegistrationModeElement(response.RegistrationMode));
            if (response.LastRegistration != null)
            {
                root.Add(
                    CreateLastRegistrationElement(
                        response.LastRegistration));
            }

            byte[] body = StrictUtf8.GetBytes(
                root.ToString(SaveOptions.DisableFormatting));
            if (body.Length > AdminApiContract.MaximumBodyBytes)
            {
                throw new AdminProtocolException(
                    "The Admin registration-mode response exceeds the body limit.");
            }

            AdminResponse<AdminServerRegistrationModeResponse> parsed =
                ParseRegistrationModeResponse(body);
            if (!parsed.IsSuccess || parsed.Payload == null)
            {
                throw new AdminProtocolException(
                    "The Admin registration-mode response failed canonical validation.");
            }

            return body;
        }

        public static AdminResponse<AdminServerRegistrationModeResponse>
            ParseRegistrationModeResponse(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(body, "Response");
            string result = ReadRequiredValue(root, "Result");
            int code = ReadRequiredInt(root, "Code");
            XElement messageElement = root.Element(Namespace + "Message");
            if (messageElement == null)
            {
                throw new AdminProtocolException(
                    "The Admin response message is missing.");
            }

            XElement modeElement = root.Element(Namespace + "RegistrationMode");
            XElement lastElement = root.Element(Namespace + "LastRegistration");
            bool success = code == 0
                && StringComparer.Ordinal.Equals(result, "OK");
            if (success)
            {
                if (modeElement == null)
                {
                    throw new AdminProtocolException(
                        "A successful registration-mode response requires its payload.");
                }

                return new AdminResponse<AdminServerRegistrationModeResponse>(
                    result,
                    code,
                    messageElement.Value,
                    new AdminServerRegistrationModeResponse(
                        ParseRegistrationMode(modeElement),
                        lastElement == null
                            ? null
                            : ParseLastRegistration(lastElement)));
            }

            if (!StringComparer.Ordinal.Equals(result, "ERROR")
                || code == 0
                || modeElement != null
                || lastElement != null)
            {
                throw new AdminProtocolException(
                    "The Admin registration-mode response envelope is inconsistent.");
            }

            EnsureNoUnexpectedPayload(root);
            return new AdminResponse<AdminServerRegistrationModeResponse>(
                result,
                code,
                messageElement.Value,
                null);
        }

        private static XElement CreateSuccessEnvelope()
        {
            return new XElement(
                Namespace + "Response",
                new XElement(Namespace + "Result", "OK"),
                new XElement(Namespace + "Code", "0"),
                new XElement(Namespace + "Message", string.Empty));
        }

        private static XElement CreateRegistrationModeElement(
            AdminRegistrationModeStatus status)
        {
            var element = new XElement(
                Namespace + "RegistrationMode",
                new XElement(
                    Namespace + "State",
                    FormatState(status.State)));
            if (status.State == AdminRegistrationModeState.Open)
            {
                element.Add(
                    new XElement(
                        Namespace + "OpenedUtc",
                        FormatUtc(status.OpenedUtc.Value)),
                    new XElement(
                        Namespace + "ExpiresUtc",
                        FormatUtc(status.ExpiresUtc.Value)),
                    new XElement(
                        Namespace + "RemainingSeconds",
                        status.RemainingSeconds.Value.ToString(
                            CultureInfo.InvariantCulture)));
            }

            return element;
        }

        private static XElement CreateLastRegistrationElement(
            AdminLastRegistration lastRegistration)
        {
            var element = new XElement(
                Namespace + "LastRegistration",
                new XElement(
                    Namespace + "CompletedUtc",
                    FormatUtc(lastRegistration.CompletedUtc)));
            if (lastRegistration.Outcome == AdminRegistrationOutcome.Failed)
            {
                element.Add(
                    new XElement(Namespace + "Outcome", "FAILED"),
                    new XElement(
                        Namespace + "FailureReason",
                        lastRegistration.FailureReason));
                return element;
            }

            element.Add(
                new XElement(
                    Namespace + "ProductCode",
                    lastRegistration.ProductCode),
                new XElement(
                    Namespace + "ServiceHostName",
                    lastRegistration.ServiceHostName),
                new XElement(
                    Namespace + "ServiceIpv4Address",
                    lastRegistration.ServiceIpv4Address),
                new XElement(
                    Namespace + "CertificateSerialNumber",
                    lastRegistration.CertificateSerialNumber),
                new XElement(
                    Namespace + "CertificateNotAfterUtc",
                    FormatUtc(lastRegistration.CertificateNotAfterUtc.Value)),
                new XElement(
                    Namespace + "Outcome",
                    lastRegistration.Outcome ==
                        AdminRegistrationOutcome.Registered
                            ? "REGISTERED"
                            : "REREGISTERED"));
            return element;
        }

        private static AdminRegistrationModeStatus ParseRegistrationMode(
            XElement element)
        {
            AdminRegistrationModeState state;
            switch (ReadRequiredValue(element, "State"))
            {
                case "CLOSED":
                    state = AdminRegistrationModeState.Closed;
                    break;
                case "OPEN":
                    state = AdminRegistrationModeState.Open;
                    break;
                case "CLAIMED":
                    state = AdminRegistrationModeState.Claimed;
                    break;
                default:
                    throw new AdminProtocolException(
                        "The Admin registration-mode state is invalid.");
            }

            try
            {
                return new AdminRegistrationModeStatus(
                    state,
                    ReadOptionalUtc(element, "OpenedUtc"),
                    ReadOptionalUtc(element, "ExpiresUtc"),
                    ReadOptionalInt(element, "RemainingSeconds"));
            }
            catch (ArgumentException exception)
            {
                throw new AdminProtocolException(
                    "The Admin registration-mode timing values are invalid.",
                    exception);
            }
        }

        private static AdminLastRegistration ParseLastRegistration(
            XElement element)
        {
            DateTime completedUtc = ReadRequiredUtc(
                element,
                "CompletedUtc");
            string outcome = ReadRequiredValue(element, "Outcome");
            try
            {
                switch (outcome)
                {
                    case "REGISTERED":
                        EnsureMissing(element, "FailureReason");
                        return ParseSuccessfulLastRegistration(
                            element,
                            completedUtc,
                            AdminRegistrationOutcome.Registered);
                    case "REREGISTERED":
                        EnsureMissing(element, "FailureReason");
                        return ParseSuccessfulLastRegistration(
                            element,
                            completedUtc,
                            AdminRegistrationOutcome.Reregistered);
                    case "FAILED":
                        EnsureMissing(element, "ProductCode");
                        EnsureMissing(element, "ServiceHostName");
                        EnsureMissing(element, "ServiceIpv4Address");
                        EnsureMissing(element, "CertificateSerialNumber");
                        EnsureMissing(element, "CertificateNotAfterUtc");
                        return AdminLastRegistration.CreateFailure(
                            completedUtc,
                            ReadRequiredValue(element, "FailureReason"));
                    default:
                        throw new AdminProtocolException(
                            "The Admin last registration outcome is invalid.");
                }
            }
            catch (ArgumentException exception)
            {
                throw new AdminProtocolException(
                    "The Admin last registration payload is invalid.",
                    exception);
            }
        }

        private static AdminLastRegistration ParseSuccessfulLastRegistration(
            XElement element,
            DateTime completedUtc,
            AdminRegistrationOutcome outcome)
        {
            return AdminLastRegistration.CreateSuccess(
                completedUtc,
                outcome,
                ReadRequiredValue(element, "ProductCode"),
                ReadRequiredValue(element, "ServiceHostName"),
                ReadRequiredValue(element, "ServiceIpv4Address"),
                ReadRequiredValue(element, "CertificateSerialNumber"),
                ReadRequiredUtc(element, "CertificateNotAfterUtc"));
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
                || body.Length > AdminApiContract.MaximumBodyBytes)
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
                    "The Admin response body is not strict UTF-8.",
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
            catch (AdminProtocolException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is XmlException
                    || exception is XmlSchemaException)
            {
                throw new AdminProtocolException(
                    "The Admin response body is invalid.",
                    exception);
            }

            XElement root = document.Root;
            if (root == null || root.Name != Namespace + expectedRootName)
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
                            && reader.Depth + 1 >
                                AdminApiContract.MaximumXmlDepth)
                        {
                            throw new AdminProtocolException(
                                "The Admin response exceeds the XML depth limit.");
                        }
                    }
                }
            }
            catch (AdminProtocolException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw new AdminProtocolException(
                    "The Admin response body contains invalid XML.",
                    exception);
            }
        }

        private static XmlSchemaSet LoadAdminSchemas()
        {
            Stream schemaStream = typeof(AdminRegistrationModeXmlCodec)
                .Assembly
                .GetManifestResourceStream(SchemaResourceName);
            if (schemaStream == null)
            {
                throw new InvalidOperationException(
                    "The embedded target Admin XML schema is missing.");
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
                "The Admin response does not match the target XML schema.",
                eventArgs.Exception);
        }

        private static string ReadRequiredValue(XElement parent, string name)
        {
            XElement element = parent.Element(Namespace + name);
            if (element == null)
            {
                throw new AdminProtocolException(
                    "The Admin response is missing " + name + ".");
            }

            return element.Value;
        }

        private static int ReadRequiredInt(XElement parent, string name)
        {
            int value;
            if (!int.TryParse(
                    ReadRequiredValue(parent, name),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                throw new AdminProtocolException(
                    "The Admin response integer is invalid.");
            }

            return value;
        }

        private static int? ReadOptionalInt(XElement parent, string name)
        {
            XElement element = parent.Element(Namespace + name);
            return element == null
                ? (int?)null
                : ReadRequiredInt(parent, name);
        }

        private static DateTime ReadRequiredUtc(XElement parent, string name)
        {
            string value = ReadRequiredValue(parent, name);
            DateTime parsed;
            if (!DateTime.TryParseExact(
                    value,
                    UtcTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out parsed)
                || parsed.Kind != DateTimeKind.Utc
                || !StringComparer.Ordinal.Equals(value, FormatUtc(parsed)))
            {
                throw new AdminProtocolException(
                    "The Admin response UTC timestamp is invalid.");
            }

            return parsed;
        }

        private static DateTime? ReadOptionalUtc(
            XElement parent,
            string name)
        {
            return parent.Element(Namespace + name) == null
                ? (DateTime?)null
                : ReadRequiredUtc(parent, name);
        }

        private static void EnsureMissing(XElement parent, string name)
        {
            if (parent.Element(Namespace + name) != null)
            {
                throw new AdminProtocolException(
                    "The Admin response contains a forbidden " + name + ".");
            }
        }

        private static void EnsureNoUnexpectedPayload(XElement root)
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
                    "The Admin error response contains an unexpected payload.");
            }
        }

        private static string FormatState(AdminRegistrationModeState state)
        {
            switch (state)
            {
                case AdminRegistrationModeState.Closed:
                    return "CLOSED";
                case AdminRegistrationModeState.Open:
                    return "OPEN";
                case AdminRegistrationModeState.Claimed:
                    return "CLAIMED";
                default:
                    throw new AdminProtocolException(
                        "The Admin registration-mode state is invalid.");
            }
        }

        private static string FormatUtc(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new AdminProtocolException(
                    "Admin timestamps must use UTC.");
            }

            return value.ToString(
                UtcTimestampFormat,
                CultureInfo.InvariantCulture);
        }
    }
}
