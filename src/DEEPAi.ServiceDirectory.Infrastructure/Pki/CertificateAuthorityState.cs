using System;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal enum CertificateAuthorityRole
    {
        ActiveIssuer = 1,
        Standby = 2
    }

    internal sealed class CertificateAuthorityState
    {
        private readonly byte[] _caSpkiSha256;

        internal CertificateAuthorityState(
            Guid siteId,
            Guid issuerInstanceId,
            CertificateAuthorityRole role,
            CertificateSerialNumber caSerialNumber,
            byte[] caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong pkiRevision,
            ulong crlNumber,
            DateTime? lastBackupUtc)
        {
            if (siteId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Site ID must not be empty.",
                    nameof(siteId));
            }

            if (issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Issuer instance ID must not be empty.",
                    nameof(issuerInstanceId));
            }

            if (!Enum.IsDefined(typeof(CertificateAuthorityRole), role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            if (!caSerialNumber.IsValid)
            {
                throw new ArgumentException(
                    "CA serial number must be valid.",
                    nameof(caSerialNumber));
            }

            if (caSpkiSha256 == null || caSpkiSha256.Length != 32)
            {
                throw new ArgumentException(
                    "CA SPKI SHA-256 must contain exactly 32 bytes.",
                    nameof(caSpkiSha256));
            }

            EnsureUtc(notBeforeUtc, nameof(notBeforeUtc));
            EnsureUtc(notAfterUtc, nameof(notAfterUtc));
            if (notAfterUtc <= notBeforeUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(notAfterUtc));
            }

            if (pkiRevision == 0 || crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkiRevision),
                    "Provisioned PKI high-water values must be positive.");
            }

            if (lastBackupUtc.HasValue)
            {
                EnsureUtc(lastBackupUtc.Value, nameof(lastBackupUtc));
            }

            SiteId = siteId;
            IssuerInstanceId = issuerInstanceId;
            Role = role;
            CaSerialNumber = caSerialNumber;
            _caSpkiSha256 = (byte[])caSpkiSha256.Clone();
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            LastBackupUtc = lastBackupUtc;
        }

        internal Guid SiteId { get; }

        internal Guid IssuerInstanceId { get; }

        internal CertificateAuthorityRole Role { get; }

        internal CertificateSerialNumber CaSerialNumber { get; }

        internal DateTime NotBeforeUtc { get; }

        internal DateTime NotAfterUtc { get; }

        internal ulong PkiRevision { get; }

        internal ulong CrlNumber { get; }

        internal DateTime? LastBackupUtc { get; }

        internal byte[] GetCaSpkiSha256()
        {
            return (byte[])_caSpkiSha256.Clone();
        }

        internal CertificateAuthorityState WithHighWater(
            ulong pkiRevision,
            ulong crlNumber)
        {
            if (pkiRevision < PkiRevision
                || crlNumber < CrlNumber
                || (pkiRevision == PkiRevision
                    && crlNumber == CrlNumber))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkiRevision),
                    "At least one PKI high-water value must increase and neither may decrease.");
            }

            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                Role,
                CaSerialNumber,
                _caSpkiSha256,
                NotBeforeUtc,
                NotAfterUtc,
                pkiRevision,
                crlNumber,
                LastBackupUtc);
        }

        internal CertificateAuthorityState WithLastBackupUtc(
            DateTime lastBackupUtc)
        {
            EnsureUtc(lastBackupUtc, nameof(lastBackupUtc));
            if (LastBackupUtc.HasValue
                && lastBackupUtc < LastBackupUtc.Value)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastBackupUtc),
                    "Last backup time must not decrease.");
            }

            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                Role,
                CaSerialNumber,
                _caSpkiSha256,
                NotBeforeUtc,
                NotAfterUtc,
                PkiRevision,
                CrlNumber,
                lastBackupUtc);
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA state timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }
        }
    }
}
