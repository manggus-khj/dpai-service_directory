using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public sealed class PeerTlsTrustSnapshot : IDisposable
    {
        private readonly string _peerIpv4Address;
        private byte[] _caCertificateDer;
        private byte[] _crlDer;

        internal PeerTlsTrustSnapshot(
            string peerEndpoint,
            byte[] caCertificateDer,
            byte[] crlDer)
        {
            string canonicalEndpoint;
            if (!AdminPeerEndpoint.TryNormalize(
                    peerEndpoint,
                    out canonicalEndpoint)
                || !StringComparer.Ordinal.Equals(
                    peerEndpoint,
                    canonicalEndpoint))
            {
                throw new ArgumentException(
                    "The Peer TLS endpoint must be canonical HTTPS IPv4.",
                    nameof(peerEndpoint));
            }

            if (caCertificateDer == null || caCertificateDer.Length == 0)
            {
                throw new ArgumentException(
                    "The pinned site CA certificate is required.",
                    nameof(caCertificateDer));
            }

            if (crlDer == null || crlDer.Length == 0)
            {
                throw new ArgumentException(
                    "The current site CRL is required.",
                    nameof(crlDer));
            }

            _peerIpv4Address = new Uri(canonicalEndpoint).Host;
            _caCertificateDer = (byte[])caCertificateDer.Clone();
            _crlDer = (byte[])crlDer.Clone();
        }

        internal bool Validate(
            X509Certificate certificate,
            SslPolicyErrors policyErrors,
            DateTime utcNow)
        {
            if (_caCertificateDer == null
                || certificate == null
                || utcNow.Kind != DateTimeKind.Utc
                || (policyErrors
                    & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            {
                return false;
            }

            try
            {
                byte[] leafDer = certificate.GetRawCertData();
                try
                {
                    string peerHostName = ReadExactDirectoryDnsName(
                        leafDer,
                        _peerIpv4Address);
                    DirectoryEndpointIdentity identity;
                    EndpointIdentityValidationError identityError;
                    if (!DirectoryEndpointIdentity.TryCreate(
                            peerHostName,
                            _peerIpv4Address,
                            out identity,
                            out identityError))
                    {
                        return false;
                    }

                    SiteCertificateAuthority.ValidateDirectoryLeaf(
                        leafDer,
                        _caCertificateDer,
                        identity,
                        utcNow);
                    ValidateCrlAndRevocation(leafDer, utcNow);
                    return true;
                }
                finally
                {
                    Array.Clear(leafDer, 0, leafDer.Length);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is InvalidDataException
                || exception is IOException
                || exception is CryptographicException
                || exception is Org.BouncyCastle.Security
                    .GeneralSecurityException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            Clear(_caCertificateDer);
            Clear(_crlDer);
            _caCertificateDer = null;
            _crlDer = null;
        }

        private void ValidateCrlAndRevocation(
            byte[] leafDer,
            DateTime utcNow)
        {
            var authority = new Org.BouncyCastle.X509.X509Certificate(
                _caCertificateDer);
            var leaf = new Org.BouncyCastle.X509.X509Certificate(leafDer);
            Org.BouncyCastle.X509.X509Crl crl =
                new Org.BouncyCastle.X509.X509CrlParser().ReadCrl(_crlDer);
            if (crl == null)
            {
                throw new InvalidDataException(
                    "The Peer Directory CRL is missing.");
            }

            crl.Verify(authority.GetPublicKey());

            byte[] canonicalDer = crl.GetEncoded();
            try
            {
                DateTime thisUpdateUtc = crl.ThisUpdate.ToUniversalTime();
                DateTime? nextUpdateUtc = crl.NextUpdate.HasValue
                    ? crl.NextUpdate.Value.ToUniversalTime()
                    : (DateTime?)null;
                if (!BytesEqual(canonicalDer, _crlDer)
                    || !crl.IssuerDN.Equivalent(authority.SubjectDN)
                    || thisUpdateUtc > utcNow
                    || !nextUpdateUtc.HasValue
                    || nextUpdateUtc.Value <= utcNow
                    || crl.GetRevokedCertificate(leaf.SerialNumber) != null)
                {
                    throw new InvalidDataException(
                        "The Peer Directory certificate CRL validation failed.");
                }
            }
            finally
            {
                Array.Clear(canonicalDer, 0, canonicalDer.Length);
            }
        }

        private static string ReadExactDirectoryDnsName(
            byte[] leafDer,
            string expectedIpv4Address)
        {
            var certificate =
                new Org.BouncyCastle.X509.X509Certificate(leafDer);
            GeneralNames alternativeNames =
                certificate.GetSubjectAlternativeNameExtension();
            if (alternativeNames == null)
            {
                throw new InvalidDataException(
                    "The Peer Directory certificate is missing SAN values.");
            }

            GeneralName[] names = alternativeNames.GetNames();
            if (names.Length != 2)
            {
                throw new InvalidDataException(
                    "The Peer Directory certificate SAN pair is invalid.");
            }

            string dnsName = null;
            string ipv4Address = null;
            foreach (GeneralName name in names)
            {
                if (name.TagNo == GeneralName.DnsName && dnsName == null)
                {
                    dnsName = DerIA5String.GetInstance(name.Name)
                        .GetString();
                }
                else if (name.TagNo == GeneralName.IPAddress
                    && ipv4Address == null)
                {
                    byte[] address = Asn1OctetString.GetInstance(name.Name)
                        .GetOctets();
                    if (address.Length == 4)
                    {
                        ipv4Address = new IPAddress(address).ToString();
                    }
                }
                else
                {
                    throw new InvalidDataException(
                        "The Peer Directory certificate SAN types are invalid.");
                }
            }

            if (dnsName == null
                || !StringComparer.Ordinal.Equals(
                    ipv4Address,
                    expectedIpv4Address))
            {
                throw new InvalidDataException(
                    "The Peer Directory certificate does not match the endpoint IPv4 address.");
            }

            return dnsName;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
