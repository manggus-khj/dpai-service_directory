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
