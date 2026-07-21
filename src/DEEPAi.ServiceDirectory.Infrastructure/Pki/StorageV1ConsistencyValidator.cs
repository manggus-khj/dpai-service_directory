using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal static class StorageV1ConsistencyValidator
    {
        internal static void Validate(
            DirectorySnapshot directory,
            ServiceDirectoryConfiguration configuration,
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            PeerPkiCacheSnapshot peerCache,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] caPrivateKeyPkcs8,
            PairedPeerCredential peerCredential,
            DateTime utcNow)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (caCertificateDer == null || crlDer == null)
            {
                throw new ArgumentNullException(
                    caCertificateDer == null
                        ? nameof(caCertificateDer)
                        : nameof(crlDer));
            }

            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Storage validation time must use DateTimeKind.Utc.",
                    nameof(utcNow));
            }

            if (directory.PendingCount != 0)
            {
                throw Invalid(
                    "Target v1 directory state cannot contain pending registrations.");
            }

            PeerCredentialConfigurationValidator.Validate(
                configuration,
                peerCredential);

            SiteCertificateAuthority.ValidateStoredCaCertificate(
                state,
                caCertificateDer,
                utcNow);

            switch (state.Role)
            {
                case CertificateAuthorityRole.ActiveIssuer:
                    ValidateActiveIssuer(
                        directory,
                        configuration,
                        state,
                        ledger,
                        peerCache,
                        caCertificateDer,
                        crlDer,
                        caPrivateKeyPkcs8,
                        utcNow);
                    break;

                case CertificateAuthorityRole.Standby:
                    ValidateStandby(
                        state,
                        ledger,
                        peerCache,
                        caCertificateDer,
                        crlDer,
                        caPrivateKeyPkcs8);
                    break;

                default:
                    throw Invalid("The CA role is invalid.");
            }
        }

        private static void ValidateActiveIssuer(
            DirectorySnapshot directory,
            ServiceDirectoryConfiguration configuration,
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            PeerPkiCacheSnapshot peerCache,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] caPrivateKeyPkcs8,
            DateTime utcNow)
        {
            if (state.IssuerInstanceId != configuration.InstanceId
                || ledger == null
                || peerCache != null
                || caPrivateKeyPkcs8 == null
                || caPrivateKeyPkcs8.Length == 0)
            {
                throw Invalid(
                    "Active issuer role files or issuer identity are inconsistent.");
            }

            if (state.PkiRevision != ledger.PkiRevision
                || state.CrlNumber != ledger.CrlNumber)
            {
                throw Invalid(
                    "Active issuer PKI high-water values are inconsistent.");
            }

            CertificateAuthorityStore.ValidateAuthority(
                state,
                caCertificateDer,
                caPrivateKeyPkcs8,
                utcNow);
            CertificateAuthorityStore.ValidateCrl(
                state,
                ledger,
                caCertificateDer,
                crlDer);

            foreach (CertificateLedgerEntry entry in
                ledger.EntriesBySerial.Values)
            {
                byte[] leafCertificate = entry.GetLeafCertificate();
                try
                {
                    SiteCertificateAuthority
                        .ValidateStoredServiceCertificate(
                            leafCertificate,
                            caCertificateDer,
                            entry,
                            configuration.DirectoryEndpointIdentity);
                }
                finally
                {
                    Array.Clear(
                        leafCertificate,
                        0,
                        leafCertificate.Length);
                }
            }

            ValidateActiveDirectoryAndLedger(directory, ledger);
        }

        internal static void ValidateActiveDirectoryAndLedger(
            DirectorySnapshot directory,
            CertificateLedgerSnapshot ledger)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            foreach (CertificateLedgerEntry entry in
                ledger.EntriesBySerial.Values)
            {
                ServiceRecord record;
                bool hasRecord = directory.TryGetRecord(
                    entry.ProductCode,
                    out record);
                if (entry.Status == CertificateLedgerStatus.Current)
                {
                    if (!hasRecord
                        || record.Deleted
                        || !record.Definition.Equals(
                            entry.ServiceDefinition))
                    {
                        throw Invalid(
                            "A CURRENT ledger certificate does not match an active directory record.");
                    }
                }
                else if (entry.Status == CertificateLedgerStatus.Retiring
                    && (!hasRecord || record.Deleted))
                {
                    throw Invalid(
                        "A RETIRING certificate requires an active directory record.");
                }
            }

            foreach (ServiceRecord record in directory.Records.Values)
            {
                CertificateLedgerEntry current;
                bool hasCurrent = ledger.TryGetCurrent(
                    record.Definition.ProductCode,
                    out current);
                if (record.Deleted)
                {
                    if (hasCurrent
                        || ledger.EntriesBySerial.Values.Any(entry =>
                            entry.ProductCode.Equals(
                                record.Definition.ProductCode)
                            && entry.Status
                                == CertificateLedgerStatus.Retiring))
                    {
                        throw Invalid(
                            "A directory tombstone has a non-revoked certificate.");
                    }

                    continue;
                }

                if (hasCurrent)
                {
                    if (!current.ServiceDefinition.Equals(
                            record.Definition))
                    {
                        throw Invalid(
                            "The active directory record and CURRENT certificate differ.");
                    }

                    continue;
                }

                bool hasMatchingHistory = ledger.EntriesBySerial.Values.Any(
                    entry => entry.ProductCode.Equals(
                            record.Definition.ProductCode)
                        && entry.Status == CertificateLedgerStatus.Revoked
                        && entry.ServiceDefinition.Equals(record.Definition));
                if (!hasMatchingHistory)
                {
                    throw Invalid(
                        "An active directory record without CURRENT certificate lacks matching issuance history.");
                }
            }
        }

        private static void ValidateStandby(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            PeerPkiCacheSnapshot peerCache,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] caPrivateKeyPkcs8)
        {
            if (ledger != null
                || peerCache == null
                || caPrivateKeyPkcs8 != null)
            {
                throw Invalid(
                    "Standby role files are inconsistent.");
            }

            if (state.IssuerInstanceId != peerCache.IssuerInstanceId
                || state.PkiRevision != peerCache.PkiRevision
                || state.CrlNumber != peerCache.CrlNumber)
            {
                throw Invalid(
                    "Standby PKI state and peer cache high-water values differ.");
            }

            byte[] expectedHash = peerCache.GetCrlSha256();
            byte[] actualHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                actualHash = sha256.ComputeHash(crlDer);
            }

            try
            {
                if (!FixedTimeEquals(expectedHash, actualHash))
                {
                    throw Invalid(
                        "Standby CRL hash does not match peer-cache.xml.");
                }
            }
            finally
            {
                Array.Clear(expectedHash, 0, expectedHash.Length);
                Array.Clear(actualHash, 0, actualHash.Length);
            }

            ValidateStandbyCrl(
                state,
                caCertificateDer,
                crlDer);
        }

        private static void ValidateStandbyCrl(
            CertificateAuthorityState state,
            byte[] caCertificateDer,
            byte[] crlDer)
        {
            var authority = new X509Certificate(caCertificateDer);
            X509Crl crl;
            try
            {
                crl = new X509CrlParser().ReadCrl(crlDer);
                crl.Verify(authority.GetPublicKey());
            }
            catch (Exception exception)
                when (exception is GeneralSecurityException
                    || exception is IOException
                    || exception is ArgumentException)
            {
                throw Invalid("Standby CRL validation failed.", exception);
            }

            byte[] canonicalDer = crl.GetEncoded();
            try
            {
                Asn1OctetString extension = crl.GetExtensionValue(
                    X509Extensions.CrlNumber);
                if (!FixedTimeEquals(canonicalDer, crlDer)
                    || !crl.IssuerDN.Equivalent(authority.SubjectDN)
                    || extension == null)
                {
                    throw Invalid(
                        "Standby CRL DER, issuer, or number is invalid.");
                }

                DerInteger number = DerInteger.GetInstance(
                    Asn1Object.FromByteArray(extension.GetOctets()));
                ulong parsedNumber;
                if (number.PositiveValue.SignValue <= 0
                    || !ulong.TryParse(
                        number.PositiveValue.ToString(),
                        out parsedNumber)
                    || parsedNumber != state.CrlNumber)
                {
                    throw Invalid(
                        "Standby CRL number does not match state.xml.");
                }
            }
            finally
            {
                Array.Clear(canonicalDer, 0, canonicalDer.Length);
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
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
