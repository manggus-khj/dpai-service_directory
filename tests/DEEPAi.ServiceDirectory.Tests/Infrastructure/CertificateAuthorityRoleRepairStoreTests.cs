using System;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateAuthorityRoleRepairStoreTests
    {
        private const string Password =
            "standby repair test password";

        [TestMethod]
        public void ConfigureStandbyKeepsOnlyPublicPkiState()
        {
            using (CertificateServiceMutationTestContext source =
                CertificateServiceMutationTestContext.Create())
            using (CaBackupPayload payload = CaptureBackup(
                source,
                TestData.Utc(2)))
            using (TargetState target = TargetState.Create())
            {
                byte[] encrypted = new CaBackupCodec().Encrypt(
                    payload,
                    Password);
                try
                {
                    using (CertificateAuthorityRoleRepairStateResult result =
                        target.Store.ConfigureStandby(
                            payload,
                            encrypted,
                            target.DirectoryIdentity,
                            TestData.Utc(2)))
                    {
                        byte[] leaf = result.DirectoryCertificate
                            .GetCertificateDer();
                        try
                        {
                            SiteCertificateAuthority.ValidateDirectoryLeaf(
                                leaf,
                                payload.CaCertificateDer,
                                target.DirectoryIdentity,
                                TestData.Utc(2));
                        }
                        finally
                        {
                            Array.Clear(leaf, 0, leaf.Length);
                        }

                        Assert.IsFalse(result.Promoted);
                        Assert.IsTrue(File.Exists(Path.Combine(
                            target.RootPath,
                            "backups",
                            "ca",
                            result.BackupArtifact.FileName)));
                    }
                }
                finally
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }

                CollectionAssert.AreEqual(
                    new[]
                    {
                        StateFileTarget.PkiMetadata,
                        StateFileTarget.PeerPkiCache,
                        StateFileTarget.CertificateRevocationList,
                        StateFileTarget.CaCertificate
                    },
                    target.FaultInjector.AppliedTargets.ToArray());
                AssertRoleFiles(
                    target.PathPolicy,
                    CertificateAuthorityRole.Standby);

                var peerStore = new PeerPkiSynchronizationStore(
                    target.RootPath,
                    target.MutationGate);
                CertificateAuthorityStatus status =
                    peerStore.GetStandbyStatus(TestData.Utc(2));
                Assert.AreEqual(
                    CertificateAuthorityOperationalState.Ready,
                    status.State);
                Assert.AreEqual(
                    CertificateAuthorityIssuerRole.Standby,
                    status.Role);
                Assert.AreEqual(source.InstanceId, status.IssuerInstanceId);
            }
        }

        [TestMethod]
        public void PromoteStandbyAtomicallyChangesRoleAndCreatesNewBackup()
        {
            using (CertificateServiceMutationTestContext source =
                CertificateServiceMutationTestContext.Create())
            using (CaBackupPayload payload = CaptureBackup(
                source,
                TestData.Utc(2)))
            using (TargetState target = TargetState.Create())
            {
                byte[] encrypted = new CaBackupCodec().Encrypt(
                    payload,
                    Password);
                try
                {
                    using (target.Store.ConfigureStandby(
                        payload,
                        encrypted,
                        target.DirectoryIdentity,
                        TestData.Utc(2)))
                    {
                    }
                }
                finally
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }

                target.FaultInjector.Clear();
                using (CertificateAuthorityRoleRepairStateResult result =
                    target.Store.PromoteStandby(
                        payload,
                        Password,
                        target.InstanceId,
                        target.DirectoryIdentity,
                        TestData.Utc(3)))
                {
                    Assert.IsTrue(result.Promoted);
                    Assert.IsTrue(File.Exists(Path.Combine(
                        target.RootPath,
                        "backups",
                        "ca",
                        result.BackupArtifact.FileName)));
                }

                CollectionAssert.AreEqual(
                    new[]
                    {
                        StateFileTarget.PkiMetadata,
                        StateFileTarget.CertificateLedger,
                        StateFileTarget.PeerPkiCache,
                        StateFileTarget.CaPrivateKey
                    },
                    target.FaultInjector.AppliedTargets.ToArray());
                AssertRoleFiles(
                    target.PathPolicy,
                    CertificateAuthorityRole.ActiveIssuer);
                byte[] metadata = target.Writer.Read(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                try
                {
                    CertificateAuthorityState state =
                        new CertificateAuthorityStateCodec()
                            .DeserializeState(metadata);
                    Assert.AreEqual(target.InstanceId,
                        state.IssuerInstanceId);
                    Assert.AreEqual(
                        CertificateAuthorityRole.ActiveIssuer,
                        state.Role);
                    Assert.AreEqual(
                        2UL,
                        state.PkiRevision);
                    Assert.AreEqual(TestData.Utc(3), state.LastBackupUtc);
                }
                finally
                {
                    Array.Clear(metadata, 0, metadata.Length);
                }
            }
        }

        [TestMethod]
        public void PromotionRejectsBackupBelowObservedHighWater()
        {
            using (CertificateServiceMutationTestContext source =
                CertificateServiceMutationTestContext.Create())
            using (CaBackupPayload older = CaptureBackup(
                source,
                TestData.Utc(2)))
            using (TargetState target = TargetState.Create())
            {
                using (CertificateServiceMutationCandidate registration =
                    source.PrepareRegistration())
                using (CertificateAuthorityStoreSnapshot unused =
                    source.Commit(registration))
                {
                }

                using (CaBackupPayload newer = CaptureBackup(
                    source,
                    TestData.Utc(4)))
                {
                    byte[] encrypted = new CaBackupCodec().Encrypt(
                        newer,
                        Password);
                    try
                    {
                        using (target.Store.ConfigureStandby(
                            newer,
                            encrypted,
                            target.DirectoryIdentity,
                            TestData.Utc(4)))
                        {
                        }
                    }
                    finally
                    {
                        Array.Clear(encrypted, 0, encrypted.Length);
                    }
                }

                Assert.ThrowsExactly<InvalidDataException>(() =>
                    target.Store.PromoteStandby(
                        older,
                        Password,
                        target.InstanceId,
                        target.DirectoryIdentity,
                        TestData.Utc(5)));
                AssertRoleFiles(
                    target.PathPolicy,
                    CertificateAuthorityRole.Standby);
            }
        }

        private static CaBackupPayload CaptureBackup(
            CertificateServiceMutationTestContext source,
            DateTime utcNow)
        {
            ulong trustRevision;
            ulong pkiRevision;
            ulong crlNumber;
            return source.Store.CaptureBackupPayload(
                utcNow,
                out trustRevision,
                out pkiRevision,
                out crlNumber);
        }

        private static void AssertRoleFiles(
            StateStoragePathPolicy pathPolicy,
            CertificateAuthorityRole role)
        {
            bool active = role == CertificateAuthorityRole.ActiveIssuer;
            Assert.AreEqual(
                active,
                File.Exists(pathPolicy.GetTargetPath(
                    StateFileTarget.CertificateLedger)));
            Assert.AreEqual(
                !active,
                File.Exists(pathPolicy.GetTargetPath(
                    StateFileTarget.PeerPkiCache)));
            Assert.AreEqual(
                active,
                File.Exists(pathPolicy.GetTargetPath(
                    StateFileTarget.CaPrivateKey)));
        }

        private sealed class TargetState : IDisposable
        {
            private TargetState(
                string rootPath,
                StateStoragePathPolicy pathPolicy,
                StateMutationGate mutationGate,
                RecordingRecoveryFaultInjector faultInjector,
                CertificateAuthorityRoleRepairStore store,
                Guid instanceId,
                DirectoryEndpointIdentity directoryIdentity,
                AtomicFileWriter writer)
            {
                RootPath = rootPath;
                PathPolicy = pathPolicy;
                MutationGate = mutationGate;
                FaultInjector = faultInjector;
                Store = store;
                InstanceId = instanceId;
                DirectoryIdentity = directoryIdentity;
                Writer = writer;
            }

            internal string RootPath { get; }

            internal StateStoragePathPolicy PathPolicy { get; }

            internal StateMutationGate MutationGate { get; }

            internal RecordingRecoveryFaultInjector FaultInjector
            {
                get;
            }

            internal CertificateAuthorityRoleRepairStore Store { get; }

            internal Guid InstanceId { get; }

            internal DirectoryEndpointIdentity DirectoryIdentity { get; }

            internal AtomicFileWriter Writer { get; }

            internal static TargetState Create()
            {
                string rootPath = Path.Combine(
                    Path.GetTempPath(),
                    "DEEPAi.ServiceDirectory.Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(rootPath);
                try
                {
                    Directory.CreateDirectory(Path.Combine(rootPath, "pki"));
                    Directory.CreateDirectory(Path.Combine(rootPath, "secrets"));
                    var pathPolicy = new StateStoragePathPolicy(rootPath);
                    var writer = new AtomicFileWriter(pathPolicy);
                    writer.Write(
                        StateFileTarget.Directory,
                        new StateXmlCodec().SerializeDirectory(
                            DirectorySnapshot.Empty()));
                    Guid instanceId = Guid.NewGuid();
                    DirectoryEndpointIdentity identity =
                        PkiTestData.DirectoryIdentity();
                    var configurationStore =
                        new XmlServiceDirectoryConfigurationStore(rootPath);
                    configurationStore.Load();
                    Assert.IsTrue(configurationStore.Initialize(
                        ServiceDirectoryConfiguration.CreateInitial(
                            identity,
                            instanceId)).IsSuccess);
                    var mutationGate = new StateMutationGate();
                    var faultInjector =
                        new RecordingRecoveryFaultInjector();
                    var accessPolicy = new NoOpSecretAccessPolicy();
                    var store = new CertificateAuthorityRoleRepairStore(
                        pathPolicy,
                        mutationGate,
                        new DpapiMachineCaPrivateKeyProtector(),
                        accessPolicy,
                        faultInjector);
                    return new TargetState(
                        rootPath,
                        pathPolicy,
                        mutationGate,
                        faultInjector,
                        store,
                        instanceId,
                        identity,
                        writer);
                }
                catch
                {
                    Directory.Delete(rootPath, true);
                    throw;
                }
            }

            public void Dispose()
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, true);
                }
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
