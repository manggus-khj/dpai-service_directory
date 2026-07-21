using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public sealed class CertificateAuthorityStandbyRepairResult
    {
        internal CertificateAuthorityStandbyRepairResult(
            DirectoryCertificateInstallationResult directoryCertificate,
            CertificateAuthorityBackupResult backup,
            bool promoted)
        {
            DirectoryCertificate = directoryCertificate
                ?? throw new ArgumentNullException(
                    nameof(directoryCertificate));
            Backup = backup
                ?? throw new ArgumentNullException(nameof(backup));
            Promoted = promoted;
        }

        public DirectoryCertificateInstallationResult DirectoryCertificate
        {
            get;
        }

        public CertificateAuthorityBackupResult Backup { get; }

        public bool Promoted { get; }
    }

    public static class CertificateAuthorityRepair
    {
        private const int MaximumConfigurationBytes = 16 * 1024 * 1024;

        public static Guid ReadInstalledInstanceId(
            string stateDirectoryPath)
        {
            return ReadInstalledConfiguration(stateDirectoryPath).InstanceId;
        }

        public static DirectoryCertificateInstallationResult
            InstallDirectoryCertificate(
                string stateDirectoryPath,
                DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Directory certificate installation time must be UTC.",
                    nameof(utcNow));
            }

            ServiceDirectoryConfiguration configuration =
                ReadInstalledConfiguration(stateDirectoryPath);
            if (TryReadInstalledRole(stateDirectoryPath)
                == CertificateAuthorityRole.Standby)
            {
                ServiceDirectoryListenerAddress listenerAddress;
                if (!ServiceDirectoryListenerAddress.TryCreate(
                    configuration.ListenAddress,
                    out listenerAddress))
                {
                    throw new InvalidDataException(
                        "The standby listener address is invalid.");
                }

                DirectoryHttpsEndpointValidator.Validate(
                    stateDirectoryPath,
                    configuration,
                    listenerAddress,
                    utcNow);
                HttpSysSslBinding binding = HttpSysSslBindingReader.Read(
                    IPAddress.Parse(listenerAddress.CanonicalAddress),
                    ServiceDirectoryListenerAddress.Port);
                var reader = new AtomicFileWriter(
                    new StateStoragePathPolicy(stateDirectoryPath));
                byte[] standbyCa = reader.Read(
                    StateFileTarget.CaCertificate,
                    CertificateAuthorityStore.MaximumCertificateBytes);
                try
                {
                    return DirectoryCertificateStore.ReadInstalled(
                        binding.GetThumbprint(),
                        standbyCa,
                        configuration.DirectoryEndpointIdentity,
                        utcNow);
                }
                finally
                {
                    Clear(standbyCa);
                }
            }

            var pathPolicy = new StateStoragePathPolicy(stateDirectoryPath);
            var accessPolicy =
                PeerSecretAccessPolicy.ForInstalledMainService();
            var store = new CertificateAuthorityStore(
                pathPolicy,
                new StateMutationGate(),
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            CertificateAuthorityStoreSnapshot snapshot = null;
            IssuedCertificateArtifact issued = null;
            byte[] privateKey = null;
            try
            {
                if (!store.TryLoad())
                {
                    throw new InvalidOperationException(
                        "PKI state is not provisioned.");
                }

                snapshot = store.GetCurrent();
                if (snapshot.State.Role
                        != CertificateAuthorityRole.ActiveIssuer
                    || !snapshot.State.IsCurrentRevisionBackedUp)
                {
                    throw new InvalidOperationException(
                        "The active site CA and a backup of its current trust revision are required before Directory leaf issuance.");
                }

                var protector = new DpapiMachineCaPrivateKeyProtector();
                privateKey = protector.Unprotect(
                    snapshot.ProtectedPrivateKey);
                SiteCertificateAuthority authority =
                    SiteCertificateAuthority.Restore(
                        snapshot.State.SiteId,
                        snapshot.CaCertificateDer,
                        privateKey,
                        utcNow);
                var random = new SecureRandom();
                PkiSerialNumber serialNumber = PkiSerialNumber.CreateRandom(
                    random,
                    value => StringComparer.Ordinal.Equals(
                            value,
                            snapshot.State.CaSerialNumber.Hex)
                        || snapshot.Ledger.EntriesBySerial.Keys.Any(
                            serial => StringComparer.Ordinal.Equals(
                                serial.Hex,
                                value)));
                issued = authority.CreateDirectoryLeaf(
                    configuration.DirectoryEndpointIdentity,
                    serialNumber,
                    utcNow,
                    random);
                return DirectoryCertificateStore.Install(
                    issued,
                    snapshot.CaCertificateDer,
                    configuration.DirectoryEndpointIdentity,
                    utcNow);
            }
            finally
            {
                if (issued != null)
                {
                    issued.Dispose();
                }

                if (snapshot != null)
                {
                    snapshot.Dispose();
                }

                if (privateKey != null)
                {
                    Array.Clear(privateKey, 0, privateKey.Length);
                }

                store.Dispose();
            }
        }

        public static void RemoveDirectoryCertificate(
            string stateDirectoryPath,
            string thumbprint,
            DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Directory certificate removal time must be UTC.",
                    nameof(utcNow));
            }

            ServiceDirectoryConfiguration configuration =
                ReadInstalledConfiguration(stateDirectoryPath);
            var pathPolicy = new StateStoragePathPolicy(
                stateDirectoryPath);
            var writer = new AtomicFileWriter(pathPolicy);
            byte[] caCertificate = null;
            try
            {
                caCertificate = writer.Read(
                    StateFileTarget.CaCertificate,
                    CertificateAuthorityStore.MaximumCertificateBytes);
                DirectoryCertificateStore.Remove(
                    thumbprint,
                    caCertificate,
                    configuration.DirectoryEndpointIdentity,
                    utcNow);
            }
            finally
            {
                Clear(caCertificate);
            }
        }

        public static CertificateAuthorityStandbyRepairResult
            ConfigureStandbyFromEncryptedBackup(
                string stateDirectoryPath,
                Guid installedInstanceId,
                string backupPath,
                string password,
                DateTime utcNow)
        {
            return ChangeStandbyRole(
                stateDirectoryPath,
                installedInstanceId,
                backupPath,
                password,
                utcNow,
                false);
        }

        public static CertificateAuthorityStandbyRepairResult
            PromoteStandbyFromEncryptedBackup(
                string stateDirectoryPath,
                Guid installedInstanceId,
                string backupPath,
                string password,
                DateTime utcNow)
        {
            return ChangeStandbyRole(
                stateDirectoryPath,
                installedInstanceId,
                backupPath,
                password,
                utcNow,
                true);
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
            CertificateAuthorityRole? installedRole =
                TryReadInstalledRole(stateDirectoryPath);
            if (installedRole == CertificateAuthorityRole.Standby
                || new AtomicFileWriter(new StateStoragePathPolicy(
                    stateDirectoryPath)).Exists(
                        StateFileTarget.PeerPkiCache))
            {
                throw new InvalidOperationException(
                    "Standby state requires the explicit standby promotion repair command.");
            }

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

        private static CertificateAuthorityStandbyRepairResult
            ChangeStandbyRole(
                string stateDirectoryPath,
                Guid installedInstanceId,
                string backupPath,
                string password,
                DateTime utcNow,
                bool promote)
        {
            ValidateCommon(
                stateDirectoryPath,
                installedInstanceId,
                password,
                utcNow);
            ServiceDirectoryConfiguration configuration =
                ReadInstalledConfiguration(stateDirectoryPath);
            if (configuration.InstanceId != installedInstanceId)
            {
                throw new InvalidDataException(
                    "The installed instance ID differs from config.xml.");
            }

            string fullBackupPath = ValidateBackupPath(backupPath);
            byte[] encrypted = ReadLimited(
                fullBackupPath,
                CaBackupCodec.MaximumBackupBytes);
            CaBackupPayload payload = null;
            CertificateAuthorityRoleRepairStateResult stateResult = null;
            try
            {
                payload = new CaBackupCodec().Decrypt(
                    encrypted,
                    password);
                var pathPolicy = new StateStoragePathPolicy(
                    stateDirectoryPath);
                var accessPolicy =
                    PeerSecretAccessPolicy.ForInstalledMainService();
                var roleStore = new CertificateAuthorityRoleRepairStore(
                    pathPolicy,
                    new StateMutationGate(),
                    new DpapiMachineCaPrivateKeyProtector(),
                    accessPolicy,
                    NoOpRecoveryJournalFaultInjector.Instance);
                stateResult = promote
                    ? roleStore.PromoteStandby(
                        payload,
                        password,
                        installedInstanceId,
                        configuration.DirectoryEndpointIdentity,
                        utcNow)
                    : roleStore.ConfigureStandby(
                        payload,
                        encrypted,
                        configuration.DirectoryEndpointIdentity,
                        utcNow);
                DirectoryCertificateInstallationResult certificate =
                    DirectoryCertificateStore.Install(
                        stateResult.DirectoryCertificate,
                        payload.CaCertificateDer,
                        configuration.DirectoryEndpointIdentity,
                        utcNow);
                CertificateAuthorityBackupResult backup =
                    CreateBackupResult(stateResult.BackupArtifact);
                return new CertificateAuthorityStandbyRepairResult(
                    certificate,
                    backup,
                    stateResult.Promoted);
            }
            finally
            {
                if (stateResult != null)
                {
                    stateResult.Dispose();
                }

                if (payload != null)
                {
                    payload.Dispose();
                }

                Clear(encrypted);
            }
        }

        private static CertificateAuthorityBackupResult CreateBackupResult(
            CaBackupFileArtifact artifact)
        {
            byte[] hash = artifact.GetSha256();
            try
            {
                return new CertificateAuthorityBackupResult(
                    artifact.FileName,
                    artifact.CreatedUtc,
                    hash);
            }
            finally
            {
                Clear(hash);
            }
        }

        private static CertificateAuthorityRole? TryReadInstalledRole(
            string stateDirectoryPath)
        {
            var writer = new AtomicFileWriter(
                new StateStoragePathPolicy(stateDirectoryPath));
            byte[] metadata = null;
            try
            {
                if (!writer.Exists(StateFileTarget.PkiMetadata))
                {
                    return null;
                }

                metadata = writer.Read(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                return new CertificateAuthorityStateCodec()
                    .DeserializeState(metadata)
                    .Role;
            }
            catch (Exception exception) when (
                IsRepairableInstalledStateFailure(exception))
            {
                return null;
            }
            finally
            {
                Clear(metadata);
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

        private static ServiceDirectoryConfiguration
            ReadInstalledConfiguration(string stateDirectoryPath)
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
                    .DeserializeConfiguration(configurationBytes);
            }
            finally
            {
                Array.Clear(
                    configurationBytes,
                    0,
                    configurationBytes.Length);
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
