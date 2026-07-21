using System;
using System.IO;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityRoleRepairStore
    {
        private void EnsureEqualActiveHighWaterBytes(
            CertificateAuthorityState current,
            CertificateAuthorityBackupState backup,
            CaBackupPayload payload)
        {
            byte[] installed = null;
            try
            {
                if (current.PkiRevision == backup.State.PkiRevision
                    && _writer.Exists(
                        StateFileTarget.CertificateLedger))
                {
                    installed = ReadRequired(
                        StateFileTarget.CertificateLedger,
                        CertificateAuthorityStateCodec
                            .MaximumDocumentBytes);
                    if (!BytesEqual(installed, payload.Ledger))
                    {
                        throw new InvalidDataException(
                            "The selected backup ledger differs at the installed PKI revision.");
                    }

                    Clear(installed);
                    installed = null;
                }

                if (current.CrlNumber == backup.State.CrlNumber
                    && _writer.Exists(
                        StateFileTarget.CertificateRevocationList))
                {
                    installed = ReadRequired(
                        StateFileTarget.CertificateRevocationList,
                        CertificateAuthorityStore.MaximumCrlBytes);
                    if (!BytesEqual(installed, payload.CrlDer))
                    {
                        throw new InvalidDataException(
                            "The selected backup CRL differs at the installed CRL number.");
                    }
                }
            }
            finally
            {
                Clear(installed);
            }
        }

        private static void ValidateOperationArguments(
            CaBackupPayload payload,
            byte[] encryptedBackup,
            DirectoryEndpointIdentity directoryIdentity,
            DateTime utcNow)
        {
            ValidateOperationArguments(
                payload,
                directoryIdentity,
                utcNow);
            if (encryptedBackup == null || encryptedBackup.Length == 0)
            {
                throw new ArgumentException(
                    "The encrypted backup bytes are required.",
                    nameof(encryptedBackup));
            }
        }

        private static void ValidateOperationArguments(
            CaBackupPayload payload,
            DirectoryEndpointIdentity directoryIdentity,
            DateTime utcNow)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (directoryIdentity == null)
            {
                throw new ArgumentNullException(
                    nameof(directoryIdentity));
            }

            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA role repair time must use DateTimeKind.Utc.",
                    nameof(utcNow));
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
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

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private static void Clear(byte[][] values)
        {
            if (values == null)
            {
                return;
            }

            foreach (byte[] value in values)
            {
                Clear(value);
            }
        }
    }
}
