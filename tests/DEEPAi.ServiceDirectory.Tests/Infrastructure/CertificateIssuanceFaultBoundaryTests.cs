using System;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateIssuanceFaultBoundaryTests
    {
        [TestMethod]
        public void PrecommitFaultsNeverPublishCandidateCertificateOrState()
        {
            var faultPoints = new[]
            {
                CertificateServiceMutationFaultPoint
                    .BeforeRegistrationClaim,
                CertificateServiceMutationFaultPoint.SerialReserved,
                CertificateServiceMutationFaultPoint.CertificateSigned
            };
            foreach (CertificateServiceMutationFaultPoint faultPoint
                in faultPoints)
            {
                using (CertificateServiceMutationTestContext context =
                    CertificateServiceMutationTestContext.Create())
                {
                    RegistrationInput input = CreateRegistrationInput();
                    var mode = new RegistrationModeOwner(
                        context.MutationGate);
                    mode.Open();
                    context.ServiceFaultInjector.Arm(faultPoint);

                    Assert.ThrowsExactly<IOException>(
                        () => Register(context, mode, input, TestData.Utc(5)),
                        faultPoint.ToString());

                    RegistrationModeState expectedMode = faultPoint ==
                        CertificateServiceMutationFaultPoint
                            .BeforeRegistrationClaim
                            ? RegistrationModeState.Open
                            : RegistrationModeState.Closed;
                    Assert.AreEqual(
                        expectedMode,
                        mode.GetSnapshot().State,
                        faultPoint.ToString());
                    AssertUnchangedInitialState(context, faultPoint.ToString());
                    Assert.AreEqual(
                        faultPoint != CertificateServiceMutationFaultPoint
                            .BeforeRegistrationClaim,
                        context.ServiceFaultInjector.LastSerialNumber.HasValue,
                        faultPoint.ToString());
                }
            }
        }

        [TestMethod]
        public void ResponseBoundaryFaultReplaysCommittedSerialWithoutReissue()
        {
            using (CertificateServiceMutationTestContext context =
                CertificateServiceMutationTestContext.Create())
            {
                RegistrationInput input = CreateRegistrationInput();
                var mode = new RegistrationModeOwner(context.MutationGate);
                mode.Open();
                context.ServiceFaultInjector.Arm(
                    CertificateServiceMutationFaultPoint.ResponseStarting);

                Assert.ThrowsExactly<IOException>(
                    () => Register(context, mode, input, TestData.Utc(5)));

                Assert.IsTrue(
                    context.ServiceFaultInjector.LastSerialNumber.HasValue);
                CertificateSerialNumber committedSerial = context
                    .ServiceFaultInjector.LastSerialNumber.Value;
                Assert.AreEqual(
                    RegistrationModeState.Closed,
                    mode.GetSnapshot().State);
                Assert.AreEqual(
                    1,
                    context.DirectoryState.CurrentSnapshot.ActiveCount);
                using (CertificateAuthorityStoreSnapshot committed =
                    context.Store.GetCurrent())
                {
                    Assert.AreEqual(2UL, committed.State.PkiRevision);
                    Assert.AreEqual(1UL, committed.State.CrlNumber);
                    Assert.IsTrue(committed.Ledger.TryGetBySerial(
                        committedSerial,
                        out CertificateLedgerEntry entry));
                    Assert.AreEqual(
                        CertificateLedgerStatus.Current,
                        entry.Status);
                }

                ExternalRegistrationServiceResult replayed = Register(
                    context,
                    mode,
                    input,
                    TestData.Utc(6));

                Assert.AreEqual(
                    ExternalRegistrationServiceStatus.Replayed,
                    replayed.Status);
                Assert.AreEqual(
                    committedSerial.Hex,
                    replayed.Certificate.SerialNumber);
                Assert.AreEqual(
                    1,
                    context.ServiceFaultInjector.ObservedPoints.Count(point =>
                        point == CertificateServiceMutationFaultPoint
                            .SerialReserved));
            }
        }

        private static RegistrationInput CreateRegistrationInput()
        {
            ServiceDefinition definition = TestData.Definition(
                productCode: "AB12",
                serviceHostName: "service.internal",
                serviceIpv4Address: "10.20.30.40");
            PkiTestSigningRequest request =
                PkiTestData.CreateRsaSigningRequest(
                    definition.ServiceEndpointIdentity);
            Assert.IsTrue(CertificateSigningRequestValidator.TryValidate(
                request.DerBytes,
                definition.ServiceEndpointIdentity,
                out ValidatedCertificateSigningRequest validated,
                out CertificateSigningRequestValidationError error),
                error.ToString());
            CertificateIssuanceRequestEvidence evidence =
                CertificateIssuanceRequestEvidence.CreateRegistration(
                    Guid.NewGuid(),
                    definition,
                    request.DerBytes);
            return new RegistrationInput(
                definition,
                validated,
                evidence);
        }

        private static ExternalRegistrationServiceResult Register(
            CertificateServiceMutationTestContext context,
            RegistrationModeOwner mode,
            RegistrationInput input,
            DateTime utcNow)
        {
            return context.Store.RegisterService(
                context.DirectoryState,
                mode,
                context.DirectoryIdentity,
                context.InstanceId,
                input.Definition,
                input.SigningRequest,
                input.Evidence,
                utcNow);
        }

        private static void AssertUnchangedInitialState(
            CertificateServiceMutationTestContext context,
            string message)
        {
            Assert.AreEqual(
                0,
                context.DirectoryState.CurrentSnapshot.ActiveCount,
                message);
            using (CertificateAuthorityStoreSnapshot current =
                context.Store.GetCurrent())
            {
                Assert.AreEqual(1UL, current.State.PkiRevision, message);
                Assert.AreEqual(1UL, current.State.CrlNumber, message);
                Assert.AreEqual(
                    0,
                    current.Ledger.EntriesBySerial.Count,
                    message);
            }
        }

        private sealed class RegistrationInput
        {
            internal RegistrationInput(
                ServiceDefinition definition,
                ValidatedCertificateSigningRequest signingRequest,
                CertificateIssuanceRequestEvidence evidence)
            {
                Definition = definition;
                SigningRequest = signingRequest;
                Evidence = evidence;
            }

            internal ServiceDefinition Definition { get; }

            internal ValidatedCertificateSigningRequest SigningRequest
            {
                get;
            }

            internal CertificateIssuanceRequestEvidence Evidence { get; }
        }
    }
}
