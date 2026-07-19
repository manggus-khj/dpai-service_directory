using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public enum AdminCaState
    {
        NotProvisioned = 1,
        BackupRequired = 2,
        Ready = 3
    }

    public enum AdminCaRole
    {
        ActiveIssuer = 1,
        Standby = 2
    }

    public enum AdminCertificateIssuanceKind
    {
        Registration = 1,
        Renewal = 2
    }

    public enum AdminCertificateStatus
    {
        Current = 1,
        Retiring = 2,
        Revoked = 3
    }

    public enum AdminCertificateRevocationReason
    {
        KeyCompromise = 1,
        CaCompromise = 2,
        AffiliationChanged = 3,
        Superseded = 4,
        CessationOfOperation = 5,
        PrivilegeWithdrawn = 6,
        AaCompromise = 7
    }

    public sealed class AdminCreateCaBackupRequest
    {
        internal AdminCreateCaBackupRequest(string password)
        {
            Password = password ?? throw new ArgumentNullException(
                nameof(password));
        }

        public string Password { get; }
    }

    public sealed class AdminRevokeCertificateRequest
    {
        internal AdminRevokeCertificateRequest(
            AdminCertificateRevocationReason reason)
        {
            if (!Enum.IsDefined(
                typeof(AdminCertificateRevocationReason),
                reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            Reason = reason;
        }

        public AdminCertificateRevocationReason Reason { get; }
    }

    public sealed class AdminServerCaStatusResponse
    {
        internal AdminServerCaStatusResponse(AdminCaState state)
        {
            if (state != AdminCaState.NotProvisioned)
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            State = state;
        }

        internal AdminServerCaStatusResponse(
            AdminCaState state,
            AdminCaRole role,
            Guid siteId,
            Guid issuerInstanceId,
            string caSerialNumber,
            string caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong pkiRevision,
            ulong crlNumber,
            DateTime? lastBackupUtc)
        {
            if (state == AdminCaState.NotProvisioned
                || !Enum.IsDefined(typeof(AdminCaState), state)
                || !Enum.IsDefined(typeof(AdminCaRole), role)
                || siteId == Guid.Empty
                || issuerInstanceId == Guid.Empty
                || !CertificateSerialNumber.TryCreate(
                    caSerialNumber,
                    out CertificateSerialNumber ignoredSerial)
                || !AdminCertificateModelValidation.IsCanonicalSha256(
                    caSpkiSha256)
                || notBeforeUtc.Kind != DateTimeKind.Utc
                || notAfterUtc.Kind != DateTimeKind.Utc
                || notAfterUtc <= notBeforeUtc
                || pkiRevision == 0
                || crlNumber == 0
                || (lastBackupUtc.HasValue
                    && lastBackupUtc.Value.Kind != DateTimeKind.Utc)
                || (state == AdminCaState.BackupRequired
                    && lastBackupUtc.HasValue)
                || (state == AdminCaState.Ready
                    && !lastBackupUtc.HasValue))
            {
                throw new ArgumentException(
                    "Admin CA status is inconsistent.");
            }

            State = state;
            Role = role;
            SiteId = siteId;
            IssuerInstanceId = issuerInstanceId;
            CaSerialNumber = caSerialNumber;
            CaSpkiSha256 = caSpkiSha256;
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            LastBackupUtc = lastBackupUtc;
        }

        public AdminCaState State { get; }

        public AdminCaRole? Role { get; }

        public Guid? SiteId { get; }

        public Guid? IssuerInstanceId { get; }

        public string CaSerialNumber { get; }

        public string CaSpkiSha256 { get; }

        public DateTime? NotBeforeUtc { get; }

        public DateTime? NotAfterUtc { get; }

        public ulong? PkiRevision { get; }

        public ulong? CrlNumber { get; }

        public DateTime? LastBackupUtc { get; }
    }

    public sealed class AdminServerCaBackupResponse
    {
        internal AdminServerCaBackupResponse(
            string fileName,
            DateTime createdUtc,
            string sha256)
        {
            if (string.IsNullOrEmpty(fileName)
                || !AdminCertificateModelValidation.IsCanonicalSha256(sha256)
                || createdUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Admin CA backup response is invalid.");
            }

            FileName = fileName;
            CreatedUtc = createdUtc;
            Sha256 = sha256;
        }

        public string FileName { get; }

        public DateTime CreatedUtc { get; }

        public string Sha256 { get; }
    }

    public sealed class AdminServerCertificateItem
    {
        internal AdminServerCertificateItem(
            string serialNumber,
            string productCode,
            AdminCertificateIssuanceKind issuanceKind,
            string serviceHostName,
            string serviceIpv4Address,
            AdminCertificateStatus status,
            DateTime issuedUtc,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            string leafSha256,
            DateTime? scheduledRevocationUtc,
            DateTime? revokedUtc,
            AdminCertificateRevocationReason? revocationReason)
        {
            bool validSerial = CertificateSerialNumber.TryCreate(
                serialNumber,
                out CertificateSerialNumber ignoredSerial);
            bool validProductCode = ProductCode.TryCreate(
                productCode,
                out ProductCode parsedProductCode)
                && StringComparer.Ordinal.Equals(
                    productCode,
                    parsedProductCode.Value);
            bool validIdentity = ServiceEndpointIdentity.TryCreate(
                serviceHostName,
                serviceIpv4Address,
                out ServiceEndpointIdentity parsedIdentity,
                out EndpointIdentityValidationError ignoredError)
                && StringComparer.Ordinal.Equals(
                    serviceHostName,
                    parsedIdentity.ServiceHostName)
                && StringComparer.Ordinal.Equals(
                    serviceIpv4Address,
                    parsedIdentity.ServiceIpv4Address);
            if (!validSerial
                || !validProductCode
                || !validIdentity
                || !AdminCertificateModelValidation.IsCanonicalSha256(
                    leafSha256)
                || !Enum.IsDefined(
                    typeof(AdminCertificateIssuanceKind),
                    issuanceKind)
                || !Enum.IsDefined(
                    typeof(AdminCertificateStatus),
                    status)
                || issuedUtc.Kind != DateTimeKind.Utc
                || notBeforeUtc.Kind != DateTimeKind.Utc
                || notAfterUtc.Kind != DateTimeKind.Utc
                || notBeforeUtc > issuedUtc
                || notAfterUtc <= issuedUtc)
            {
                throw new ArgumentException(
                    "Admin certificate item is invalid.");
            }

            bool current = status == AdminCertificateStatus.Current;
            bool retiring = status == AdminCertificateStatus.Retiring;
            bool revoked = status == AdminCertificateStatus.Revoked;
            if (revoked != revokedUtc.HasValue
                || revoked != revocationReason.HasValue
                || (current && scheduledRevocationUtc.HasValue)
                || (retiring && !scheduledRevocationUtc.HasValue)
                || (scheduledRevocationUtc.HasValue
                    && scheduledRevocationUtc.Value.Kind
                        != DateTimeKind.Utc)
                || (scheduledRevocationUtc.HasValue
                    && (scheduledRevocationUtc.Value <= issuedUtc
                        || scheduledRevocationUtc.Value > notAfterUtc))
                || (revokedUtc.HasValue
                    && (revokedUtc.Value.Kind != DateTimeKind.Utc
                        || revokedUtc.Value < issuedUtc)))
            {
                throw new ArgumentException(
                    "Admin certificate revocation fields are inconsistent.");
            }

            SerialNumber = serialNumber;
            ProductCode = productCode;
            IssuanceKind = issuanceKind;
            ServiceHostName = serviceHostName;
            ServiceIpv4Address = serviceIpv4Address;
            Status = status;
            IssuedUtc = issuedUtc;
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            LeafSha256 = leafSha256;
            ScheduledRevocationUtc = scheduledRevocationUtc;
            RevokedUtc = revokedUtc;
            RevocationReason = revocationReason;
        }

        public string SerialNumber { get; }

        public string ProductCode { get; }

        public AdminCertificateIssuanceKind IssuanceKind { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public AdminCertificateStatus Status { get; }

        public DateTime IssuedUtc { get; }

        public DateTime NotBeforeUtc { get; }

        public DateTime NotAfterUtc { get; }

        public string LeafSha256 { get; }

        public DateTime? ScheduledRevocationUtc { get; }

        public DateTime? RevokedUtc { get; }

        public AdminCertificateRevocationReason? RevocationReason { get; }
    }

    public sealed class AdminServerCertificatesResponse
    {
        internal AdminServerCertificatesResponse(
            IReadOnlyList<AdminServerCertificateItem> items,
            int totalCount,
            string nextCursor)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            if (totalCount < items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(totalCount));
            }

            TotalCount = totalCount;
            NextCursor = nextCursor;
        }

        public IReadOnlyList<AdminServerCertificateItem> Items { get; }

        public int TotalCount { get; }

        public string NextCursor { get; }
    }

    public sealed class AdminServerCertificateRevocationResponse
    {
        internal AdminServerCertificateRevocationResponse(
            string serialNumber,
            DateTime revokedUtc,
            AdminCertificateRevocationReason reason,
            ulong pkiRevision,
            ulong crlNumber,
            bool replayed)
        {
            if (!CertificateSerialNumber.TryCreate(
                    serialNumber,
                    out CertificateSerialNumber ignoredSerial)
                || revokedUtc.Kind != DateTimeKind.Utc
                || !Enum.IsDefined(
                    typeof(AdminCertificateRevocationReason),
                    reason)
                || pkiRevision == 0
                || crlNumber == 0)
            {
                throw new ArgumentException(
                    "Admin certificate revocation response is invalid.");
            }

            SerialNumber = serialNumber;
            RevokedUtc = revokedUtc;
            Reason = reason;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            Replayed = replayed;
        }

        public string SerialNumber { get; }

        public DateTime RevokedUtc { get; }

        public AdminCertificateRevocationReason Reason { get; }

        public ulong PkiRevision { get; }

        public ulong CrlNumber { get; }

        public bool Replayed { get; }
    }

    internal static class AdminCertificateModelValidation
    {
        internal static bool IsCanonicalSha256(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            byte[] decoded = null;
            try
            {
                decoded = Convert.FromBase64String(value);
                return decoded.Length == 32
                    && StringComparer.Ordinal.Equals(
                        value,
                        Convert.ToBase64String(decoded));
            }
            catch (FormatException)
            {
                return false;
            }
            finally
            {
                if (decoded != null)
                {
                    Array.Clear(decoded, 0, decoded.Length);
                }
            }
        }
    }
}
