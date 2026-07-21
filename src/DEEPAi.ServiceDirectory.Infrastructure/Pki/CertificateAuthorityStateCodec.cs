using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CertificateAuthorityStateCodec
    {
        internal const int MaximumDocumentBytes = 16 * 1024 * 1024;

        private const string SchemaVersion = "1";
        private const string DocumentSizeLimitMessage =
            "PKI XML exceeds its size limit.";
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal byte[] SerializeState(CertificateAuthorityState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var root = new XElement(
                "CertificateAuthorityState",
                new XAttribute("SchemaVersion", SchemaVersion),
                new XElement("SiteId", FormatGuid(state.SiteId)),
                new XElement(
                    "IssuerInstanceId",
                    FormatGuid(state.IssuerInstanceId)),
                new XElement(
                    "Role",
                    state.Role == CertificateAuthorityRole.ActiveIssuer
                        ? "ACTIVE_ISSUER"
                        : "STANDBY"),
                new XElement(
                    "CaSerialNumber",
                    state.CaSerialNumber.Hex),
                new XElement(
                    "CaSpkiSha256",
                    Convert.ToBase64String(state.GetCaSpkiSha256())),
                new XElement(
                    "NotBeforeUtc",
                    FormatUtc(state.NotBeforeUtc)),
                new XElement(
                    "NotAfterUtc",
                    FormatUtc(state.NotAfterUtc)),
                new XElement(
                    "PkiRevision",
                    state.PkiRevision.ToString(CultureInfo.InvariantCulture)),
                new XElement(
                    "CrlNumber",
                    state.CrlNumber.ToString(CultureInfo.InvariantCulture)));
            if (state.LastBackupUtc.HasValue)
            {
                root.Add(new XElement(
                    "LastBackupUtc",
                    FormatUtc(state.LastBackupUtc.Value)));
            }

            return Serialize(root);
        }

        internal CertificateAuthorityState DeserializeState(byte[] contents)
        {
            XElement root = Parse(contents, "CertificateAuthorityState");
            RequireRootAttribute(root);
            XElement[] elements = root.Elements().ToArray();
            int expectedCount = elements.Length == 10 ? 10 : 9;
            if (elements.Length != expectedCount)
            {
                throw Invalid("CA state contains an invalid element count.");
            }

            string[] requiredNames =
            {
                "SiteId",
                "IssuerInstanceId",
                "Role",
                "CaSerialNumber",
                "CaSpkiSha256",
                "NotBeforeUtc",
                "NotAfterUtc",
                "PkiRevision",
                "CrlNumber"
            };
            RequireOrderedNames(elements, requiredNames);
            if (elements.Length == 10
                && !IsCanonicalSimpleElement(
                    elements[9],
                    "LastBackupUtc"))
            {
                throw Invalid("CA state contains an unexpected element.");
            }

            Guid siteId = ParseGuid(elements[0].Value, "SiteId");
            Guid issuerInstanceId = ParseGuid(
                elements[1].Value,
                "IssuerInstanceId");
            CertificateAuthorityRole role;
            if (StringComparer.Ordinal.Equals(
                    elements[2].Value,
                    "ACTIVE_ISSUER"))
            {
                role = CertificateAuthorityRole.ActiveIssuer;
            }
            else if (StringComparer.Ordinal.Equals(
                elements[2].Value,
                "STANDBY"))
            {
                role = CertificateAuthorityRole.Standby;
            }
            else
            {
                throw Invalid("CA role is invalid.");
            }

            CertificateSerialNumber caSerialNumber;
            if (!CertificateSerialNumber.TryCreate(
                    elements[3].Value,
                    out caSerialNumber))
            {
                throw Invalid("CA serial number is invalid.");
            }

            byte[] caSpkiSha256 = ParseSha256(
                elements[4].Value,
                "CaSpkiSha256");
            CertificateAuthorityState state;
            try
            {
                state = new CertificateAuthorityState(
                    siteId,
                    issuerInstanceId,
                    role,
                    caSerialNumber,
                    caSpkiSha256,
                    ParseUtc(elements[5].Value, "NotBeforeUtc"),
                    ParseUtc(elements[6].Value, "NotAfterUtc"),
                    ParsePositiveUInt64(elements[7].Value, "PkiRevision"),
                    ParsePositiveUInt64(elements[8].Value, "CrlNumber"),
                    elements.Length == 10
                        ? (DateTime?)ParseUtc(
                            elements[9].Value,
                            "LastBackupUtc")
                        : null);
            }
            catch (ArgumentException exception)
            {
                throw Invalid("CA state is inconsistent.", exception);
            }
            finally
            {
                Array.Clear(caSpkiSha256, 0, caSpkiSha256.Length);
            }

            RequireCanonicalDocument(
                contents,
                SerializeState(state),
                "pki/state.xml");
            return state;
        }

        internal byte[] SerializeLedger(CertificateLedgerSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var root = new XElement(
                "CertificateLedger",
                new XAttribute("SchemaVersion", SchemaVersion),
                new XAttribute(
                    "PkiRevision",
                    snapshot.PkiRevision.ToString(
                        CultureInfo.InvariantCulture)),
                new XAttribute(
                    "CrlNumber",
                    snapshot.CrlNumber.ToString(
                        CultureInfo.InvariantCulture)));
            foreach (CertificateLedgerEntry entry in snapshot
                .EntriesBySerial
                .Values
                .OrderBy(item => item.SerialNumber.Hex, StringComparer.Ordinal))
            {
                var element = new XElement(
                    "Certificate",
                    new XElement("SerialNumber", entry.SerialNumber.Hex),
                    new XElement("ProductCode", entry.ProductCode.Value),
                    new XElement(
                        "IssuanceRequestId",
                        FormatGuid(entry.IssuanceRequestId)),
                    new XElement(
                        "IssuanceKind",
                        FormatIssuanceKind(entry.IssuanceKind)),
                    new XElement(
                        "Name",
                        entry.ServiceDefinition.Name),
                    new XElement(
                        "ServiceHostName",
                        entry.ServiceIdentity.ServiceHostName),
                    new XElement(
                        "ServiceIpv4Address",
                        entry.ServiceIdentity.ServiceIpv4Address),
                    new XElement(
                        "Port",
                        entry.ServiceDefinition.Port.ToString(
                            CultureInfo.InvariantCulture)),
                    new XElement(
                        "CsrSha256",
                        Convert.ToBase64String(entry.GetCsrSha256())),
                    new XElement(
                        "RequestPayloadSha256",
                        Convert.ToBase64String(
                            entry.GetRequestPayloadSha256())),
                    new XElement(
                        "SubjectPublicKeyInfoSha256",
                        Convert.ToBase64String(
                            entry.GetSubjectPublicKeyInfoSha256())),
                    new XElement(
                        "LeafCertificate",
                        Convert.ToBase64String(
                            entry.GetLeafCertificate())),
                    new XElement("IssuedUtc", FormatUtc(entry.IssuedUtc)),
                    new XElement(
                        "NotBeforeUtc",
                        FormatUtc(entry.NotBeforeUtc)),
                    new XElement(
                        "NotAfterUtc",
                        FormatUtc(entry.NotAfterUtc)),
                    new XElement("Status", FormatStatus(entry.Status)));
                if (entry.ScheduledRevocationUtc.HasValue)
                {
                    element.Add(new XElement(
                        "ScheduledRevocationUtc",
                        FormatUtc(entry.ScheduledRevocationUtc.Value)));
                }

                if (entry.RevokedUtc.HasValue)
                {
                    element.Add(new XElement(
                        "RevokedUtc",
                        FormatUtc(entry.RevokedUtc.Value)));
                    element.Add(new XElement(
                        "RevocationReason",
                        FormatReason(entry.RevocationReason.Value)));
                }

                root.Add(element);
            }

            return Serialize(root);
        }

        internal bool IsLedgerWithinDocumentLimit(
            CertificateLedgerSnapshot snapshot)
        {
            byte[] contents = null;
            try
            {
                contents = SerializeLedger(snapshot);
                return true;
            }
            catch (InvalidDataException exception)
                when (StringComparer.Ordinal.Equals(
                    exception.Message,
                    DocumentSizeLimitMessage))
            {
                return false;
            }
            finally
            {
                if (contents != null)
                {
                    Array.Clear(contents, 0, contents.Length);
                }
            }
        }

        internal CertificateLedgerSnapshot DeserializeLedger(byte[] contents)
        {
            XElement root = Parse(contents, "CertificateLedger");
            XAttribute[] attributes = root.Attributes().ToArray();
            if (attributes.Length != 3
                || attributes[0].Name.LocalName != "SchemaVersion"
                || attributes[0].Name.NamespaceName.Length != 0
                || !StringComparer.Ordinal.Equals(
                    attributes[0].Value,
                    SchemaVersion)
                || attributes[1].Name.LocalName != "PkiRevision"
                || attributes[1].Name.NamespaceName.Length != 0
                || attributes[2].Name.LocalName != "CrlNumber"
                || attributes[2].Name.NamespaceName.Length != 0)
            {
                throw Invalid("Certificate ledger attributes are invalid.");
            }

            ulong pkiRevision = ParsePositiveUInt64(
                attributes[1].Value,
                "PkiRevision");
            ulong crlNumber = ParsePositiveUInt64(
                attributes[2].Value,
                "CrlNumber");
            var entries = new List<CertificateLedgerEntry>();
            foreach (XElement element in root.Elements())
            {
                if (element.Name.NamespaceName.Length != 0
                    || element.Name.LocalName != "Certificate"
                    || element.HasAttributes)
                {
                    throw Invalid("Certificate ledger entry is invalid.");
                }

                entries.Add(ParseLedgerEntry(element));
            }

            CertificateLedgerSnapshot snapshot;
            try
            {
                snapshot = new CertificateLedgerSnapshot(
                    entries,
                    pkiRevision,
                    crlNumber);
            }
            catch (ArgumentException exception)
            {
                throw Invalid(
                    "Certificate ledger invariants are invalid.",
                    exception);
            }

            RequireCanonicalDocument(
                contents,
                SerializeLedger(snapshot),
                "pki/ledger.xml");
            return snapshot;
        }

        private static CertificateLedgerEntry ParseLedgerEntry(
            XElement element)
        {
            XElement[] values = element.Elements().ToArray();
            if (values.Length < 16 || values.Length > 19)
            {
                throw Invalid("Certificate ledger entry shape is invalid.");
            }

            string[] requiredNames =
            {
                "SerialNumber",
                "ProductCode",
                "IssuanceRequestId",
                "IssuanceKind",
                "Name",
                "ServiceHostName",
                "ServiceIpv4Address",
                "Port",
                "CsrSha256",
                "RequestPayloadSha256",
                "SubjectPublicKeyInfoSha256",
                "LeafCertificate",
                "IssuedUtc",
                "NotBeforeUtc",
                "NotAfterUtc",
                "Status"
            };
            RequireOrderedNames(values, requiredNames);
            CertificateSerialNumber serialNumber;
            ProductCode productCode;
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError identityError;
            if (!CertificateSerialNumber.TryCreate(
                    values[0].Value,
                    out serialNumber)
                || !ProductCode.TryCreate(values[1].Value, out productCode)
                || !StringComparer.Ordinal.Equals(
                    values[1].Value,
                    productCode.Value)
                || !ServiceEndpointIdentity.TryCreate(
                    values[5].Value,
                    values[6].Value,
                    out identity,
                    out identityError)
                || !StringComparer.Ordinal.Equals(
                    values[5].Value,
                    identity.ServiceHostName)
                || !StringComparer.Ordinal.Equals(
                    values[6].Value,
                    identity.ServiceIpv4Address))
            {
                throw Invalid("Certificate ledger identity is invalid.");
            }

            ServiceDefinition definition;
            ServiceDefinitionValidationError definitionError;
            if (!ServiceDefinition.TryCreate(
                    values[4].Value,
                    productCode.Value,
                    identity,
                    ParsePort(values[7].Value),
                    out definition,
                    out definitionError)
                || !StringComparer.Ordinal.Equals(
                    values[4].Value,
                    definition.Name))
            {
                throw Invalid(
                    "Certificate ledger service definition is invalid: "
                    + definitionError
                    + ".");
            }

            int index = 16;
            DateTime? scheduled = null;
            DateTime? revoked = null;
            CertificateRevocationReason? reason = null;
            if (index < values.Length
                && values[index].Name.LocalName == "ScheduledRevocationUtc")
            {
                RequireCanonicalSimpleElement(
                    values[index],
                    "ScheduledRevocationUtc");
                scheduled = ParseUtc(
                    values[index++].Value,
                    "ScheduledRevocationUtc");
            }

            if (index < values.Length
                && values[index].Name.LocalName == "RevokedUtc")
            {
                RequireCanonicalSimpleElement(
                    values[index],
                    "RevokedUtc");
                revoked = ParseUtc(values[index++].Value, "RevokedUtc");
                if (index >= values.Length
                    || values[index].Name.LocalName != "RevocationReason")
                {
                    throw Invalid("Revocation reason is missing.");
                }

                RequireCanonicalSimpleElement(
                    values[index],
                    "RevocationReason");
                reason = ParseReason(values[index++].Value);
            }

            if (index != values.Length)
            {
                throw Invalid("Certificate ledger entry has unexpected elements.");
            }

            byte[][] hashes =
            {
                ParseSha256(values[8].Value, "CsrSha256"),
                ParseSha256(values[9].Value, "RequestPayloadSha256"),
                ParseSha256(values[10].Value, "SubjectPublicKeyInfoSha256")
            };
            byte[] leafCertificate = ParseLeafCertificate(values[11].Value);
            try
            {
                return CertificateLedgerEntry.Restore(
                    serialNumber,
                    definition,
                    ParseGuid(values[2].Value, "IssuanceRequestId"),
                    ParseIssuanceKind(values[3].Value),
                    hashes[0],
                    hashes[1],
                    hashes[2],
                    leafCertificate,
                    ParseUtc(values[12].Value, "IssuedUtc"),
                    ParseUtc(values[13].Value, "NotBeforeUtc"),
                    ParseUtc(values[14].Value, "NotAfterUtc"),
                    ParseStatus(values[15].Value),
                    scheduled,
                    revoked,
                    reason);
            }
            catch (ArgumentException exception)
            {
                throw Invalid(
                    "Certificate ledger entry is inconsistent.",
                    exception);
            }
            finally
            {
                foreach (byte[] hash in hashes)
                {
                    Array.Clear(hash, 0, hash.Length);
                }

                Array.Clear(
                    leafCertificate,
                    0,
                    leafCertificate.Length);
            }
        }

        private static XElement Parse(byte[] contents, string rootName)
        {
            if (contents == null
                || contents.Length == 0
                || contents.Length > MaximumDocumentBytes)
            {
                throw Invalid("PKI XML document size is invalid.");
            }

            if (contents.Length >= 3
                && contents[0] == 0xef
                && contents[1] == 0xbb
                && contents[2] == 0xbf)
            {
                throw Invalid("PKI XML documents must not contain a BOM.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(contents);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid("PKI XML is not strict UTF-8.", exception);
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = true,
                CloseInput = true
            };
            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    XDocument document = XDocument.Load(
                        reader,
                        LoadOptions.None);
                    if (document.Declaration == null
                        || document.Root == null
                        || document.Root.Name.NamespaceName.Length != 0
                        || document.Root.Name.LocalName != rootName)
                    {
                        throw Invalid("PKI XML root is invalid.");
                    }

                    if (document.Root.DescendantsAndSelf().Any(
                        element => element.Ancestors().Count() + 1 > 16))
                    {
                        throw Invalid("PKI XML exceeds the maximum depth.");
                    }

                    if (document.Nodes().Any(node => !(node is XElement))
                        || document.DescendantNodes().Any(node =>
                            node is XComment
                            || node is XProcessingInstruction
                            || node is XDocumentType))
                    {
                        throw Invalid(
                            "PKI XML contains unsupported nodes.");
                    }

                    return document.Root;
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is XmlException
                    || exception is DecoderFallbackException)
            {
                throw Invalid("PKI XML is invalid.", exception);
            }
        }

        private static byte[] Serialize(XElement root)
        {
            var settings = new XmlWriterSettings
            {
                Encoding = StrictUtf8,
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = false,
                CloseOutput = false
            };
            using (var stream = new MemoryStream())
            {
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    new XDocument(
                        new XDeclaration("1.0", "utf-8", null),
                        root).Save(writer);
                }

                byte[] contents = EnsureFinalCrLf(stream.ToArray());
                if (contents.Length > MaximumDocumentBytes)
                {
                    Array.Clear(contents, 0, contents.Length);
                    throw Invalid(DocumentSizeLimitMessage);
                }

                return contents;
            }
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

        private static void RequireRootAttribute(XElement root)
        {
            XAttribute[] attributes = root.Attributes().ToArray();
            if (attributes.Length != 1
                || attributes[0].Name.LocalName != "SchemaVersion"
                || attributes[0].Name.NamespaceName.Length != 0
                || !StringComparer.Ordinal.Equals(
                    attributes[0].Value,
                    SchemaVersion))
            {
                throw Invalid("PKI state schema version is invalid.");
            }
        }

        private static void RequireCanonicalDocument(
            byte[] supplied,
            byte[] canonical,
            string fileName)
        {
            if (supplied.Length != canonical.Length)
            {
                throw Invalid(
                    fileName + " does not use the canonical v1 representation.");
            }

            for (int index = 0; index < supplied.Length; index++)
            {
                if (supplied[index] != canonical[index])
                {
                    throw Invalid(
                        fileName
                        + " does not use the canonical v1 representation.");
                }
            }
        }

        private static void RequireOrderedNames(
            XElement[] elements,
            string[] requiredNames)
        {
            if (elements.Length < requiredNames.Length)
            {
                throw Invalid("PKI XML is missing required elements.");
            }

            for (int index = 0; index < requiredNames.Length; index++)
            {
                XElement element = elements[index];
                if (element.Name.NamespaceName.Length != 0
                    || element.Name.LocalName != requiredNames[index]
                    || element.HasAttributes
                    || element.HasElements)
                {
                    throw Invalid("PKI XML element order is invalid.");
                }
            }
        }

        private static bool IsCanonicalSimpleElement(
            XElement element,
            string expectedName)
        {
            return element != null
                && element.Name.NamespaceName.Length == 0
                && element.Name.LocalName == expectedName
                && !element.HasAttributes
                && !element.HasElements;
        }

        private static void RequireCanonicalSimpleElement(
            XElement element,
            string expectedName)
        {
            if (!IsCanonicalSimpleElement(element, expectedName))
            {
                throw Invalid("PKI XML optional element is invalid.");
            }
        }

        private static string FormatGuid(Guid value)
        {
            return value.ToString("D").ToLowerInvariant();
        }

        private static Guid ParseGuid(string value, string fieldName)
        {
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(value, FormatGuid(parsed)))
            {
                throw Invalid(fieldName + " is invalid.");
            }

            return parsed;
        }

        private static string FormatUtc(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("UTC DateTime is required.");
            }

            return value.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture);
        }

        private static DateTime ParseUtc(string value, string fieldName)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out parsed)
                || parsed.Kind != DateTimeKind.Utc
                || !StringComparer.Ordinal.Equals(value, FormatUtc(parsed)))
            {
                throw Invalid(fieldName + " is invalid.");
            }

            return parsed;
        }

        private static ulong ParsePositiveUInt64(
            string value,
            string fieldName)
        {
            ulong parsed;
            if (!ulong.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || parsed == 0
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(CultureInfo.InvariantCulture)))
            {
                throw Invalid(fieldName + " is invalid.");
            }

            return parsed;
        }

        private static byte[] ParseSha256(string value, string fieldName)
        {
            byte[] parsed;
            try
            {
                parsed = Convert.FromBase64String(value);
            }
            catch (FormatException exception)
            {
                throw Invalid(fieldName + " is invalid.", exception);
            }

            if (parsed.Length != 32
                || !StringComparer.Ordinal.Equals(
                    value,
                    Convert.ToBase64String(parsed)))
            {
                Array.Clear(parsed, 0, parsed.Length);
                throw Invalid(fieldName + " is invalid.");
            }

            return parsed;
        }

        private static byte[] ParseLeafCertificate(string value)
        {
            byte[] parsed;
            try
            {
                parsed = Convert.FromBase64String(value);
            }
            catch (FormatException exception)
            {
                throw Invalid("LeafCertificate is invalid.", exception);
            }

            if (parsed.Length == 0
                || parsed.Length
                    > CertificateLedgerEntry.MaximumLeafCertificateBytes
                || !StringComparer.Ordinal.Equals(
                    value,
                    Convert.ToBase64String(parsed)))
            {
                Array.Clear(parsed, 0, parsed.Length);
                throw Invalid("LeafCertificate is invalid.");
            }

            return parsed;
        }

        private static int ParsePort(string value)
        {
            int parsed;
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || parsed < 1
                || parsed > ushort.MaxValue
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(CultureInfo.InvariantCulture)))
            {
                throw Invalid("Port is invalid.");
            }

            return parsed;
        }

        private static string FormatIssuanceKind(
            CertificateIssuanceKind value)
        {
            return value == CertificateIssuanceKind.Registration
                ? "REGISTRATION"
                : value == CertificateIssuanceKind.Renewal
                    ? "RENEWAL"
                    : throw new ArgumentOutOfRangeException(nameof(value));
        }

        private static CertificateIssuanceKind ParseIssuanceKind(
            string value)
        {
            if (StringComparer.Ordinal.Equals(value, "REGISTRATION"))
            {
                return CertificateIssuanceKind.Registration;
            }

            if (StringComparer.Ordinal.Equals(value, "RENEWAL"))
            {
                return CertificateIssuanceKind.Renewal;
            }

            throw Invalid("IssuanceKind is invalid.");
        }

        private static string FormatStatus(CertificateLedgerStatus value)
        {
            switch (value)
            {
                case CertificateLedgerStatus.Current:
                    return "CURRENT";
                case CertificateLedgerStatus.Retiring:
                    return "RETIRING";
                case CertificateLedgerStatus.Revoked:
                    return "REVOKED";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static CertificateLedgerStatus ParseStatus(string value)
        {
            switch (value)
            {
                case "CURRENT":
                    return CertificateLedgerStatus.Current;
                case "RETIRING":
                    return CertificateLedgerStatus.Retiring;
                case "REVOKED":
                    return CertificateLedgerStatus.Revoked;
                default:
                    throw Invalid("Certificate status is invalid.");
            }
        }

        internal static string FormatReason(CertificateRevocationReason value)
        {
            switch (value)
            {
                case CertificateRevocationReason.KeyCompromise:
                    return "KEY_COMPROMISE";
                case CertificateRevocationReason.CaCompromise:
                    return "CA_COMPROMISE";
                case CertificateRevocationReason.AffiliationChanged:
                    return "AFFILIATION_CHANGED";
                case CertificateRevocationReason.Superseded:
                    return "SUPERSEDED";
                case CertificateRevocationReason.CessationOfOperation:
                    return "CESSATION_OF_OPERATION";
                case CertificateRevocationReason.PrivilegeWithdrawn:
                    return "PRIVILEGE_WITHDRAWN";
                case CertificateRevocationReason.AaCompromise:
                    return "AA_COMPROMISE";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        internal static CertificateRevocationReason ParseReason(string value)
        {
            switch (value)
            {
                case "KEY_COMPROMISE":
                    return CertificateRevocationReason.KeyCompromise;
                case "CA_COMPROMISE":
                    return CertificateRevocationReason.CaCompromise;
                case "AFFILIATION_CHANGED":
                    return CertificateRevocationReason.AffiliationChanged;
                case "SUPERSEDED":
                    return CertificateRevocationReason.Superseded;
                case "CESSATION_OF_OPERATION":
                    return CertificateRevocationReason.CessationOfOperation;
                case "PRIVILEGE_WITHDRAWN":
                    return CertificateRevocationReason.PrivilegeWithdrawn;
                case "AA_COMPROMISE":
                    return CertificateRevocationReason.AaCompromise;
                default:
                    throw Invalid("RevocationReason is invalid.");
            }
        }

        private static InvalidDataException Invalid(string message)
        {
            return new InvalidDataException(message);
        }

        private static InvalidDataException Invalid(
            string message,
            Exception innerException)
        {
            return new InvalidDataException(message, innerException);
        }
    }
}
