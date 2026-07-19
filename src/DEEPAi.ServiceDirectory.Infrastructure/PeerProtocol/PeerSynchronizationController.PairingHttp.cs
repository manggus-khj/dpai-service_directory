using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private const string PairingHelloPath =
            "/api/sync/pairing/hello";
        private const string PairingKeyConfirmPath =
            "/api/sync/pairing/key-confirm";
        private const string PairingDecisionPath =
            "/api/sync/pairing/decision";
        private const string PairingCommitPath =
            "/api/sync/pairing/commit";

        private PeerHttpResponseData ProcessPairingRequest(
            PeerHttpHandlerRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            switch (request.AbsolutePath)
            {
                case PairingHelloPath:
                    return ProcessPairingHello(request);
                case PairingKeyConfirmPath:
                    return ProcessPairingKeyConfirm(request);
                case PairingDecisionPath:
                    return ProcessPairingDecision(request);
                case PairingCommitPath:
                    return ProcessPairingCommit(request);
                default:
                    return PeerHttpResponseData.Bodyless(404);
            }
        }

        private PeerHttpResponseData ProcessPairingHello(
            PeerHttpHandlerRequest request)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!HasActiveTransientPairingLocked()
                    || !IsPairingRemoteAddressAllowedLocked(request))
                {
                    if (HasActiveTransientPairingLocked())
                    {
                        WritePairingRemoteEndpointFailure(
                            request,
                            SecurityAuditOperation.PeerPairingHello);
                        return PeerHttpResponseData.Bodyless(403);
                    }

                    return CreateUnsignedPairingError(
                        409,
                        PeerSyncResponseCode.Conflict);
                }

                byte[] body = null;
                try
                {
                    body = request.GetBody();
                    PeerPairingHelloRequest hello = PeerSyncXmlCodec
                        .ParsePairingHelloRequest(body);
                    PairingHelloDisposition disposition =
                        _pairing.ReceiveHello(
                            hello,
                            _pairing.CurrentPeerEndpoint);
                    if (disposition
                        == PairingHelloDisposition.RetainedInitiator)
                    {
                        return CreateUnsignedPairingError(
                            409,
                            PeerSyncResponseCode.Conflict);
                    }

                    if (disposition
                        == PairingHelloDisposition.AcceptedAsResponder)
                    {
                        DisposePairingKeyAgreementLocked();
                        _pairingGeneration = NextGeneration(
                            _pairingGeneration);
                        _responderHelloResult =
                            CreateResponderHelloResultLocked(hello);
                    }
                    else if (_responderHelloResult == null)
                    {
                        FailTransientPairingLocked();
                        return CreateUnsignedPairingError(
                            500,
                            PeerSyncResponseCode.Internal);
                    }

                    byte[] responseBody = PeerSyncXmlCodec
                        .SerializeControlResponse(
                            PeerControlResponse
                                .CreatePairingHelloSuccess(
                                    _responderHelloResult));
                    try
                    {
                        return PeerHttpResponseData.Xml(
                            200,
                            responseBody,
                            null);
                    }
                    finally
                    {
                        Clear(responseBody);
                    }
                }
                catch (PeerSyncProtocolException)
                {
                    FailTransientPairingLocked();
                    return CreateUnsignedPairingError(
                        400,
                        PeerSyncResponseCode.BadRequest);
                }
                catch (ArgumentException)
                {
                    FailTransientPairingLocked();
                    return CreateUnsignedPairingError(
                        400,
                        PeerSyncResponseCode.BadRequest);
                }
                catch (InvalidOperationException)
                {
                    FailTransientPairingLocked();
                    return CreateUnsignedPairingError(
                        409,
                        PeerSyncResponseCode.Conflict);
                }
                catch (CryptographicException)
                {
                    FailTransientPairingLocked();
                    return CreateUnsignedPairingError(
                        400,
                        PeerSyncResponseCode.BadRequest);
                }
                finally
                {
                    Clear(body);
                }
            }
        }

        private PeerHttpResponseData ProcessPairingKeyConfirm(
            PeerHttpHandlerRequest request)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!HasActiveTransientPairingLocked())
                {
                    WritePairingAuthenticationFailure(
                        request,
                        SecurityAuditOperation.PeerPairingKeyConfirm,
                        SecurityAuditReason.SessionInvalid);
                    return PeerHttpResponseData.Bodyless(401);
                }

                if (!IsPairingRemoteAddressAllowedLocked(request))
                {
                    WritePairingRemoteEndpointFailure(
                        request,
                        SecurityAuditOperation.PeerPairingKeyConfirm);
                    return PeerHttpResponseData.Bodyless(403);
                }

                byte[] body = null;
                try
                {
                    body = request.GetBody();
                    PeerPairingKeyConfirmation remoteConfirmation =
                        PeerSyncXmlCodec.ParsePairingKeyConfirmRequest(
                            body);
                    _pairing.AcceptRemoteKeyConfirmation(
                        remoteConfirmation);
                    PeerPairingKeyConfirmation localConfirmation =
                        _pairing.CreateLocalKeyConfirmation();
                    byte[] responseBody = PeerSyncXmlCodec
                        .SerializeControlResponse(
                            PeerControlResponse
                                .CreatePairingKeyConfirmSuccess(
                                    localConfirmation));
                    try
                    {
                        return PeerHttpResponseData.Xml(
                            200,
                            responseBody,
                            null);
                    }
                    finally
                    {
                        Clear(responseBody);
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
                        SecurityAuditOperation.PeerPairingKeyConfirm,
                        SecurityAuditReason.SignatureInvalid);
                    FailTransientPairingLocked();
                    return PeerHttpResponseData.Bodyless(401);
                }
                finally
                {
                    Clear(body);
                }
            }
        }

        private PeerHttpResponseData ProcessPairingDecision(
            PeerHttpHandlerRequest request)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!HasActiveTransientPairingLocked())
                {
                    PeerHttpResponseData replayResponse;
                    if (TryReplayCompletedPairingDecisionLocked(
                            request,
                            out replayResponse))
                    {
                        return replayResponse;
                    }

                    WritePairingAuthenticationFailure(
                        request,
                        SecurityAuditOperation.PeerPairingDecision,
                        SecurityAuditReason.SessionInvalid);
                    return PeerHttpResponseData.Bodyless(401);
                }

                if (!IsPairingRemoteAddressAllowedLocked(request))
                {
                    WritePairingRemoteEndpointFailure(
                        request,
                        SecurityAuditOperation.PeerPairingDecision);
                    return PeerHttpResponseData.Bodyless(403);
                }

                byte[] requestMac = null;
                if (!PairingMacHeaderCodec.TryParseExactlyOne(
                        request.GetHeaderValues(
                            PairingMacHeaderCodec.HeaderName),
                        out requestMac))
                {
                    WritePairingAuthenticationFailure(
                        request,
                        SecurityAuditOperation.PeerPairingDecision,
                        SecurityAuditReason
                            .AuthenticationDataMissingOrMalformed);
                    FailTransientPairingLocked();
                    return PeerHttpResponseData.Bodyless(401);
                }

                byte[] requestBody = null;
                byte[] successBody = null;
                try
                {
                    string peerEndpoint = _pairing.CurrentPeerEndpoint;
                    long replayDeadlineTimestamp =
                        PairingDecisionReplayEntry.CreateDeadline(
                            _pairing.RemainingPairingTime);
                    requestBody = request.GetBody();
                    successBody = PeerSyncXmlCodec
                        .SerializeControlResponse(
                            PeerControlResponse.CreateUnitSuccess());
                    using (PairingRemoteDecisionResult result =
                        _pairing.ProcessRemoteDecision(
                            requestBody,
                            requestMac,
                            200,
                            successBody))
                    {
                        CapturePairingDecisionReplayLocked(
                            result,
                            peerEndpoint,
                            replayDeadlineTimestamp,
                            requestBody,
                            requestMac);
                        if (result.PairingCancelled)
                        {
                            byte[] cancelledBody =
                                result.CopyResponseBody();
                            byte[] cancelledMac =
                                result.CopyResponseMac();
                            try
                            {
                                DisposeTransientPairingLocked();
                                return CreatePairingMacResponse(
                                    200,
                                    cancelledBody,
                                    cancelledMac);
                            }
                            finally
                            {
                                Clear(cancelledBody);
                                Clear(cancelledMac);
                            }
                        }

                        if (_pairing.State
                                == PairingNegotiationState.BothConfirmed
                            && !PersistBothConfirmedLocked())
                        {
                            return CreateSignedPairingDecisionErrorLocked(
                                requestBody,
                                requestMac,
                                500,
                                PeerSyncResponseCode.Internal);
                        }

                        byte[] responseBody = result.CopyResponseBody();
                        byte[] responseMac = result.CopyResponseMac();
                        try
                        {
                            return CreatePairingMacResponse(
                                200,
                                responseBody,
                                responseMac);
                        }
                        finally
                        {
                            Clear(responseBody);
                            Clear(responseMac);
                        }
                    }
                }
                catch (PairingDecisionConflictException exception)
                {
                    return HandleAuthenticatedPairingDecisionConflictLocked(
                        requestBody,
                        requestMac,
                        exception);
                }
                catch (Exception exception) when (
                    exception is PeerSyncProtocolException
                    || exception is ArgumentException
                    || exception is InvalidOperationException
                    || exception is CryptographicException)
                {
                    WritePairingAuthenticationFailure(
                        request,
                        SecurityAuditOperation.PeerPairingDecision,
                        SecurityAuditReason.SignatureInvalid);
                    FailTransientPairingLocked();
                    return PeerHttpResponseData.Bodyless(401);
                }
                finally
                {
                    Clear(requestMac);
                    Clear(requestBody);
                    Clear(successBody);
                }
            }
        }

        private PeerPairingHelloResult CreateResponderHelloResultLocked(
            PeerPairingHelloRequest hello)
        {
            if (hello == null)
            {
                throw new ArgumentNullException(nameof(hello));
            }

            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            if (hello.InitiatorLastPeerKeyEpoch == ulong.MaxValue
                || configuration.LastPeerKeyEpoch == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The pairing key epoch is exhausted.");
            }

            ulong keyEpoch = Math.Max(
                hello.InitiatorLastPeerKeyEpoch,
                configuration.LastPeerKeyEpoch) + 1UL;
            byte[] responderNonce = null;
            byte[] responderPublicKey = null;
            byte[] initiatorNonce = null;
            byte[] initiatorPublicKey = null;
            byte[] transcriptHash = null;
            PairingSecretContext secretContext = null;
            using (ECDiffieHellmanCng keyAgreement =
                CreatePairingKeyAgreement())
            {
                try
                {
                    responderNonce = CreateRandomPairingBytes(
                        PeerSyncContract.PairingNonceLength);
                    responderPublicKey = keyAgreement.Key.Export(
                        CngKeyBlobFormat.EccPublicBlob);
                    var localAddress = RequireListenerAddress(
                        configuration.ListenAddress);
                    string localEndpoint = localAddress.HttpPrefix
                        .TrimEnd('/');
                    var result = new PeerPairingHelloResult(
                        hello.PairingId,
                        configuration.InstanceId,
                        localEndpoint,
                        responderNonce,
                        responderPublicKey,
                        configuration.LastPeerKeyEpoch,
                        keyEpoch);

                    initiatorNonce = hello.CopyInitiatorNonce();
                    initiatorPublicKey = hello
                        .CopyInitiatorPublicKey();
                    transcriptHash = PairingTranscript.CreateHash(
                        hello.PairingId,
                        hello.InitiatorInstanceId,
                        configuration.InstanceId,
                        hello.InitiatorEndpoint,
                        localEndpoint,
                        initiatorNonce,
                        responderNonce,
                        initiatorPublicKey,
                        responderPublicKey,
                        hello.InitiatorLastPeerKeyEpoch,
                        configuration.LastPeerKeyEpoch,
                        keyEpoch);
                    secretContext = PairingSecretContext
                        .CreateFromKeyAgreement(
                            keyAgreement,
                            initiatorPublicKey,
                            transcriptHash);
                    _pairing.CompleteHello(result, secretContext);
                    secretContext = null;
                    return result;
                }
                finally
                {
                    if (secretContext != null)
                    {
                        secretContext.Dispose();
                    }

                    Clear(responderNonce);
                    Clear(responderPublicKey);
                    Clear(initiatorNonce);
                    Clear(initiatorPublicKey);
                    Clear(transcriptHash);
                }
            }
        }

        private bool HasActiveTransientPairingLocked()
        {
            return _pairing != null
                && _pairing.State != PairingNegotiationState.Unpaired;
        }

        private bool IsPairingRemoteAddressAllowedLocked(
            PeerHttpHandlerRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string peerEndpoint = _pairing == null
                ? null
                : _pairing.CurrentPeerEndpoint;
            return IsRemoteAddressForPeerEndpoint(
                request.RemoteEndpoint,
                peerEndpoint);
        }

        private static bool IsRemoteAddressForPeerEndpoint(
            IPEndPoint remoteEndpoint,
            string peerEndpoint)
        {
            if (remoteEndpoint == null
                || remoteEndpoint.Address == null
                || string.IsNullOrEmpty(peerEndpoint))
            {
                return false;
            }

            Uri uri;
            IPAddress configuredAddress;
            return Uri.TryCreate(peerEndpoint, UriKind.Absolute, out uri)
                && IPAddress.TryParse(uri.Host, out configuredAddress)
                && configuredAddress.Equals(remoteEndpoint.Address);
        }

        private void FailTransientPairingLocked()
        {
            if (_pairing == null)
            {
                return;
            }

            if (_pairing.State != PairingNegotiationState.BothConfirmed)
            {
                _pairing.FailCurrentPairing();
                DisposeTransientPairingLocked();
                DisposePairingDecisionReplayLocked();
            }
        }

        private void DisposePairingKeyAgreementLocked()
        {
            if (_pairingKeyAgreement != null)
            {
                _pairingKeyAgreement.Dispose();
                _pairingKeyAgreement = null;
            }
        }

        private static ECDiffieHellmanCng CreatePairingKeyAgreement()
        {
            return new ECDiffieHellmanCng(256);
        }

        private static byte[] CreateRandomPairingBytes(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var value = new byte[count];
            using (RandomNumberGenerator random =
                RandomNumberGenerator.Create())
            {
                random.GetBytes(value);
            }

            return value;
        }

        private static PeerHttpResponseData CreateUnsignedPairingError(
            int httpStatus,
            PeerSyncResponseCode code)
        {
            byte[] body = PeerSyncXmlCodec.SerializeControlResponse(
                PeerControlResponse.CreateError(code));
            try
            {
                return PeerHttpResponseData.Xml(
                    httpStatus,
                    body,
                    null);
            }
            finally
            {
                Clear(body);
            }
        }

        private static PeerHttpResponseData CreatePairingMacResponse(
            int httpStatus,
            byte[] body,
            byte[] mac)
        {
            var headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                {
                    PairingMacHeaderCodec.HeaderName,
                    PairingMacHeaderCodec.Format(mac)
                }
            };
            return PeerHttpResponseData.Xml(
                httpStatus,
                body,
                headers);
        }

        private void WritePairingAuthenticationFailure(
            PeerHttpHandlerRequest request,
            SecurityAuditOperation operation,
            SecurityAuditReason reason)
        {
            IPEndPoint remoteEndpoint = request.RemoteEndpoint;
            _securityAuditLogger.WriteFailure(
                SecurityAuditEventId.PeerAuthenticationRejected,
                SecurityAuditBoundary.Peer,
                operation,
                reason,
                request.RequestId,
                null,
                remoteEndpoint == null
                    ? null
                    : remoteEndpoint.Address);
        }

        private void WritePairingRemoteEndpointFailure(
            PeerHttpHandlerRequest request,
            SecurityAuditOperation operation)
        {
            IPEndPoint remoteEndpoint = request.RemoteEndpoint;
            _securityAuditLogger.WriteFailure(
                SecurityAuditEventId.NetworkBoundaryRejected,
                SecurityAuditBoundary.Peer,
                operation,
                SecurityAuditReason.RemoteEndpointMismatch,
                request.RequestId,
                null,
                remoteEndpoint == null
                    ? null
                    : remoteEndpoint.Address);
        }
    }
}
