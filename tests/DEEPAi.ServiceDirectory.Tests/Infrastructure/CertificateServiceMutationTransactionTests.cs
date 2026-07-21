using System;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateServiceMutationTransactionTests
    {
        [TestMethod]
        public void RegistrationCommitsDirectoryAndPkiInCanonicalTargetOrder()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            using (CertificateServiceMutationCandidate candidate =
                context.PrepareRegistration())
            using (CertificateAuthorityStoreSnapshot committed =
                context.Commit(candidate))
            {
                CollectionAssert.AreEqual(
                    new[]
                    {
                        StateFileTarget.Directory,
                        StateFileTarget.PkiMetadata,
                        StateFileTarget.CertificateLedger
                    },
                    context.FaultInjector.AppliedTargets.ToArray());
                Assert.AreEqual(1UL, context.DirectoryState
                    .CurrentSnapshot.LogicalClock);
                Assert.AreEqual(2UL, committed.State.PkiRevision);
                Assert.AreEqual(1UL, committed.State.CrlNumber);
                AssertReplayMatches(candidate, committed.Ledger);
            }
        }

        [TestMethod]
        public void ImmediateRegistrationClosesModeAndReplaysAfterClose()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                ServiceDefinition definition = TestData.Definition(
                    productCode: "AB12",
                    serviceHostName: "service.internal",
                    serviceIpv4Address: "10.20.30.40");
                PkiTestSigningRequest request =
                    PkiTestData.CreateRsaSigningRequest(
                        definition.ServiceEndpointIdentity);
                Assert.IsTrue(
                    CertificateSigningRequestValidator.TryValidate(
                        request.DerBytes,
                        definition.ServiceEndpointIdentity,
                        out ValidatedCertificateSigningRequest validated,
                        out CertificateSigningRequestValidationError error),
                    error.ToString());
                var evidence =
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        Guid.NewGuid(),
                        definition,
                        request.DerBytes);
                var mode = new RegistrationModeOwner(
                    context.MutationGate);
                mode.Open();

                ExternalRegistrationServiceResult registered =
                    context.Store.RegisterService(
                        context.DirectoryState,
                        mode,
                        context.DirectoryIdentity,
                        context.InstanceId,
                        definition,
                        validated,
                        evidence,
                        TestData.Utc(5));
                ExternalRegistrationServiceResult replayed =
                    context.Store.RegisterService(
                        context.DirectoryState,
                        mode,
                        context.DirectoryIdentity,
                        context.InstanceId,
                        definition,
                        validated,
                        evidence,
                        TestData.Utc(6));

                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Registered,
                    registered.Status);
                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Replayed,
                    replayed.Status);
                Assert.AreEqual(
                    registered.Certificate.SerialNumber,
                    replayed.Certificate.SerialNumber);
                Assert.AreEqual(
                    RegistrationModeState.Closed,
                    mode.GetSnapshot().State);
                Assert.IsNotNull(mode.GetSnapshot().LastResult);
                Assert.AreEqual(
                    1,
                    context.DirectoryState.CurrentSnapshot.ActiveCount);
            }
        }

        [TestMethod]
        public void DefinitionPreservingRenewalDoesNotWriteDirectoryOrCrl()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            using (CertificateServiceMutationCandidate registration =
                context.PrepareRegistration())
            {
                using (context.Commit(registration))
                {
                }

                context.FaultInjector.Clear();
                byte[] directoryBefore = File.ReadAllBytes(
                    Path.Combine(context.RootPath, "directory.xml"));
                using (CertificateServiceMutationCandidate renewal =
                    context.PrepareRenewal())
                using (CertificateAuthorityStoreSnapshot committed =
                    context.Commit(renewal))
                {
                    byte[] directoryAfter = File.ReadAllBytes(
                        Path.Combine(context.RootPath, "directory.xml"));
                    CollectionAssert.AreEqual(
                        directoryBefore,
                        directoryAfter);
                    CollectionAssert.AreEqual(
                        new[]
                        {
                            StateFileTarget.PkiMetadata,
                            StateFileTarget.CertificateLedger
                        },
                        context.FaultInjector.AppliedTargets.ToArray());
                    Assert.AreNotEqual(
                        registration.SerialNumber,
                        renewal.SerialNumber);
                    Assert.AreEqual(3UL, committed.State.PkiRevision);
                    Assert.AreEqual(1UL, committed.State.CrlNumber);
                    AssertReplayMatches(renewal, committed.Ledger);
                }
            }
        }

        [TestMethod]
        public void PreApplyJournalFailuresRollBackEntireRegistration()
        {
            var faultPoints = new[]
            {
                RecoveryJournalFaultPoint.ImagesFlushed,
                RecoveryJournalFaultPoint.PreparedFlushed
            };
            foreach (RecoveryJournalFaultPoint faultPoint in faultPoints)
            {
                using (CertificateServiceMutationTestContext context =
                    CertificateServiceMutationTestContext.Create())
                using (CertificateServiceMutationCandidate candidate =
                    context.PrepareRegistration())
                {
                    context.FaultInjector.Arm(faultPoint);

                    if (faultPoint ==
                        RecoveryJournalFaultPoint.ImagesFlushed)
                    {
                        Assert.ThrowsExactly<IOException>(
                            () => context.Commit(candidate),
                            faultPoint.ToString());
                    }
                    else
                    {
                        Assert.ThrowsExactly<RecoveryRequiredException>(
                            () => context.Commit(candidate),
                            faultPoint.ToString());
                    }
                    context.RecoverAfterInjectedFailure();

                    Assert.AreEqual(
                        0,
                        context.DirectoryState.CurrentSnapshot.ActiveCount,
                        faultPoint.ToString());
                    using (CertificateAuthorityStoreSnapshot recovered =
                        context.Store.GetCurrent())
                    {
                        Assert.AreEqual(
                            1UL,
                            recovered.State.PkiRevision,
                            faultPoint.ToString());
                        Assert.AreEqual(
                            0,
                            recovered.Ledger.EntriesBySerial.Count,
                            faultPoint.ToString());
                    }
                }
            }
        }

        [TestMethod]
        public void TargetWriteFailureRollsBackEntireRegistration()
        {
            var targets = new[]
            {
                StateFileTarget.Directory,
                StateFileTarget.PkiMetadata,
                StateFileTarget.CertificateLedger
            };
            foreach (StateFileTarget target in targets)
            {
                using (CertificateServiceMutationTestContext context =
                    CertificateServiceMutationTestContext.Create())
                using (CertificateServiceMutationCandidate candidate =
                    context.PrepareRegistration())
                {
                    context.FaultInjector.Arm(
                        RecoveryJournalFaultPoint.TargetApplied,
                        target);

                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => context.Commit(candidate),
                        target.ToString());
                    Assert.AreEqual(
                        StateCoordinatorStatus.RecoveryRequired,
                        context.DirectoryState.Status,
                        target.ToString());

                    context.RecoverAfterInjectedFailure();

                    Assert.AreEqual(
                        0,
                        context.DirectoryState.CurrentSnapshot.ActiveCount,
                        target.ToString());
                    using (CertificateAuthorityStoreSnapshot recovered =
                        context.Store.GetCurrent())
                    {
                        Assert.AreEqual(
                            1UL,
                            recovered.State.PkiRevision,
                            target.ToString());
                        Assert.AreEqual(
                            1UL,
                            recovered.State.CrlNumber,
                            target.ToString());
                        Assert.AreEqual(
                            0,
                            recovered.Ledger.EntriesBySerial.Count,
                            target.ToString());
                    }
                }
            }
        }

        [TestMethod]
        public void EveryDeletionTargetFailureRollsBackDirectoryLedgerAndCrl()
        {
            var targets = new[]
            {
                StateFileTarget.Directory,
                StateFileTarget.PkiMetadata,
                StateFileTarget.CertificateLedger,
                StateFileTarget.CertificateRevocationList
            };
            foreach (StateFileTarget target in targets)
            {
                using (CertificateServiceMutationTestContext context =
                    CertificateServiceMutationTestContext.Create())
                using (CertificateServiceMutationCandidate registration =
                    context.PrepareRegistration())
                {
                    using (context.Commit(registration))
                    {
                    }

                    context.FaultInjector.Clear();
                    context.FaultInjector.Arm(
                        RecoveryJournalFaultPoint.TargetApplied,
                        target);
                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => context.Store.DeleteService(
                            context.DirectoryState,
                            context.InstanceId,
                            registration.Evidence.ServiceDefinition
                                .ProductCode,
                            TestData.Utc(20)),
                        target.ToString());

                    context.RecoverAfterInjectedFailure();

                    Assert.IsTrue(
                        context.DirectoryState.CurrentSnapshot.TryGetRecord(
                            registration.Evidence.ServiceDefinition
                                .ProductCode,
                            out ServiceRecord active),
                        target.ToString());
                    Assert.IsFalse(active.Deleted, target.ToString());
                    using (CertificateAuthorityStoreSnapshot recovered =
                        context.Store.GetCurrent())
                    {
                        Assert.AreEqual(
                            2UL,
                            recovered.State.PkiRevision,
                            target.ToString());
                        Assert.AreEqual(
                            1UL,
                            recovered.State.CrlNumber,
                            target.ToString());
                        Assert.IsTrue(recovered.Ledger.TryGetBySerial(
                            registration.SerialNumber,
                            out CertificateLedgerEntry current));
                        Assert.AreEqual(
                            CertificateLedgerStatus.Current,
                            current.Status,
                            target.ToString());
                    }
                }
            }
        }

        [TestMethod]
        public void LostResponseAfterCommittedRegistrationReplaysSameSerial()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            using (CertificateServiceMutationCandidate candidate =
                context.PrepareRegistration())
            {
                context.FaultInjector.Arm(
                    RecoveryJournalFaultPoint.CommittedFlushed);

                Assert.ThrowsExactly<RecoveryRequiredException>(
                    () => context.Commit(candidate));
                context.RecoverAfterInjectedFailure();

                using (CertificateAuthorityStoreSnapshot recovered =
                    context.Store.GetCurrent())
                {
                    AssertReplayMatches(candidate, recovered.Ledger);
                    CertificateLedgerEntry replayed;
                    recovered.Ledger.ResolveIssuanceRequest(
                        candidate.Evidence,
                        out replayed);
                    Assert.AreEqual(
                        candidate.SerialNumber,
                        replayed.SerialNumber);
                }
            }
        }

        [TestMethod]
        public void CommittedSerialRevocationNeverRollsBackCrlNumber()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            using (CertificateServiceMutationCandidate registration =
                context.PrepareRegistration())
            {
                using (context.Commit(registration))
                {
                }

                context.FaultInjector.Clear();
                using (CertificateServiceMutationCandidate revocation =
                    context.PrepareSerialRevocation())
                {
                    context.FaultInjector.Arm(
                        RecoveryJournalFaultPoint.CommittedFlushed);

                    Assert.ThrowsExactly<RecoveryRequiredException>(
                        () => context.Commit(revocation));
                    context.RecoverAfterInjectedFailure();

                    CollectionAssert.AreEqual(
                        new[]
                        {
                            StateFileTarget.PkiMetadata,
                            StateFileTarget.CertificateLedger,
                            StateFileTarget.CertificateRevocationList
                        },
                        context.FaultInjector.AppliedTargets.ToArray());
                    using (CertificateAuthorityStoreSnapshot recovered =
                        context.Store.GetCurrent())
                    {
                        Assert.AreEqual(
                            revocation.NextState.CrlNumber,
                            recovered.State.CrlNumber);
                        Assert.AreEqual(2UL, recovered.State.CrlNumber);
                        Assert.IsTrue(recovered.Ledger.TryGetBySerial(
                            revocation.SerialNumber,
                            out CertificateLedgerEntry revoked));
                        Assert.AreEqual(
                            CertificateLedgerStatus.Revoked,
                            revoked.Status);
                    }
                }
            }
        }

        [TestMethod]
        public void DeletionCommitsTombstoneLedgerAndCrlTogether()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            using (CertificateServiceMutationCandidate registration =
                context.PrepareRegistration())
            {
                using (context.Commit(registration))
                {
                }

                using (CertificateServiceMutationCandidate renewal =
                    context.PrepareRenewal())
                {
                    using (context.Commit(renewal))
                    {
                    }

                    context.FaultInjector.Clear();
                    CertificateServiceDeletionResult result =
                        context.Store.DeleteService(
                            context.DirectoryState,
                            context.InstanceId,
                            registration.Evidence.ServiceDefinition
                                .ProductCode,
                            TestData.Utc(20));

                    Assert.AreEqual(
                        CertificateServiceDeletionStatus.Deleted,
                        result.Status);
                    Assert.AreEqual(
                        renewal.SerialNumber,
                        result.RevokedSerialNumber);
                    CollectionAssert.AreEqual(
                        new[]
                        {
                            StateFileTarget.Directory,
                            StateFileTarget.PkiMetadata,
                            StateFileTarget.CertificateLedger,
                            StateFileTarget.CertificateRevocationList
                        },
                        context.FaultInjector.AppliedTargets.ToArray());
                    Assert.IsTrue(
                        context.DirectoryState.CurrentSnapshot.TryGetRecord(
                            registration.Evidence.ServiceDefinition
                                .ProductCode,
                            out ServiceRecord tombstone));
                    Assert.IsTrue(tombstone.Deleted);

                    using (CertificateAuthorityStoreSnapshot committed =
                        context.Store.GetCurrent())
                    {
                        Assert.AreEqual(4UL, committed.State.PkiRevision);
                        Assert.AreEqual(2UL, committed.State.CrlNumber);
                        foreach (CertificateLedgerEntry revoked in
                            committed.Ledger.EntriesBySerial.Values)
                        {
                            Assert.AreEqual(
                                CertificateLedgerStatus.Revoked,
                                revoked.Status);
                            Assert.AreEqual(
                                CertificateRevocationReason
                                    .CessationOfOperation,
                                revoked.RevocationReason.Value);
                        }
                    }
                }
            }
        }

        private static void AssertReplayMatches(
            CertificateServiceMutationCandidate candidate,
            CertificateLedgerSnapshot ledger)
        {
            CertificateIssuanceReplayStatus status =
                ledger.ResolveIssuanceRequest(
                    candidate.Evidence,
                    out CertificateLedgerEntry replayed);
            Assert.AreEqual(
                CertificateIssuanceReplayStatus.ExactReplay,
                status);
            Assert.IsNotNull(replayed);
            Assert.AreEqual(candidate.SerialNumber, replayed.SerialNumber);
        }
    }
}
