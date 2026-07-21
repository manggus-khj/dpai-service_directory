using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public sealed class PeerServiceRecord
    {
        public PeerServiceRecord(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port,
            DateTime lastModifiedUtc,
            bool deleted,
            DateTime? deletedUtc,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError definitionError;
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError identityError;
            if (!ServiceEndpointIdentity.TryCreate(
                    serviceHostName,
                    serviceIpv4Address,
                    out identity,
                    out identityError)
                || !ServiceDefinition.TryCreate(
                    name,
                    productCode,
                    identity,
                    port,
                    out definition,
                    out definitionError)
                || !StringComparer.Ordinal.Equals(name, definition.Name)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    definition.ProductCode.Value)
                || !StringComparer.Ordinal.Equals(
                    serviceHostName,
                    identity.ServiceHostName)
                || !StringComparer.Ordinal.Equals(
                    serviceIpv4Address,
                    identity.ServiceIpv4Address))
            {
                throw new ArgumentException(
                    "The Peer service record is invalid or non-canonical.");
            }

            if (lastModifiedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Last modified time must be UTC.",
                    nameof(lastModifiedUtc));
            }

            if (deleted != deletedUtc.HasValue)
            {
                throw new ArgumentException(
                    "Deleted and DeletedUtc must describe the same state.",
                    nameof(deletedUtc));
            }

            if (deletedUtc.HasValue
                && deletedUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Deleted time must be UTC.",
                    nameof(deletedUtc));
            }

            if (logicalVersion == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalVersion));
            }

            if (originInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Origin instance ID cannot be empty.",
                    nameof(originInstanceId));
            }

            Name = definition.Name;
            ProductCode = definition.ProductCode.Value;
            ServiceHostName = identity.ServiceHostName;
            ServiceIpv4Address = identity.ServiceIpv4Address;
            Port = definition.Port;
            LastModifiedUtc = lastModifiedUtc;
            Deleted = deleted;
            DeletedUtc = deletedUtc;
            LogicalVersion = logicalVersion;
            OriginInstanceId = originInstanceId;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }

        public DateTime LastModifiedUtc { get; }

        public bool Deleted { get; }

        public DateTime? DeletedUtc { get; }

        public ulong LogicalVersion { get; }

        public Guid OriginInstanceId { get; }
    }

    public sealed class PeerPkiStateRequest
    {
        public PeerPkiStateRequest(
            Guid instanceId,
            Guid knownIssuerInstanceId,
            ulong knownPkiRevision,
            ulong knownCrlNumber)
        {
            if (instanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Instance ID cannot be empty.",
                    nameof(instanceId));
            }

            if (knownIssuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Known issuer instance ID cannot be empty.",
                    nameof(knownIssuerInstanceId));
            }

            if (knownPkiRevision == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(knownPkiRevision));
            }

            if (knownCrlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(knownCrlNumber));
            }

            InstanceId = instanceId;
            KnownIssuerInstanceId = knownIssuerInstanceId;
            KnownPkiRevision = knownPkiRevision;
            KnownCrlNumber = knownCrlNumber;
        }

        public Guid InstanceId { get; }

        public Guid KnownIssuerInstanceId { get; }

        public ulong KnownPkiRevision { get; }

        public ulong KnownCrlNumber { get; }
    }

    public sealed class PeerActiveCertificate
    {
        public PeerActiveCertificate(
            string productCode,
            string serialNumber,
            string leafSha256,
            DateTime notAfterUtc)
        {
            DEEPAi.ServiceDirectory.Domain.ProductCode parsedProductCode;
            CertificateSerialNumber parsedSerialNumber;
            if (!DEEPAi.ServiceDirectory.Domain.ProductCode.TryCreate(
                    productCode,
                    out parsedProductCode)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    parsedProductCode.Value)
                || !CertificateSerialNumber.TryCreate(
                    serialNumber,
                    out parsedSerialNumber)
                || !PeerContractModelValidation.IsCanonicalSha256(
                    leafSha256)
                || notAfterUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The active Peer certificate is invalid or non-canonical.");
            }

            ProductCode = parsedProductCode.Value;
            SerialNumber = parsedSerialNumber.Hex;
            LeafSha256 = leafSha256;
            NotAfterUtc = notAfterUtc;
        }

        public string ProductCode { get; }

        public string SerialNumber { get; }

        public string LeafSha256 { get; }

        public DateTime NotAfterUtc { get; }
    }

    public sealed class PeerPkiState
    {
        private readonly byte[] _crl;

        public PeerPkiState(
            Guid issuerInstanceId,
            ulong pkiRevision,
            ulong crlNumber,
            string crlSha256,
            byte[] crl,
            IReadOnlyList<PeerActiveCertificate> activeCertificates)
        {
            if (issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Issuer instance ID cannot be empty.",
                    nameof(issuerInstanceId));
            }

            if (pkiRevision == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pkiRevision));
            }

            if (crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(crlNumber));
            }

            if (crl == null)
            {
                throw new ArgumentNullException(nameof(crl));
            }

            if (crl.Length == 0
                || !PeerContractModelValidation.IsCanonicalSha256(
                    crlSha256)
                || !PeerContractModelValidation.HashMatches(
                    crl,
                    crlSha256))
            {
                throw new ArgumentException(
                    "The CRL bytes and SHA-256 value are invalid.",
                    nameof(crl));
            }

            if (activeCertificates == null)
            {
                throw new ArgumentNullException(nameof(activeCertificates));
            }

            if (activeCertificates.Count
                > PeerSyncContract.MaximumActiveCertificateCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(activeCertificates),
                    "A Peer PKI state cannot exceed 1,000 active certificates.");
            }

            var copy = new List<PeerActiveCertificate>(
                activeCertificates.Count);
            var serialNumbers = new HashSet<string>(
                StringComparer.Ordinal);
            string previousProductCode = null;
            foreach (PeerActiveCertificate certificate in activeCertificates)
            {
                if (certificate == null)
                {
                    throw new ArgumentException(
                        "A Peer PKI state cannot contain a null certificate.",
                        nameof(activeCertificates));
                }

                if (previousProductCode != null
                    && string.CompareOrdinal(
                        previousProductCode,
                        certificate.ProductCode) >= 0)
                {
                    throw new ArgumentException(
                        "Active certificates must be strictly ProductCode Ordinal ascending.",
                        nameof(activeCertificates));
                }

                if (!serialNumbers.Add(certificate.SerialNumber))
                {
                    throw new ArgumentException(
                        "Active certificate serial numbers must be unique.",
                        nameof(activeCertificates));
                }

                previousProductCode = certificate.ProductCode;
                copy.Add(certificate);
            }

            IssuerInstanceId = issuerInstanceId;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            CrlSha256 = crlSha256;
            _crl = (byte[])crl.Clone();
            ActiveCertificates = copy.AsReadOnly();
        }

        public Guid IssuerInstanceId { get; }

        public ulong PkiRevision { get; }

        public ulong CrlNumber { get; }

        public string CrlSha256 { get; }

        public IReadOnlyList<PeerActiveCertificate> ActiveCertificates { get; }

        public byte[] GetCrl()
        {
            return (byte[])_crl.Clone();
        }
    }

    public enum PeerPkiStateResponseKind
    {
        PkiState = 1,
        Error = 2
    }

    public sealed class PeerPkiStateResponse
    {
        private PeerPkiStateResponse(
            PeerPkiStateResponseKind kind,
            PeerSyncResponseCode code,
            string message,
            PeerPkiState pkiState)
        {
            if (!Enum.IsDefined(typeof(PeerPkiStateResponseKind), kind)
                || !Enum.IsDefined(typeof(PeerSyncResponseCode), code)
                || message == null
                || message.Length > 512)
            {
                throw new ArgumentException(
                    "The Peer PKI response envelope is invalid.");
            }

            bool success = kind == PeerPkiStateResponseKind.PkiState;
            if (success != (code == PeerSyncResponseCode.Ok)
                || success != (pkiState != null))
            {
                throw new ArgumentException(
                    "The Peer PKI response payload and result are inconsistent.");
            }

            Kind = kind;
            Code = code;
            Message = message;
            PkiState = pkiState;
        }

        public PeerPkiStateResponseKind Kind { get; }

        public string Result => IsSuccess ? "OK" : "ERROR";

        public PeerSyncResponseCode Code { get; }

        public string Message { get; }

        public PeerPkiState PkiState { get; }

        public bool IsSuccess => Code == PeerSyncResponseCode.Ok;

        public static PeerPkiStateResponse CreateSuccess(
            PeerPkiState pkiState)
        {
            if (pkiState == null)
            {
                throw new ArgumentNullException(nameof(pkiState));
            }

            return new PeerPkiStateResponse(
                PeerPkiStateResponseKind.PkiState,
                PeerSyncResponseCode.Ok,
                string.Empty,
                pkiState);
        }

        public static PeerPkiStateResponse CreateError(
            PeerSyncResponseCode code)
        {
            if (code == PeerSyncResponseCode.Ok)
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            return new PeerPkiStateResponse(
                PeerPkiStateResponseKind.Error,
                code,
                string.Empty,
                null);
        }

        internal static PeerPkiStateResponse CreateParsedError(
            PeerSyncResponseCode code,
            string message)
        {
            return new PeerPkiStateResponse(
                PeerPkiStateResponseKind.Error,
                code,
                message,
                null);
        }
    }

    internal static class PeerContractModelValidation
    {
        internal static bool IsCanonicalSha256(string value)
        {
            byte[] decoded = null;
            try
            {
                return TryDecodeCanonicalBase64(value, 32, out decoded);
            }
            finally
            {
                if (decoded != null)
                {
                    Array.Clear(decoded, 0, decoded.Length);
                }
            }
        }

        internal static bool HashMatches(byte[] value, string sha256)
        {
            byte[] expected;
            if (!TryDecodeCanonicalBase64(sha256, 32, out expected))
            {
                return false;
            }

            byte[] actual;
            using (SHA256 algorithm = SHA256.Create())
            {
                actual = algorithm.ComputeHash(value);
            }

            try
            {
                if (actual.Length != expected.Length)
                {
                    return false;
                }

                int difference = 0;
                for (int index = 0; index < actual.Length; index++)
                {
                    difference |= actual[index] ^ expected[index];
                }

                return difference == 0;
            }
            finally
            {
                Array.Clear(actual, 0, actual.Length);
                Array.Clear(expected, 0, expected.Length);
            }
        }

        internal static bool TryDecodeCanonicalBase64(
            string value,
            int expectedLength,
            out byte[] decoded)
        {
            decoded = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            try
            {
                decoded = Convert.FromBase64String(value);
                if (decoded.Length != expectedLength
                    || !StringComparer.Ordinal.Equals(
                        value,
                        Convert.ToBase64String(decoded)))
                {
                    Array.Clear(decoded, 0, decoded.Length);
                    decoded = null;
                    return false;
                }

                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
