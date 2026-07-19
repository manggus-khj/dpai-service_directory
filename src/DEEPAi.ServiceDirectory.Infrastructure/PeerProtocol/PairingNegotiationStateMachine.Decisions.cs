using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal sealed partial class PairingNegotiationStateMachine
    {
        internal PairingLocalDecisionMessage CreateLocalDecision(
            Guid pairingId,
            PeerPairingDecisionValue decision)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                byte[] requestBody = null;
                byte[] requestMac = null;
                try
                {
                    EnsureDecisionState();
                    if (pairingId == Guid.Empty || pairingId != _pairingId)
                    {
                        throw new InvalidOperationException(
                            "The local decision pairing ID does not match the active pairing.");
                    }

                    if (!Enum.IsDefined(
                        typeof(PeerPairingDecisionValue),
                        decision))
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(decision));
                    }

                    if (_localDecisionRequest != null)
                    {
                        if (_localDecisionRequest.Decision != decision)
                        {
                            throw new InvalidOperationException(
                                "A role may not replace its terminal pairing decision.");
                        }

                        return new PairingLocalDecisionMessage(
                            _localDecisionRequest,
                            _localDecisionBody,
                            _localDecisionMac,
                            true,
                            false);
                    }

                    if (_state == PairingNegotiationState.BothConfirmed)
                    {
                        throw new InvalidOperationException(
                            "BothConfirmed cannot be entered without a local terminal decision.");
                    }

                    var request = new PeerPairingDecision(
                        _pairingId,
                        _keyEpoch,
                        _localRole.Value,
                        _localInstanceId,
                        _peerInstanceId,
                        _transcriptHash,
                        decision);
                    requestBody = PeerSyncXmlCodec
                        .SerializePairingDecisionRequest(request);
                    requestMac = _secretContext.CreateDecisionRequestMac(
                        _pairingId,
                        _keyEpoch,
                        ToCryptographyRole(_localRole.Value),
                        _localInstanceId,
                        _peerInstanceId,
                        ToTerminalDecision(decision));

                    _localDecisionRequest = request;
                    _localDecisionBody = requestBody;
                    _localDecisionMac = requestMac;
                    requestBody = null;
                    requestMac = null;

                    bool cancelled = decision
                        == PeerPairingDecisionValue.Cancelled;
                    var message = new PairingLocalDecisionMessage(
                        _localDecisionRequest,
                        _localDecisionBody,
                        _localDecisionMac,
                        false,
                        cancelled);
                    if (cancelled)
                    {
                        ResetToUnpaired();
                    }
                    else
                    {
                        Clear(_sas);
                        _sas = null;
                        EnterBothConfirmedIfReady();
                    }

                    return message;
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
                finally
                {
                    Clear(requestBody);
                    Clear(requestMac);
                }
            }
        }

        // For a first request, rawResponseBody is parsed and signed atomically
        // with the decision. For an exact replay, the stored signed response is
        // returned and rawResponseBody is deliberately ignored.
        internal PairingRemoteDecisionResult ProcessRemoteDecision(
            byte[] rawRequestBody,
            byte[] requestMac,
            int httpStatus,
            byte[] rawResponseBody)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                byte[] transcriptHash = null;
                byte[] expectedRequestMac = null;
                byte[] responseMac = null;
                try
                {
                    EnsureDecisionState();
                    if (rawRequestBody == null)
                    {
                        throw new ArgumentNullException(
                            nameof(rawRequestBody));
                    }

                    if (rawRequestBody.Length == 0)
                    {
                        throw new ArgumentException(
                            "The pairing decision request body must not be empty.",
                            nameof(rawRequestBody));
                    }

                    PeerPairingDecision request = PeerSyncXmlCodec
                        .ParsePairingDecisionRequest(rawRequestBody);
                    ValidateRemoteDecisionBinding(
                        request,
                        out transcriptHash);
                    expectedRequestMac = _secretContext
                        .CreateDecisionRequestMac(
                            _pairingId,
                            _keyEpoch,
                            ToCryptographyRole(request.SenderRole),
                            request.SenderInstanceId,
                            request.ReceiverInstanceId,
                            ToTerminalDecision(request.Decision));
                    if (!PairingTerminalMessageAuthenticator.VerifyMac(
                            expectedRequestMac,
                            requestMac))
                    {
                        throw new InvalidOperationException(
                            "The pairing decision request MAC is invalid.");
                    }

                    if (_remoteDecisionRequest != null)
                    {
                        if (!CachedRemoteDecisionMatches(
                                request,
                                rawRequestBody,
                                requestMac))
                        {
                            throw new InvalidOperationException(
                                "A role may not replace or re-encode its terminal pairing decision.");
                        }

                        return new PairingRemoteDecisionResult(
                            _remoteDecisionRequest,
                            _remoteDecisionResponseBody,
                            _remoteDecisionResponseMac,
                            true,
                            false);
                    }

                    if (_state == PairingNegotiationState.BothConfirmed)
                    {
                        throw new InvalidOperationException(
                            "BothConfirmed cannot be entered without a remote terminal decision.");
                    }

                    if (rawResponseBody == null)
                    {
                        throw new ArgumentNullException(
                            nameof(rawResponseBody));
                    }

                    PeerControlResponse response = PeerSyncXmlCodec
                        .ParsePairingDecisionResponse(rawResponseBody);
                    responseMac = _secretContext
                        .CreateDecisionResponseMac(
                            _pairingId,
                            _keyEpoch,
                            ToCryptographyRole(_localRole.Value),
                            _localInstanceId,
                            _peerInstanceId,
                            requestMac,
                            httpStatus,
                            response.Result,
                            checked((uint)response.Code),
                            rawResponseBody);

                    _remoteDecisionRequest = request;
                    _remoteDecisionBody =
                        (byte[])rawRequestBody.Clone();
                    _remoteDecisionMac = (byte[])requestMac.Clone();
                    _remoteDecisionResponseBody =
                        (byte[])rawResponseBody.Clone();
                    _remoteDecisionResponseMac = responseMac;
                    responseMac = null;

                    bool cancelled = request.Decision
                        == PeerPairingDecisionValue.Cancelled;
                    var result = new PairingRemoteDecisionResult(
                        _remoteDecisionRequest,
                        _remoteDecisionResponseBody,
                        _remoteDecisionResponseMac,
                        false,
                        cancelled);
                    if (cancelled)
                    {
                        ResetToUnpaired();
                    }
                    else
                    {
                        EnterBothConfirmedIfReady();
                    }

                    return result;
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
                finally
                {
                    Clear(transcriptHash);
                    Clear(expectedRequestMac);
                    Clear(responseMac);
                }
            }
        }

        internal PairingBothConfirmedMaterial CreateBothConfirmedMaterial()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                if (_state != PairingNegotiationState.BothConfirmed
                    || _secretContext == null
                    || _transcriptHash == null
                    || _peerInstanceId == Guid.Empty
                    || _keyEpoch == 0)
                {
                    throw new InvalidOperationException(
                        "The pairing has not reached BothConfirmed.");
                }

                byte[] pairRoot = null;
                try
                {
                    pairRoot = _secretContext.DerivePairRoot();
                    return new PairingBothConfirmedMaterial(
                        _pairingId,
                        _localInstanceId,
                        _peerInstanceId,
                        _localEndpoint,
                        _peerEndpoint,
                        _keyEpoch,
                        _transcriptHash,
                        pairRoot);
                }
                finally
                {
                    Clear(pairRoot);
                }
            }
        }

        private void EnsureDecisionState()
        {
            if ((_state != PairingNegotiationState.SasPending
                    && _state != PairingNegotiationState.BothConfirmed)
                || _secretContext == null
                || _transcriptHash == null
                || !_localRole.HasValue
                || _peerInstanceId == Guid.Empty
                || _keyEpoch == 0)
            {
                throw new InvalidOperationException(
                    "A terminal decision is not valid in the current pairing state.");
            }
        }

        private void ValidateRemoteDecisionBinding(
            PeerPairingDecision request,
            out byte[] transcriptHash)
        {
            transcriptHash = null;
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            PeerPairingRole remoteRole = OppositeRole(_localRole.Value);
            if (request.PairingId != _pairingId
                || request.KeyEpoch != _keyEpoch
                || request.SenderRole != remoteRole
                || request.SenderInstanceId != _peerInstanceId
                || request.ReceiverInstanceId != _localInstanceId)
            {
                throw new InvalidOperationException(
                    "The pairing decision binding does not match the active pairing.");
            }

            transcriptHash = request.CopyTranscriptHash();
            if (!PairingCryptography.FixedTimeEquals32(
                    _transcriptHash,
                    transcriptHash))
            {
                throw new InvalidOperationException(
                    "The pairing decision transcript does not match.");
            }
        }

        private bool CachedRemoteDecisionMatches(
            PeerPairingDecision request,
            byte[] rawRequestBody,
            byte[] requestMac)
        {
            return request.Decision == _remoteDecisionRequest.Decision
                && BytesEqual(_remoteDecisionBody, rawRequestBody)
                && requestMac != null
                && requestMac.Length
                    == PairingCryptography.AuthenticationCodeLength
                && PairingCryptography.FixedTimeEquals32(
                    _remoteDecisionMac,
                    requestMac);
        }

        private void EnterBothConfirmedIfReady()
        {
            if (_localDecisionRequest != null
                && _remoteDecisionRequest != null
                && _localDecisionRequest.Decision
                    == PeerPairingDecisionValue.Confirmed
                && _remoteDecisionRequest.Decision
                    == PeerPairingDecisionValue.Confirmed)
            {
                Clear(_sas);
                _sas = null;
                _state = PairingNegotiationState.BothConfirmed;
            }
        }

        private static PairingTerminalDecision ToTerminalDecision(
            PeerPairingDecisionValue decision)
        {
            switch (decision)
            {
                case PeerPairingDecisionValue.Confirmed:
                    return PairingTerminalDecision.Confirmed;
                case PeerPairingDecisionValue.Cancelled:
                    return PairingTerminalDecision.Cancelled;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(decision));
            }
        }
    }
}
