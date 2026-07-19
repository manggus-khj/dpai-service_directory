using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CaBackupFileArtifact
    {
        private readonly byte[] _sha256;

        internal CaBackupFileArtifact(
            string fileName,
            DateTime createdUtc,
            byte[] sha256)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || Path.GetFileName(fileName) != fileName)
            {
                throw new ArgumentException(
                    "CA backup file name must not contain a path.",
                    nameof(fileName));
            }

            if (createdUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA backup creation time must be UTC.",
                    nameof(createdUtc));
            }

            if (sha256 == null || sha256.Length != 32)
            {
                throw new ArgumentException(
                    "CA backup SHA-256 must contain exactly 32 bytes.",
                    nameof(sha256));
            }

            FileName = fileName;
            CreatedUtc = createdUtc;
            _sha256 = (byte[])sha256.Clone();
        }

        internal string FileName { get; }

        internal DateTime CreatedUtc { get; }

        internal byte[] GetSha256()
        {
            return (byte[])_sha256.Clone();
        }
    }

    internal sealed class CaBackupFileStore
    {
        private const string BackupDirectoryRelativePath = @"backups\ca";

        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly ISecretFileAccessPolicy _accessPolicy;

        internal CaBackupFileStore(
            string stateDirectoryPath,
            ISecretFileAccessPolicy accessPolicy)
            : this(
                new StateStoragePathPolicy(stateDirectoryPath),
                accessPolicy)
        {
        }

        internal CaBackupFileStore(
            StateStoragePathPolicy pathPolicy,
            ISecretFileAccessPolicy accessPolicy)
        {
            _pathPolicy = pathPolicy
                ?? throw new ArgumentNullException(nameof(pathPolicy));
            _accessPolicy = accessPolicy
                ?? throw new ArgumentNullException(nameof(accessPolicy));
        }

        internal CaBackupFileArtifact WriteNew(
            Guid siteId,
            DateTime createdUtc,
            byte[] encryptedBackup)
        {
            if (siteId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Site ID must not be empty.",
                    nameof(siteId));
            }

            if (createdUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA backup creation time must be UTC.",
                    nameof(createdUtc));
            }

            if (encryptedBackup == null
                || encryptedBackup.Length == 0
                || encryptedBackup.Length > CaBackupCodec.MaximumBackupBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(encryptedBackup));
            }

            string directoryPath = GetBackupDirectoryPath();
            EnsureBackupDirectory(directoryPath);
            string fileName = "site-ca-"
                + siteId.ToString("D").ToLowerInvariant()
                + "-"
                + createdUtc.ToString(
                    "yyyyMMdd'T'HHmmssfff'Z'",
                    CultureInfo.InvariantCulture)
                + ".dpca";
            string destinationPath = Path.Combine(
                directoryPath,
                fileName);
            _pathPolicy.EnsurePathIsInsideStateDirectory(destinationPath);
            _pathPolicy.EnsureExistingFileIsSafe(destinationPath);
            if (File.Exists(destinationPath))
            {
                throw new IOException(
                    "A CA backup with the same canonical name already exists.");
            }

            string temporaryPath = destinationPath
                + "."
                + Guid.NewGuid().ToString("N")
                + ".tmp";
            bool temporaryExists = false;
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    temporaryExists = true;
                    _accessPolicy.ProtectExistingFile(temporaryPath);
                    stream.Write(
                        encryptedBackup,
                        0,
                        encryptedBackup.Length);
                    stream.Flush(true);
                }

                WindowsFileSystem.MoveWriteThrough(
                    temporaryPath,
                    destinationPath,
                    false);
                temporaryExists = false;
                _accessPolicy.ValidateExistingFile(destinationPath);

                byte[] hash = null;
                using (SHA256 sha256 = SHA256.Create())
                {
                    hash = sha256.ComputeHash(encryptedBackup);
                }
                try
                {
                    return new CaBackupFileArtifact(
                        fileName,
                        createdUtc,
                        hash);
                }
                finally
                {
                    Array.Clear(hash, 0, hash.Length);
                }
            }
            finally
            {
                if (temporaryExists && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        internal void DeleteUnapproved(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || Path.GetFileName(fileName) != fileName)
            {
                throw new ArgumentException(
                    "CA backup file name must not contain a path.",
                    nameof(fileName));
            }

            string path = Path.Combine(GetBackupDirectoryPath(), fileName);
            _pathPolicy.EnsurePathIsInsideStateDirectory(path);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        internal string ResolveExistingForRepair(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)
                || Path.GetFileName(fileName) != fileName
                || !fileName.EndsWith(".dpca", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "CA backup file name is invalid.",
                    nameof(fileName));
            }

            string directoryPath = GetBackupDirectoryPath();
            _pathPolicy.EnsureDirectoryIsSafe(directoryPath);
            string path = Path.Combine(directoryPath, fileName);
            _pathPolicy.EnsurePathIsInsideStateDirectory(path);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            _accessPolicy.ValidateExistingFile(path);
            return path;
        }

        private string GetBackupDirectoryPath()
        {
            string path = Path.GetFullPath(Path.Combine(
                _pathPolicy.StateDirectoryPath,
                BackupDirectoryRelativePath));
            _pathPolicy.EnsurePathIsInsideStateDirectory(path);
            return path;
        }

        private void EnsureBackupDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            _pathPolicy.EnsureDirectoryIsSafe(path);
        }
    }
}
