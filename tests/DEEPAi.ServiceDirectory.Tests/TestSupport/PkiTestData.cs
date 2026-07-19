using System;
using DEEPAi.ServiceDirectory.Domain;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.TestSupport
{
    internal sealed class PkiTestSigningRequest
    {
        internal PkiTestSigningRequest(
            byte[] derBytes,
            AsymmetricCipherKeyPair keyPair)
        {
            DerBytes = derBytes;
            KeyPair = keyPair;
        }

        internal byte[] DerBytes { get; }

        internal AsymmetricCipherKeyPair KeyPair { get; }
    }

    internal static class PkiTestData
    {
        internal static PkiTestSigningRequest CreateRsaSigningRequest(
            ServiceEndpointIdentity identity,
            int keySizeBits = 2048,
            string signatureAlgorithm = "SHA256WITHRSA",
            bool addUnsupportedExtension = false,
            bool unsupportedExtensionCritical = true,
            bool addExtraDnsSan = false,
            bool duplicateExtensionRequest = false,
            bool addUnsupportedAttribute = false)
        {
            var random = new SecureRandom();
            var keyGenerator = new RsaKeyPairGenerator();
            keyGenerator.Init(new RsaKeyGenerationParameters(
                BigInteger.ValueOf(65537),
                random,
                keySizeBits,
                64));
            AsymmetricCipherKeyPair keyPair = keyGenerator.GenerateKeyPair();
            return CreateSigningRequest(
                identity,
                keyPair,
                signatureAlgorithm,
                addUnsupportedExtension,
                unsupportedExtensionCritical,
                addExtraDnsSan,
                duplicateExtensionRequest,
                addUnsupportedAttribute,
                random);
        }

        internal static PkiTestSigningRequest CreateEcdsaP256SigningRequest(
            ServiceEndpointIdentity identity)
        {
            var random = new SecureRandom();
            var keyGenerator = new ECKeyPairGenerator("ECDSA");
            keyGenerator.Init(new ECKeyGenerationParameters(
                X9ObjectIdentifiers.Prime256v1,
                random));
            AsymmetricCipherKeyPair keyPair = keyGenerator.GenerateKeyPair();
            return CreateSigningRequest(
                identity,
                keyPair,
                "SHA256WITHECDSA",
                false,
                false,
                false,
                false,
                false,
                random);
        }

        internal static ServiceEndpointIdentity ServiceIdentity(
            string hostName = "vms-bridge.example.local",
            string ipv4Address = "10.20.30.40")
        {
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;
            if (!ServiceEndpointIdentity.TryCreate(
                    hostName,
                    ipv4Address,
                    out identity,
                    out error))
            {
                throw new InvalidOperationException(
                    "The PKI test service identity is invalid: " + error);
            }

            return identity;
        }

        internal static DirectoryEndpointIdentity DirectoryIdentity()
        {
            DirectoryEndpointIdentity identity;
            EndpointIdentityValidationError error;
            if (!DirectoryEndpointIdentity.TryCreate(
                    "management.example.local",
                    "10.20.30.10",
                    out identity,
                    out error))
            {
                throw new InvalidOperationException(
                    "The PKI test Directory identity is invalid: " + error);
            }

            return identity;
        }

        private static PkiTestSigningRequest CreateSigningRequest(
            ServiceEndpointIdentity identity,
            AsymmetricCipherKeyPair keyPair,
            string signatureAlgorithm,
            bool addUnsupportedExtension,
            bool unsupportedExtensionCritical,
            bool addExtraDnsSan,
            bool duplicateExtensionRequest,
            bool addUnsupportedAttribute,
            SecureRandom random)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            var generalNames = addExtraDnsSan
                ? new GeneralNames(new[]
                {
                    new GeneralName(GeneralName.DnsName, identity.ServiceHostName),
                    new GeneralName(GeneralName.IPAddress, identity.ServiceIpv4Address),
                    new GeneralName(GeneralName.DnsName, "extra.example.local")
                })
                : new GeneralNames(new[]
                {
                    new GeneralName(GeneralName.DnsName, identity.ServiceHostName),
                    new GeneralName(GeneralName.IPAddress, identity.ServiceIpv4Address)
                });
            var extensionsGenerator = new X509ExtensionsGenerator();
            extensionsGenerator.AddExtension(
                X509Extensions.SubjectAlternativeName,
                false,
                generalNames);
            if (addUnsupportedExtension)
            {
                extensionsGenerator.AddExtension(
                    new DerObjectIdentifier("1.3.6.1.4.1.55555.1"),
                    unsupportedExtensionCritical,
                    new DerUtf8String("unsupported"));
            }

            var extensionRequest = new AttributePkcs(
                PkcsObjectIdentifiers.Pkcs9AtExtensionRequest,
                new DerSet(extensionsGenerator.Generate()));
            var attributes = duplicateExtensionRequest
                ? new DerSet(new Asn1Encodable[]
                {
                    extensionRequest,
                    extensionRequest
                })
                : addUnsupportedAttribute
                    ? new DerSet(new Asn1Encodable[]
                    {
                        extensionRequest,
                        new AttributePkcs(
                            new DerObjectIdentifier("1.2.840.113549.1.9.7"),
                            new DerSet(new DerUtf8String("not-allowed")))
                    })
                    : new DerSet(extensionRequest);
            var signatureFactory = new Asn1SignatureFactory(
                signatureAlgorithm,
                keyPair.Private,
                random);
            var request = new Pkcs10CertificationRequest(
                signatureFactory,
                new X509Name("CN=" + identity.ServiceHostName),
                keyPair.Public,
                attributes);
            return new PkiTestSigningRequest(request.GetDerEncoded(), keyPair);
        }
    }
}
