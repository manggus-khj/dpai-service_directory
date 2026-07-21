using System;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public static class PeerSyncContract
    {
        public const string XmlNamespace =
            "urn:deepai:service-directory:peer";
        public const string XmlContentType =
            "application/xml; charset=utf-8";
        public const string PkiStatePath = "/api/sync/pki-state";
        public const string PairingAlgorithm =
            "DPAI-SD-ECDH-P256-HMAC-SHA256-v1";
        public const int MaximumControlBodyBytes = 16 * 1024;
        public const int MaximumExchangeBodyBytes = 4 * 1024 * 1024;
        public const int MaximumXmlDepth = 16;
        public const int MaximumBatchItemCount = 1000;
        public const int MaximumActiveCertificateCount = 1000;
        public const int PairingNonceLength = 32;
        public const int PairingPublicKeyLength = 72;
        public const int TranscriptHashLength = 32;
        public const int AuthenticationCodeLength = 32;
        public const int SessionIdLength = 16;
    }

    public enum PeerSyncProtocolFailure
    {
        InvalidRequest = 1,
        BodyTooLarge = 2,
        ItemLimitExceeded = 3
    }

    public sealed class PeerSyncProtocolException : Exception
    {
        public PeerSyncProtocolException(
            PeerSyncProtocolFailure failure,
            string message)
            : base(message)
        {
            Failure = failure;
        }

        public PeerSyncProtocolException(
            PeerSyncProtocolFailure failure,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            Failure = failure;
        }

        public PeerSyncProtocolFailure Failure { get; }
    }
}
