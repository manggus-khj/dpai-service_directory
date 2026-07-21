using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public interface ICertificateAuthorityPeerSynchronization
    {
        CertificateAuthorityIssuerRole GetPeerPkiRole();

        PeerPkiState GetPeerPkiState();

        PeerPkiState GetKnownPeerPkiState();

        void ApplyPeerPkiState(PeerPkiState state, DateTime utcNow);
    }

    public interface IPeerTlsTrustProvider
    {
        PeerTlsTrustSnapshot CapturePeerTlsTrust(
            string peerEndpoint,
            DateTime utcNow);
    }
}
