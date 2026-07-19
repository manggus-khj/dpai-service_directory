using System;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private AdminHandlerResult<AdminServerSyncDisableResponse>
            DisableCore(bool forgetPeer)
        {
            ServiceDirectoryConfiguration originalConfiguration;
            PairedPeerCredential credential;
            bool notificationRequired;
            PeerNotificationOperation operation = forgetPeer
                ? PeerNotificationOperation.Revoke
                : PeerNotificationOperation.Release;
            lock (_gate)
            {
                ThrowIfDisposed();
                originalConfiguration = _configurationState.GetCurrent();
                DurableSynchronizationState state =
                    originalConfiguration.Synchronization.State;
                if (state != DurableSynchronizationState.Enabled
                    && state
                        != DurableSynchronizationState.PairedDisabled)
                {
                    return DisableFailure(
                        AdminServerErrorCode.Conflict);
                }

                credential = _configurationState.CopyCredential();
                if (credential == null
                    || !CredentialMatchesConfiguration(
                        credential,
                        originalConfiguration))
                {
                    if (credential != null)
                    {
                        credential.Dispose();
                    }

                    return DisableFailure(
                        AdminServerErrorCode.Internal);
                }

                notificationRequired = forgetPeer
                    || state == DurableSynchronizationState.Enabled;
            }

            bool notificationConfirmed = false;
            try
            {
                if (notificationRequired)
                {
                    notificationConfirmed = forgetPeer
                        ? TrySendRevoke(credential)
                        : TrySendRelease(credential);
                }

                DateTime notificationUtc = GetUtcNow();
                PeerNotificationResult notificationResult =
                    !notificationRequired
                        ? PeerNotificationResult.NotRequired
                        : notificationConfirmed
                            ? PeerNotificationResult.Confirmed
                            : PeerNotificationResult.Unconfirmed;
                var notification = new PeerNotificationStatus(
                    operation,
                    notificationResult,
                    notificationUtc);

                lock (_gate)
                {
                    if (_disposed)
                    {
                        return DisableFailure(
                            AdminServerErrorCode.Internal);
                    }

                    ServiceDirectoryConfiguration current =
                        _configurationState.GetCurrent();
                    if (!CredentialBindingMatchesConfiguration(
                            credential,
                            current)
                        || (current.Synchronization.State
                                != originalConfiguration
                                    .Synchronization.State
                            && !(originalConfiguration
                                    .Synchronization.State
                                        == DurableSynchronizationState
                                            .Enabled
                                && current.Synchronization.State
                                    == DurableSynchronizationState
                                        .PairedDisabled)))
                    {
                        return DisableFailure(
                            AdminServerErrorCode.Conflict);
                    }

                    bool committed;
                    AdminPairingState localState;
                    if (forgetPeer)
                    {
                        committed = CommitUnpairedLocked(
                            current,
                            notification);
                        localState = AdminPairingState.Unpaired;
                    }
                    else
                    {
                        SynchronizationConfiguration synchronization =
                            SynchronizationConfiguration.PairedDisabled(
                                credential.PeerEndpoint,
                                credential.PeerInstanceId,
                                credential.KeyEpoch,
                                current.Synchronization
                                    .LastSynchronization,
                                notification);
                        using (PairedPeerCredential disabled =
                            ChangeCredentialState(
                                credential,
                                DurablePeerCredentialState.PairedDisabled))
                        {
                            committed = CommitSynchronizationLocked(
                                current.LastPeerKeyEpoch,
                                synchronization,
                                disabled);
                        }

                        if (committed)
                        {
                            DisposeSessionLocked();
                        }

                        localState = AdminPairingState.PairedDisabled;
                    }

                    if (!committed)
                    {
                        return DisableFailure(
                            AdminServerErrorCode.Internal);
                    }

                    _initialSynchronizationPending = false;
                    if (originalConfiguration.Synchronization.State
                        == DurableSynchronizationState.Enabled)
                    {
                        if (!TryWriteSyncStopped(
                                credential.PeerInstanceId,
                                "ADMIN_REQUEST"))
                        {
                            return DisableFailure(
                                AdminServerErrorCode.Internal);
                        }
                    }

                    return AdminHandlerResult<
                        AdminServerSyncDisableResponse>.Success(
                            new AdminServerSyncDisableResponse(
                                localState,
                                (AdminPeerNotificationOperation)(int)
                                    operation,
                                (AdminPeerNotificationResult)(int)
                                    notificationResult,
                                notificationUtc));
                }
            }
            finally
            {
                credential.Dispose();
            }
        }

        private bool ForgetCurrentPeerForRepair()
        {
            ServiceDirectoryConfiguration originalConfiguration;
            PairedPeerCredential credential = null;
            lock (_gate)
            {
                ThrowIfDisposed();
                originalConfiguration = _configurationState.GetCurrent();
                if (originalConfiguration.Synchronization.State
                    == DurableSynchronizationState.Unpaired)
                {
                    DisposeTransientPairingLocked();
                    return true;
                }

                credential = _configurationState.CopyCredential();
                if (credential == null
                    || !CredentialMatchesConfiguration(
                        credential,
                        originalConfiguration))
                {
                    if (credential != null)
                    {
                        credential.Dispose();
                    }

                    return false;
                }
            }

            try
            {
                bool notificationConfirmed = TrySendRevoke(credential);
                DateTime notificationUtc = GetUtcNow();
                var notification = new PeerNotificationStatus(
                    PeerNotificationOperation.Revoke,
                    notificationConfirmed
                        ? PeerNotificationResult.Confirmed
                        : PeerNotificationResult.Unconfirmed,
                    notificationUtc);

                lock (_gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    ServiceDirectoryConfiguration current =
                        _configurationState.GetCurrent();
                    if (current.Synchronization.State
                        == DurableSynchronizationState.Unpaired)
                    {
                        DisposeTransientPairingLocked();
                        return true;
                    }

                    if (!CredentialBindingMatchesConfiguration(
                            credential,
                            current))
                    {
                        return false;
                    }

                    bool wasEnabled = current.Synchronization.State
                        == DurableSynchronizationState.Enabled;
                    bool committed = CommitUnpairedLocked(
                        current,
                        notification);
                    if (!committed)
                    {
                        return false;
                    }

                    _initialSynchronizationPending = false;
                    if (wasEnabled)
                    {
                        if (!TryWriteSyncStopped(
                                credential.PeerInstanceId,
                                "REPAIR"))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            finally
            {
                credential.Dispose();
            }
        }

        private bool TrySendRelease(PairedPeerCredential credential)
        {
            ActivePeerSession session = null;
            byte[] sessionId = null;
            byte[] requestBody = null;
            byte[] responseBody = null;
            try
            {
                SyncCycleOutcome handshake = CreateOutboundSession(
                    credential,
                    out session);
                if (!handshake.IsSuccess || session == null)
                {
                    return false;
                }

                sessionId = session.CopySessionId();
                requestBody = PeerSyncXmlCodec.SerializeReleaseRequest(
                    new PeerReleaseRequest(
                        credential.LocalInstanceId,
                        sessionId));
                using (OutboundRequestResult requestResult =
                    SendAuthenticatedRequest(
                        credential,
                        null,
                        session,
                        OutboundPeerAuthenticationPurpose.Session,
                        PeerAuthenticationContract.ReleasePath,
                        requestBody))
                {
                    if (!requestResult.IsVerified)
                    {
                        return false;
                    }

                    responseBody = requestResult.Response.CopyBody();
                    PeerControlResponse response;
                    try
                    {
                        response = PeerSyncXmlCodec
                            .ParseAuthenticatedReleaseResponse(
                                responseBody);
                    }
                    catch (PeerSyncProtocolException)
                    {
                        return false;
                    }

                    return response.IsSuccess
                        && IsHttpStatusConsistent(
                            requestResult.Response.StatusCode,
                            response.Code);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (session != null)
                {
                    session.Dispose();
                }

                Clear(sessionId);
                Clear(requestBody);
                Clear(responseBody);
            }
        }

        private bool TrySendRevoke(PairedPeerCredential credential)
        {
            byte[] pairRoot = null;
            byte[] requestBody = null;
            byte[] responseBody = null;
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
                    requestBody = PeerSyncXmlCodec.SerializeRevokeRequest(
                        new PeerRevokeRequest(
                            credential.LocalInstanceId,
                            credential.PeerInstanceId,
                            credential.KeyEpoch,
                            Guid.NewGuid()));
                    using (OutboundRequestResult requestResult =
                        SendAuthenticatedRequest(
                            credential,
                            pairAuthentication,
                            null,
                            OutboundPeerAuthenticationPurpose.Revoke,
                            PeerAuthenticationContract.RevokePath,
                            requestBody))
                    {
                        if (!requestResult.IsVerified)
                        {
                            return false;
                        }

                        responseBody = requestResult.Response.CopyBody();
                        PeerControlResponse response;
                        try
                        {
                            response = PeerSyncXmlCodec
                                .ParseAuthenticatedRevokeResponse(
                                    responseBody);
                        }
                        catch (PeerSyncProtocolException)
                        {
                            return false;
                        }

                        return response.IsSuccess
                            && IsHttpStatusConsistent(
                                requestResult.Response.StatusCode,
                                response.Code);
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                Clear(pairRoot);
                Clear(requestBody);
                Clear(responseBody);
            }
        }

        private static bool CredentialMatchesConfiguration(
            PairedPeerCredential credential,
            ServiceDirectoryConfiguration configuration)
        {
            if (credential == null || configuration == null)
            {
                return false;
            }

            DurablePeerCredentialState expectedCredentialState;
            switch (configuration.Synchronization.State)
            {
                case DurableSynchronizationState.PairedPendingCommit:
                    expectedCredentialState =
                        DurablePeerCredentialState.PairedPendingCommit;
                    break;
                case DurableSynchronizationState.PairedDisabled:
                    expectedCredentialState =
                        DurablePeerCredentialState.PairedDisabled;
                    break;
                case DurableSynchronizationState.Enabled:
                    expectedCredentialState =
                        DurablePeerCredentialState.Enabled;
                    break;
                default:
                    return false;
            }

            return credential.State == expectedCredentialState
                && CredentialBindingMatchesConfiguration(
                    credential,
                    configuration);
        }

        private static bool CredentialBindingMatchesConfiguration(
            PairedPeerCredential credential,
            ServiceDirectoryConfiguration configuration)
        {
            return credential != null
                && configuration != null
                && credential.LocalInstanceId
                    == configuration.InstanceId
                && configuration.Synchronization.PeerInstanceId
                    == credential.PeerInstanceId
                && configuration.Synchronization.KeyEpoch
                    == credential.KeyEpoch
                && StringComparer.Ordinal.Equals(
                    configuration.Synchronization.PeerEndpoint,
                    credential.PeerEndpoint);
        }

        private static AdminHandlerResult<
            AdminServerSyncDisableResponse> DisableFailure(
            AdminServerErrorCode code)
        {
            return AdminHandlerResult<
                AdminServerSyncDisableResponse>.Failure(code);
        }
    }
}
