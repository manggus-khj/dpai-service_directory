using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public static class CertificateAuthorityRepair
    {
        private const int MaximumConfigurationBytes = 16 * 1024 * 1024;

        public static Guid ReadInstalledInstanceId(
            string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            {
                throw new ArgumentException(
                    "State directory path is required.",
                    nameof(stateDirectoryPath));
            }

            var pathPolicy = new StateStoragePathPolicy(
                stateDirectoryPath);
            var fileWriter = new AtomicFileWriter(pathPolicy);
            byte[] configurationBytes = fileWriter.Read(
                StateFileTarget.Config,
                MaximumConfigurationBytes);
            try
            {
                return new StateXmlCodec()
                    .DeserializeConfiguration(configurationBytes)
                    .InstanceId;
            }
            finally
            {
                Array.Clear(
                    configurationBytes,
                    0,
                    configurationBytes.Length);
            }
        }

        public static CertificateAuthorityBackupResult
            ProvisionAndCreateInitialBackup(
                string stateDirectoryPath,
                Guid installedInstanceId,
                string password,
                DateTime utcNow)
        {
            ValidateCommon(
                stateDirectoryPath,
                installedInstanceId,
                password,
                utcNow);
            var mutationGate = new StateMutationGate();
            var accessPolicy =
                PeerSecretAccessPolicy.ForInstalledMainService();
            var store = new CertificateAuthorityStore(
                new StateStoragePathPolicy(stateDirectoryPath),
                mutationGate,
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            CertificateAuthorityAdministration administration = null;
            try
            {
                if (store.TryLoad())
                {
                    throw new InvalidOperationException(
                        "PKI state is already provisioned.");
                }

                store.Provision(installedInstanceId, utcNow);
                administration = new CertificateAuthorityAdministration(
                    store,
                    new CaBackupFileStore(
                        new StateStoragePathPolicy(stateDirectoryPath),
                        accessPolicy));
                store = null;
                return administration.CreateBackup(password, utcNow);
            }
            finally
            {
                if (administration != null)
                {
                    administration.Dispose();
                }
                else if (store != null)
                {
                    store.Dispose();
                }
            }
        }

        public static void RestoreFromEncryptedBackup(
            string stateDirectoryPath,
            Guid installedInstanceId,
            string backupPath,
            string password,
            DateTime utcNow)
        {
            ValidateCommon(
                stateDirectoryPath,
                installedInstanceId,
                password,
                utcNow);
            string fullBackupPath = ValidateBackupPath(backupPath);
            byte[] encrypted = ReadLimited(
                fullBackupPath,
                CaBackupCodec.MaximumBackupBytes);
            var codec = new CaBackupCodec();
            CaBackupPayload payload = null;
            var accessPolicy =
                PeerSecretAccessPolicy.ForInstalledMainService();
            var store = new CertificateAuthorityStore(
                new StateStoragePathPolicy(stateDirectoryPath),
                new StateMutationGate(),
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            try
            {
                payload = codec.Decrypt(encrypted, password);
                try
                {
                    store.TryLoad();
                }
                catch (Exception exception) when (
                    IsRepairableInstalledStateFailure(exception))
                {
                    // The encrypted backup is independently authenticated.
                    // Restore captures every readable installed target as a
                    // journal before-image so a failed repair leaves those
                    // exact bytes in place.
                }
                store.Restore(
                    payload,
                    installedInstanceId,
                    utcNow);
            }
            finally
            {
                store.Dispose();
                if (payload != null)
                {
                    payload.Dispose();
                }

                Array.Clear(encrypted, 0, encrypted.Length);
            }
        }

        private static void ValidateCommon(
            string stateDirectoryPath,
            Guid installedInstanceId,
            string password,
            DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            {
                throw new ArgumentException(
                    "State directory path is required.",
                    nameof(stateDirectoryPath));
            }

            if (installedInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Installed instance ID must not be empty.",
                    nameof(installedInstanceId));
            }

            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Repair time must be UTC.",
                    nameof(utcNow));
            }

            CaBackupCodec.ValidatePassword(password);
        }

        private static string ValidateBackupPath(string backupPath)
        {
            if (string.IsNullOrWhiteSpace(backupPath)
                || !Path.IsPathRooted(backupPath))
            {
                throw new ArgumentException(
                    "CA repair backup must use an absolute local path.",
                    nameof(backupPath));
            }

            string fullPath = Path.GetFullPath(backupPath);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                || fullPath.StartsWith("//", StringComparison.Ordinal)
                || !fullPath.EndsWith(".dpca", StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "CA repair backup must be a local .dpca file.");
            }

            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "CA repair backup was not found.",
                    fullPath);
            }

            for (FileSystemInfo current = file;
                current != null;
                current = current is FileInfo
                    ? ((FileInfo)current).Directory
                    : ((DirectoryInfo)current).Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "CA repair backup path must not contain reparse points.");
                }
            }

            return fullPath;
        }

        private static byte[] ReadLimited(string path, int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length <= 0 || stream.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "CA repair backup size is invalid.");
                }

                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(
                        bytes,
                        offset,
                        bytes.Length - offset);
                    if (read == 0)
                    {
                        Array.Clear(bytes, 0, bytes.Length);
                        throw new EndOfStreamException(
                            "CA repair backup ended unexpectedly.");
                    }

                    offset += read;
                }

                return bytes;
            }
        }

        private static bool IsRepairableInstalledStateFailure(
            Exception exception)
        {
            return exception is InvalidDataException
                || exception is FileNotFoundException
                || exception is EndOfStreamException
                || exception is CryptographicException
                || exception is SecurityException
                || exception is UnauthorizedAccessException;
        }
    }
}
