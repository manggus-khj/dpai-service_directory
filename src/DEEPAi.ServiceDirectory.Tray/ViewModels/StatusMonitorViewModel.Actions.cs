using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.Tray.Clients;

namespace DEEPAi.ServiceDirectory.Tray.ViewModels
{
    public sealed partial class StatusMonitorViewModel
    {
        private Task StartServiceAsync()
        {
            return ExecuteWatchdogControlAsync(
                WatchdogPipeCommand.Start,
                "서비스 시작 명령을 전달했습니다.");
        }

        private Task StopServiceAsync()
        {
            return ExecuteWatchdogControlAsync(
                WatchdogPipeCommand.Stop,
                "서비스 중지 명령을 전달했습니다.");
        }

        private Task RestartServiceAsync()
        {
            return ExecuteWatchdogControlAsync(
                WatchdogPipeCommand.Restart,
                "서비스 재시작 명령을 전달했습니다.");
        }

        private async Task ExecuteWatchdogControlAsync(
            WatchdogPipeCommand command,
            string successMessage)
        {
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            WatchdogCallResult result = await _watchdogClient.SendAsync(
                command,
                cancellationToken);
            if (!result.IsSuccess)
            {
                SetStatus(result.ErrorMessage, true);
            }
            else
            {
                SetStatus(successMessage, false);
            }

            await RefreshWatchdogAsync(cancellationToken);
        }

        private async Task ApprovePendingAsync()
        {
            AdminPendingItem selected = SelectedPending;
            if (selected == null)
            {
                return;
            }

            try
            {
                await _adminClient.ApprovePendingAsync(
                    selected.Id,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus("승인 요청을 처리했습니다.", false);
                await RefreshPendingWithGateAsync();
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task RejectPendingAsync()
        {
            AdminPendingItem selected = SelectedPending;
            if (selected == null)
            {
                return;
            }

            try
            {
                await _adminClient.RejectPendingAsync(
                    selected.Id,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus("승인 요청을 거절했습니다.", false);
                await RefreshPendingWithGateAsync();
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task DeleteServiceAsync()
        {
            AdminServiceItem selected = SelectedService;
            if (selected == null)
            {
                return;
            }

            if (!_confirmDelete(
                selected.ProductCode
                + " 등록 서비스를 삭제하시겠습니까?\n삭제 정보는 톰스톤으로 동기화됩니다."))
            {
                return;
            }

            try
            {
                await _adminClient.DeleteServiceAsync(
                    selected.ProductCode,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus("등록 서비스 삭제 요청을 처리했습니다.", false);
                await RefreshServicesWithGateAsync();
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task SyncNowAsync()
        {
            try
            {
                await _adminClient.SyncNowAsync(_lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus("수동 동기화를 요청했습니다.", false);
                await RefreshSyncAsync(_lifetimeCancellation.Token, true);
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task EnableSyncAsync()
        {
            string canonicalEndpoint;
            if (!AdminPeerEndpoint.TryNormalize(
                PeerEndpointInput,
                out canonicalEndpoint))
            {
                SetStatus(
                    "피어 endpoint는 http://{IP}:21000 형식이어야 합니다.",
                    true);
                return;
            }

            try
            {
                PeerEndpointInput = canonicalEndpoint;
                await _adminClient.EnableSyncAsync(
                    canonicalEndpoint,
                    RePairRequested,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus(
                    RePairRequested
                        ? "재페어링을 시작했습니다. 양쪽 화면의 SAS를 확인하십시오."
                        : "동기화 활성화 또는 페어링을 요청했습니다.",
                    false);
                await RefreshSyncAsync(_lifetimeCancellation.Token, true);
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task ConfirmPairingAsync()
        {
            Guid? pairingId = _syncStatus?.PairingId;
            if (!pairingId.HasValue)
            {
                return;
            }

            try
            {
                await _adminClient.ConfirmPairingAsync(
                    pairingId.Value,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus("로컬 SAS 확인을 제출했습니다.", false);
                await RefreshSyncAsync(_lifetimeCancellation.Token, true);
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task CancelPairingAsync()
        {
            Guid? pairingId = _syncStatus?.PairingId;
            if (!pairingId.HasValue)
            {
                return;
            }

            try
            {
                await _adminClient.CancelPairingAsync(
                    pairingId.Value,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                SetStatus("페어링을 취소했습니다.", false);
                await RefreshSyncAsync(_lifetimeCancellation.Token, true);
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private Task DisableSyncAsync()
        {
            return DisableOrForgetPeerAsync(false);
        }

        private Task ForgetPeerAsync()
        {
            return DisableOrForgetPeerAsync(true);
        }

        private async Task DisableOrForgetPeerAsync(bool forgetPeer)
        {
            try
            {
                AdminSyncDisableResult result = await _adminClient.DisableSyncAsync(
                    forgetPeer,
                    _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                if (result.IsPeerConfirmationUnconfirmed)
                {
                    PeerNotificationWarning = "로컬 "
                        + result.LocalPairingState.ToString()
                        + " 처리는 완료됐지만 상대 피어의 "
                        + result.PeerNotificationOperation.ToString().ToUpperInvariant()
                        + " 확인을 받지 못했습니다.";
                    SetStatus(PeerNotificationWarning, true);
                }
                else
                {
                    SetStatus(
                        forgetPeer
                            ? "로컬 피어 자격 증명을 폐기했습니다."
                            : "동기화를 비활성화했습니다.",
                        false);
                }

                await RefreshSyncAsync(_lifetimeCancellation.Token, true);
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task SaveLoggingAsync()
        {
            int days;
            if (!int.TryParse(
                LogRetentionDaysText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out days)
                || days < AdminApiContract.MinimumLogRetentionDays
                || days > AdminApiContract.MaximumLogRetentionDays)
            {
                SetStatus("로그 보존기간은 1~1095일 정수여야 합니다.", true);
                return;
            }

            try
            {
                AdminLoggingSettings saved =
                    await _adminClient.UpdateLoggingSettingsAsync(
                        days,
                        _lifetimeCancellation.Token);
                MarkAdminSuccess(true);
                LogRetentionDaysText = saved.LogRetentionDays.ToString(
                    CultureInfo.InvariantCulture);
                SetStatus("로그 보존기간을 저장했습니다.", false);
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task PreviousPendingPageAsync()
        {
            if (_pendingPreviousCursors.Count == 0
                || !await _listRefreshGate.WaitAsync(
                    0,
                    _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                string targetCursor = _pendingPreviousCursors.Peek();
                AdminPage<AdminPendingItem> page = await _adminClient.GetPendingAsync(
                    targetCursor,
                    _lifetimeCancellation.Token);
                _pendingPreviousCursors.Pop();
                _pendingCursor = targetCursor;
                _pendingPageNumber--;
                ApplyPendingPage(page, true);
            }
            catch (AdminApiException exception)
            {
                await HandlePendingNavigationFailureAsync(exception);
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }

        private async Task NextPendingPageAsync()
        {
            string targetCursor = _pendingNextCursor;
            if (string.IsNullOrEmpty(targetCursor)
                || !await _listRefreshGate.WaitAsync(
                    0,
                    _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                AdminPage<AdminPendingItem> page = await _adminClient.GetPendingAsync(
                    targetCursor,
                    _lifetimeCancellation.Token);
                _pendingPreviousCursors.Push(_pendingCursor);
                _pendingCursor = targetCursor;
                _pendingPageNumber++;
                ApplyPendingPage(page, true);
            }
            catch (AdminApiException exception)
            {
                await HandlePendingNavigationFailureAsync(exception);
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }

        private async Task PreviousServicePageAsync()
        {
            if (_servicePreviousCursors.Count == 0
                || !await _listRefreshGate.WaitAsync(
                    0,
                    _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                string targetCursor = _servicePreviousCursors.Peek();
                AdminPage<AdminServiceItem> page = await _adminClient.GetServicesAsync(
                    targetCursor,
                    _lifetimeCancellation.Token);
                _servicePreviousCursors.Pop();
                _serviceCursor = targetCursor;
                _servicePageNumber--;
                ApplyServicePage(page, true);
            }
            catch (AdminApiException exception)
            {
                await HandleServiceNavigationFailureAsync(exception);
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }

        private async Task NextServicePageAsync()
        {
            string targetCursor = _serviceNextCursor;
            if (string.IsNullOrEmpty(targetCursor)
                || !await _listRefreshGate.WaitAsync(
                    0,
                    _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                AdminPage<AdminServiceItem> page = await _adminClient.GetServicesAsync(
                    targetCursor,
                    _lifetimeCancellation.Token);
                _servicePreviousCursors.Push(_serviceCursor);
                _serviceCursor = targetCursor;
                _servicePageNumber++;
                ApplyServicePage(page, true);
            }
            catch (AdminApiException exception)
            {
                await HandleServiceNavigationFailureAsync(exception);
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }

        private async Task HandlePendingNavigationFailureAsync(
            AdminApiException exception)
        {
            if (exception.IsConflict)
            {
                ResetPendingPaging();
                await RefreshPendingPageCoreAsync(
                    _lifetimeCancellation.Token,
                    true);
            }
            else
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task HandleServiceNavigationFailureAsync(
            AdminApiException exception)
        {
            if (exception.IsConflict)
            {
                ResetServicePaging();
                await RefreshServicePageCoreAsync(
                    _lifetimeCancellation.Token,
                    true);
            }
            else
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task RefreshPendingWithGateAsync()
        {
            if (!await _listRefreshGate.WaitAsync(
                0,
                _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                await RefreshPendingPageCoreAsync(
                    _lifetimeCancellation.Token,
                    true);
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }

        private async Task RefreshServicesWithGateAsync()
        {
            if (!await _listRefreshGate.WaitAsync(
                0,
                _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                await RefreshServicePageCoreAsync(
                    _lifetimeCancellation.Token,
                    true);
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }
    }
}
