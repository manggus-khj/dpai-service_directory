using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private PeerHttpResponseData ProcessAdmittedNormalRequestLocked(
            PeerHttpHandlerRequest request,
            PeerInboundOperation operation,
            PeerRequestAuthenticationData verifiedRequest,
            byte[] body)
        {
            switch (operation)
            {
                case PeerInboundOperation.Handshake:
                    return ProcessInboundHandshakeLocked(
                        request,
                        verifiedRequest,
                        body);
                case PeerInboundOperation.Exchange:
                    return ProcessInboundExchangeLocked(
                        verifiedRequest,
                        body);
                case PeerInboundOperation.PkiState:
                    return ProcessInboundPkiStateLocked(
                        verifiedRequest,
                        body);
                case PeerInboundOperation.Release:
                    return ProcessInboundReleaseLocked(
                        verifiedRequest,
                        body);
                case PeerInboundOperation.Revoke:
                    return ProcessInboundRevokeLocked(
                        verifiedRequest,
                        body);
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private PeerHttpResponseData ProcessInboundHandshakeLocked(
            PeerHttpHandlerRequest request,
            PeerRequestAuthenticationData verifiedRequest,
            byte[] body)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            SynchronizationConfiguration synchronization =
                configuration.Synchronization;
            if (synchronization.State
                    != DurableSynchronizationState.Enabled)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Handshake,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.SyncDisabled);
            }

            PeerHandshakeRequest handshake =
                PeerSyncXmlCodec.ParseAuthenticatedHandshakeRequest(body);
            if (handshake.InstanceId
                    != synchronization.PeerInstanceId.Value
                || handshake.PeerInstanceId != configuration.InstanceId
                || handshake.KeyEpoch != synchronization.KeyEpoch.Value)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Handshake,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.PeerMismatch);
            }

            if (!handshake.SyncEnabled)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Handshake,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.SyncDisabled);
            }

            var bodyUtcNow = new DateTimeOffset(handshake.UtcNow);
            if (!PeerAuthenticationContract.IsTimestampFresh(
                    bodyUtcNow,
                    request.ReceivedAtUtc))
            {
                WriteAuthenticationFailure(
                    request,
                    PeerInboundOperation.Handshake,
                    SecurityAuditReason.TimestampOutsideAllowedWindow);
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Handshake,
                    verifiedRequest,
                    401,
                    PeerSyncResponseCode.ClockSkew);
            }

            if (_syncRunning)
            {
                int initiatorOrder = CompareCanonicalInstanceIds(
                    configuration.InstanceId,
                    synchronization.PeerInstanceId.Value);
                if (initiatorOrder < 0)
                {
                    // Both peers initiated at once. The canonical smaller
                    // InstanceId owns this cycle, so only one session can be
                    // installed on both sides.
                    return CreateSignedErrorLocked(
                        PeerInboundOperation.Handshake,
                        verifiedRequest,
                        409,
                        PeerSyncResponseCode.Conflict);
                }

                _outboundSynchronizationSuperseded = true;
            }

            byte[] requestHandshakeNonce = null;
            byte[] responseHandshakeNonce = null;
            byte[] sessionId = null;
            byte[] pairRoot = null;
            ActivePeerSession newSession = null;
            try
            {
                using (PairedPeerCredential credential =
                    _configurationState.CopyCredential())
                {
                    if (credential == null
                        || credential.State
                            != DurablePeerCredentialState.Enabled
                        || credential.LocalInstanceId
                            != configuration.InstanceId
                        || credential.PeerInstanceId
                            != synchronization.PeerInstanceId.Value
                        || credential.KeyEpoch
                            != synchronization.KeyEpoch.Value)
                    {
                        return CreateSignedErrorLocked(
                            PeerInboundOperation.Handshake,
                            verifiedRequest,
                            500,
                            PeerSyncResponseCode.Internal);
                    }

                    pairRoot = credential.CopyPairRoot();
                }

                requestHandshakeNonce = handshake.CopyHandshakeNonce();
                responseHandshakeNonce = CreateNormalRandomBytes(
                    PeerSyncContract.PairingNonceLength);
                sessionId = CreateNormalRandomBytes(
                    PeerSyncContract.SessionIdLength);
                DateTime responseUtc = GetUtcNow();
                DateTime expiresUtc = responseUtc.AddMinutes(
                    PeerAuthenticationContract.SessionLifetimeMinutes);
                newSession = ActivePeerSession.CreateFromHandshake(
                    configuration.InstanceId,
                    synchronization.PeerInstanceId.Value,
                    synchronization.KeyEpoch.Value,
                    pairRoot,
                    requestHandshakeNonce,
                    responseHandshakeNonce,
                    sessionId,
                    new DateTimeOffset(responseUtc),
                    new DateTimeOffset(expiresUtc));

                var result = new PeerHandshakeResult(
                    configuration.InstanceId,
                    synchronization.KeyEpoch.Value,
                    responseHandshakeNonce,
                    sessionId,
                    expiresUtc,
                    responseUtc,
                    true);
                PeerHttpResponseData response =
                    PeerAuthenticatedResponseFactory.CreateControl(
                        verifiedRequest,
                        _pairAuthentication,
                        null,
                        PeerResponseKeySource.Handshake,
                        200,
                        PeerControlResponse.CreateHandshakeSuccess(result),
                        (byte[])sessionId.Clone(),
                        new DateTimeOffset(responseUtc));

                DisposeSessionLocked();
                _activeSession = newSession;
                newSession = null;
                _pushProcessor = new PeerPushBatchProcessor(
                    synchronization.PeerInstanceId.Value,
                    _stateCoordinator);
                _outboundLease = null;
                return response;
            }
            finally
            {
                if (newSession != null)
                {
                    newSession.Dispose();
                }

                Clear(requestHandshakeNonce);
                Clear(responseHandshakeNonce);
                Clear(sessionId);
                Clear(pairRoot);
            }
        }

        private PeerHttpResponseData ProcessInboundExchangeLocked(
            PeerRequestAuthenticationData verifiedRequest,
            byte[] body)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            if (configuration.Synchronization.State
                    != DurableSynchronizationState.Enabled)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Exchange,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.SyncDisabled);
            }

            PeerPushExchangeRequest push;
            try
            {
                push = PeerSyncXmlCodec
                    .ParseAuthenticatedPushRequest(body);
            }
            catch (PeerSyncProtocolException pushFailure)
                when (pushFailure.Failure
                    == PeerSyncProtocolFailure.InvalidRequest)
            {
                try
                {
                    PeerPullExchangeRequest pull = PeerSyncXmlCodec
                        .ParseAuthenticatedPullRequest(body);
                    return ProcessInboundPullLocked(
                        verifiedRequest,
                        pull);
                }
                catch
                {
                    ResetInboundExchangeLocked();
                    throw;
                }
            }
            catch
            {
                ResetInboundExchangeLocked();
                throw;
            }

            return ProcessInboundPushLocked(verifiedRequest, push);
        }

        private PeerHttpResponseData ProcessInboundPkiStateLocked(
            PeerRequestAuthenticationData verifiedRequest,
            byte[] body)
        {
            if (_peerPki == null)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.PkiState,
                    verifiedRequest,
                    500,
                    PeerSyncResponseCode.Internal);
            }

            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            if (configuration.Synchronization.State
                    != DurableSynchronizationState.Enabled)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.PkiState,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.SyncDisabled);
            }

            if (_peerPki.GetPeerPkiRole()
                != CertificateAuthorityIssuerRole.ActiveIssuer)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.PkiState,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.Conflict);
            }

            PeerPkiStateRequest request = PeerContractXmlCodec
                .ParseAuthenticatedPkiStateRequest(body);
            if (request.InstanceId != _activeSession.PeerInstanceId)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.PkiState,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.PeerMismatch);
            }

            PeerPkiState current = _peerPki.GetPeerPkiState();
            if (request.KnownIssuerInstanceId
                    != current.IssuerInstanceId
                || request.KnownPkiRevision > current.PkiRevision
                || request.KnownCrlNumber > current.CrlNumber)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.PkiState,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.Conflict);
            }

            return PeerAuthenticatedResponseFactory.CreatePkiState(
                verifiedRequest,
                _activeSession,
                200,
                PeerPkiStateResponse.CreateSuccess(current),
                new DateTimeOffset(GetUtcNow()));
        }

        private PeerHttpResponseData ProcessInboundPushLocked(
            PeerRequestAuthenticationData verifiedRequest,
            PeerPushExchangeRequest push)
        {
            if (_pushProcessor == null)
            {
                _pushProcessor = new PeerPushBatchProcessor(
                    _activeSession.PeerInstanceId,
                    _stateCoordinator);
            }

            PeerPushBatchProcessingResult result =
                _pushProcessor.Process(push);
            if (!result.IsAccepted)
            {
                PeerSyncResponseCode responseCode = result.ResponseCode;
                int statusCode = MapInboundResponseStatus(responseCode);
                if (responseCode == PeerSyncResponseCode.LimitExceeded)
                {
                    statusCode = 413;
                }

                ResetInboundExchangeLocked();
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Exchange,
                    verifiedRequest,
                    statusCode,
                    responseCode);
            }

            Guid? serverSnapshotId = null;
            if (result.IsCompleted)
            {
                Guid snapshotId = CreateNormalNonEmptyGuid();
                _outboundLease = new PeerOutboundSnapshotLease(
                    _activeSession.LocalInstanceId,
                    snapshotId,
                    result.CurrentOutboundSnapshot);
                serverSnapshotId = snapshotId;
            }

            var acknowledgement = new PeerExchangeAcknowledgement(
                push.SnapshotId,
                push.BatchIndex,
                serverSnapshotId);
            return PeerAuthenticatedResponseFactory.CreateExchange(
                verifiedRequest,
                _activeSession,
                200,
                PeerExchangeResponse.CreatePushSuccess(acknowledgement),
                new DateTimeOffset(GetUtcNow()));
        }

        private PeerHttpResponseData ProcessInboundPullLocked(
            PeerRequestAuthenticationData verifiedRequest,
            PeerPullExchangeRequest pull)
        {
            if (_outboundLease == null)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Exchange,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.Conflict);
            }

            PeerOutboundBatchReadResult result = _outboundLease.Read(pull);
            if (!result.IsServed)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Exchange,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.Conflict);
            }

            return PeerAuthenticatedResponseFactory.CreateExchange(
                verifiedRequest,
                _activeSession,
                200,
                PeerExchangeResponse.CreatePullSuccess(result.Batch),
                new DateTimeOffset(GetUtcNow()));
        }

        private PeerHttpResponseData ProcessInboundReleaseLocked(
            PeerRequestAuthenticationData verifiedRequest,
            byte[] body)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            SynchronizationConfiguration currentSynchronization =
                configuration.Synchronization;
            if (currentSynchronization.State
                    != DurableSynchronizationState.Enabled)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Release,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.SyncDisabled);
            }

            PeerReleaseRequest release =
                PeerSyncXmlCodec.ParseAuthenticatedReleaseRequest(body);
            if (release.InstanceId
                    != currentSynchronization.PeerInstanceId.Value
                || !ReleaseSessionMatches(release))
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Release,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.PeerMismatch);
            }

            DateTime responseUtc = GetUtcNow();
            PeerHttpResponseData success =
                PeerAuthenticatedResponseFactory.CreateControl(
                    verifiedRequest,
                    _pairAuthentication,
                    _activeSession,
                    PeerResponseKeySource.Session,
                    200,
                    PeerControlResponse.CreateUnitSuccess(),
                    _activeSession.CopySessionId(),
                    new DateTimeOffset(responseUtc));
            PeerHttpResponseData logFailure =
                PeerAuthenticatedResponseFactory.CreateControl(
                    verifiedRequest,
                    _pairAuthentication,
                    _activeSession,
                    PeerResponseKeySource.Session,
                    500,
                    PeerControlResponse.CreateError(
                        PeerSyncResponseCode.Internal),
                    _activeSession.CopySessionId(),
                    new DateTimeOffset(responseUtc));
            using (PairedPeerCredential currentCredential =
                _configurationState.CopyCredential())
            {
                if (currentCredential == null)
                {
                    return CreateSignedErrorLocked(
                        PeerInboundOperation.Release,
                        verifiedRequest,
                        500,
                        PeerSyncResponseCode.Internal);
                }

                using (PairedPeerCredential disabledCredential =
                    ChangeCredentialState(
                        currentCredential,
                        DurablePeerCredentialState.PairedDisabled))
                {
                    SynchronizationConfiguration disabled =
                        SynchronizationConfiguration.PairedDisabled(
                            currentSynchronization.PeerEndpoint,
                            currentSynchronization.PeerInstanceId.Value,
                            currentSynchronization.KeyEpoch.Value,
                            currentSynchronization.LastSynchronization,
                            currentSynchronization.LastPeerNotification);
                    if (!CommitSynchronizationLocked(
                            configuration.LastPeerKeyEpoch,
                            disabled,
                            disabledCredential))
                    {
                        return CreateSignedErrorLocked(
                            PeerInboundOperation.Release,
                            verifiedRequest,
                            500,
                            PeerSyncResponseCode.Internal);
                    }
                }
            }

            bool logSucceeded = TryWriteSyncStopped(
                currentSynchronization.PeerInstanceId.Value,
                "PEER_RELEASE");
            DisposeSessionLocked();
            return logSucceeded ? success : logFailure;
        }

        private PeerHttpResponseData ProcessInboundRevokeLocked(
            PeerRequestAuthenticationData verifiedRequest,
            byte[] body)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            SynchronizationConfiguration synchronization =
                configuration.Synchronization;
            if (synchronization.State
                    == DurableSynchronizationState.Unpaired
                || !synchronization.PeerInstanceId.HasValue
                || !synchronization.KeyEpoch.HasValue)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Revoke,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.Conflict);
            }

            PeerRevokeRequest revoke =
                PeerSyncXmlCodec.ParseAuthenticatedRevokeRequest(body);
            if (revoke.InstanceId != synchronization.PeerInstanceId.Value
                || revoke.PeerInstanceId != configuration.InstanceId
                || revoke.KeyEpoch != synchronization.KeyEpoch.Value)
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Revoke,
                    verifiedRequest,
                    409,
                    PeerSyncResponseCode.PeerMismatch);
            }

            DateTime responseUtc = GetUtcNow();
            PeerHttpResponseData success =
                PeerAuthenticatedResponseFactory.CreateControl(
                    verifiedRequest,
                    _pairAuthentication,
                    null,
                    PeerResponseKeySource.Revoke,
                    200,
                    PeerControlResponse.CreateUnitSuccess(),
                    null,
                    new DateTimeOffset(responseUtc));
            PeerHttpResponseData logFailure =
                PeerAuthenticatedResponseFactory.CreateControl(
                    verifiedRequest,
                    _pairAuthentication,
                    null,
                    PeerResponseKeySource.Revoke,
                    500,
                    PeerControlResponse.CreateError(
                        PeerSyncResponseCode.Internal),
                    null,
                    new DateTimeOffset(responseUtc));
            if (!CommitUnpairedLocked(
                    configuration,
                    synchronization.LastPeerNotification))
            {
                return CreateSignedErrorLocked(
                    PeerInboundOperation.Revoke,
                    verifiedRequest,
                    500,
                    PeerSyncResponseCode.Internal);
            }

            bool logSucceeded =
                synchronization.State
                    != DurableSynchronizationState.Enabled
                || TryWriteSyncStopped(
                    synchronization.PeerInstanceId.Value,
                    "PEER_REVOKE");
            return logSucceeded ? success : logFailure;
        }

        private bool TryWriteSyncStopped(
            Guid peerInstanceId,
            string reason)
        {
            try
            {
                int retentionDays = _configurationState.GetCurrent()
                    .LogRetentionDays;
                _systemLog.WriteSyncStopped(
                    peerInstanceId,
                    reason,
                    retentionDays);
                return true;
            }
            catch (SystemLogRetentionAfterWriteException)
            {
                return true;
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is SecurityException)
            {
                return false;
            }
        }

        private bool ReleaseSessionMatches(PeerReleaseRequest release)
        {
            byte[] requestSession = null;
            byte[] activeSession = null;
            try
            {
                requestSession = release.CopySessionId();
                activeSession = _activeSession.CopySessionId();
                return PeerAuthenticationContract.FixedTimeEquals16(
                    requestSession,
                    activeSession);
            }
            finally
            {
                Clear(requestSession);
                Clear(activeSession);
            }
        }

        private void ResetInboundExchangeLocked()
        {
            _pushProcessor = _activeSession == null
                ? null
                : new PeerPushBatchProcessor(
                    _activeSession.PeerInstanceId,
                    _stateCoordinator);
            _outboundLease = null;
        }

        private static int MapInboundResponseStatus(
            PeerSyncResponseCode responseCode)
        {
            switch (responseCode)
            {
                case PeerSyncResponseCode.BadRequest:
                    return 400;
                case PeerSyncResponseCode.NotFound:
                    return 404;
                case PeerSyncResponseCode.NotPeer:
                    return 403;
                case PeerSyncResponseCode.ClockSkew:
                    return 401;
                case PeerSyncResponseCode.LimitExceeded:
                    return 429;
                case PeerSyncResponseCode.Conflict:
                case PeerSyncResponseCode.PeerMismatch:
                case PeerSyncResponseCode.SyncDisabled:
                case PeerSyncResponseCode.RevisionCollision:
                case PeerSyncResponseCode.DirectoryCapacity:
                case PeerSyncResponseCode.LogicalClockExhausted:
                    return 409;
                case PeerSyncResponseCode.Internal:
                    return 500;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(responseCode));
            }
        }

        private static int CompareCanonicalInstanceIds(
            Guid left,
            Guid right)
        {
            return string.CompareOrdinal(
                left.ToString("D").ToLowerInvariant(),
                right.ToString("D").ToLowerInvariant());
        }

        private static byte[] CreateNormalRandomBytes(int length)
        {
            var value = new byte[length];
            using (RandomNumberGenerator random =
                RandomNumberGenerator.Create())
            {
                random.GetBytes(value);
            }

            return value;
        }

        private static Guid CreateNormalNonEmptyGuid()
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
