using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private bool _pairingInitiatorWorkerQueued;

        private void QueuePairingInitiatorAttempt()
        {
            lock (_gate)
            {
                if (_disposed
                    || _stopping
                    || _pairingInitiatorWorkerQueued
                    || _pairing == null
                    || _pairing.State
                        != PairingNegotiationState.PairingWindowOpen)
                {
                    return;
                }

                _pairingInitiatorWorkerQueued = true;
            }

            if (!TryQueueBackgroundWork(
                    _ => RunPairingInitiatorAttempt(),
                    null))
            {
                lock (_gate)
                {
                    _pairingInitiatorWorkerQueued = false;
                }
            }
        }

        private void RunPairingInitiatorAttempt()
        {
            PeerPairingHelloRequest hello = null;
            byte[] helloBody = null;
            int generation = 0;
            try
            {
                lock (_gate)
                {
                    if (_disposed
                        || !TryPrepareInitiatorHelloLocked(
                            out hello,
                            out helloBody,
                            out generation))
                    {
                        return;
                    }
                }

                var outboundHello = new PeerOutboundHttpRequest(
                    GetInitiatorPeerEndpoint(generation),
                    PairingHelloPath,
                    helloBody,
                    null,
                    PairingControlTimeout);
                PeerInboundHttpResponse helloResponse = null;
                for (int attempt = 0;
                    attempt < PairingTransportAttempts;
                    attempt++)
                {
                    PeerHttpTransportResult result =
                        _transport.Send(outboundHello);
                    if (!result.IsSuccess)
                    {
                        continue;
                    }

                    if (result.Response.StatusCode == 409)
                    {
                        // In a simultaneous hello, the canonical smaller
                        // InstanceId remains initiator. The other in-flight
                        // hello will switch this side to responder.
                        return;
                    }

                    helloResponse = result.Response;
                    break;
                }

                if (helloResponse == null)
                {
                    return;
                }

                byte[] responseBody = null;
                PeerPairingKeyConfirmation localConfirmation;
                try
                {
                    if (!TryReadPairingXmlResponse(
                            helloResponse,
                            out responseBody)
                        || helloResponse.StatusCode != 200)
                    {
                        FailInitiatorPairingIfCurrent(
                            generation,
                            hello.PairingId);
                        return;
                    }

                    PeerControlResponse parsed = PeerSyncXmlCodec
                        .ParsePairingHelloResponse(responseBody);
                    if (!parsed.IsSuccess
                        || parsed.PairingHello == null)
                    {
                        FailInitiatorPairingIfCurrent(
                            generation,
                            hello.PairingId);
                        return;
                    }

                    lock (_gate)
                    {
                        if (!IsCurrentInitiatorPairingLocked(
                                generation,
                                hello.PairingId))
                        {
                            return;
                        }

                        PairingSecretContext secretContext =
                            CreateInitiatorSecretContextLocked(
                                hello,
                                parsed.PairingHello);
                        _pairing.CompleteHello(
                            parsed.PairingHello,
                            secretContext);
                        DisposePairingKeyAgreementLocked();
                        localConfirmation = _pairing
                            .CreateLocalKeyConfirmation();
                    }
                }
                catch (Exception exception) when (
                    exception is PeerSyncProtocolException
                    || exception is ArgumentException
                    || exception is InvalidOperationException
                    || exception is CryptographicException)
                {
                    FailInitiatorPairingIfCurrent(
                        generation,
                        hello.PairingId);
                    return;
                }
                finally
                {
                    Clear(responseBody);
                }

                SendInitiatorKeyConfirmation(
                    generation,
                    hello.PairingId,
                    localConfirmation);
            }
            catch
            {
                if (hello != null)
                {
                    FailInitiatorPairingIfCurrent(
                        generation,
                        hello.PairingId);
                }
            }
            finally
            {
                Clear(helloBody);
                lock (_gate)
                {
                    _pairingInitiatorWorkerQueued = false;
                }
            }
        }

        private bool TryPrepareInitiatorHelloLocked(
            out PeerPairingHelloRequest hello,
            out byte[] body,
            out int generation)
        {
            hello = null;
            body = null;
            generation = _pairingGeneration;
            if (_pairing == null
                || _pairing.State
                    != PairingNegotiationState.PairingWindowOpen)
            {
                return false;
            }

            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            byte[] nonce = null;
            byte[] publicKey = null;
            ECDiffieHellmanCng keyAgreement = null;
            try
            {
                keyAgreement = CreatePairingKeyAgreement();
                nonce = CreateRandomPairingBytes(
                    PeerSyncContract.PairingNonceLength);
                publicKey = keyAgreement.Key.Export(
                    CngKeyBlobFormat.EccPublicBlob);
                Guid pairingId = CreateNonEmptyPairingId();
                string localEndpoint = RequireListenerAddress(
                    configuration.ListenAddress).HttpPrefix.TrimEnd('/');
                hello = new PeerPairingHelloRequest(
                    pairingId,
                    configuration.InstanceId,
                    localEndpoint,
                    nonce,
                    publicKey,
                    configuration.LastPeerKeyEpoch);
                _pairing.BeginInitiator(hello);
                body = PeerSyncXmlCodec.SerializePairingHelloRequest(
                    hello);
                DisposePairingKeyAgreementLocked();
                _pairingKeyAgreement = keyAgreement;
                keyAgreement = null;
                generation = _pairingGeneration;
                return true;
            }
            finally
            {
                if (keyAgreement != null)
                {
                    keyAgreement.Dispose();
                }

                Clear(nonce);
                Clear(publicKey);
            }
        }

        private string GetInitiatorPeerEndpoint(int generation)
        {
            lock (_gate)
            {
                if (_disposed
                    || generation != _pairingGeneration
                    || _pairing == null)
                {
                    throw new InvalidOperationException(
                        "The initiator pairing changed before transport dispatch.");
                }

                return _pairing.CurrentPeerEndpoint;
            }
        }

        private PairingSecretContext
            CreateInitiatorSecretContextLocked(
            PeerPairingHelloRequest hello,
            PeerPairingHelloResult result)
        {
            if (_pairingKeyAgreement == null)
            {
                throw new InvalidOperationException(
                    "The initiator ECDH key is not available.");
            }

            byte[] initiatorNonce = null;
            byte[] initiatorPublicKey = null;
            byte[] responderNonce = null;
            byte[] responderPublicKey = null;
            byte[] transcriptHash = null;
            try
            {
                initiatorNonce = hello.CopyInitiatorNonce();
                initiatorPublicKey = hello.CopyInitiatorPublicKey();
                responderNonce = result.CopyResponderNonce();
                responderPublicKey = result.CopyResponderPublicKey();
                transcriptHash = PairingTranscript.CreateHash(
                    hello.PairingId,
                    hello.InitiatorInstanceId,
                    result.ResponderInstanceId,
                    hello.InitiatorEndpoint,
                    result.ResponderEndpoint,
                    initiatorNonce,
                    responderNonce,
                    initiatorPublicKey,
                    responderPublicKey,
                    hello.InitiatorLastPeerKeyEpoch,
                    result.ResponderLastPeerKeyEpoch,
                    result.KeyEpoch);
                return PairingSecretContext.CreateFromKeyAgreement(
                    _pairingKeyAgreement,
                    responderPublicKey,
                    transcriptHash);
            }
            finally
            {
                Clear(initiatorNonce);
                Clear(initiatorPublicKey);
                Clear(responderNonce);
                Clear(responderPublicKey);
                Clear(transcriptHash);
            }
        }

        private void SendInitiatorKeyConfirmation(
            int generation,
            Guid pairingId,
            PeerPairingKeyConfirmation localConfirmation)
        {
            byte[] requestBody = null;
            try
            {
                requestBody = PeerSyncXmlCodec
                    .SerializePairingKeyConfirmRequest(
                        localConfirmation);
                string peerEndpoint;
                lock (_gate)
                {
                    if (!IsCurrentInitiatorPairingLocked(
                            generation,
                            pairingId))
                    {
                        return;
                    }

                    peerEndpoint = _pairing.CurrentPeerEndpoint;
                }

                var outboundRequest = new PeerOutboundHttpRequest(
                    peerEndpoint,
                    PairingKeyConfirmPath,
                    requestBody,
                    null,
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
                    try
                    {
                        PeerInboundHttpResponse response =
                            transportResult.Response;
                        if (!TryReadPairingXmlResponse(
                                response,
                                out responseBody)
                            || response.StatusCode != 200)
                        {
                            continue;
                        }

                        PeerControlResponse parsed = PeerSyncXmlCodec
                            .ParsePairingKeyConfirmResponse(
                                responseBody);
                        if (!parsed.IsSuccess
                            || parsed.PairingKeyConfirmation == null)
                        {
                            continue;
                        }

                        lock (_gate)
                        {
                            if (!IsCurrentInitiatorPairingLocked(
                                    generation,
                                    pairingId))
                            {
                                return;
                            }

                            _pairing.AcceptRemoteKeyConfirmation(
                                parsed.PairingKeyConfirmation);
                        }

                        return;
                    }
                    catch (Exception exception) when (
                        exception is PeerSyncProtocolException
                        || exception is ArgumentException
                        || exception is InvalidOperationException
                        || exception is CryptographicException)
                    {
                        // Retry only the same key-confirm request.
                    }
                    finally
                    {
                        Clear(responseBody);
                    }
                }
            }
            finally
            {
                Clear(requestBody);
            }
        }

        private void QueueLocalPairingDecision(
            PairingLocalDecisionMessage decisionMessage)
        {
            if (decisionMessage == null)
            {
                throw new ArgumentNullException(nameof(decisionMessage));
            }

            if (!TryQueueBackgroundWork(
                    _ => RunLocalPairingDecisionWorker(decisionMessage),
                    null))
            {
                decisionMessage.Dispose();
            }
        }

        private void RunLocalPairingDecisionWorker(
            PairingLocalDecisionMessage decisionMessage)
        {
            try
            {
                SendLocalPairingDecision(decisionMessage);
            }
            catch
            {
                decisionMessage.Dispose();
                QueuePairingCommitAttempt();
            }
        }

        private void SendLocalPairingDecision(
            PairingLocalDecisionMessage decisionMessage)
        {
            using (decisionMessage)
            {
                byte[] requestBody = null;
                byte[] requestMac = null;
                try
                {
                    requestBody = decisionMessage.CopyRequestBody();
                    requestMac = decisionMessage.CopyRequestMac();
                    string peerEndpoint;
                    lock (_gate)
                    {
                        if (_disposed
                            || _pairing == null
                            || _pairing.CurrentPairingId
                                != decisionMessage.Request.PairingId)
                        {
                            QueuePairingCommitAttempt();
                            return;
                        }

                        peerEndpoint = _pairing.CurrentPeerEndpoint;
                    }

                    var headers = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        {
                            PairingMacHeaderCodec.HeaderName,
                            PairingMacHeaderCodec.Format(requestMac)
                        }
                    };
                    var outboundRequest = new PeerOutboundHttpRequest(
                        peerEndpoint,
                        PairingDecisionPath,
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

                            lock (_gate)
                            {
                                if (_disposed
                                    || _pairing == null
                                    || _pairing.CurrentPairingId
                                        != decisionMessage.Request
                                            .PairingId)
                                {
                                    QueuePairingCommitAttempt();
                                    return;
                                }

                                PeerControlResponse parsed = _pairing
                                    .VerifyLocalDecisionResponse(
                                        decisionMessage,
                                        response.StatusCode,
                                        responseBody,
                                        responseMac);
                                if (response.StatusCode != 200
                                    || !parsed.IsSuccess)
                                {
                                    return;
                                }

                                if (_pairing.State
                                        == PairingNegotiationState
                                            .BothConfirmed
                                    && !PersistBothConfirmedLocked())
                                {
                                    return;
                                }
                            }

                            QueuePairingCommitAttempt();
                            return;
                        }
                        catch (Exception exception) when (
                            exception is PeerSyncProtocolException
                            || exception is ArgumentException
                            || exception is InvalidOperationException
                            || exception is CryptographicException)
                        {
                            // Retry only the exact cached decision bytes.
                        }
                        finally
                        {
                            Clear(responseBody);
                            Clear(responseMac);
                        }
                    }

                    QueuePairingCommitAttempt();
                }
                finally
                {
                    Clear(requestBody);
                    Clear(requestMac);
                }
            }
        }

        private bool IsCurrentInitiatorPairingLocked(
            int generation,
            Guid pairingId)
        {
            return !_disposed
                && generation == _pairingGeneration
                && _pairing != null
                && _pairing.CurrentPairingId == pairingId
                && _pairing.LocalRole == PeerPairingRole.Initiator;
        }

        private void FailInitiatorPairingIfCurrent(
            int generation,
            Guid pairingId)
        {
            lock (_gate)
            {
                if (IsCurrentInitiatorPairingLocked(
                        generation,
                        pairingId))
                {
                    FailTransientPairingLocked();
                }
            }
        }

        private static Guid CreateNonEmptyPairingId()
        {
            Guid value;
            do
            {
                value = Guid.NewGuid();
            }
            while (value == Guid.Empty);

            return value;
        }
    }
}
