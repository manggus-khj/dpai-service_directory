using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerTlsTrustSnapshotTests
    {
        [TestMethod]
        public void ValidatesPinnedCaEndpointSanAndCurrentCrlBeforePeerHttp()
        {
            var random = new SecureRandom();
            DateTime utcNow = TestData.Utc(0);
            SiteCertificateAuthority authority = SiteCertificateAuthority
                .Create(Guid.NewGuid(), utcNow, random);
            DirectoryEndpointIdentity identity =
                PkiTestData.DirectoryIdentity();
            using (IssuedCertificateArtifact leaf =
                authority.CreateDirectoryLeaf(
                    identity,
                    PkiSerialNumber.CreateRandom(random, null),
                    utcNow,
                    random))
            {
                byte[] ca = authority.GetCertificateDer();
                byte[] crl = authority.CreateRevocationList(
                        1,
                        new List<RevokedCertificateEntry>(),
                        utcNow,
                        utcNow.AddDays(7),
                        random)
                    .GetDerBytes();
                byte[] leafDer = leaf.GetCertificateDer();
                try
                {
                    using (var certificate = new X509Certificate2(leafDer))
                    using (var trust = new PeerTlsTrustSnapshot(
                        "https://10.20.30.10:21000",
                        ca,
                        crl))
                    {
                        Assert.IsTrue(trust.Validate(
                            certificate,
                            SslPolicyErrors.RemoteCertificateChainErrors,
                            TestData.Utc(1)));
                    }

                    using (var certificate = new X509Certificate2(leafDer))
                    using (var wrongEndpoint = new PeerTlsTrustSnapshot(
                        "https://10.20.30.11:21000",
                        ca,
                        crl))
                    {
                        Assert.IsFalse(wrongEndpoint.Validate(
                            certificate,
                            SslPolicyErrors.RemoteCertificateChainErrors,
                            TestData.Utc(1)));
                    }
                }
                finally
                {
                    Array.Clear(ca, 0, ca.Length);
                    Array.Clear(crl, 0, crl.Length);
                    Array.Clear(leafDer, 0, leafDer.Length);
                }
            }
        }
    }
}
