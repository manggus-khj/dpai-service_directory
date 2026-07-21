using System;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private SyncCycleOutcome SynchronizePeerPkiState(
            PairedPeerCredential credential,
            ActivePeerSession session,
            long? clockSkewSeconds)
        {
            if (_peerPki == null)
            {
                return SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.Internal,
                    clockSkewSeconds);
            }

            try
            {
                CertificateAuthorityIssuerRole role =
                    _peerPki.GetPeerPkiRole();
                if (role == CertificateAuthorityIssuerRole.ActiveIssuer)
                {
                    return SyncCycleOutcome.Success(clockSkewSeconds);
                }

                if (role != CertificateAuthorityIssuerRole.Standby)
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Conflict,
                        clockSkewSeconds);
                }

                PeerPkiState known = _peerPki.GetKnownPeerPkiState();
                var request = new PeerPkiStateRequest(
                    credential.LocalInstanceId,
                    known.IssuerInstanceId,
                    known.PkiRevision,
                    known.CrlNumber);
                byte[] requestBody = null;
                byte[] responseBody = null;
                try
                {
                    requestBody = PeerContractXmlCodec
                        .SerializePkiStateRequest(request);
                    using (OutboundRequestResult requestResult =
                        SendAuthenticatedRequest(
                            credential,
                            null,
                            session,
                            OutboundPeerAuthenticationPurpose.Session,
                            PeerAuthenticationContract.PkiStatePath,
                            requestBody))
                    {
                        if (!requestResult.IsVerified)
                        {
                            return SyncCycleOutcome.Failure(
                                requestResult.FailureCode,
                                clockSkewSeconds);
                        }

                        responseBody = requestResult.Response.CopyBody();
                        PeerPkiStateResponse response = PeerContractXmlCodec
                            .ParseAuthenticatedPkiStateResponse(
                                responseBody,
                                request,
                                known);
                        if (!IsHttpStatusConsistent(
                                requestResult.Response.StatusCode,
                                response.Code))
                        {
                            return SyncCycleOutcome.Failure(
                                PeerSyncResponseCode.BadRequest,
                                clockSkewSeconds);
                        }

                        if (!response.IsSuccess)
                        {
                            return SyncCycleOutcome.Failure(
                                response.Code,
                                clockSkewSeconds);
                        }

                        _peerPki.ApplyPeerPkiState(
                            response.PkiState,
                            GetUtcNow());
                        return SyncCycleOutcome.Success(clockSkewSeconds);
                    }
                }
                finally
                {
                    Clear(requestBody);
                    Clear(responseBody);
                }
            }
            catch (PeerSyncProtocolException)
            {
                return SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.BadRequest,
                    clockSkewSeconds);
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                || exception is InvalidOperationException
                || exception is ArgumentException
                || exception is OverflowException)
            {
                return SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.Conflict,
                    clockSkewSeconds);
            }
        }
    }
}
