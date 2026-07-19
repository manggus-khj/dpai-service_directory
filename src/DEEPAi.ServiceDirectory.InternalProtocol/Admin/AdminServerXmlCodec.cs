using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static class AdminServerXmlCodec
    {
        private const string SchemaResourceName =
            "DEEPAi.ServiceDirectory.InternalProtocol.Admin.admin.xsd";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XNamespace Namespace =
            AdminApiContract.XmlNamespace;
        private static readonly XmlSchemaSet AdminSchemas =
            LoadAdminSchemas();

        public static AdminEnableSyncRequest ParseEnableSyncRequest(
            byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(body, "EnableSync");
            string peerEndpoint = ReadRequiredValue(root, "PeerEndpoint");
            string canonicalEndpoint;
            if (!AdminPeerEndpoint.TryNormalize(
                    peerEndpoint,
                    out canonicalEndpoint)
                || !StringComparer.Ordinal.Equals(
                    peerEndpoint,
                    canonicalEndpoint))
            {
                throw new AdminProtocolException(
                    "The Admin PeerEndpoint is not canonical.");
            }

            return new AdminEnableSyncRequest(
                canonicalEndpoint,
                ReadRequiredBoolean(root, "RePair"));
        }

        public static AdminPairingConfirmationRequest
            ParsePairingConfirmationRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "PairingConfirmation");
            return new AdminPairingConfirmationRequest(
                ReadRequiredCanonicalGuid(root, "PairingId"),
                ReadRequiredBoolean(root, "Confirmed"));
        }

        public static AdminPairingCancellationRequest
            ParsePairingCancellationRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "PairingCancellation");
            return new AdminPairingCancellationRequest(
                ReadRequiredCanonicalGuid(root, "PairingId"));
        }

        public static AdminDisableSyncRequest ParseDisableSyncRequest(
            byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(body, "DisableSync");
            return new AdminDisableSyncRequest(
                ReadRequiredBoolean(root, "ForgetPeer"));
        }

        public static AdminLoggingSettingsRequest
            ParseLoggingSettingsRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "LoggingSettings");
            int logRetentionDays = ReadRequiredInt(
                root,
                "LogRetentionDays");
            if (logRetentionDays <
                    AdminApiContract.MinimumLogRetentionDays
                || logRetentionDays >
                    AdminApiContract.MaximumLogRetentionDays)
            {
                throw new AdminProtocolException(
                    "The Admin log retention period is out of range.");
            }

            return new AdminLoggingSettingsRequest(logRetentionDays);
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
                    "The Admin request body size is invalid.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException exception)
            {
                throw new AdminProtocolException(
                    "The Admin request body is not strict UTF-8.",
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
                using (XmlReader reader = XmlReader.Create(
                    textReader,
                    settings))
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
                    "The Admin request body contains invalid XML.",
                    exception);
            }

            XElement root = document.Root;
            if (root == null
                || root.Name != Namespace + expectedRootName)
            {
                throw new AdminProtocolException(
                    "The Admin request root or namespace is invalid.");
            }

            EnsureNoRequestAttributes(root);
            return root;
        }

        private static void ValidateXmlDepth(
            string xml,
            int maximumCharacters)
        {
            var settings = new XmlReaderSettings
            {
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = Math.Max(
                    1,
                    maximumCharacters),
                ValidationType = ValidationType.None,
                XmlResolver = null
            };

            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(
                    textReader,
                    settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element
                            && reader.Depth + 1 >
                                AdminApiContract.MaximumXmlDepth)
                        {
                            throw new AdminProtocolException(
                                "The Admin request exceeds the XML depth limit.");
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
                    "The Admin request body contains invalid XML.",
                    exception);
            }
        }

        private static XmlSchemaSet LoadAdminSchemas()
        {
            Stream schemaStream = typeof(AdminServerXmlCodec)
                .Assembly
                .GetManifestResourceStream(SchemaResourceName);
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
                "The Admin request does not match the fixed XML schema.",
                eventArgs.Exception);
        }

        private static string ReadRequiredValue(
            XElement parent,
            string name)
        {
            XElement element = parent.Element(Namespace + name);
            if (element == null || string.IsNullOrWhiteSpace(element.Value))
            {
                throw new AdminProtocolException(
                    "A required Admin request value is missing: "
                    + name
                    + ".");
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
                        throw new AdminProtocolException(
                            "Admin requests must not contain attributes.");
                    }
                }
            }
        }

        private static bool ReadRequiredBoolean(
            XElement parent,
            string name)
        {
            string value = ReadRequiredValue(parent, name);
            if (StringComparer.Ordinal.Equals(value, "true"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(value, "false"))
            {
                return false;
            }

            throw new AdminProtocolException(
                "The Admin request boolean is invalid: "
                + name
                + ".");
        }

        private static Guid ReadRequiredCanonicalGuid(
            XElement parent,
            string name)
        {
            string value = ReadRequiredValue(parent, name);
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString("D")))
            {
                throw new AdminProtocolException(
                    "The Admin PairingId is not a canonical non-empty GUID.");
            }

            return parsed;
        }

        private static int ReadRequiredInt(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            int value;
            if (!int.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString(CultureInfo.InvariantCulture)))
            {
                throw new AdminProtocolException(
                    "The Admin request integer is invalid: "
                    + name
                    + ".");
            }

            return value;
        }
    }
}
