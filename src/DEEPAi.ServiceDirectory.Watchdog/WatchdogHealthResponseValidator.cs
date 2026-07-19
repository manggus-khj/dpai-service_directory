using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal static class WatchdogHealthResponseValidator
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XNamespace ExternalNamespace =
            ExternalApiContract.XmlNamespace;

        internal static bool IsValid(byte[] body)
        {
            if (body == null
                || body.Length == 0
                || body.Length > ExternalApiContract.MaximumBodyBytes
                || HasUtf8Bom(body))
            {
                return false;
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            XDocument document;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument =
                        ExternalApiContract.MaximumBodyBytes,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                    IgnoreWhitespace = false
                };
                using (var input = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(input, settings))
                {
                    document = XDocument.Load(
                        reader,
                        LoadOptions.PreserveWhitespace);
                }
            }
            catch (XmlException)
            {
                return false;
            }

            XElement root = document.Root;
            if (root == null
                || root.Name != ExternalNamespace + "Response"
                || document.Nodes().Any(
                    node => node != root
                        && !IsWhitespaceText(node))
                || !HasOnlyNamespaceDeclarationAttributes(root)
                || !HasValidDepth(root, ExternalApiContract.MaximumXmlDepth))
            {
                return false;
            }

            XElement[] children = root.Elements().ToArray();
            if ((children.Length != 4 && children.Length != 5)
                || children[0].Name != ExternalNamespace + "Result"
                || children[1].Name != ExternalNamespace + "Code"
                || children[2].Name != ExternalNamespace + "Message"
                || children[3].Name != ExternalNamespace + "UtcNow"
                || (children.Length == 5
                    && children[4].Name
                        != ExternalNamespace + "Extensions")
                || HasNonWhitespaceTextOutsideElements(root)
                || !IsSimpleValue(children[0], "OK")
                || !IsSimpleValue(children[1], "0")
                || !IsSimpleValue(children[2], string.Empty)
                || !IsValidUtc(children[3]))
            {
                return false;
            }

            for (int index = 0; index < 4; index++)
            {
                if (children[index].Attributes().Any()
                    || children[index].Elements().Any())
                {
                    return false;
                }
            }

            return children.Length != 5
                || (children[4].Attributes().All(
                        attribute => attribute.IsNamespaceDeclaration)
                    && !HasNonWhitespaceTextOutsideElements(children[4]));
        }

        private static bool IsSimpleValue(XElement element, string expected)
        {
            return StringComparer.Ordinal.Equals(element.Value, expected)
                && HasOnlyTextNodes(element);
        }

        private static bool IsValidUtc(XElement element)
        {
            string value = element.Value;
            if (!HasOnlyTextNodes(element)
                || !HasCanonicalUtcLexicalForm(value))
            {
                return false;
            }

            DateTime parsed;
            if (!DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out parsed)
                || parsed.Kind != DateTimeKind.Utc)
            {
                return false;
            }

            return true;
        }

        private static bool HasOnlyTextNodes(XElement element)
        {
            return element.Nodes().All(
                node => node.GetType() == typeof(XText));
        }

        private static bool HasCanonicalUtcLexicalForm(string value)
        {
            const int WholeSecondLength = 20;
            const int FractionSeparatorIndex = 19;

            if (value == null)
            {
                return false;
            }

            if (value.Length == WholeSecondLength)
            {
                return value[WholeSecondLength - 1] == 'Z';
            }

            int fractionLength = value.Length - WholeSecondLength - 1;
            if (fractionLength < 1
                || fractionLength > 7
                || value[FractionSeparatorIndex] != '.'
                || value[value.Length - 1] != 'Z')
            {
                return false;
            }

            for (int index = FractionSeparatorIndex + 1;
                index < value.Length - 1;
                index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasUtf8Bom(byte[] body)
        {
            return body.Length >= 3
                && body[0] == 0xEF
                && body[1] == 0xBB
                && body[2] == 0xBF;
        }

        private static bool HasOnlyNamespaceDeclarationAttributes(
            XElement element)
        {
            return element.Attributes().All(attribute =>
                attribute.IsNamespaceDeclaration
                && StringComparer.Ordinal.Equals(
                    attribute.Name.LocalName,
                    "xmlns")
                && StringComparer.Ordinal.Equals(
                    attribute.Value,
                    ExternalApiContract.XmlNamespace));
        }

        private static bool HasValidDepth(XElement root, int maximumDepth)
        {
            foreach (XElement element in root.DescendantsAndSelf())
            {
                int depth = 1;
                for (XElement parent = element.Parent;
                    parent != null;
                    parent = parent.Parent)
                {
                    depth++;
                }

                if (depth > maximumDepth)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasNonWhitespaceTextOutsideElements(
            XElement element)
        {
            return element.Nodes().Any(node =>
                !(node is XElement) && !IsWhitespaceText(node));
        }

        private static bool IsWhitespaceText(XNode node)
        {
            var text = node as XText;
            return text != null && string.IsNullOrWhiteSpace(text.Value);
        }
    }
}
