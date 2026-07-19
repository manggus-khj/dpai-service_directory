using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerConfigurationTransactionStoreTests
    {
        private static readonly Guid LocalInstanceId = new Guid(
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77");
        private static readonly Guid PeerInstanceId = new Guid(
            "9f2ed127-9834-42b4-a379-eaad9df8fcec");
        private static readonly Guid PairingId = new Guid(
            "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12");
        private static readonly DateTime CommitExpiresUtc = new DateTime(
            2026,
            7,
            19,
            2,
            0,
            0,
            DateTimeKind.Utc);

        [TestMethod]
        public void StoreCommitsPendingCompletionAndForgetAsAtomicPairs()
        {
            string root = CreateInitializedStateDirectory();
            try
            {
                var protector = new CloneProtector();
                var accessPolicy = new NoOpAccessPolicy();
                using (var store = new PeerConfigurationTransactionStore(
                    root,
                    protector,
                    accessPolicy))
                using (PeerConfigurationSnapshot initial = store.Load())
                using (PairedPeerCredential pendingCredential =
                    CreateCredential(
                        DurablePeerCredentialState.PairedPendingCommit,
                        true,
                        false))
                using (PeerConfigurationSnapshot pending = store.Commit(
                    initial,
                    CreatePendingConfiguration(
                        initial.Configuration,
                        true,
                        false),
                    pendingCredential))
                {
                    Assert.AreEqual(
                        DurableSynchronizationState.PairedPendingCommit,
                        pending.Configuration.Synchronization.State);
                    Assert.IsNotNull(pending.Credential);
                    Assert.IsTrue(File.Exists(PeerPath(root)));
                    Assert.IsFalse(File.Exists(PeerPath(root) + ".bak"));

                    using (PairedPeerCredential completedCredential =
                        CreateCredential(
                            DurablePeerCredentialState.PairedDisabled,
                            true,
                            true))
                    using (PeerConfigurationSnapshot completed = store.Commit(
                        pending,
                        CreatePairedDisabledConfiguration(
                            pending.Configuration),
                        completedCredential))
                    {
                        Assert.AreEqual(
                            DurableSynchronizationState.PairedDisabled,
                            completed.Configuration.Synchronization.State);
                        Assert.IsTrue(
                            completed.Credential.LocalCommitConfirmed);
                        Assert.IsTrue(
                            completed.Credential.RemoteCommitConfirmed);

                        using (PeerConfigurationSnapshot forgotten =
                            store.Commit(
                                completed,
                                CreateUnpairedConfiguration(
                                    completed.Configuration),
                                null))
                        {
                            Assert.AreEqual(
                                DurableSynchronizationState.Unpaired,
                                forgotten.Configuration.Synchronization.State);
                            Assert.AreEqual(
                                42UL,
                                forgotten.Configuration.LastPeerKeyEpoch);
                            Assert.IsNull(forgotten.Credential);
                            Assert.IsFalse(File.Exists(PeerPath(root)));
                            Assert.IsFalse(
                                File.Exists(PeerPath(root) + ".bak"));
                        }
                    }
                }

                AssertJournalIsEmpty(root);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void PreparedPairCommitRollsBackConfigAndSecretTogether()
        {
            string root = CreateInitializedStateDirectory();
            try
            {
                var protector = new CloneProtector();
                var accessPolicy = new NoOpAccessPolicy();
                var fault = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.TargetApplied,
                    StateFileTarget.PeerSecret);
                using (var interrupted =
                    new PeerConfigurationTransactionStore(
                        root,
                        protector,
                        accessPolicy,
                        fault))
                using (PeerConfigurationSnapshot initial = interrupted.Load())
                using (PairedPeerCredential pendingCredential =
                    CreateCredential(
                        DurablePeerCredentialState.PairedPendingCommit,
                        false,
                        false))
                {
                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => interrupted.Commit(
                            initial,
                            CreatePendingConfiguration(
                                initial.Configuration,
                                false,
                                false),
                            pendingCredential));
                }

                using (var recovered = new PeerConfigurationTransactionStore(
                    root,
                    protector,
                    accessPolicy))
                using (PeerConfigurationSnapshot state = recovered.Load())
                {
                    Assert.AreEqual(
                        DurableSynchronizationState.Unpaired,
                        state.Configuration.Synchronization.State);
                    Assert.AreEqual(
                        0UL,
                        state.Configuration.LastPeerKeyEpoch);
                    Assert.IsNull(state.Credential);
                    Assert.IsFalse(File.Exists(PeerPath(root)));
                    Assert.IsFalse(File.Exists(PeerPath(root) + ".bak"));
                }

                AssertJournalIsEmpty(root);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void CommittedPairCommitRollsForwardConfigAndSecretTogether()
        {
            string root = CreateInitializedStateDirectory();
            try
            {
                var protector = new CloneProtector();
                var accessPolicy = new NoOpAccessPolicy();
                var fault = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.CommittedFlushed,
                    null);
                using (var interrupted =
                    new PeerConfigurationTransactionStore(
                        root,
                        protector,
                        accessPolicy,
                        fault))
                using (PeerConfigurationSnapshot initial = interrupted.Load())
                using (PairedPeerCredential pendingCredential =
                    CreateCredential(
                        DurablePeerCredentialState.PairedPendingCommit,
                        false,
                        false))
                {
                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => interrupted.Commit(
                            initial,
                            CreatePendingConfiguration(
                                initial.Configuration,
                                false,
                                false),
                            pendingCredential));
                }

                using (var recovered = new PeerConfigurationTransactionStore(
                    root,
                    protector,
                    accessPolicy))
                using (PeerConfigurationSnapshot state = recovered.Load())
                {
                    Assert.AreEqual(
                        DurableSynchronizationState.PairedPendingCommit,
                        state.Configuration.Synchronization.State);
                    Assert.AreEqual(
                        42UL,
                        state.Configuration.LastPeerKeyEpoch);
                    Assert.IsNotNull(state.Credential);
                    Assert.AreEqual(PairingId, state.Credential.PairingId);
                    Assert.IsTrue(File.Exists(PeerPath(root)));
                    Assert.IsFalse(File.Exists(PeerPath(root) + ".bak"));
                }

                AssertJournalIsEmpty(root);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void StoreRejectsStaleSnapshotAndExternalMutation()
        {
            string root = CreateInitializedStateDirectory();
            try
            {
                var protector = new CloneProtector();
                var accessPolicy = new NoOpAccessPolicy();
                using (var store = new PeerConfigurationTransactionStore(
                    root,
                    protector,
                    accessPolicy))
                using (PeerConfigurationSnapshot first = store.Load())
                using (PeerConfigurationSnapshot second = store.Commit(
                    first,
                    first.Configuration.WithLogRetentionDays(31),
                    null))
                {
                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => store.Commit(
                            first,
                            first.Configuration.WithLogRetentionDays(32),
                            null));

                    var codec = new StateXmlCodec();
                    File.WriteAllBytes(
                        Path.Combine(root, "config.xml"),
                        codec.SerializeConfiguration(
                            second.Configuration.WithLogRetentionDays(32)));
                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => store.Commit(
                            second,
                            second.Configuration.WithLogRetentionDays(33),
                            null));
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void PeerCommitRebasesOnLatestAdminLogRetention()
        {
            string root = CreateInitializedStateDirectory();
            try
            {
                var store = new PeerConfigurationTransactionStore(
                    root,
                    new CloneProtector(),
                    new NoOpAccessPolicy());
                using (var state =
                    new ServiceDirectoryRuntimeConfigurationState(store))
                {
                    ServiceDirectoryConfiguration stalePeerBaseline =
                        state.GetCurrent();
                    Assert.IsTrue(
                        state.SetLogRetentionDays(1095).IsSuccess);

                    RuntimeConfigurationCommitResult peerCommit =
                        state.CommitPeerState(
                            stalePeerBaseline.WithSynchronization(
                                stalePeerBaseline.LastPeerKeyEpoch,
                                stalePeerBaseline.Synchronization),
                            null);

                    Assert.AreEqual(
                        RuntimeConfigurationCommitStatus.Completed,
                        peerCommit.Status);
                    Assert.AreEqual(
                        1095,
                        state.GetCurrent().LogRetentionDays);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateInitializedStateDirectory()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "dpai-peer-transaction-tests-"
                    + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "secrets"));
            var configurationStore =
                new XmlServiceDirectoryConfigurationStore(root);
            Assert.AreEqual(
                ConfigurationLoadFailureCode.Missing,
                configurationStore.Load().FailureCode);
            Assert.IsTrue(configurationStore.Initialize(
                ServiceDirectoryConfiguration.CreateInitial(
                    "10.0.0.1",
                    LocalInstanceId)).IsSuccess);
            return root;
        }

        private static ServiceDirectoryConfiguration
            CreatePendingConfiguration(
                ServiceDirectoryConfiguration current,
                bool localConfirmed,
                bool remoteConfirmed)
        {
            return current.WithSynchronization(
                42,
                SynchronizationConfiguration.PairedPendingCommit(
                    "http://10.0.0.2:21000",
                    PeerInstanceId,
                    42,
                    PairingId,
                    CommitExpiresUtc,
                    localConfirmed,
                    remoteConfirmed,
                    current.Synchronization.LastSynchronization,
                    current.Synchronization.LastPeerNotification));
        }

        private static ServiceDirectoryConfiguration
            CreatePairedDisabledConfiguration(
                ServiceDirectoryConfiguration current)
        {
            return current.WithSynchronization(
                42,
                SynchronizationConfiguration.PairedDisabled(
                    "http://10.0.0.2:21000",
                    PeerInstanceId,
                    42,
                    current.Synchronization.LastSynchronization,
                    current.Synchronization.LastPeerNotification));
        }

        private static ServiceDirectoryConfiguration
            CreateUnpairedConfiguration(
                ServiceDirectoryConfiguration current)
        {
            return current.WithSynchronization(
                current.LastPeerKeyEpoch,
                SynchronizationConfiguration.Unpaired(
                    current.Synchronization.LastSynchronization,
                    current.Synchronization.LastPeerNotification));
        }

        private static PairedPeerCredential CreateCredential(
            DurablePeerCredentialState state,
            bool localConfirmed,
            bool remoteConfirmed)
        {
            PairingCommitEvidence local = localConfirmed
                ? CreateEvidence(1)
                : null;
            PairingCommitEvidence remote = remoteConfirmed
                ? CreateEvidence(101)
                : null;
            try
            {
                return new PairedPeerCredential(
                    state,
                    PairingRole.Initiator,
                    PairingId,
                    LocalInstanceId,
                    PeerInstanceId,
                    "http://10.0.0.1:21000",
                    "http://10.0.0.2:21000",
                    42,
                    Bytes(32, 32),
                    Bytes(64, 32),
                    CommitExpiresUtc,
                    localConfirmed,
                    remoteConfirmed,
                    local,
                    remote);
            }
            finally
            {
                if (local != null)
                {
                    local.Dispose();
                }

                if (remote != null)
                {
                    remote.Dispose();
                }
            }
        }

        private static PairingCommitEvidence CreateEvidence(int seed)
        {
            return new PairingCommitEvidence(
                Bytes(seed, 32),
                200,
                Encoding.UTF8.GetBytes("<Response />"),
                Bytes(seed + 32, 32));
        }

        private static byte[] Bytes(int seed, int count)
        {
            var result = new byte[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = (byte)(seed + index);
            }

            return result;
        }

        private static string PeerPath(string root)
        {
            return Path.Combine(root, "secrets", "peer.dat");
        }

        private static void AssertJournalIsEmpty(string root)
        {
            string journal = Path.Combine(root, "journal");
            Assert.IsTrue(Directory.Exists(journal));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(journal).Length);
        }

        private sealed class CloneProtector : IPeerCredentialProtector
        {
            public byte[] Protect(byte[] plaintext)
            {
                return (byte[])plaintext.Clone();
            }

            public byte[] Unprotect(byte[] protectedBytes)
            {
                return (byte[])protectedBytes.Clone();
            }
        }

        private sealed class NoOpAccessPolicy : IPeerSecretAccessPolicy
        {
            public void ProtectExistingFile(string path)
            {
                ValidateExistingFile(path);
            }

            public void ValidateExistingFile(string path)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        "Expected test peer credential is missing.",
                        path);
                }
            }
        }

        private sealed class ThrowOnceFaultInjector
            : IRecoveryJournalFaultInjector
        {
            private readonly RecoveryJournalFaultPoint _faultPoint;
            private readonly StateFileTarget? _target;
            private bool _thrown;

            internal ThrowOnceFaultInjector(
                RecoveryJournalFaultPoint faultPoint,
                StateFileTarget? target)
            {
                _faultPoint = faultPoint;
                _target = target;
            }

            public void OnFault(
                RecoveryJournalFaultPoint faultPoint,
                StateFileTarget? target)
            {
                if (!_thrown
                    && faultPoint == _faultPoint
                    && target == _target)
                {
                    _thrown = true;
                    throw new IOException(
                        "Injected peer configuration interruption.");
                }
            }
        }
    }
}
