using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CertificateAuthorityStoreSnapshot : IDisposable
    {
        private bool _disposed;

        internal CertificateAuthorityStoreSnapshot(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] metadataBytes,
            byte[] ledgerBytes,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            MetadataBytes = CloneRequired(metadataBytes, nameof(metadataBytes));
            LedgerBytes = CloneRequired(ledgerBytes, nameof(ledgerBytes));
            CaCertificateDer = CloneRequired(
                caCertificateDer,
                nameof(caCertificateDer));
            CrlDer = CloneRequired(crlDer, nameof(crlDer));
            ProtectedPrivateKey = CloneRequired(
                protectedPrivateKey,
                nameof(protectedPrivateKey));
        }

        internal CertificateAuthorityState State { get; }

        internal CertificateLedgerSnapshot Ledger { get; }

        internal byte[] MetadataBytes { get; private set; }

        internal byte[] LedgerBytes { get; private set; }

        internal byte[] CaCertificateDer { get; private set; }

        internal byte[] CrlDer { get; private set; }

        internal byte[] ProtectedPrivateKey { get; private set; }

        internal CertificateAuthorityStoreSnapshot Clone()
        {
            ThrowIfDisposed();
            return new CertificateAuthorityStoreSnapshot(
                State,
                Ledger,
                MetadataBytes,
                LedgerBytes,
                CaCertificateDer,
                CrlDer,
                ProtectedPrivateKey);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Clear(MetadataBytes);
            Clear(LedgerBytes);
            Clear(CaCertificateDer);
            Clear(CrlDer);
            Clear(ProtectedPrivateKey);
            MetadataBytes = null;
            LedgerBytes = null;
            CaCertificateDer = null;
            CrlDer = null;
            ProtectedPrivateKey = null;
            _disposed = true;
        }

        private static byte[] CloneRequired(byte[] value, string name)
        {
            if (value == null || value.Length == 0)
            {
                throw new ArgumentException(
                    "PKI state bytes must not be empty.",
                    name);
            }

            return (byte[])value.Clone();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(CertificateAuthorityStoreSnapshot));
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

    internal sealed partial class CertificateAuthorityStore : IDisposable
    {
        internal const int MaximumCertificateBytes = 128 * 1024;
        internal const int MaximumCrlBytes = 16 * 1024 * 1024;
        internal const int CrlValidityDays = 7;

        private readonly object _lifecycleGate = new object();
        private readonly StateMutationGate _mutationGate;
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly AtomicFileWriter _fileWriter;
        private readonly RecoveryJournalManager _journalManager;
        private readonly CertificateAuthorityStateCodec _codec;
        private readonly ICaPrivateKeyProtector _protector;
        private readonly IPeerSecretAccessPolicy _accessPolicy;
        private CertificateAuthorityStoreSnapshot _current;
        private bool _recoveryRequired;
        private bool _disposed;

        internal CertificateAuthorityStore(
            string stateDirectoryPath,
            StateMutationGate mutationGate)
            : this(
                new StateStoragePathPolicy(stateDirectoryPath),
                mutationGate,
                new DpapiMachineCaPrivateKeyProtector(),
                PeerSecretAccessPolicy.ForInstalledMainService(),
                NoOpRecoveryJournalFaultInjector.Instance)
        {
        }

        internal CertificateAuthorityStore(
            StateStoragePathPolicy pathPolicy,
            StateMutationGate mutationGate,
            ICaPrivateKeyProtector protector,
            IPeerSecretAccessPolicy accessPolicy,
            IRecoveryJournalFaultInjector faultInjector)
        {
            _pathPolicy = pathPolicy
                ?? throw new ArgumentNullException(nameof(pathPolicy));
            _mutationGate = mutationGate
                ?? throw new ArgumentNullException(nameof(mutationGate));
            _protector = protector
                ?? throw new ArgumentNullException(nameof(protector));
            if (accessPolicy == null)
            {
                throw new ArgumentNullException(nameof(accessPolicy));
            }

            _accessPolicy = accessPolicy;

            _fileWriter = new AtomicFileWriter(pathPolicy, accessPolicy);
            _journalManager = new RecoveryJournalManager(
                pathPolicy,
                _fileWriter,
                faultInjector ?? NoOpRecoveryJournalFaultInjector.Instance);
            _codec = new CertificateAuthorityStateCodec();
        }

        internal bool TryLoad()
        {
            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    DisposeCurrent();
                    try
                    {
                        _journalManager.Recover(
                            ValidateRecoveryTargets,
                            ValidateAllInstalledState);
                        _current = ReadCurrent(true);
                        return _current != null;
                    }
                    catch
                    {
                        _recoveryRequired = true;
                        throw;
                    }
                }
            });
        }

        internal CertificateAuthorityStoreSnapshot GetCurrent()
        {
            lock (_lifecycleGate)
            {
                ThrowIfAvailable();
                return _current.Clone();
            }
        }


        public void Dispose()
        {
            _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    DisposeCurrent();
                    _disposed = true;
                }
            });
        }

        private void CommitReplacement(
            CertificateAuthorityStoreSnapshot current,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey)
        {
            CommitReplacement(
                current,
                null,
                nextState,
                nextLedger,
                caCertificateDer,
                crlDer,
                protectedPrivateKey);
        }

        private void CommitReplacement(
            CertificateAuthorityStoreSnapshot current,
            byte[][] repairBeforeImages,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey)
        {
            ValidateStateAndLedger(nextState, nextLedger);
            byte[] metadata = _codec.SerializeState(nextState);
            byte[] ledger = _codec.SerializeLedger(nextLedger);
            try
            {
                IReadOnlyList<StateFileChange> changes = BuildChanges(
                    current,
                    repairBeforeImages,
                    metadata,
                    ledger,
                    caCertificateDer,
                    crlDer,
                    protectedPrivateKey);
                CertificateAuthorityStoreSnapshot applied = null;
                _journalManager.Commit(
                    changes,
                    () => applied = ReadCurrent(false));
                ReplaceCurrent(applied);
            }
            finally
            {
                Clear(metadata);
                Clear(ledger);
            }
        }

        private CertificateAuthorityStoreSnapshot ReadCurrent(
            bool allowMissing)
        {
            string pkiDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "pki");
            if (!Directory.Exists(pkiDirectory))
            {
                string caKeyPath = _pathPolicy.GetTargetPath(
                    StateFileTarget.CaPrivateKey);
                _pathPolicy.EnsureExistingFileIsSafe(caKeyPath);
                _pathPolicy.EnsureExistingFileIsSafe(caKeyPath + ".bak");
                if (File.Exists(caKeyPath)
                    || File.Exists(caKeyPath + ".bak")
                    || HasCaBackupArtifacts())
                {
                    throw new InvalidDataException(
                        "CA artifacts exist without the PKI state directory.");
                }

                if (allowMissing)
                {
                    return null;
                }

                throw new FileNotFoundException(
                    "Provisioned PKI state is missing.");
            }

            _pathPolicy.EnsureDirectoryIsSafe(pkiDirectory);
            StateFileTarget[] targets = GetPkiTargets();
            bool[] exists = targets.Select(_fileWriter.Exists).ToArray();
            int existingCount = exists.Count(value => value);
            if (existingCount == 0)
            {
                if (targets.Any(_fileWriter.BackupExists)
                    || HasCaBackupArtifacts())
                {
                    throw new InvalidDataException(
                        "PKI backup artifacts exist without primary PKI state.");
                }

                if (!allowMissing)
                {
                    throw new FileNotFoundException(
                        "Provisioned PKI state is missing.");
                }

                return null;
            }

            if (existingCount != targets.Length)
            {
                throw new InvalidDataException(
                    "Provisioned PKI state is incomplete.");
            }

            if (_fileWriter.BackupExists(StateFileTarget.CaPrivateKey))
            {
                throw new InvalidDataException(
                    "ca.key.bak is forbidden.");
            }

            byte[] metadata = null;
            byte[] ledgerBytes = null;
            byte[] certificate = null;
            byte[] crl = null;
            byte[] protectedKey = null;
            byte[] privateKey = null;
            try
            {
                metadata = _fileWriter.Read(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                ledgerBytes = _fileWriter.Read(
                    StateFileTarget.CertificateLedger,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                certificate = _fileWriter.Read(
                    StateFileTarget.CaCertificate,
                    MaximumCertificateBytes);
                crl = _fileWriter.Read(
                    StateFileTarget.CertificateRevocationList,
                    MaximumCrlBytes);
                protectedKey = _fileWriter.Read(
                    StateFileTarget.CaPrivateKey,
                    DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes);
                _accessPolicy.ValidateExistingFile(
                    _pathPolicy.GetTargetPath(
                        StateFileTarget.CaPrivateKey));
                privateKey = _protector.Unprotect(protectedKey);

                CertificateAuthorityState state =
                    _codec.DeserializeState(metadata);
                CertificateLedgerSnapshot ledger =
                    _codec.DeserializeLedger(ledgerBytes);
                ValidateStateAndLedger(state, ledger);
                ValidateAuthority(
                    state,
                    certificate,
                    privateKey,
                    DateTime.UtcNow);
                ValidateCrl(state, ledger, certificate, crl);
                return new CertificateAuthorityStoreSnapshot(
                    state,
                    ledger,
                    metadata,
                    ledgerBytes,
                    certificate,
                    crl,
                    protectedKey);
            }
            finally
            {
                Clear(metadata);
                Clear(ledgerBytes);
                Clear(certificate);
                Clear(crl);
                Clear(protectedKey);
                Clear(privateKey);
            }
        }

        private static void ValidateAuthority(
            CertificateAuthorityState state,
            byte[] certificateDer,
            byte[] privateKeyPkcs8,
            DateTime utcNow)
        {
            SiteCertificateAuthority authority =
                SiteCertificateAuthority.Restore(
                    state.SiteId,
                    certificateDer,
                    privateKeyPkcs8,
                    utcNow);
            byte[] spki = authority.GetSpkiSha256();
            byte[] expectedSpki = state.GetCaSpkiSha256();
            try
            {
                if (authority.SerialNumber != state.CaSerialNumber
                    || authority.NotBeforeUtc != state.NotBeforeUtc
                    || authority.NotAfterUtc != state.NotAfterUtc
                    || !FixedTimeEquals(spki, expectedSpki))
                {
                    throw new InvalidDataException(
                        "CA certificate does not match its persisted metadata.");
                }
            }
            finally
            {
                Clear(spki);
                Clear(expectedSpki);
            }
        }

        private static void ValidateCrl(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] caCertificateDer,
            byte[] crlDer)
        {
            var certificate = new X509Certificate(caCertificateDer);
            var parser = new X509CrlParser();
            X509Crl crl;
            try
            {
                crl = parser.ReadCrl(crlDer);
                crl.Verify(certificate.GetPublicKey());
            }
            catch (Exception exception)
                when (exception is GeneralSecurityException
                    || exception is IOException
                    || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    "Signed CRL validation failed.",
                    exception);
            }

            if (!crl.IssuerDN.Equivalent(certificate.SubjectDN))
            {
                throw new InvalidDataException(
                    "CRL issuer does not match the site CA.");
            }

            DateTime thisUpdateUtc = crl.ThisUpdate.ToUniversalTime();
            DateTime? nextUpdateUtc = crl.NextUpdate.HasValue
                ? crl.NextUpdate.Value.ToUniversalTime()
                : (DateTime?)null;
            if (thisUpdateUtc < state.NotBeforeUtc
                || thisUpdateUtc > state.NotAfterUtc
                || !nextUpdateUtc.HasValue
                || nextUpdateUtc.Value <= thisUpdateUtc
                || nextUpdateUtc.Value > state.NotAfterUtc)
            {
                throw new InvalidDataException(
                    "CRL validity is outside the site CA validity period.");
            }

            Asn1OctetString extension = crl.GetExtensionValue(
                X509Extensions.CrlNumber);
            if (extension == null)
            {
                throw new InvalidDataException("CRL number is missing.");
            }

            DerInteger number = DerInteger.GetInstance(
                Asn1Object.FromByteArray(extension.GetOctets()));
            if (number.PositiveValue.SignValue <= 0
                || !ulong.TryParse(
                    number.PositiveValue.ToString(),
                    out ulong parsedNumber)
                || parsedNumber != state.CrlNumber
                || parsedNumber != ledger.CrlNumber)
            {
                throw new InvalidDataException(
                    "CRL number does not match persisted high-water state.");
            }

            ISet<X509CrlEntry> revokedCertificates =
                crl.GetRevokedCertificates();
            int expectedRevokedCount = ledger.EntriesBySerial.Values.Count(
                entry => entry.Status == CertificateLedgerStatus.Revoked);
            int actualRevokedCount = revokedCertificates == null
                ? 0
                : revokedCertificates.Count;
            if (actualRevokedCount != expectedRevokedCount)
            {
                throw new InvalidDataException(
                    "CRL contains entries that do not match the certificate ledger.");
            }

            foreach (CertificateLedgerEntry entry in ledger
                .EntriesBySerial.Values)
            {
                PkiSerialNumber serial;
                if (!PkiSerialNumber.TryParse(
                        entry.SerialNumber.Hex,
                        out serial))
                {
                    throw new InvalidDataException(
                        "Ledger contains a non-canonical serial.");
                }

                X509CrlEntry revokedEntry =
                    crl.GetRevokedCertificate(serial.Value);
                bool listed = revokedEntry != null;
                if (listed
                    != (entry.Status == CertificateLedgerStatus.Revoked))
                {
                    throw new InvalidDataException(
                        "CRL membership does not match the certificate ledger.");
                }

                if (!listed)
                {
                    continue;
                }

                if (!entry.RevokedUtc.HasValue
                    || !entry.RevocationReason.HasValue
                    || !AreEqualToAsn1Second(
                        revokedEntry.RevocationDate,
                        entry.RevokedUtc.Value))
                {
                    throw new InvalidDataException(
                        "CRL revocation time does not match the certificate ledger.");
                }

                Asn1OctetString reasonExtension =
                    revokedEntry.GetExtensionValue(X509Extensions.ReasonCode);
                if (reasonExtension == null)
                {
                    throw new InvalidDataException(
                        "CRL revocation reason is missing.");
                }

                DerEnumerated reason;
                try
                {
                    reason = DerEnumerated.GetInstance(
                        Asn1Object.FromByteArray(
                            reasonExtension.GetOctets()));
                }
                catch (Exception exception)
                    when (exception is ArgumentException
                        || exception is IOException)
                {
                    throw new InvalidDataException(
                        "CRL revocation reason is invalid.",
                        exception);
                }

                if (reason.IntValueExact
                    != (int)entry.RevocationReason.Value)
                {
                    throw new InvalidDataException(
                        "CRL revocation reason does not match the certificate ledger.");
                }
            }
        }

        private static bool AreEqualToAsn1Second(
            DateTime left,
            DateTime right)
        {
            long leftTicks = left.ToUniversalTime().Ticks
                / TimeSpan.TicksPerSecond;
            long rightTicks = right.ToUniversalTime().Ticks
                / TimeSpan.TicksPerSecond;
            return leftTicks == rightTicks;
        }

        private static void ValidateStateAndLedger(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger)
        {
            if (state.PkiRevision != ledger.PkiRevision
                || state.CrlNumber != ledger.CrlNumber)
            {
                throw new InvalidDataException(
                    "CA metadata and certificate ledger high-water values differ.");
            }
        }

        private void ValidateAllInstalledState()
        {
            PeerConfigurationTransactionStore
                .ValidateInstalledNonPkiStateFiles(
                    _pathPolicy,
                    _accessPolicy);
            using (CertificateAuthorityStoreSnapshot state =
                ReadCurrent(true))
            {
            }
        }

        internal static void ValidateInstalledStateFiles(
            StateStoragePathPolicy pathPolicy,
            IPeerSecretAccessPolicy accessPolicy)
        {
            var store = new CertificateAuthorityStore(
                pathPolicy,
                new StateMutationGate(),
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            try
            {
                using (CertificateAuthorityStoreSnapshot state =
                    store.ReadCurrent(true))
                {
                }
            }
            finally
            {
                store.Dispose();
            }
        }

        private static void ValidateRecoveryTargets(
            IReadOnlyList<StateFileTarget> targets)
        {
            foreach (StateFileTarget target in targets)
            {
                StateFileTargets.Get(target);
            }
        }

        private static IReadOnlyList<StateFileChange> BuildChanges(
            CertificateAuthorityStoreSnapshot current,
            byte[][] repairBeforeImages,
            byte[] metadata,
            byte[] ledger,
            byte[] certificate,
            byte[] crl,
            byte[] protectedKey)
        {
            byte[][] next =
            {
                metadata,
                ledger,
                crl,
                certificate,
                protectedKey
            };
            StateFileTarget[] targets = GetPkiTargets();
            byte[][] previous = current != null
                ? new[]
                {
                    current.MetadataBytes,
                    current.LedgerBytes,
                    current.CrlDer,
                    current.CaCertificateDer,
                    current.ProtectedPrivateKey
                }
                : repairBeforeImages ?? new byte[targets.Length][];
            if (previous.Length != targets.Length)
            {
                throw new ArgumentException(
                    "Repair before images do not match the PKI targets.",
                    nameof(repairBeforeImages));
            }
            var changes = new List<StateFileChange>(targets.Length);
            for (int index = 0; index < targets.Length; index++)
            {
                if (!ByteArraysEqual(previous[index], next[index]))
                {
                    changes.Add(new StateFileChange(
                        targets[index],
                        previous[index] != null,
                        previous[index],
                        true,
                        next[index]));
                }
            }

            if (changes.Count == 0)
            {
                throw new InvalidOperationException(
                    "PKI replacement contains no state change.");
            }

            return changes.AsReadOnly();
        }

        private byte[][] ReadRepairBeforeImages()
        {
            StateFileTarget[] targets = GetPkiTargets();
            int[] maximumBytes =
            {
                CertificateAuthorityStateCodec.MaximumDocumentBytes,
                CertificateAuthorityStateCodec.MaximumDocumentBytes,
                MaximumCrlBytes,
                MaximumCertificateBytes,
                DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes
            };
            var images = new byte[targets.Length][];
            try
            {
                for (int index = 0; index < targets.Length; index++)
                {
                    if (!_fileWriter.Exists(targets[index]))
                    {
                        continue;
                    }

                    // Repair may be required precisely because the secret
                    // file's DACL is no longer canonical.  The elevated,
                    // offline repair process captures any safely addressed
                    // readable bytes as the journal before-image, then the
                    // normal secret writer restores the exact DACL on the
                    // replacement.
                    images[index] = _fileWriter.Read(
                        targets[index],
                        maximumBytes[index]);
                }

                return images;
            }
            catch
            {
                ClearImages(images);
                throw;
            }
        }

        private bool HasCaBackupArtifacts()
        {
            string backupDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "backups",
                "ca");
            if (!Directory.Exists(backupDirectory))
            {
                return false;
            }

            _pathPolicy.EnsureDirectoryIsSafe(backupDirectory);
            return Directory.EnumerateFiles(
                    backupDirectory,
                    "site-ca-*.dpca",
                    SearchOption.TopDirectoryOnly)
                .Any();
        }

        private static void ClearImages(byte[][] images)
        {
            if (images == null)
            {
                return;
            }

            for (int index = 0; index < images.Length; index++)
            {
                Clear(images[index]);
            }
        }

        private static IEnumerable<RevokedCertificateEntry>
            CreateRevokedEntries(CertificateLedgerSnapshot ledger)
        {
            foreach (CertificateLedgerEntry entry in ledger
                .EntriesBySerial
                .Values
                .Where(item =>
                    item.Status == CertificateLedgerStatus.Revoked))
            {
                PkiSerialNumber serial;
                if (!PkiSerialNumber.TryParse(
                        entry.SerialNumber.Hex,
                        out serial)
                    || !entry.RevokedUtc.HasValue
                    || !entry.RevocationReason.HasValue)
                {
                    throw new InvalidDataException(
                        "Revoked ledger entry is incomplete.");
                }

                yield return new RevokedCertificateEntry(
                    serial,
                    entry.RevokedUtc.Value,
                    entry.RevocationReason.Value);
            }
        }

        private static bool IsOperatorReason(
            CertificateRevocationReason reason)
        {
            return reason == CertificateRevocationReason.KeyCompromise
                || reason == CertificateRevocationReason.CaCompromise
                || reason == CertificateRevocationReason.AffiliationChanged
                || reason == CertificateRevocationReason.PrivilegeWithdrawn
                || reason == CertificateRevocationReason.AaCompromise;
        }

        private void EnsurePkiDirectories()
        {
            string pkiDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "pki");
            string secretsDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "secrets");
            Directory.CreateDirectory(pkiDirectory);
            Directory.CreateDirectory(secretsDirectory);
            _pathPolicy.EnsureDirectoryIsSafe(pkiDirectory);
            _pathPolicy.EnsureDirectoryIsSafe(secretsDirectory);
        }

        private bool AnyTargetExists()
        {
            return GetPkiTargets().Any(_fileWriter.Exists);
        }

        private static StateFileTarget[] GetPkiTargets()
        {
            return new[]
            {
                StateFileTarget.PkiMetadata,
                StateFileTarget.CertificateLedger,
                StateFileTarget.CertificateRevocationList,
                StateFileTarget.CaCertificate,
                StateFileTarget.CaPrivateKey
            };
        }

        private static DateTime GetNextCrlUpdate(
            DateTime thisUpdateUtc,
            DateTime caNotAfterUtc)
        {
            DateTime candidate = thisUpdateUtc.AddDays(CrlValidityDays);
            if (candidate > caNotAfterUtc)
            {
                candidate = caNotAfterUtc;
            }

            if (candidate <= thisUpdateUtc)
            {
                throw new InvalidOperationException(
                    "The site CA expires before a new CRL can be published.");
            }

            return candidate;
        }

        private void ReplaceCurrent(
            CertificateAuthorityStoreSnapshot next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            CertificateAuthorityStoreSnapshot previous = _current;
            _current = next;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void DisposeCurrent()
        {
            if (_current != null)
            {
                _current.Dispose();
                _current = null;
            }
        }

        private void ThrowIfAvailable()
        {
            ThrowIfDisposed();
            if (_recoveryRequired)
            {
                throw new RecoveryRequiredException(
                    "PKI state requires repair recovery.",
                    null);
            }

            if (_current == null)
            {
                throw new InvalidOperationException(
                    "PKI state is not provisioned.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(CertificateAuthorityStore));
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

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

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            return ByteArraysEqual(left, right);
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "PKI timestamps must use DateTimeKind.Utc.",
                    parameterName);
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
