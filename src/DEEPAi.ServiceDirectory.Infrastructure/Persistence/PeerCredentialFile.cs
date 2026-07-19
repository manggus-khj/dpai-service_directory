using System;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class PeerCredentialFile
    {
        internal const int MaximumProtectedBytes = 128 * 1024;

        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly AtomicFileWriter _fileWriter;
        private readonly PeerCredentialBinaryCodec _codec;
        private readonly IPeerCredentialProtector _protector;
        private readonly IPeerSecretAccessPolicy _accessPolicy;

        internal PeerCredentialFile(
            string stateDirectoryPath,
            IPeerCredentialProtector protector,
            IPeerSecretAccessPolicy accessPolicy)
            : this(
                new StateStoragePathPolicy(stateDirectoryPath),
                protector,
                accessPolicy)
        {
        }

        internal PeerCredentialFile(
            StateStoragePathPolicy pathPolicy,
            IPeerCredentialProtector protector,
            IPeerSecretAccessPolicy accessPolicy)
        {
            if (pathPolicy == null)
            {
                throw new ArgumentNullException(nameof(pathPolicy));
            }

            if (protector == null)
            {
                throw new ArgumentNullException(nameof(protector));
            }

            if (accessPolicy == null)
            {
                throw new ArgumentNullException(nameof(accessPolicy));
            }

            _pathPolicy = pathPolicy;
            _fileWriter = new AtomicFileWriter(pathPolicy);
            _codec = new PeerCredentialBinaryCodec();
            _protector = protector;
            _accessPolicy = accessPolicy;
        }

        internal bool Exists()
        {
            EnsureNoBackupCredential();
            return _fileWriter.Exists(StateFileTarget.PeerSecret);
        }

        internal PairedPeerCredential ReadExisting()
        {
            byte[] protectedBytes = null;
            try
            {
                protectedBytes = ReadExistingProtectedBytes();
                return DecodeProtected(protectedBytes);
            }
            finally
            {
                Clear(protectedBytes);
            }
        }

        internal byte[] ReadExistingProtectedBytes()
        {
            EnsureNoBackupCredential();
            if (!_fileWriter.Exists(StateFileTarget.PeerSecret))
            {
                throw new FileNotFoundException(
                    "The protected peer credential is missing.",
                    _pathPolicy.GetTargetPath(StateFileTarget.PeerSecret));
            }

            string path = _pathPolicy.GetTargetPath(
                StateFileTarget.PeerSecret);
            _accessPolicy.ValidateExistingFile(path);
            return _fileWriter.Read(
                StateFileTarget.PeerSecret,
                MaximumProtectedBytes);
        }

        internal byte[] EncodeProtected(PairedPeerCredential credential)
        {
            byte[] plaintext = null;
            try
            {
                plaintext = _codec.Serialize(credential);
                byte[] protectedBytes = _protector.Protect(plaintext);
                if (protectedBytes == null
                    || protectedBytes.Length == 0
                    || protectedBytes.Length > MaximumProtectedBytes)
                {
                    Clear(protectedBytes);
                    throw new InvalidDataException(
                        "The protected peer credential has an invalid size.");
                }

                return protectedBytes;
            }
            finally
            {
                Clear(plaintext);
            }
        }

        internal PairedPeerCredential DecodeProtected(
            byte[] protectedBytes)
        {
            if (protectedBytes == null)
            {
                throw new ArgumentNullException(nameof(protectedBytes));
            }

            if (protectedBytes.Length == 0
                || protectedBytes.Length > MaximumProtectedBytes)
            {
                throw new InvalidDataException(
                    "The protected peer credential has an invalid size.");
            }

            byte[] plaintext = null;
            try
            {
                plaintext = _protector.Unprotect(protectedBytes);
                return _codec.Deserialize(plaintext);
            }
            finally
            {
                Clear(plaintext);
            }
        }

        internal void ValidateWrittenFile()
        {
            EnsureNoBackupCredential();
            string path = _pathPolicy.GetTargetPath(
                StateFileTarget.PeerSecret);
            _accessPolicy.ValidateExistingFile(path);
            using (PairedPeerCredential credential = ReadExisting())
            {
                // Successful read is the validation result. The credential is
                // disposed here so this boundary never becomes its runtime owner.
            }
        }

        private void EnsureNoBackupCredential()
        {
            if (_fileWriter.BackupExists(StateFileTarget.PeerSecret))
            {
                throw new InvalidDataException(
                    "A peer.dat.bak credential is forbidden.");
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
