using System;

namespace DEEPAi.ServiceDirectory.Domain.Certificates
{
    public enum CertificateIssuanceKind
    {
        Registration = 0,
        Renewal = 1
    }

    public enum CertificateLedgerStatus
    {
        Current = 0,
        Retiring = 1,
        Revoked = 2
    }

    public enum CertificateRevocationReason
    {
        Unspecified = 0,
        KeyCompromise = 1,
        CaCompromise = 2,
        AffiliationChanged = 3,
        Superseded = 4,
        CessationOfOperation = 5,
        PrivilegeWithdrawn = 9,
        AaCompromise = 10
    }

    public sealed class CertificateLedgerEntry
    {
        public const int Sha256Length = 32;

        private readonly byte[] _csrSha256;
        private readonly byte[] _requestPayloadSha256;
        private readonly byte[] _subjectPublicKeyInfoSha256;
        private readonly byte[] _leafCertificateSha256;

        private CertificateLedgerEntry(
            CertificateSerialNumber serialNumber,
            ProductCode productCode,
            Guid issuanceRequestId,
            CertificateIssuanceKind issuanceKind,
            ServiceEndpointIdentity serviceIdentity,
            byte[] csrSha256,
            byte[] requestPayloadSha256,
            byte[] subjectPublicKeyInfoSha256,
            byte[] leafCertificateSha256,
            DateTime issuedUtc,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            CertificateLedgerStatus status,
            DateTime? scheduledRevocationUtc,
            DateTime? revokedUtc,
            CertificateRevocationReason? revocationReason)
        {
            SerialNumber = serialNumber;
            ProductCode = productCode;
            IssuanceRequestId = issuanceRequestId;
            IssuanceKind = issuanceKind;
            ServiceIdentity = serviceIdentity;
            _csrSha256 = CloneSha256(csrSha256, nameof(csrSha256));
            _requestPayloadSha256 = CloneSha256(
                requestPayloadSha256,
                nameof(requestPayloadSha256));
            _subjectPublicKeyInfoSha256 = CloneSha256(
                subjectPublicKeyInfoSha256,
                nameof(subjectPublicKeyInfoSha256));
            _leafCertificateSha256 = CloneSha256(
                leafCertificateSha256,
                nameof(leafCertificateSha256));
            IssuedUtc = issuedUtc;
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            Status = status;
            ScheduledRevocationUtc = scheduledRevocationUtc;
            RevokedUtc = revokedUtc;
            RevocationReason = revocationReason;
        }

        public CertificateSerialNumber SerialNumber { get; }

        public ProductCode ProductCode { get; }

        public Guid IssuanceRequestId { get; }

        public CertificateIssuanceKind IssuanceKind { get; }

        public ServiceEndpointIdentity ServiceIdentity { get; }

        public DateTime IssuedUtc { get; }

        public DateTime NotBeforeUtc { get; }

        public DateTime NotAfterUtc { get; }

        public CertificateLedgerStatus Status { get; }

        public DateTime? ScheduledRevocationUtc { get; }

        public DateTime? RevokedUtc { get; }

        public CertificateRevocationReason? RevocationReason { get; }

        public static CertificateLedgerEntry CreateIssued(
            CertificateSerialNumber serialNumber,
            ProductCode productCode,
            Guid issuanceRequestId,
            CertificateIssuanceKind issuanceKind,
            ServiceEndpointIdentity serviceIdentity,
            byte[] csrSha256,
            byte[] requestPayloadSha256,
            byte[] subjectPublicKeyInfoSha256,
            byte[] leafCertificateSha256,
            DateTime issuedUtc,
            DateTime notBeforeUtc,
            DateTime notAfterUtc)
        {
            if (!serialNumber.IsValid)
            {
                throw new ArgumentException(
                    "Certificate serial number must be valid.",
                    nameof(serialNumber));
            }

            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "Product code must be valid.",
                    nameof(productCode));
            }

            if (issuanceRequestId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Issuance request ID must not be empty.",
                    nameof(issuanceRequestId));
            }

            if (!Enum.IsDefined(typeof(CertificateIssuanceKind), issuanceKind))
            {
                throw new ArgumentOutOfRangeException(nameof(issuanceKind));
            }

            if (serviceIdentity == null)
            {
                throw new ArgumentNullException(nameof(serviceIdentity));
            }

            EnsureUtc(issuedUtc, nameof(issuedUtc));
            EnsureUtc(notBeforeUtc, nameof(notBeforeUtc));
            EnsureUtc(notAfterUtc, nameof(notAfterUtc));
            if (notBeforeUtc > issuedUtc || notAfterUtc <= issuedUtc)
            {
                throw new ArgumentException(
                    "Certificate validity must contain the issuance time.",
                    nameof(notAfterUtc));
            }

            return new CertificateLedgerEntry(
                serialNumber,
                productCode,
                issuanceRequestId,
                issuanceKind,
                serviceIdentity,
                csrSha256,
                requestPayloadSha256,
                subjectPublicKeyInfoSha256,
                leafCertificateSha256,
                issuedUtc,
                notBeforeUtc,
                notAfterUtc,
                CertificateLedgerStatus.Current,
                null,
                null,
                null);
        }

        public static CertificateLedgerEntry Restore(
            CertificateSerialNumber serialNumber,
            ProductCode productCode,
            Guid issuanceRequestId,
            CertificateIssuanceKind issuanceKind,
            ServiceEndpointIdentity serviceIdentity,
            byte[] csrSha256,
            byte[] requestPayloadSha256,
            byte[] subjectPublicKeyInfoSha256,
            byte[] leafCertificateSha256,
            DateTime issuedUtc,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            CertificateLedgerStatus status,
            DateTime? scheduledRevocationUtc,
            DateTime? revokedUtc,
            CertificateRevocationReason? revocationReason)
        {
            CertificateLedgerEntry entry = CreateIssued(
                serialNumber,
                productCode,
                issuanceRequestId,
                issuanceKind,
                serviceIdentity,
                csrSha256,
                requestPayloadSha256,
                subjectPublicKeyInfoSha256,
                leafCertificateSha256,
                issuedUtc,
                notBeforeUtc,
                notAfterUtc);

            if (status == CertificateLedgerStatus.Current)
            {
                if (scheduledRevocationUtc.HasValue
                    || revokedUtc.HasValue
                    || revocationReason.HasValue)
                {
                    throw new ArgumentException(
                        "A current certificate cannot contain revocation state.",
                        nameof(status));
                }

                return entry;
            }

            if (status == CertificateLedgerStatus.Retiring)
            {
                if (!scheduledRevocationUtc.HasValue
                    || revokedUtc.HasValue
                    || revocationReason.HasValue)
                {
                    throw new ArgumentException(
                        "A retiring certificate must contain only a scheduled revocation.",
                        nameof(status));
                }

                return entry.ScheduleRevocation(
                    scheduledRevocationUtc.Value);
            }

            if (status != CertificateLedgerStatus.Revoked
                || !revokedUtc.HasValue
                || !revocationReason.HasValue)
            {
                throw new ArgumentException(
                    "A revoked certificate must contain its time and reason.",
                    nameof(status));
            }

            if (scheduledRevocationUtc.HasValue)
            {
                entry = entry.ScheduleRevocation(
                    scheduledRevocationUtc.Value);
            }

            return entry.Revoke(
                revokedUtc.Value,
                revocationReason.Value);
        }

        public CertificateLedgerEntry ScheduleRevocation(DateTime scheduledRevocationUtc)
        {
            if (Status != CertificateLedgerStatus.Current)
            {
                throw new InvalidOperationException(
                    "Only the current certificate can enter the renewal overlap period.");
            }

            EnsureUtc(scheduledRevocationUtc, nameof(scheduledRevocationUtc));
            if (scheduledRevocationUtc <= IssuedUtc
                || scheduledRevocationUtc > NotAfterUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(scheduledRevocationUtc),
                    "Scheduled revocation must be after issuance and no later than expiry.");
            }

            return new CertificateLedgerEntry(
                SerialNumber,
                ProductCode,
                IssuanceRequestId,
                IssuanceKind,
                ServiceIdentity,
                _csrSha256,
                _requestPayloadSha256,
                _subjectPublicKeyInfoSha256,
                _leafCertificateSha256,
                IssuedUtc,
                NotBeforeUtc,
                NotAfterUtc,
                CertificateLedgerStatus.Retiring,
                scheduledRevocationUtc,
                null,
                null);
        }

        public CertificateLedgerEntry Revoke(
            DateTime revokedUtc,
            CertificateRevocationReason reason)
        {
            if (Status == CertificateLedgerStatus.Revoked)
            {
                throw new InvalidOperationException(
                    "A revoked certificate cannot be revoked again.");
            }

            EnsureUtc(revokedUtc, nameof(revokedUtc));
            if (revokedUtc < IssuedUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(revokedUtc),
                    "Revocation cannot precede issuance.");
            }

            if (!Enum.IsDefined(typeof(CertificateRevocationReason), reason)
                || reason == CertificateRevocationReason.Unspecified)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            return new CertificateLedgerEntry(
                SerialNumber,
                ProductCode,
                IssuanceRequestId,
                IssuanceKind,
                ServiceIdentity,
                _csrSha256,
                _requestPayloadSha256,
                _subjectPublicKeyInfoSha256,
                _leafCertificateSha256,
                IssuedUtc,
                NotBeforeUtc,
                NotAfterUtc,
                CertificateLedgerStatus.Revoked,
                ScheduledRevocationUtc,
                revokedUtc,
                reason);
        }

        public bool MatchesIssuanceRequest(
            Guid issuanceRequestId,
            byte[] csrSha256,
            byte[] requestPayloadSha256)
        {
            return issuanceRequestId == IssuanceRequestId
                && AreEqual(_csrSha256, csrSha256)
                && AreEqual(_requestPayloadSha256, requestPayloadSha256);
        }

        public byte[] GetCsrSha256()
        {
            return (byte[])_csrSha256.Clone();
        }

        public byte[] GetRequestPayloadSha256()
        {
            return (byte[])_requestPayloadSha256.Clone();
        }

        public byte[] GetSubjectPublicKeyInfoSha256()
        {
            return (byte[])_subjectPublicKeyInfoSha256.Clone();
        }

        public byte[] GetLeafCertificateSha256()
        {
            return (byte[])_leafCertificateSha256.Clone();
        }

        private static byte[] CloneSha256(byte[] value, string parameterName)
        {
            if (value == null || value.Length != Sha256Length)
            {
                throw new ArgumentException(
                    "SHA-256 values must be exactly 32 bytes.",
                    parameterName);
            }

            return (byte[])value.Clone();
        }

        private static bool AreEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Certificate ledger timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }
        }
    }
}
