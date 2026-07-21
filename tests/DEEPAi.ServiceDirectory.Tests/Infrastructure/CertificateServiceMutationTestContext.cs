using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    internal sealed class CertificateServiceMutationCandidate : IDisposable
    {
        internal CertificateServiceMutationCandidate(
            CertificateServiceMutationOperation operation,
            DirectorySnapshot expectedDirectory,
            DirectorySnapshot nextDirectory,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] nextCrlDer,
            CertificateIssuanceRequestEvidence evidence,
            CertificateSerialNumber serialNumber)
        {
            Operation = operation;
            ExpectedDirectory = expectedDirectory;
            NextDirectory = nextDirectory;
            NextState = nextState;
            NextLedger = nextLedger;
            NextCrlDer = (byte[])nextCrlDer.Clone();
            Evidence = evidence;
            SerialNumber = serialNumber;
        }

        internal CertificateServiceMutationOperation Operation { get; }

        internal DirectorySnapshot ExpectedDirectory { get; }

        internal DirectorySnapshot NextDirectory { get; }

        internal CertificateAuthorityState NextState { get; }

        internal CertificateLedgerSnapshot NextLedger { get; }

        internal byte[] NextCrlDer { get; private set; }

        internal CertificateIssuanceRequestEvidence Evidence { get; }

        internal CertificateSerialNumber SerialNumber { get; }

        public void Dispose()
        {
            if (NextCrlDer != null)
            {
                Array.Clear(NextCrlDer, 0, NextCrlDer.Length);
                NextCrlDer = null;
            }
        }
    }

    internal sealed class RecordingRecoveryFaultInjector
        : IRecoveryJournalFaultInjector
    {
        private readonly List<StateFileTarget> _appliedTargets =
            new List<StateFileTarget>();
        private RecoveryJournalFaultPoint? _armedPoint;
        private StateFileTarget? _armedTarget;

        internal IReadOnlyList<StateFileTarget> AppliedTargets =>
            _appliedTargets.AsReadOnly();

        internal void Clear()
        {
            _appliedTargets.Clear();
            _armedPoint = null;
            _armedTarget = null;
        }

        internal void Arm(
            RecoveryJournalFaultPoint point,
            StateFileTarget? target = null)
        {
            _armedPoint = point;
            _armedTarget = target;
        }

        public void OnFault(
            RecoveryJournalFaultPoint faultPoint,
            StateFileTarget? target)
        {
            if (faultPoint == RecoveryJournalFaultPoint.TargetApplied
                && target.HasValue)
            {
                _appliedTargets.Add(target.Value);
            }

            if (_armedPoint == faultPoint
                && (!_armedTarget.HasValue
                    || _armedTarget == target))
            {
                _armedPoint = null;
                _armedTarget = null;
                throw new IOException(
                    "Injected recovery journal fault: " + faultPoint + ".");
            }
        }
    }

    internal sealed partial class CertificateServiceMutationTestContext
        : IDisposable
    {
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly DpapiMachineCaPrivateKeyProtector _protector;
        private readonly NoOpSecretAccessPolicy _accessPolicy;
        private CertificateAuthorityStore _store;

        private CertificateServiceMutationTestContext(
            string rootPath,
            StateStoragePathPolicy pathPolicy,
            StateMutationGate mutationGate,
            StateMutationCoordinator directoryState,
            CertificateAuthorityStore store,
            DpapiMachineCaPrivateKeyProtector protector,
            NoOpSecretAccessPolicy accessPolicy,
            RecordingRecoveryFaultInjector faultInjector,
            RecordingCertificateServiceMutationFaultInjector
                serviceFaultInjector,
            Guid instanceId,
            DirectoryEndpointIdentity directoryIdentity)
        {
            RootPath = rootPath;
            _pathPolicy = pathPolicy;
            MutationGate = mutationGate;
            DirectoryState = directoryState;
            _store = store;
            _protector = protector;
            _accessPolicy = accessPolicy;
            FaultInjector = faultInjector;
            ServiceFaultInjector = serviceFaultInjector;
            InstanceId = instanceId;
            DirectoryIdentity = directoryIdentity;
        }

        internal string RootPath { get; }

        internal StateMutationGate MutationGate { get; }

        internal StateMutationCoordinator DirectoryState { get; }

        internal RecordingRecoveryFaultInjector FaultInjector { get; }

        internal RecordingCertificateServiceMutationFaultInjector
            ServiceFaultInjector { get; }

        internal Guid InstanceId { get; }

        internal DirectoryEndpointIdentity DirectoryIdentity { get; }

        internal CertificateAuthorityStore Store => _store;

        internal static CertificateServiceMutationTestContext Create()
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "DEEPAi.ServiceDirectory.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            try
            {
                var pathPolicy = new StateStoragePathPolicy(rootPath);
                var writer = new AtomicFileWriter(pathPolicy);
                writer.Write(
                    StateFileTarget.Directory,
                    new StateXmlCodec().SerializeDirectory(
                        DirectorySnapshot.Empty()));

                Guid instanceId = Guid.NewGuid();
                DirectoryEndpointIdentity directoryIdentity =
                    PkiTestData.DirectoryIdentity();
                var configurationStore =
                    new XmlServiceDirectoryConfigurationStore(rootPath);
                configurationStore.Load();
                if (!configurationStore.Initialize(
                        ServiceDirectoryConfiguration.CreateInitial(
                            directoryIdentity,
                            instanceId)).IsSuccess)
                {
                    throw new InvalidOperationException(
                        "The test config.xml could not be initialized.");
                }

                var mutationGate = new StateMutationGate();
                var stateStore = new XmlServiceDirectoryStateStore(rootPath);
                StateCoordinatorOpenResult opened =
                    StateMutationCoordinator.Open(stateStore, mutationGate);
                if (!opened.IsSuccess)
                {
                    throw new InvalidOperationException(
                        "The test directory state could not be opened.");
                }

                var protector = new DpapiMachineCaPrivateKeyProtector();
                var accessPolicy = new NoOpSecretAccessPolicy();
                var injector = new RecordingRecoveryFaultInjector();
                var serviceFaultInjector =
                    new RecordingCertificateServiceMutationFaultInjector();
                var store = new CertificateAuthorityStore(
                    pathPolicy,
                    mutationGate,
                    protector,
                    accessPolicy,
                    injector,
                    serviceFaultInjector);
                store.Provision(instanceId, TestData.Utc(0));
                if (!store.MarkBackupCompleted(1, 1, 1, TestData.Utc(1)))
                {
                    store.Dispose();
                    throw new InvalidOperationException(
                        "The test CA backup marker could not be committed.");
                }

                injector.Clear();
                return new CertificateServiceMutationTestContext(
                    rootPath,
                    pathPolicy,
                    mutationGate,
                    opened.Coordinator,
                    store,
                    protector,
                    accessPolicy,
                    injector,
                    serviceFaultInjector,
                    instanceId,
                    directoryIdentity);
            }
            catch
            {
                Directory.Delete(rootPath, true);
                throw;
            }
        }

        internal CertificateServiceMutationCandidate PrepareSerialRevocation()
        {
            DirectorySnapshot directory = DirectoryState.CurrentSnapshot;
            using (CertificateAuthorityStoreSnapshot current =
                _store.GetCurrent())
            {
                CertificateLedgerEntry selected = current.Ledger
                    .EntriesBySerial.Values.Single(
                        entry => entry.Status
                            == CertificateLedgerStatus.Current);
                DateTime revokedUtc = TestData.Utc(15);
                CertificateLedgerEntry[] entries = current.Ledger
                    .EntriesBySerial.Values
                    .Select(entry => entry.SerialNumber
                            == selected.SerialNumber
                        ? entry.Revoke(
                            revokedUtc,
                            CertificateRevocationReason.KeyCompromise)
                        : entry)
                    .ToArray();
                var nextLedger = new CertificateLedgerSnapshot(
                    entries,
                    current.State.PkiRevision + 1,
                    current.State.CrlNumber + 1);
                CertificateAuthorityState nextState =
                    current.State.WithHighWater(
                        nextLedger.PkiRevision,
                        nextLedger.CrlNumber);
                byte[] crlDer = CreateCrl(
                    current,
                    nextLedger,
                    revokedUtc);
                return new CertificateServiceMutationCandidate(
                    CertificateServiceMutationOperation.SerialRevocation,
                    directory,
                    directory,
                    nextState,
                    nextLedger,
                    crlDer,
                    null,
                    selected.SerialNumber);
            }
        }

        internal CertificateAuthorityStoreSnapshot Commit(
            CertificateServiceMutationCandidate candidate)
        {
            return _store.CommitServiceMutation(
                DirectoryState,
                candidate.Operation,
                candidate.ExpectedDirectory,
                candidate.NextDirectory,
                candidate.NextState,
                candidate.NextLedger,
                candidate.NextCrlDer,
                TestData.Utc(16));
        }

        internal void RecoverAfterInjectedFailure()
        {
            var writer = new AtomicFileWriter(_pathPolicy, _accessPolicy);
            var manager = new RecoveryJournalManager(
                _pathPolicy,
                writer,
                NoOpRecoveryJournalFaultInjector.Instance);
            manager.Recover(
                targets =>
                {
                    foreach (StateFileTarget target in targets)
                    {
                        StateFileTargets.Get(target);
                    }
                },
                () => ValidateRecoveredState(writer));

            StateLoadResult directoryRecovery = DirectoryState.Recover();
            if (!directoryRecovery.IsSuccess)
            {
                throw new InvalidDataException(
                    "The recovered directory state could not be published.");
            }

            _store.Dispose();
            _store = new CertificateAuthorityStore(
                _pathPolicy,
                MutationGate,
                _protector,
                _accessPolicy,
                FaultInjector,
                ServiceFaultInjector);
            if (!_store.TryLoad())
            {
                throw new InvalidDataException(
                    "The recovered CA state could not be loaded.");
            }
        }

        public void Dispose()
        {
            if (_store != null)
            {
                _store.Dispose();
                _store = null;
            }

            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, true);
            }
        }

        private byte[] CreateCrl(
            CertificateAuthorityStoreSnapshot current,
            CertificateLedgerSnapshot ledger,
            DateTime utcNow)
        {
            byte[] privateKey = null;
            try
            {
                privateKey = _protector.Unprotect(
                    current.ProtectedPrivateKey);
                SiteCertificateAuthority authority =
                    SiteCertificateAuthority.Restore(
                        current.State.SiteId,
                        current.CaCertificateDer,
                        privateKey,
                        utcNow);
                var revoked = new List<RevokedCertificateEntry>();
                foreach (CertificateLedgerEntry entry in
                    ledger.EntriesBySerial.Values)
                {
                    if (entry.Status != CertificateLedgerStatus.Revoked)
                    {
                        continue;
                    }

                    if (!PkiSerialNumber.TryParse(
                            entry.SerialNumber.Hex,
                            out PkiSerialNumber serial))
                    {
                        throw new InvalidDataException(
                            "The test ledger contains an invalid serial.");
                    }

                    revoked.Add(new RevokedCertificateEntry(
                        serial,
                        entry.RevokedUtc.Value,
                        entry.RevocationReason.Value));
                }

                DateTime nextUpdateUtc = utcNow.AddDays(
                    CertificateAuthorityStore.CrlValidityDays);
                if (nextUpdateUtc > authority.NotAfterUtc)
                {
                    nextUpdateUtc = authority.NotAfterUtc;
                }

                return authority.CreateRevocationList(
                        ledger.CrlNumber,
                        revoked,
                        utcNow,
                        nextUpdateUtc,
                        new SecureRandom())
                    .GetDerBytes();
            }
            finally
            {
                Clear(privateKey);
            }
        }

        private void ValidateRecoveredState(AtomicFileWriter writer)
        {
            var stateCodec = new StateXmlCodec();
            DirectorySnapshot directory = stateCodec.DeserializeSnapshot(
                writer.Read(
                    StateFileTarget.Directory,
                    RecoveryJournalManager.MaximumImageBytes));
            var caCodec = new CertificateAuthorityStateCodec();
            CertificateAuthorityState state = caCodec.DeserializeState(
                writer.Read(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes));
            CertificateLedgerSnapshot ledger = caCodec.DeserializeLedger(
                writer.Read(
                    StateFileTarget.CertificateLedger,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes),
                state.CrlNumber);
            byte[] caCertificate = null;
            byte[] crl = null;
            byte[] protectedKey = null;
            byte[] privateKey = null;
            try
            {
                caCertificate = writer.Read(
                    StateFileTarget.CaCertificate,
                    CertificateAuthorityStore.MaximumCertificateBytes);
                crl = writer.Read(
                    StateFileTarget.CertificateRevocationList,
                    CertificateAuthorityStore.MaximumCrlBytes);
                protectedKey = writer.Read(
                    StateFileTarget.CaPrivateKey,
                    DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes);
                privateKey = _protector.Unprotect(protectedKey);
                CertificateAuthorityStore.ValidateAuthority(
                    state,
                    caCertificate,
                    privateKey,
                    TestData.Utc(16));
                CertificateAuthorityStore.ValidateCrl(
                    state,
                    ledger,
                    caCertificate,
                    crl);
                StorageV1ConsistencyValidator
                    .ValidateActiveDirectoryAndLedger(directory, ledger);
            }
            finally
            {
                Clear(caCertificate);
                Clear(crl);
                Clear(protectedKey);
                Clear(privateKey);
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private sealed class NoOpSecretAccessPolicy
            : IPeerSecretAccessPolicy
        {
            public void ProtectExistingFile(string path)
            {
            }

            public void ValidateExistingFile(string path)
            {
            }
        }
    }
}
