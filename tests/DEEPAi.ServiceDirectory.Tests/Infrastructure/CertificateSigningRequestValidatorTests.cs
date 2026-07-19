using System;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateSigningRequestValidatorTests
    {
        [TestMethod]
        public void ValidatorAcceptsRsaAndNamedP256WithExactSanPair()
        {
            ServiceEndpointIdentity identity = PkiTestData.ServiceIdentity();

            AssertValid(PkiTestData.CreateRsaSigningRequest(identity), identity);
            AssertValid(PkiTestData.CreateEcdsaP256SigningRequest(identity), identity);
        }

        [TestMethod]
        public void ValidatorRejectsMismatchedAndAdditionalSanValues()
        {
            ServiceEndpointIdentity identity = PkiTestData.ServiceIdentity();
            ServiceEndpointIdentity changedIdentity = PkiTestData.ServiceIdentity(
                "vms-bridge.example.local",
                "10.20.30.41");
            PkiTestSigningRequest request = PkiTestData.CreateRsaSigningRequest(identity);

            AssertInvalid(
                request.DerBytes,
                changedIdentity,
                CertificateSigningRequestValidationError.SubjectAlternativeNameMismatch);
            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(
                    identity,
                    addExtraDnsSan: true).DerBytes,
                identity,
                CertificateSigningRequestValidationError.SubjectAlternativeNameInvalid);
        }

        [TestMethod]
        public void ValidatorRejectsWeakKeySha1AndUnsupportedExtensions()
        {
            ServiceEndpointIdentity identity = PkiTestData.ServiceIdentity();

            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(identity, 1024).DerBytes,
                identity,
                CertificateSigningRequestValidationError.PublicKeyTooWeak);
            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(
                    identity,
                    signatureAlgorithm: "SHA1WITHRSA").DerBytes,
                identity,
                CertificateSigningRequestValidationError.SignatureAlgorithmNotAllowed);
            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(
                    identity,
                    addUnsupportedExtension: true).DerBytes,
                identity,
                CertificateSigningRequestValidationError.UnsupportedExtension);
            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(
                    identity,
                    addUnsupportedExtension: true,
                    unsupportedExtensionCritical: false).DerBytes,
                identity,
                CertificateSigningRequestValidationError.UnsupportedExtension);
        }

        [TestMethod]
        public void ValidatorRejectsTamperedSignatureAndOversizedInput()
        {
            ServiceEndpointIdentity identity = PkiTestData.ServiceIdentity();
            byte[] request = PkiTestData.CreateRsaSigningRequest(identity).DerBytes;
            byte[] tampered = (byte[])request.Clone();
            tampered[tampered.Length - 1] ^= 0x01;

            AssertInvalid(
                tampered,
                identity,
                CertificateSigningRequestValidationError.SignatureInvalid);
            AssertInvalid(
                new byte[CertificateSigningRequestValidator.MaximumDerLength + 1],
                identity,
                CertificateSigningRequestValidationError.TooLarge);
        }

        [TestMethod]
        public void ValidatorRejectsDuplicateOrUnsupportedAttributes()
        {
            ServiceEndpointIdentity identity = PkiTestData.ServiceIdentity();

            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(
                    identity,
                    duplicateExtensionRequest: true).DerBytes,
                identity,
                CertificateSigningRequestValidationError.RequestedExtensionsInvalid);
            AssertInvalid(
                PkiTestData.CreateRsaSigningRequest(
                    identity,
                    addUnsupportedAttribute: true).DerBytes,
                identity,
                CertificateSigningRequestValidationError.RequestedExtensionsInvalid);
        }

        private static void AssertValid(
            PkiTestSigningRequest request,
            ServiceEndpointIdentity identity)
        {
            ValidatedCertificateSigningRequest validated;
            CertificateSigningRequestValidationError error;

            Assert.IsTrue(CertificateSigningRequestValidator.TryValidate(
                request.DerBytes,
                identity,
                out validated,
                out error));
            Assert.AreEqual(CertificateSigningRequestValidationError.None, error);
            Assert.IsNotNull(validated);
            Assert.AreEqual(identity, validated.Identity);
            Assert.AreEqual(32, validated.GetSubjectPublicKeyInfoSha256().Length);
            CollectionAssert.AreEqual(request.DerBytes, validated.GetDerBytes());
        }

        private static void AssertInvalid(
            byte[] request,
            ServiceEndpointIdentity identity,
            CertificateSigningRequestValidationError expectedError)
        {
            ValidatedCertificateSigningRequest validated;
            CertificateSigningRequestValidationError error;

            Assert.IsFalse(CertificateSigningRequestValidator.TryValidate(
                request,
                identity,
                out validated,
                out error));
            Assert.IsNull(validated);
            Assert.AreEqual(expectedError, error);
        }
    }
}
