using System;
using System.Diagnostics;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private PairingDecisionReplayEntry _completedDecisionReplay;

        private void CapturePairingDecisionReplayLocked(
            PairingRemoteDecisionResult result,
            string peerEndpoint,
            long deadlineTimestamp,
            byte[] requestBody,
            byte[] requestMac)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (Stopwatch.GetTimestamp() >= deadlineTimestamp)
            {
                DisposePairingDecisionReplayLocked();
                return;
            }

            byte[] responseBody = null;
            byte[] responseMac = null;
            PairingDecisionReplayEntry replacement = null;
            try
            {
                responseBody = result.CopyResponseBody();
                responseMac = result.CopyResponseMac();
                replacement = new PairingDecisionReplayEntry(
                    result.Decision.PairingId,
                    result.Decision.SenderInstanceId,
                    peerEndpoint,
                    result.Decision.Decision,
                    deadlineTimestamp,
                    requestBody,
                    requestMac,
                    200,
                    responseBody,
                    responseMac);
                DisposePairingDecisionReplayLocked();
                _completedDecisionReplay = replacement;
                replacement = null;
            }
            finally
            {
                if (replacement != null)
                {
                    replacement.Dispose();
                }

                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private bool TryReplayCompletedPairingDecisionLocked(
            PeerHttpHandlerRequest request,
            out PeerHttpResponseData response)
        {
            response = null;
            PairingDecisionReplayEntry replay =
                _completedDecisionReplay;
            if (replay == null)
            {
                return false;
            }

            if (replay.IsExpired)
            {
                DisposePairingDecisionReplayLocked();
                return false;
            }

            if (!IsRemoteAddressForPeerEndpoint(
                    request.RemoteEndpoint,
                    replay.PeerEndpoint)
                || !IsPairingDecisionReplayBindingCurrentLocked(replay))
            {
                return false;
            }

            byte[] requestMac = null;
            byte[] requestBody = null;
            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                if (!PairingMacHeaderCodec.TryParseExactlyOne(
                        request.GetHeaderValues(
                            PairingMacHeaderCodec.HeaderName),
                        out requestMac))
                {
                    return false;
                }

                requestBody = request.GetBody();
                if (!replay.TryCopyResponse(
                        requestBody,
                        requestMac,
                        out responseBody,
                        out responseMac))
                {
                    return false;
                }

                response = CreatePairingMacResponse(
                    replay.ResponseStatusCode,
                    responseBody,
                    responseMac);
                return true;
            }
            finally
            {
                Clear(requestMac);
                Clear(requestBody);
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private bool IsPairingDecisionReplayBindingCurrentLocked(
            PairingDecisionReplayEntry replay)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            using (PairedPeerCredential credential =
                _configurationState.CopyCredential())
            {
                if (replay.Decision
                    == PeerPairingDecisionValue.Cancelled)
                {
                    return configuration.Synchronization.State
                            == DurableSynchronizationState.Unpaired
                        && credential == null;
                }

                return replay.Decision
                        == PeerPairingDecisionValue.Confirmed
                    && configuration.Synchronization.State
                        != DurableSynchronizationState.Unpaired
                    && credential != null
                    && credential.PairingId == replay.PairingId
                    && credential.PeerInstanceId
                        == replay.PeerInstanceId
                    && StringComparer.Ordinal.Equals(
                        credential.PeerEndpoint,
                        replay.PeerEndpoint);
            }
        }

        private PeerHttpResponseData
            CreateSignedPairingDecisionErrorLocked(
                byte[] requestBody,
                byte[] requestMac,
                int httpStatus,
                PeerSyncResponseCode code)
        {
            if (_pairing == null)
            {
                throw new InvalidOperationException(
                    "The transient pairing context is unavailable.");
            }

            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                responseBody = PeerSyncXmlCodec.SerializeControlResponse(
                    PeerControlResponse.CreateError(code));
                responseMac = _pairing.CreateRemoteDecisionResponseMac(
                    requestBody,
                    requestMac,
                    httpStatus,
                    responseBody);
                return CreatePairingMacResponse(
                    httpStatus,
                    responseBody,
                    responseMac);
            }
            finally
            {
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private PeerHttpResponseData
            HandleAuthenticatedPairingDecisionConflictLocked(
                byte[] requestBody,
                byte[] requestMac,
                PairingDecisionConflictException conflict)
        {
            if (conflict == null)
            {
                throw new ArgumentNullException(nameof(conflict));
            }

            PeerHttpResponseData conflictResponse;
            PeerHttpResponseData failureResponse;
            Guid pairingId;
            try
            {
                pairingId = conflict.PairingId;
                conflictResponse =
                    CreateSignedConflictingPairingDecisionErrorLocked(
                        requestBody,
                        requestMac,
                        409,
                        PeerSyncResponseCode.Conflict);
                failureResponse =
                    CreateSignedConflictingPairingDecisionErrorLocked(
                        requestBody,
                        requestMac,
                        500,
                        PeerSyncResponseCode.Internal);
            }
            catch (Exception signingFailure)
            {
                RecordFatalBackgroundFailure(
                    new AggregateException(
                        "An authenticated conflicting pairing decision could not be signed.",
                        conflict,
                        signingFailure));
                return PeerHttpResponseData.Bodyless(500);
            }

            Exception cancellationFailure = null;
            bool cancelled = false;
            try
            {
                cancelled =
                    CancelAuthenticatedPairingDecisionConflictLocked(
                        pairingId);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }

            if (cancelled)
            {
                return conflictResponse;
            }

            Exception fatal = cancellationFailure == null
                ? (Exception)new InvalidOperationException(
                    "An authenticated conflicting pairing decision could not be durably cancelled.",
                    conflict)
                : new AggregateException(
                    "An authenticated conflicting pairing decision could not be durably cancelled.",
                    conflict,
                    cancellationFailure);
            RecordFatalBackgroundFailure(fatal);
            return failureResponse;
        }

        private PeerHttpResponseData
            CreateSignedConflictingPairingDecisionErrorLocked(
                byte[] requestBody,
                byte[] requestMac,
                int httpStatus,
                PeerSyncResponseCode code)
        {
            if (_pairing == null)
            {
                throw new InvalidOperationException(
                    "The transient pairing context is unavailable.");
            }

            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                responseBody = PeerSyncXmlCodec.SerializeControlResponse(
                    PeerControlResponse.CreateError(code));
                responseMac = _pairing
                    .CreateConflictingRemoteDecisionResponseMac(
                        requestBody,
                        requestMac,
                        httpStatus,
                        responseBody);
                return CreatePairingMacResponse(
                    httpStatus,
                    responseBody,
                    responseMac);
            }
            finally
            {
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private bool CancelAuthenticatedPairingDecisionConflictLocked(
            Guid pairingId)
        {
            if (_pairing == null
                || pairingId == Guid.Empty)
            {
                return false;
            }

            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            if (configuration.Synchronization.State
                == DurableSynchronizationState.Unpaired)
            {
                DisposeTransientPairingLocked();
                DisposePairingDecisionReplayLocked();
                return true;
            }

            if (configuration.Synchronization.State
                    != DurableSynchronizationState.PairedPendingCommit
                || configuration.Synchronization.PairingId != pairingId)
            {
                return false;
            }

            return CommitUnpairedLocked(
                configuration,
                configuration.Synchronization.LastPeerNotification);
        }

        private void DisposePairingDecisionReplayLocked()
        {
            if (_completedDecisionReplay == null)
            {
                return;
            }

            _completedDecisionReplay.Dispose();
            _completedDecisionReplay = null;
        }
    }
}
