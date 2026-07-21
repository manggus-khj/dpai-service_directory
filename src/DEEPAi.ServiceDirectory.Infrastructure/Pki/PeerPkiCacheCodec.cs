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
    internal sealed class PeerPkiCacheCodec
    {
        internal const int MaximumDocumentBytes = 16 * 1024 * 1024;

        private const string SchemaVersion = "1";
        private const string UtcTimestampFormat =
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal byte[] Serialize(PeerPkiCacheSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var certificates = new XElement("ActiveCertificates");
            foreach (PeerPkiCacheCertificate certificate in
                snapshot.ActiveCertificates)
            {
                certificates.Add(
                    new XElement(
                        "Certificate",
                        new XElement(
                            "ProductCode",
                            certificate.ProductCode.Value),
                        new XElement(
                            "SerialNumber",
                            certificate.SerialNumber.Hex),
                        new XElement(
                            "LeafSha256",
                            Convert.ToBase64String(
                                certificate.GetLeafSha256())),
                        new XElement(
                            "NotAfterUtc",
                            FormatUtc(certificate.NotAfterUtc))));
            }

            var root = new XElement(
                "PeerPkiCache",
                new XAttribute("SchemaVersion", SchemaVersion),
                new XElement(
                    "IssuerInstanceId",
                    snapshot.IssuerInstanceId.ToString("D")),
                new XElement(
                    "PkiRevision",
                    snapshot.PkiRevision.ToString(
                        CultureInfo.InvariantCulture)),
                new XElement(
                    "CrlNumber",
                    snapshot.CrlNumber.ToString(
                        CultureInfo.InvariantCulture)),
                new XElement(
                    "CrlSha256",
                    Convert.ToBase64String(snapshot.GetCrlSha256())),
                certificates);
            return SerializeDocument(root);
        }

        internal PeerPkiCacheSnapshot Deserialize(byte[] contents)
        {
            XElement root = ParseDocument(contents);
            XAttribute[] attributes = root.Attributes().ToArray();
            if (attributes.Length != 1
                || attributes[0].Name.NamespaceName.Length != 0
                || attributes[0].Name.LocalName != "SchemaVersion"
                || !StringComparer.Ordinal.Equals(
                    attributes[0].Value,
                    SchemaVersion))
            {
                throw Invalid("Peer PKI cache attributes are invalid.");
            }

            XElement[] elements = root.Elements().ToArray();
            string[] requiredNames =
            {
                "IssuerInstanceId",
                "PkiRevision",
                "CrlNumber",
                "CrlSha256",
                "ActiveCertificates"
            };
            if (elements.Length != requiredNames.Length)
            {
                throw Invalid("Peer PKI cache element count is invalid.");
            }

            for (int index = 0; index < requiredNames.Length; index++)
            {
                XElement element = elements[index];
                bool collection = index == requiredNames.Length - 1;
                if (element.Name.NamespaceName.Length != 0
                    || element.Name.LocalName != requiredNames[index]
                    || element.HasAttributes
                    || (!collection && element.HasElements))
                {
                    throw Invalid("Peer PKI cache element order is invalid.");
                }
            }

            int certificateCount = 0;
            foreach (XElement unused in elements[4].Elements())
            {
                certificateCount++;
                if (certificateCount
                    > PeerPkiCacheSnapshot.MaximumActiveCertificateCount)
                {
                    throw Invalid(
                        "Peer PKI cache exceeds the certificate limit.");
                }
            }

            var certificates =
                new List<PeerPkiCacheCertificate>(certificateCount);
            foreach (XElement element in elements[4].Elements())
            {
                certificates.Add(ParseCertificate(element));
            }

            byte[] crlSha256 = ParseSha256(
                elements[3].Value,
                "CrlSha256");
            PeerPkiCacheSnapshot snapshot;
            try
            {
                snapshot = new PeerPkiCacheSnapshot(
                    ParseGuid(elements[0].Value),
                    ParsePositiveUInt64(
                        elements[1].Value,
                        "PkiRevision"),
                    ParsePositiveUInt64(
                        elements[2].Value,
                        "CrlNumber"),
                    crlSha256,
                    certificates);
            }
            catch (ArgumentException exception)
            {
                throw Invalid(
                    "Peer PKI cache invariants are invalid.",
                    exception);
            }
            finally
            {
                Array.Clear(crlSha256, 0, crlSha256.Length);
            }

            RequireCanonical(contents, Serialize(snapshot));
            return snapshot;
        }

        private static PeerPkiCacheCertificate ParseCertificate(
            XElement element)
        {
            if (element.Name.NamespaceName.Length != 0
                || element.Name.LocalName != "Certificate"
                || element.HasAttributes)
            {
                throw Invalid("Peer PKI cache certificate is invalid.");
            }

            XElement[] values = element.Elements().ToArray();
            string[] names =
            {
                "ProductCode",
                "SerialNumber",
                "LeafSha256",
                "NotAfterUtc"
            };
            if (values.Length != names.Length)
            {
                throw Invalid("Peer PKI cache certificate shape is invalid.");
            }

            for (int index = 0; index < names.Length; index++)
            {
                if (values[index].Name.NamespaceName.Length != 0
                    || values[index].Name.LocalName != names[index]
                    || values[index].HasAttributes
                    || values[index].HasElements)
                {
                    throw Invalid(
                        "Peer PKI cache certificate order is invalid.");
                }
            }

            ProductCode productCode;
            CertificateSerialNumber serialNumber;
            if (!ProductCode.TryCreate(values[0].Value, out productCode)
                || !StringComparer.Ordinal.Equals(
                    values[0].Value,
                    productCode.Value)
                || !CertificateSerialNumber.TryCreate(
                    values[1].Value,
                    out serialNumber))
            {
                throw Invalid(
                    "Peer PKI cache certificate identity is invalid.");
            }

            byte[] leafSha256 = ParseSha256(
                values[2].Value,
                "LeafSha256");
            try
            {
                return new PeerPkiCacheCertificate(
                    productCode,
                    serialNumber,
                    leafSha256,
                    ParseUtc(values[3].Value, "NotAfterUtc"));
            }
            finally
            {
                Array.Clear(leafSha256, 0, leafSha256.Length);
            }
        }

        private static XElement ParseDocument(byte[] contents)
        {
            if (contents == null
                || contents.Length == 0
                || contents.Length > MaximumDocumentBytes)
            {
                throw Invalid("Peer PKI cache size is invalid.");
            }

            if (contents.Length >= 3
                && contents[0] == 0xef
                && contents[1] == 0xbb
                && contents[2] == 0xbf)
            {
                throw Invalid("Peer PKI cache must not contain a BOM.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(contents);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid("Peer PKI cache is not strict UTF-8.", exception);
            }

            var settings = new XmlReaderSettings
            {
                CheckCharacters = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = true,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = Math.Max(1, xml.Length)
            };
            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(
                    textReader,
                    settings))
                {
                    XDocument document = XDocument.Load(reader, LoadOptions.None);
                    if (document.Declaration == null
                        || document.Root == null
                        || document.Root.Name.NamespaceName.Length != 0
                        || document.Root.Name.LocalName != "PeerPkiCache"
                        || document.Nodes().Any(node => !(node is XElement))
                        || document.DescendantNodes().Any(node =>
                            node is XComment
                            || node is XProcessingInstruction
                            || node is XDocumentType
                            || node is XCData)
                        || document.Root.DescendantsAndSelf().Any(
                            value => value.Ancestors().Count() + 1 > 16))
                    {
                        throw Invalid("Peer PKI cache XML shape is invalid.");
                    }

                    return document.Root;
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw Invalid("Peer PKI cache XML is invalid.", exception);
            }
        }

        private static byte[] SerializeDocument(XElement root)
        {
            var settings = new XmlWriterSettings
            {
                CheckCharacters = true,
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

                if (stream.Length > MaximumDocumentBytes)
                {
                    throw Invalid("Peer PKI cache exceeds its size limit.");
                }

                return EnsureFinalCrLf(stream.ToArray());
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

        private static Guid ParseGuid(string value)
        {
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString("D")))
            {
                throw Invalid("IssuerInstanceId is invalid.");
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

            if (parsed.Length != CertificateLedgerEntry.Sha256Length
                || !StringComparer.Ordinal.Equals(
                    value,
                    Convert.ToBase64String(parsed)))
            {
                Array.Clear(parsed, 0, parsed.Length);
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
                UtcTimestampFormat,
                CultureInfo.InvariantCulture);
        }

        private static DateTime ParseUtc(string value, string fieldName)
        {
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
                throw Invalid(fieldName + " is invalid.");
            }

            return parsed;
        }

        private static void RequireCanonical(
            byte[] supplied,
            byte[] canonical)
        {
            if (supplied.Length != canonical.Length)
            {
                throw Invalid(
                    "Peer PKI cache is not in canonical v1 form.");
            }

            for (int index = 0; index < supplied.Length; index++)
            {
                if (supplied[index] != canonical[index])
                {
                    throw Invalid(
                        "Peer PKI cache is not in canonical v1 form.");
                }
            }
        }

        private static InvalidDataException Invalid(string message)
        {
            return new InvalidDataException(message);
        }

        private static InvalidDataException Invalid(
            string message,
            Exception exception)
        {
            return new InvalidDataException(message, exception);
        }
    }
}
