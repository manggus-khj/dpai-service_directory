using System;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateAuthorityRotationStoreTests
    {
        [TestMethod]
        public void PrepareAndCancelUseOneDurableBSlotTransition()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                var paths = new StateStoragePathPolicy(context.RootPath);
                Guid rotationId;
                using (CertificateAuthorityStoreSnapshot published =
                    context.Store.PrepareRotation(TestData.Utc(2)))
                {
                    Assert.AreEqual(
                        CertificateAuthorityRotationPhase.Published,
                        published.State.RotationPhase);
                    Assert.AreEqual(2UL, published.State.TrustRevision);
                    Assert.AreEqual(2UL, published.State.PkiRevision);
                    Assert.IsFalse(
                        published.State.IsCurrentRevisionBackedUp);
                    Assert.IsNotNull(published.State.OtherAuthority);
                    Assert.AreEqual(
                        CertificateAuthorityLiveRole.Next,
                        published.State.OtherAuthority.Role);
                    Assert.AreEqual(
                        CertificateAuthoritySlot.B,
                        published.State.OtherAuthority.Slot);
                    rotationId = published.State.RotationId.Value;
                }

                Assert.IsTrue(File.Exists(paths.GetTargetPath(
                    StateFileTarget.CaCertificateB)));
                Assert.IsTrue(File.Exists(paths.GetTargetPath(
                    StateFileTarget.CertificateRevocationListB)));
                Assert.IsTrue(File.Exists(paths.GetTargetPath(
                    StateFileTarget.CaPrivateKeyB)));

                ulong trustRevision;
                ulong pkiRevision;
                ulong crlNumber;
                using (CaBackupPayload payload =
                    context.Store.CaptureBackupPayload(
                        TestData.Utc(3),
                        out trustRevision,
                        out pkiRevision,
                        out crlNumber))
                {
                    Assert.IsTrue(payload.HasOtherAuthority);
                    Assert.AreEqual(2UL, trustRevision);
                    Assert.AreEqual(2UL, pkiRevision);
                    Assert.AreEqual(1UL, crlNumber);
                }

                Assert.IsTrue(context.Store.MarkBackupCompleted(
                    trustRevision,
                    pkiRevision,
                    crlNumber,
                    TestData.Utc(3)));

                using (CertificateAuthorityStoreSnapshot stable =
                    context.Store.CancelRotation(rotationId))
                {
                    Assert.AreEqual(
                        CertificateAuthorityRotationPhase.Stable,
                        stable.State.RotationPhase);
                    Assert.AreEqual(3UL, stable.State.TrustRevision);
                    Assert.AreEqual(3UL, stable.State.PkiRevision);
                    Assert.IsNull(stable.State.OtherAuthority);
                    Assert.IsFalse(
                        stable.State.IsCurrentRevisionBackedUp);
                }

                Assert.IsFalse(File.Exists(paths.GetTargetPath(
                    StateFileTarget.CaCertificateB)));
                Assert.IsFalse(File.Exists(paths.GetTargetPath(
                    StateFileTarget.CertificateRevocationListB)));
                Assert.IsFalse(File.Exists(paths.GetTargetPath(
                    StateFileTarget.CaPrivateKeyB)));
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    context.Store.PrepareRotation(TestData.Utc(4)));
            }
        }
    }
}
