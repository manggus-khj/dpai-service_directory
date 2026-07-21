using System;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public static class ExternalApiContract
    {
        public const string XmlNamespace =
            "urn:deepai:service-directory:external";
        public const string ApiKeyHeaderName = "X-DPAI-API-Key";
        public const string XmlContentType =
            "application/xml; charset=utf-8";
        public const string CrlContentType = "application/pkix-crl";
        public const string CaPath = "/pki/ca";
        public const string CrlPath = "/pki/crl";
        public const string IssuerCrlPathPrefix = "/pki/crl/";
        public const int MaximumBodyBytes = 16 * 1024;
        public const int MaximumCertificateRequestBodyBytes = 64 * 1024;
        public const int MaximumCaResponseBytes = 32 * 1024;
        public const int MaximumCrlResponseBytes = 4 * 1024 * 1024;
        public const int MaximumCertificateSigningRequestBytes = 48 * 1024;
        public const int MaximumCaCertificateBytes = 32 * 1024;
        public const int MaximumLeafCertificateBytes = 32 * 1024;
        public const int MaximumProofSignatureBytes =
            MaximumCertificateRequestBodyBytes;
        public const int RenewalNonceBytes = 16;
        public const int Sha256Bytes = 32;
        public const int MaximumRawQueryBytes = 2048;
        public const int MaximumQueryFieldCount = 16;
        public const int MaximumXmlDepth = 16;
        public const int MaximumMessageCharacters = 512;

        public static bool IsIssuerCrlPath(
            string value,
            string caSerialNumber)
        {
            return !string.IsNullOrEmpty(caSerialNumber)
                && StringComparer.Ordinal.Equals(
                    value,
                    IssuerCrlPathPrefix + caSerialNumber);
        }

        public static bool TryParseIssuerCrlPath(
            string value,
            out string caSerialNumber)
        {
            caSerialNumber = null;
            if (string.IsNullOrEmpty(value)
                || !value.StartsWith(
                    IssuerCrlPathPrefix,
                    StringComparison.Ordinal)
                || value.Length != IssuerCrlPathPrefix.Length + 32)
            {
                return false;
            }

            string candidate = value.Substring(IssuerCrlPathPrefix.Length);
            for (int index = 0; index < candidate.Length; index++)
            {
                char character = candidate[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }

            caSerialNumber = candidate;
            return true;
        }
    }

    public sealed class ExternalProtocolException : Exception
    {
        public ExternalProtocolException(string message)
            : base(message)
        {
        }

        public ExternalProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
