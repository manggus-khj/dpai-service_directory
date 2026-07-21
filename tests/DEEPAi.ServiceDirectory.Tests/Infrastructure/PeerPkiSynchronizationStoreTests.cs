using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerPkiSynchronizationStoreTests
    {
        [TestMethod]
        public void ActiveStateContainsOnlyProductCurrentMapping()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                using (CertificateServiceMutationCandidate registration =
                    context.PrepareRegistration())
                using (CertificateAuthorityStoreSnapshot unused =
                    context.Commit(registration))
                {
                }

                string renewalSerial;
                using (CertificateServiceMutationCandidate renewal =
                    context.PrepareRenewal())
                using (CertificateAuthorityStoreSnapshot unused =
                    context.Commit(renewal))
                {
                    renewalSerial = renewal.SerialNumber.Hex;
                }

                var store = new PeerPkiSynchronizationStore(
                    context.RootPath,
                    context.MutationGate);
                PeerPkiState state = store.GetActiveState();

                Assert.AreEqual(context.InstanceId, state.IssuerInstanceId);
                Assert.AreEqual(1, state.ActiveCertificates.Count);
                Assert.AreEqual(
                    "AB12",
                    state.ActiveCertificates[0].ProductCode);
                Assert.AreEqual(
                    renewalSerial,
                    state.ActiveCertificates[0].SerialNumber);
            }
        }

        [TestMethod]
        public void StandbyApplyCommitsMetadataCacheAndCrlTogether()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                var peerStore = new PeerPkiSynchronizationStore(
                    context.RootPath,
                    context.MutationGate);
                PeerPkiState known = peerStore.GetActiveState();

                using (CertificateServiceMutationCandidate registration =
                    context.PrepareRegistration())
                using (CertificateAuthorityStoreSnapshot unused =
                    context.Commit(registration))
                {
                }

                PeerPkiState received = peerStore.GetActiveState();
                ConvertActiveFilesToStandby(context, known);
                context.FaultInjector.Clear();
                var standby = new PeerPkiSynchronizationStore(
                    new StateStoragePathPolicy(context.RootPath),
                    context.MutationGate,
                    context.FaultInjector);

                standby.ApplyStandbyState(received, TestData.Utc(17));

                CollectionAssert.AreEqual(
                    new[]
                    {
                        StateFileTarget.PkiMetadata,
                        StateFileTarget.PeerPkiCache,
                        StateFileTarget.CertificateRevocationList
                    },
                    context.FaultInjector.AppliedTargets.ToArray());
                Assert.IsFalse(File.Exists(
                    new StateStoragePathPolicy(context.RootPath)
                        .GetTargetPath(
                            StateFileTarget.CertificateLedger)));
                Assert.IsFalse(File.Exists(
                    new StateStoragePathPolicy(context.RootPath)
                        .GetTargetPath(StateFileTarget.CaPrivateKey)));

                PeerPkiState applied = standby.GetKnownStandbyState();
                Assert.AreEqual(received.PkiRevision, applied.PkiRevision);
                Assert.AreEqual(received.CrlNumber, applied.CrlNumber);
                Assert.AreEqual(1, applied.ActiveCertificates.Count);
                Assert.AreEqual(
                    received.ActiveCertificates[0].SerialNumber,
                    applied.ActiveCertificates[0].SerialNumber);
            }
        }

        [TestMethod]
        public void StandbyRejectsDifferentContentAtCurrentRevision()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                var peerStore = new PeerPkiSynchronizationStore(
                    context.RootPath,
                    context.MutationGate);
                PeerPkiState known = peerStore.GetActiveState();
                ConvertActiveFilesToStandby(context, known);
                var standby = new PeerPkiSynchronizationStore(
                    context.RootPath,
                    context.MutationGate);
                byte[] knownCrl = known.GetCrl();
                PeerPkiState conflicting;
                try
                {
                    conflicting = new PeerPkiState(
                        known.IssuerInstanceId,
                        known.PkiRevision,
                        known.CrlNumber,
                        known.CrlSha256,
                        knownCrl,
                        new[]
                        {
                            new PeerActiveCertificate(
                                "AB12",
                                "01000000000000000000000000000002",
                                Convert.ToBase64String(new byte[32]),
                                TestData.Utc(17).AddDays(1))
                        });
                }
                finally
                {
                    Array.Clear(knownCrl, 0, knownCrl.Length);
                }

                Assert.ThrowsExactly<InvalidDataException>(() =>
                    standby.ApplyStandbyState(
                        conflicting,
                        TestData.Utc(17)));
            }
        }

        private static void ConvertActiveFilesToStandby(
            CertificateServiceMutationTestContext context,
            PeerPkiState known)
        {
            var pathPolicy = new StateStoragePathPolicy(context.RootPath);
            var writer = new AtomicFileWriter(pathPolicy);
            CertificateAuthorityState active;
            byte[] stateBytes = writer.Read(
                StateFileTarget.PkiMetadata,
                CertificateAuthorityStateCodec.MaximumDocumentBytes);
            try
            {
                active = new CertificateAuthorityStateCodec()
                    .DeserializeState(stateBytes);
            }
            finally
            {
                Array.Clear(stateBytes, 0, stateBytes.Length);
            }

            var standbyState = new CertificateAuthorityState(
                active.SiteId,
                known.IssuerInstanceId,
                CertificateAuthorityRole.Standby,
                active.CaSerialNumber,
                active.GetCaSpkiSha256(),
                active.NotBeforeUtc,
                active.NotAfterUtc,
                known.PkiRevision,
                known.CrlNumber,
                null);
            byte[] metadata = new CertificateAuthorityStateCodec()
                .SerializeState(standbyState);
            byte[] cacheBytes = new PeerPkiCacheCodec().Serialize(
                CreateCache(known));
            byte[] crl = known.GetCrl();
            try
            {
                writer.Write(StateFileTarget.PkiMetadata, metadata);
                writer.Write(StateFileTarget.PeerPkiCache, cacheBytes);
                writer.Write(StateFileTarget.CertificateRevocationList, crl);
            }
            finally
            {
                Array.Clear(metadata, 0, metadata.Length);
                Array.Clear(cacheBytes, 0, cacheBytes.Length);
                Array.Clear(crl, 0, crl.Length);
            }

            DeletePrimaryAndBackup(
                pathPolicy,
                StateFileTarget.CertificateLedger);
            DeletePrimaryAndBackup(
                pathPolicy,
                StateFileTarget.CaPrivateKey);
        }

        private static PeerPkiCacheSnapshot CreateCache(PeerPkiState state)
        {
            var certificates = new List<PeerPkiCacheCertificate>();
            foreach (PeerActiveCertificate certificate in
                state.ActiveCertificates)
            {
                Assert.IsTrue(ProductCode.TryCreate(
                    certificate.ProductCode,
                    out ProductCode productCode));
                Assert.IsTrue(CertificateSerialNumber.TryCreate(
                    certificate.SerialNumber,
                    out CertificateSerialNumber serialNumber));
                byte[] leafSha256 = Convert.FromBase64String(
                    certificate.LeafSha256);
                try
                {
                    certificates.Add(new PeerPkiCacheCertificate(
                        productCode,
                        serialNumber,
                        leafSha256,
                        certificate.NotAfterUtc));
                }
                finally
                {
                    Array.Clear(leafSha256, 0, leafSha256.Length);
                }
            }

            byte[] crlSha256 = Convert.FromBase64String(state.CrlSha256);
            try
            {
                return new PeerPkiCacheSnapshot(
                    state.IssuerInstanceId,
                    state.PkiRevision,
                    state.CrlNumber,
                    crlSha256,
                    certificates);
            }
            finally
            {
                Array.Clear(crlSha256, 0, crlSha256.Length);
            }
        }

        private static void DeletePrimaryAndBackup(
            StateStoragePathPolicy pathPolicy,
            StateFileTarget target)
        {
            string path = pathPolicy.GetTargetPath(target);
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
