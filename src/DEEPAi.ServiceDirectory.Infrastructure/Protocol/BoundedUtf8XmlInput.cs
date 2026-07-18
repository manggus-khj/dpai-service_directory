using System;
using System.IO;
using System.Text;
using System.Xml;

namespace DEEPAi.ServiceDirectory.Infrastructure.Protocol
{
    public enum XmlInputFailureCode
    {
        None = 0,
        InvalidUtf8,
        InvalidXml,
        DepthExceeded
    }

    public sealed class XmlInputValidationResult
    {
        private XmlInputValidationResult(
            bool isSuccess,
            XmlInputFailureCode failureCode)
        {
            if (isSuccess)
            {
                if (failureCode != XmlInputFailureCode.None)
                {
                    throw new ArgumentException("A successful XML result cannot contain a failure code.");
                }
            }
            else if (failureCode == XmlInputFailureCode.None
                || !Enum.IsDefined(typeof(XmlInputFailureCode), failureCode))
            {
                throw new ArgumentException("A failed XML result requires a defined failure code.");
            }

            IsSuccess = isSuccess;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public XmlInputFailureCode FailureCode { get; }

        internal static XmlInputValidationResult Success()
        {
            return new XmlInputValidationResult(true, XmlInputFailureCode.None);
        }

        internal static XmlInputValidationResult Failure(
            XmlInputFailureCode failureCode)
        {
            return new XmlInputValidationResult(false, failureCode);
        }
    }

    public sealed class BoundedUtf8XmlInput
    {
        private const int CharacterBufferSize = 4096;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public XmlInputValidationResult Validate(BoundedRequestBody body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            string xmlText;
            try
            {
                xmlText = DecodeUtf8(body);
            }
            catch (DecoderFallbackException)
            {
                return XmlInputValidationResult.Failure(
                    XmlInputFailureCode.InvalidUtf8);
            }

            var settings = new XmlReaderSettings
            {
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = Math.Max(1L, body.Length),
                ValidationType = ValidationType.None,
                XmlResolver = null
            };

            try
            {
                bool rootFound = false;
                using (var textReader = new StringReader(xmlText))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (reader.Depth >= XmlInputLimits.MaximumDepth)
                        {
                            return XmlInputValidationResult.Failure(
                                XmlInputFailureCode.DepthExceeded);
                        }

                        if (reader.Depth == 0)
                        {
                            rootFound = true;
                        }
                    }
                }

                return rootFound
                    ? XmlInputValidationResult.Success()
                    : XmlInputValidationResult.Failure(XmlInputFailureCode.InvalidXml);
            }
            catch (XmlException)
            {
                return XmlInputValidationResult.Failure(
                    XmlInputFailureCode.InvalidXml);
            }
        }

        private static string DecodeUtf8(BoundedRequestBody body)
        {
            using (Stream stream = body.OpenRead())
            using (var reader = new StreamReader(
                stream,
                StrictUtf8,
                false,
                CharacterBufferSize,
                false))
            {
                string text = reader.ReadToEnd();
                if (text.Length > 0 && text[0] == '\uFEFF')
                {
                    return text.Substring(1);
                }

                return text;
            }
        }
    }
}
