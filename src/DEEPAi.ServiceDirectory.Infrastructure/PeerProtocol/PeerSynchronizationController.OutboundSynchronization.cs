using System;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private sealed class SyncCycleOutcome
        {
            private SyncCycleOutcome(
                PeerSyncResponseCode code,
                long? clockSkewSeconds)
            {
                Code = code;
                ClockSkewSeconds = clockSkewSeconds;
            }

            internal PeerSyncResponseCode Code { get; }

            internal bool IsSuccess => Code == PeerSyncResponseCode.Ok;

            internal long? ClockSkewSeconds { get; }

            internal string ResultCode => FormatSyncResult(Code);

            internal static SyncCycleOutcome Success(
                long? clockSkewSeconds)
            {
                return new SyncCycleOutcome(
                    PeerSyncResponseCode.Ok,
                    clockSkewSeconds);
            }

            internal static SyncCycleOutcome Failure(
                PeerSyncResponseCode code,
                long? clockSkewSeconds)
            {
                if (code == PeerSyncResponseCode.Ok)
                {
                    throw new ArgumentOutOfRangeException(nameof(code));
                }

                return new SyncCycleOutcome(code, clockSkewSeconds);
            }
        }

        private SyncCycleOutcome PerformSynchronizationCycle(
            PairedPeerCredential credential)
        {
            ActivePeerSession session = null;
            try
            {
                SyncCycleOutcome handshake = CreateOutboundSession(
                    credential,
                    out session);
                if (!handshake.IsSuccess)
                {
                    return handshake;
                }

                return ExchangeFullSnapshots(
                    credential,
                    session,
                    handshake.ClockSkewSeconds);
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }
            }
        }

        private SyncCycleOutcome CreateOutboundSession(
            PairedPeerCredential credential,
            out ActivePeerSession outboundSession)
        {
            outboundSession = null;
            byte[] pairRoot = null;
            byte[] handshakeRequestNonce = null;
            byte[] responseBody = null;
            byte[] responseHandshakeNonce = null;
            byte[] responseSessionId = null;
            byte[] responseHeaderSessionId = null;
            ActivePeerSession inboundSession = null;
            try
            {
                pairRoot = credential.CopyPairRoot();
                using (PeerPairAuthenticationContext pairAuthentication =
                    PeerPairAuthenticationContext.CreateFromPairRoot(
                        credential.LocalInstanceId,
                        credential.PeerInstanceId,
                        credential.KeyEpoch,
                        pairRoot))
                {
                    handshakeRequestNonce = CreateRandomBytes(
                        PeerSyncContract.PairingNonceLength);
                    var handshakeRequest = new PeerHandshakeRequest(
                        credential.LocalInstanceId,
                        credential.PeerInstanceId,
                        credential.KeyEpoch,
                        handshakeRequestNonce,
                        GetUtcNow(),
                        true);
                    byte[] requestBody = null;
                    try
                    {
                        requestBody = PeerSyncXmlCodec
                            .SerializeHandshakeRequest(handshakeRequest);
                        using (OutboundRequestResult requestResult =
                            SendAuthenticatedRequest(
                                credential,
                                pairAuthentication,
                                null,
                                OutboundPeerAuthenticationPurpose.Handshake,
                                PeerAuthenticationContract.HandshakePath,
                                requestBody))
                        {
                            if (!requestResult.IsVerified)
                            {
                                return SyncCycleOutcome.Failure(
                                    requestResult.FailureCode,
                                    requestResult.ClockSkewSeconds);
                            }

                            responseBody = requestResult.Response.CopyBody();
                            PeerControlResponse response;
                            try
                            {
                                response = PeerSyncXmlCodec
                                    .ParseAuthenticatedHandshakeResponse(
                                        responseBody);
                            }
                            catch (PeerSyncProtocolException)
                            {
                                return SyncCycleOutcome.Failure(
                                    PeerSyncResponseCode.BadRequest,
                                    requestResult.ClockSkewSeconds);
                            }

                            if (!IsHttpStatusConsistent(
                                    requestResult.Response.StatusCode,
                                    response.Code))
                            {
                                return SyncCycleOutcome.Failure(
                                    PeerSyncResponseCode.BadRequest,
                                    requestResult.ClockSkewSeconds);
                            }

                            if (!response.IsSuccess)
                            {
                                return SyncCycleOutcome.Failure(
                                    response.Code,
                                    requestResult.ClockSkewSeconds);
                            }

                            PeerHandshakeResult handshake =
                                response.Handshake;
                            if (handshake == null
                                || handshake.InstanceId
                                    != credential.PeerInstanceId
                                || handshake.KeyEpoch
                                    != credential.KeyEpoch
                                || !handshake.SyncEnabled
                                || !requestResult.Response.HasSession)
                            {
                                return SyncCycleOutcome.Failure(
                                    handshake != null
                                        && !handshake.SyncEnabled
                                        ? PeerSyncResponseCode.SyncDisabled
                                        : PeerSyncResponseCode.PeerMismatch,
                                    requestResult.ClockSkewSeconds);
                            }

                            responseHandshakeNonce =
                                handshake.CopyHandshakeNonce();
                            responseSessionId = handshake.CopySessionId();
                            responseHeaderSessionId = requestResult.Response
                                .CopySessionId();
                            if (!PeerAuthenticationContract.FixedTimeEquals16(
                                    responseSessionId,
                                    responseHeaderSessionId))
                            {
                                return SyncCycleOutcome.Failure(
                                    PeerSyncResponseCode.PeerMismatch,
                                    requestResult.ClockSkewSeconds);
                            }

                            var responseTime = new DateTimeOffset(
                                handshake.UtcNow);
                            var expiry = new DateTimeOffset(
                                handshake.ExpiresUtc);
                            outboundSession = ActivePeerSession
                                .CreateFromHandshake(
                                    credential.LocalInstanceId,
                                    credential.PeerInstanceId,
                                    credential.KeyEpoch,
                                    pairRoot,
                                    handshakeRequestNonce,
                                    responseHandshakeNonce,
                                    responseSessionId,
                                    responseTime,
                                    expiry);
                            inboundSession = ActivePeerSession
                                .CreateFromHandshake(
                                    credential.LocalInstanceId,
                                    credential.PeerInstanceId,
                                    credential.KeyEpoch,
                                    pairRoot,
                                    handshakeRequestNonce,
                                    responseHandshakeNonce,
                                    responseSessionId,
                                    responseTime,
                                    expiry);

                            lock (_gate)
                            {
                                if (_disposed
                                    || _outboundSynchronizationSuperseded
                                    || !HasCurrentPeerBindingLocked(
                                        credential,
                                        DurableSynchronizationState.Enabled))
                                {
                                    outboundSession.Dispose();
                                    outboundSession = null;
                                    inboundSession.Dispose();
                                    inboundSession = null;
                                    return SyncCycleOutcome.Failure(
                                        _outboundSynchronizationSuperseded
                                            ? PeerSyncResponseCode.Conflict
                                            : PeerSyncResponseCode.SyncDisabled,
                                        requestResult.ClockSkewSeconds);
                                }

                                DisposeSessionLocked();
                                _activeSession = inboundSession;
                                inboundSession = null;
                            }

                            return SyncCycleOutcome.Success(
                                requestResult.ClockSkewSeconds);
                        }
                    }
                    finally
                    {
                        Clear(requestBody);
                    }
                }
            }
            finally
            {
                if (inboundSession != null)
                {
                    inboundSession.Dispose();
                }

                Clear(pairRoot);
                Clear(handshakeRequestNonce);
                Clear(responseBody);
                Clear(responseHandshakeNonce);
                Clear(responseSessionId);
                Clear(responseHeaderSessionId);
            }
        }

        private SyncCycleOutcome ExchangeFullSnapshots(
            PairedPeerCredential credential,
            ActivePeerSession session,
            long? clockSkewSeconds)
        {
            if (IsOutboundSynchronizationSuperseded())
            {
                return SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.Conflict,
                    clockSkewSeconds);
            }

            DEEPAi.ServiceDirectory.Domain.DirectorySnapshot
                currentSnapshot;
            if (!_stateCoordinator.TryGetReadySnapshot(out currentSnapshot))
            {
                return SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.Internal,
                    clockSkewSeconds);
            }

            var synchronizationSnapshot = new SynchronizationSnapshot(
                currentSnapshot.Records.Values,
                currentSnapshot.LogicalClock);
            var pushLease = new PeerOutboundSnapshotLease(
                credential.LocalInstanceId,
                Guid.NewGuid(),
                synchronizationSnapshot);

            Guid? serverSnapshotId = null;
            for (uint batchIndex = 0;
                (ulong)batchIndex < (ulong)pushLease.BatchCount;
                batchIndex = checked(batchIndex + 1U))
            {
                if (IsOutboundSynchronizationSuperseded())
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Conflict,
                        clockSkewSeconds);
                }

                PeerOutboundBatchReadResult read = pushLease.Read(
                    new PeerPullExchangeRequest(
                        pushLease.SnapshotId,
                        batchIndex));
                if (!read.IsServed || read.Batch == null)
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Internal,
                        clockSkewSeconds);
                }

                PeerPullExchangeBatch source = read.Batch;
                var push = new PeerPushExchangeRequest(
                    source.InstanceId,
                    source.SnapshotId,
                    source.LogicalClock,
                    source.BatchIndex,
                    source.TotalCount,
                    source.IsLastBatch,
                    source.Items);
                PeerExchangeResponse response;
                SyncCycleOutcome send = SendExchange(
                    credential,
                    session,
                    PeerSyncXmlCodec.SerializePushRequest(push),
                    clockSkewSeconds,
                    out response);
                if (!send.IsSuccess)
                {
                    return send;
                }

                if (response.Kind
                        != PeerExchangeResponseKind.PushAcknowledgement
                    || response.Acknowledgement == null
                    || response.Acknowledgement.SnapshotId
                        != push.SnapshotId
                    || response.Acknowledgement.BatchIndex
                        != push.BatchIndex
                    || (!push.IsLastBatch
                        && response.Acknowledgement.ServerSnapshotId
                            .HasValue)
                    || (push.IsLastBatch
                        && !response.Acknowledgement.ServerSnapshotId
                            .HasValue))
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.BadRequest,
                        clockSkewSeconds);
                }

                if (push.IsLastBatch)
                {
                    serverSnapshotId = response.Acknowledgement
                        .ServerSnapshotId;
                }
            }

            if (!serverSnapshotId.HasValue)
            {
                return SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.BadRequest,
                    clockSkewSeconds);
            }

            var inboundProcessor = new PeerPushBatchProcessor(
                credential.PeerInstanceId,
                _stateCoordinator);
            uint pullIndex = 0;
            while (true)
            {
                if (IsOutboundSynchronizationSuperseded())
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Conflict,
                        clockSkewSeconds);
                }

                var pullRequest = new PeerPullExchangeRequest(
                    serverSnapshotId.Value,
                    pullIndex);
                PeerExchangeResponse response;
                SyncCycleOutcome send = SendExchange(
                    credential,
                    session,
                    PeerSyncXmlCodec.SerializePullRequest(pullRequest),
                    clockSkewSeconds,
                    out response);
                if (!send.IsSuccess)
                {
                    return send;
                }

                if (response.Kind != PeerExchangeResponseKind.PullBatch
                    || response.PullBatch == null)
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.BadRequest,
                        clockSkewSeconds);
                }

                PeerPullExchangeBatch batch = response.PullBatch;
                if (batch.InstanceId != credential.PeerInstanceId
                    || batch.SnapshotId != serverSnapshotId.Value
                    || batch.BatchIndex != pullIndex)
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.PeerMismatch,
                        clockSkewSeconds);
                }

                var staged = new PeerPushExchangeRequest(
                    batch.InstanceId,
                    batch.SnapshotId,
                    batch.LogicalClock,
                    batch.BatchIndex,
                    batch.TotalCount,
                    batch.IsLastBatch,
                    batch.Items);
                if (IsOutboundSynchronizationSuperseded())
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Conflict,
                        clockSkewSeconds);
                }

                PeerPushBatchProcessingResult processed;
                if (batch.IsLastBatch)
                {
                    // The final Process call publishes directory.xml and the
                    // observed LogicalClock.  Hold the controller gate across
                    // the final binding check and commit so disable, release,
                    // revoke, and simultaneous-handshake supersession are
                    // totally ordered with that publication.
                    lock (_gate)
                    {
                        if (_outboundSynchronizationSuperseded
                            || !HasCurrentPeerBindingLocked(
                                credential,
                                DurableSynchronizationState.Enabled))
                        {
                            return SyncCycleOutcome.Failure(
                                PeerSyncResponseCode.Conflict,
                                clockSkewSeconds);
                        }

                        processed = inboundProcessor.Process(staged);
                    }
                }
                else
                {
                    processed = inboundProcessor.Process(staged);
                }

                if (!processed.IsAccepted)
                {
                    return SyncCycleOutcome.Failure(
                        processed.ResponseCode,
                        clockSkewSeconds);
                }

                if (batch.IsLastBatch != processed.IsCompleted)
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.BadRequest,
                        clockSkewSeconds);
                }

                if (processed.IsCompleted)
                {
                    if (IsOutboundSynchronizationSuperseded())
                    {
                        return SyncCycleOutcome.Failure(
                            PeerSyncResponseCode.Conflict,
                            clockSkewSeconds);
                    }

                    return SyncCycleOutcome.Success(clockSkewSeconds);
                }

                pullIndex = checked(pullIndex + 1U);
            }
        }

        private SyncCycleOutcome SendExchange(
            PairedPeerCredential credential,
            ActivePeerSession session,
            byte[] requestBody,
            long? clockSkewSeconds,
            out PeerExchangeResponse response)
        {
            response = null;
            try
            {
                if (IsOutboundSynchronizationSuperseded())
                {
                    return SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Conflict,
                        clockSkewSeconds);
                }

                using (OutboundRequestResult requestResult =
                    SendAuthenticatedRequest(
                        credential,
                        null,
                        session,
                        OutboundPeerAuthenticationPurpose.Session,
                        PeerAuthenticationContract.ExchangePath,
                        requestBody))
                {
                    if (!requestResult.IsVerified)
                    {
                        return SyncCycleOutcome.Failure(
                            requestResult.FailureCode,
                            clockSkewSeconds);
                    }

                    if (IsOutboundSynchronizationSuperseded())
                    {
                        return SyncCycleOutcome.Failure(
                            PeerSyncResponseCode.Conflict,
                            clockSkewSeconds);
                    }

                    byte[] responseBody = null;
                    try
                    {
                        responseBody = requestResult.Response.CopyBody();
                        try
                        {
                            response = PeerSyncXmlCodec
                                .ParseAuthenticatedExchangeResponse(
                                    responseBody);
                        }
                        catch (PeerSyncProtocolException)
                        {
                            return SyncCycleOutcome.Failure(
                                PeerSyncResponseCode.BadRequest,
                                clockSkewSeconds);
                        }
                    }
                    finally
                    {
                        Clear(responseBody);
                    }

                    if (!IsHttpStatusConsistent(
                            requestResult.Response.StatusCode,
                            response.Code))
                    {
                        response = null;
                        return SyncCycleOutcome.Failure(
                            PeerSyncResponseCode.BadRequest,
                            clockSkewSeconds);
                    }

                    if (!response.IsSuccess)
                    {
                        PeerSyncResponseCode code = response.Code;
                        response = null;
                        return SyncCycleOutcome.Failure(
                            code,
                            clockSkewSeconds);
                    }

                    return SyncCycleOutcome.Success(clockSkewSeconds);
                }
            }
            finally
            {
                Clear(requestBody);
            }
        }

        private bool IsOutboundSynchronizationSuperseded()
        {
            lock (_gate)
            {
                return _outboundSynchronizationSuperseded;
            }
        }

        private bool HasCurrentPeerBindingLocked(
            PairedPeerCredential credential,
            DurableSynchronizationState requiredState)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            return configuration.Synchronization.State == requiredState
                && configuration.InstanceId == credential.LocalInstanceId
                && configuration.Synchronization.PeerInstanceId
                    == credential.PeerInstanceId
                && configuration.Synchronization.KeyEpoch
                    == credential.KeyEpoch
                && StringComparer.Ordinal.Equals(
                    configuration.Synchronization.PeerEndpoint,
                    credential.PeerEndpoint);
        }

        private static bool IsHttpStatusConsistent(
            int statusCode,
            PeerSyncResponseCode code)
        {
            switch (code)
            {
                case PeerSyncResponseCode.Ok:
                    return statusCode == 200;
                case PeerSyncResponseCode.BadRequest:
                    return statusCode == 400;
                case PeerSyncResponseCode.NotFound:
                    return statusCode == 404;
                case PeerSyncResponseCode.LimitExceeded:
                    return statusCode == 413 || statusCode == 429;
                case PeerSyncResponseCode.NotPeer:
                    return statusCode == 403;
                case PeerSyncResponseCode.ClockSkew:
                    return statusCode == 401;
                case PeerSyncResponseCode.Conflict:
                case PeerSyncResponseCode.PeerMismatch:
                case PeerSyncResponseCode.SyncDisabled:
                case PeerSyncResponseCode.RevisionCollision:
                case PeerSyncResponseCode.DirectoryCapacity:
                case PeerSyncResponseCode.LogicalClockExhausted:
                    return statusCode == 409;
                case PeerSyncResponseCode.Internal:
                    return statusCode == 500;
                default:
                    return false;
            }
        }

        private static string FormatSyncResult(PeerSyncResponseCode code)
        {
            switch (code)
            {
                case PeerSyncResponseCode.Ok:
                    return "OK";
                case PeerSyncResponseCode.BadRequest:
                    return "BAD_REQUEST";
                case PeerSyncResponseCode.NotFound:
                    return "NOT_FOUND";
                case PeerSyncResponseCode.Conflict:
                    return "CONFLICT";
                case PeerSyncResponseCode.LimitExceeded:
                    return "LIMIT_EXCEEDED";
                case PeerSyncResponseCode.NotPeer:
                    return "NOT_PEER";
                case PeerSyncResponseCode.PeerMismatch:
                    return "PEER_MISMATCH";
                case PeerSyncResponseCode.ClockSkew:
                    return "CLOCK_SKEW";
                case PeerSyncResponseCode.SyncDisabled:
                    return "SYNC_DISABLED";
                case PeerSyncResponseCode.RevisionCollision:
                    return "REVISION_COLLISION";
                case PeerSyncResponseCode.DirectoryCapacity:
                    return "DIRECTORY_CAPACITY";
                case PeerSyncResponseCode.LogicalClockExhausted:
                    return "LOGICAL_CLOCK_EXHAUSTED";
                case PeerSyncResponseCode.Internal:
                    return "INTERNAL";
                default:
                    throw new ArgumentOutOfRangeException(nameof(code));
            }
        }
    }
}
