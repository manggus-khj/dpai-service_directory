using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private static readonly TimeSpan PairingControlTimeout =
            TimeSpan.FromSeconds(10);
        private const int PairingTransportAttempts = 3;

        private PeerHttpResponseData ProcessPairingCommit(
            PeerHttpHandlerRequest request)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                using (PairedPeerCredential credential =
                    _configurationState.CopyCredential())
                {
                    if (credential == null)
                    {
                        WritePairingAuthenticationFailure(
                            request,
                            SecurityAuditOperation.PeerPairingCommit,
                            SecurityAuditReason.SessionInvalid);
                        return PeerHttpResponseData.Bodyless(401);
                    }

                    byte[] requestMac = null;
                    if (!PairingMacHeaderCodec.TryParseExactlyOne(
                            request.GetHeaderValues(
                                PairingMacHeaderCodec.HeaderName),
                            out requestMac))
                    {
                        WritePairingAuthenticationFailure(
                            request,
                            SecurityAuditOperation.PeerPairingCommit,
                            SecurityAuditReason
                                .AuthenticationDataMissingOrMalformed);
                        return PeerHttpResponseData.Bodyless(401);
                    }

                    byte[] requestBody = null;
                    byte[] transcriptHash = null;
                    byte[] pairRoot = null;
                    byte[] expectedRequestMac = null;
                    try
                    {
                        requestBody = request.GetBody();
                        PeerPairingCommit commit = PeerSyncXmlCodec
                            .ParsePairingCommitRequest(requestBody);
                        transcriptHash = credential
                            .CopyTranscriptHash();
                        pairRoot = credential.CopyPairRoot();
                        expectedRequestMac =
                            PairingTerminalMessageAuthenticator
                                .CreateCommitRequestMac(
                                    pairRoot,
                                    transcriptHash,
                                    commit.PairingId,
                                    commit.KeyEpoch,
                                    ToPairingConfirmationDirection(
                                        commit.SenderRole),
                                    commit.SenderInstanceId,
                                    commit.ReceiverInstanceId);
                        if (!PairingTerminalMessageAuthenticator
                            .VerifyMac(
                                expectedRequestMac,
                                requestMac))
                        {
                            WritePairingAuthenticationFailure(
                                request,
                                SecurityAuditOperation
                                    .PeerPairingCommit,
                                SecurityAuditReason.SignatureInvalid);
                            return PeerHttpResponseData.Bodyless(401);
                        }

                        if (!IsPairingRemoteAddressAllowed(
                                request,
                                credential.PeerEndpoint))
                        {
                            WritePairingRemoteEndpointFailure(
                                request,
                                SecurityAuditOperation
                                    .PeerPairingCommit);
                            return CreateSignedPairingCommitError(
                                credential,
                                requestMac,
                                403,
                                PeerSyncResponseCode.NotPeer);
                        }

                        if (!IsPairingCommitBoundToCredential(
                                commit,
                                credential,
                                transcriptHash))
                        {
                            WritePairingAuthenticationFailure(
                                request,
                                SecurityAuditOperation
                                    .PeerPairingCommit,
                                SecurityAuditReason
                                    .PeerBindingMismatch);
                            return CreateSignedPairingCommitError(
                                credential,
                                requestMac,
                                409,
                                PeerSyncResponseCode.PeerMismatch);
                        }

                        if (GetUtcNow() >= credential.CommitExpiresUtc)
                        {
                            return CreateSignedPairingCommitError(
                                credential,
                                requestMac,
                                409,
                                PeerSyncResponseCode.Conflict);
                        }

                        if (credential.RemoteCommitConfirmed)
                        {
                            return CreateStoredRemoteCommitResponse(
                                credential,
                                requestMac);
                        }

                        byte[] responseBody = PeerSyncXmlCodec
                            .SerializeControlResponse(
                                PeerControlResponse
                                    .CreateUnitSuccess());
                        byte[] responseMac = null;
                        try
                        {
                            responseMac =
                                PairingTerminalMessageAuthenticator
                                    .CreateCommitResponseMac(
                                        pairRoot,
                                        transcriptHash,
                                        credential.PairingId,
                                        credential.KeyEpoch,
                                        ToPairingConfirmationDirection(
                                            credential.LocalRole),
                                        credential.LocalInstanceId,
                                        credential.PeerInstanceId,
                                        requestMac,
                                        200,
                                        "OK",
                                        0,
                                        responseBody);
                            using (var evidence =
                                new PairingCommitEvidence(
                                    requestMac,
                                    200,
                                    responseBody,
                                    responseMac))
                            {
                                if (!PersistPairingCommitEvidenceLocked(
                                        false,
                                        evidence))
                                {
                                    return CreateSignedPairingCommitError(
                                        credential,
                                        requestMac,
                                        500,
                                        PeerSyncResponseCode.Internal);
                                }
                            }

                            PeerHttpResponseData response =
                                CreatePairingMacResponse(
                                    200,
                                    responseBody,
                                    responseMac);
                            QueuePairingCommitAttempt();
                            return response;
                        }
                        finally
                        {
                            Clear(responseBody);
                            Clear(responseMac);
                        }
                    }
                    catch (Exception exception) when (
                        exception is PeerSyncProtocolException
                        || exception is ArgumentException
                        || exception is InvalidOperationException
                        || exception is CryptographicException)
                    {
                        WritePairingAuthenticationFailure(
                            request,
                            SecurityAuditOperation.PeerPairingCommit,
                            SecurityAuditReason
                                .AuthenticationDataMissingOrMalformed);
                        return PeerHttpResponseData.Bodyless(401);
                    }
                    finally
                    {
                        Clear(requestMac);
                        Clear(requestBody);
                        Clear(transcriptHash);
                        Clear(pairRoot);
                        Clear(expectedRequestMac);
                    }
                }
            }
        }

        private void SendLocalPairingCommit()
        {
            PairedPeerCredential credential;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                if (configuration.Synchronization.State
                        != DurableSynchronizationState
                            .PairedPendingCommit
                    || configuration.Synchronization
                        .LocalCommitConfirmed == true
                    || GetUtcNow() >= configuration.Synchronization
                        .CommitExpiresUtc.Value)
                {
                    return;
                }

                credential = _configurationState.CopyCredential();
            }

            using (credential)
            {
                if (credential == null
                    || credential.State
                        != DurablePeerCredentialState
                            .PairedPendingCommit
                    || credential.LocalCommitConfirmed)
                {
                    return;
                }

                byte[] transcriptHash = null;
                byte[] pairRoot = null;
                byte[] requestBody = null;
                byte[] requestMac = null;
                try
                {
                    transcriptHash = credential.CopyTranscriptHash();
                    pairRoot = credential.CopyPairRoot();
                    var requestModel = new PeerPairingCommit(
                        credential.PairingId,
                        credential.KeyEpoch,
                        ToWirePairingRole(credential.LocalRole),
                        credential.LocalInstanceId,
                        credential.PeerInstanceId,
                        transcriptHash);
                    requestBody = PeerSyncXmlCodec
                        .SerializePairingCommitRequest(requestModel);
                    requestMac = PairingTerminalMessageAuthenticator
                        .CreateCommitRequestMac(
                            pairRoot,
                            transcriptHash,
                            credential.PairingId,
                            credential.KeyEpoch,
                            ToPairingConfirmationDirection(
                                credential.LocalRole),
                            credential.LocalInstanceId,
                            credential.PeerInstanceId);
                    var headers = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        {
                            PairingMacHeaderCodec.HeaderName,
                            PairingMacHeaderCodec.Format(requestMac)
                        }
                    };
                    var outboundRequest = new PeerOutboundHttpRequest(
                        credential.PeerEndpoint,
                        PairingCommitPath,
                        requestBody,
                        headers,
                        PairingControlTimeout);

                    for (int attempt = 0;
                        attempt < PairingTransportAttempts;
                        attempt++)
                    {
                        PeerHttpTransportResult transportResult =
                            _transport.Send(outboundRequest);
                        if (!transportResult.IsSuccess)
                        {
                            continue;
                        }

                        byte[] responseBody = null;
                        byte[] responseMac = null;
                        byte[] expectedResponseMac = null;
                        try
                        {
                            PeerInboundHttpResponse response =
                                transportResult.Response;
                            if (!TryReadPairingXmlResponse(
                                    response,
                                    out responseBody)
                                || !PairingMacHeaderCodec
                                    .TryParseExactlyOne(
                                        response.GetHeaderValues(
                                            PairingMacHeaderCodec
                                                .HeaderName),
                                        out responseMac))
                            {
                                continue;
                            }

                            PeerControlResponse parsed =
                                PeerSyncXmlCodec
                                    .ParsePairingCommitResponse(
                                        responseBody);
                            expectedResponseMac =
                                PairingTerminalMessageAuthenticator
                                    .CreateCommitResponseMac(
                                        pairRoot,
                                        transcriptHash,
                                        credential.PairingId,
                                        credential.KeyEpoch,
                                        ToPairingConfirmationDirection(
                                            OppositePairingRole(
                                                credential.LocalRole)),
                                        credential.PeerInstanceId,
                                        credential.LocalInstanceId,
                                        requestMac,
                                        response.StatusCode,
                                        parsed.Result,
                                        checked((uint)parsed.Code),
                                        responseBody);
                            if (!PairingTerminalMessageAuthenticator
                                    .VerifyMac(
                                        expectedResponseMac,
                                        responseMac)
                                || response.StatusCode != 200
                                || !parsed.IsSuccess)
                            {
                                continue;
                            }

                            using (var evidence =
                                new PairingCommitEvidence(
                                    requestMac,
                                    response.StatusCode,
                                    responseBody,
                                    responseMac))
                            {
                                lock (_gate)
                                {
                                    if (_disposed)
                                    {
                                        return;
                                    }

                                    ServiceDirectoryConfiguration current =
                                        _configurationState.GetCurrent();
                                    if (current.Synchronization.State
                                            == DurableSynchronizationState
                                                .PairedDisabled
                                        && current.Synchronization.KeyEpoch
                                            == credential.KeyEpoch)
                                    {
                                        return;
                                    }

                                    if (current.Synchronization.State
                                            != DurableSynchronizationState
                                                .PairedPendingCommit
                                        || current.Synchronization.PairingId
                                            != credential.PairingId
                                        || !PersistPairingCommitEvidenceLocked(
                                            true,
                                            evidence))
                                    {
                                        return;
                                    }
                                }
                            }

                            return;
                        }
                        catch (Exception exception) when (
                            exception is PeerSyncProtocolException
                            || exception is ArgumentException
                            || exception is InvalidOperationException
                            || exception is CryptographicException)
                        {
                            // Retry only the same canonical commit request.
                        }
                        finally
                        {
                            Clear(responseBody);
                            Clear(responseMac);
                            Clear(expectedResponseMac);
                        }
                    }
                }
                finally
                {
                    Clear(transcriptHash);
                    Clear(pairRoot);
                    Clear(requestBody);
                    Clear(requestMac);
                }
            }
        }

        private static bool IsPairingCommitBoundToCredential(
            PeerPairingCommit commit,
            PairedPeerCredential credential,
            byte[] credentialTranscriptHash)
        {
            if (commit == null
                || credential == null
                || commit.PairingId != credential.PairingId
                || commit.KeyEpoch != credential.KeyEpoch
                || commit.SenderRole
                    != ToWirePairingRole(
                        OppositePairingRole(credential.LocalRole))
                || commit.SenderInstanceId
                    != credential.PeerInstanceId
                || commit.ReceiverInstanceId
                    != credential.LocalInstanceId)
            {
                return false;
            }

            byte[] requestTranscriptHash = null;
            try
            {
                requestTranscriptHash = commit.CopyTranscriptHash();
                return PairingCryptography.FixedTimeEquals32(
                    credentialTranscriptHash,
                    requestTranscriptHash);
            }
            finally
            {
                Clear(requestTranscriptHash);
            }
        }

        private static bool IsPairingRemoteAddressAllowed(
            PeerHttpHandlerRequest request,
            string peerEndpoint)
        {
            return request != null
                && IsRemoteAddressForPeerEndpoint(
                    request.RemoteEndpoint,
                    peerEndpoint);
        }

        private static PeerHttpResponseData CreateStoredRemoteCommitResponse(
            PairedPeerCredential credential,
            byte[] requestMac)
        {
            PairingCommitEvidence evidence =
                credential.RemoteCommitEvidence;
            if (evidence == null)
            {
                return CreateSignedPairingCommitError(
                    credential,
                    requestMac,
                    500,
                    PeerSyncResponseCode.Internal);
            }

            byte[] storedRequestMac = null;
            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                storedRequestMac = evidence.CopyRequestMac();
                if (!PairingTerminalMessageAuthenticator.VerifyMac(
                        storedRequestMac,
                        requestMac))
                {
                    return CreateSignedPairingCommitError(
                        credential,
                        requestMac,
                        409,
                        PeerSyncResponseCode.Conflict);
                }

                responseBody = evidence.CopyResponseBody();
                responseMac = evidence.CopyResponseMac();
                return CreatePairingMacResponse(
                    evidence.ResponseStatusCode,
                    responseBody,
                    responseMac);
            }
            finally
            {
                Clear(storedRequestMac);
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private static PeerHttpResponseData CreateSignedPairingCommitError(
            PairedPeerCredential credential,
            byte[] requestMac,
            int httpStatus,
            PeerSyncResponseCode code)
        {
            byte[] transcriptHash = null;
            byte[] pairRoot = null;
            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                transcriptHash = credential.CopyTranscriptHash();
                pairRoot = credential.CopyPairRoot();
                PeerControlResponse error =
                    PeerControlResponse.CreateError(code);
                responseBody = PeerSyncXmlCodec
                    .SerializeControlResponse(error);
                responseMac = PairingTerminalMessageAuthenticator
                    .CreateCommitResponseMac(
                        pairRoot,
                        transcriptHash,
                        credential.PairingId,
                        credential.KeyEpoch,
                        ToPairingConfirmationDirection(
                            credential.LocalRole),
                        credential.LocalInstanceId,
                        credential.PeerInstanceId,
                        requestMac,
                        httpStatus,
                        error.Result,
                        checked((uint)error.Code),
                        responseBody);
                return CreatePairingMacResponse(
                    httpStatus,
                    responseBody,
                    responseMac);
            }
            finally
            {
                Clear(transcriptHash);
                Clear(pairRoot);
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private static bool TryReadPairingXmlResponse(
            PeerInboundHttpResponse response,
            out byte[] body)
        {
            body = null;
            if (response == null
                || !StringComparer.Ordinal.Equals(
                    response.ContentType,
                    PeerSyncContract.XmlContentType)
                || !string.IsNullOrEmpty(response.ContentEncoding))
            {
                return false;
            }

            byte[] candidate = response.CopyBody();
            if (candidate.Length == 0
                || candidate.Length
                    > PeerSyncContract.MaximumControlBodyBytes)
            {
                Clear(candidate);
                return false;
            }

            body = candidate;
            return true;
        }

        private static PairingConfirmationDirection
            ToPairingConfirmationDirection(PeerPairingRole role)
        {
            switch (role)
            {
                case PeerPairingRole.Initiator:
                    return PairingConfirmationDirection.Initiator;
                case PeerPairingRole.Responder:
                    return PairingConfirmationDirection.Responder;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }
    }
}
