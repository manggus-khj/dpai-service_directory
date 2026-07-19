using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.Tray.Clients;

namespace DEEPAi.ServiceDirectory.Tray.ViewModels
{
    public sealed partial class StatusMonitorViewModel
    {
        private static readonly TimeSpan SyncPollingInterval =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ListPollingInterval =
            TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WatchdogPollingInterval =
            TimeSpan.FromSeconds(10);

        private async Task RunSyncPollingAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    if (_automaticAdminPollingSuspended)
                    {
                        await Task.Delay(SyncPollingInterval, cancellationToken);
                        continue;
                    }

                    await DelayForAdminRetryAsync(cancellationToken);
                    if (!_automaticAdminPollingSuspended)
                    {
                        await RefreshSyncAsync(cancellationToken, false);
                    }
                    await Task.Delay(SyncPollingInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                HandleUnexpectedException(exception);
            }
        }

        private async Task RunListPollingAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    if (_automaticAdminPollingSuspended)
                    {
                        await Task.Delay(ListPollingInterval, cancellationToken);
                        continue;
                    }

                    await DelayForAdminRetryAsync(cancellationToken);
                    if (!_automaticAdminPollingSuspended)
                    {
                        await RefreshVisibleListAsync(cancellationToken, false);
                    }
                    await Task.Delay(ListPollingInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                HandleUnexpectedException(exception);
            }
        }

        private async Task RunWatchdogPollingAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await RefreshWatchdogAsync(cancellationToken);
                    await Task.Delay(WatchdogPollingInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                HandleUnexpectedException(exception);
            }
        }

        private async Task RefreshCurrentPageAsync()
        {
            CancellationToken cancellationToken = _lifetimeCancellation.Token;
            if (SelectedPageIndex == 3 || SelectedPageIndex == 0)
            {
                await RefreshSyncAsync(cancellationToken, true);
                await RefreshWatchdogAsync(cancellationToken);
            }
            else if (SelectedPageIndex == 4)
            {
                await RefreshLoggingAsync(cancellationToken, true);
                await RefreshCertificateAdministrationAsync(
                    cancellationToken,
                    true);
            }
            else
            {
                await RefreshVisibleListAsync(cancellationToken, true);
            }
        }

        private async Task RefreshSyncAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            if (!await _syncRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                try
                {
                    AdminSyncStatus status = await _adminClient.GetSyncStatusAsync(
                        cancellationToken);
                    ApplySyncStatus(status, manualProbe);
                }
                catch (AdminApiException exception)
                {
                    HandleAdminFailure(exception);
                }
            }
            finally
            {
                _syncRefreshGate.Release();
            }
        }

        private async Task RefreshWatchdogAsync(
            CancellationToken cancellationToken)
        {
            if (!await _watchdogRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                await RefreshWatchdogCoreAsync(cancellationToken);
            }
            finally
            {
                _watchdogRefreshGate.Release();
            }
        }

        private async Task RefreshWatchdogCoreAsync(
            CancellationToken cancellationToken)
        {
            WatchdogCallResult result = await _watchdogClient.SendAsync(
                WatchdogPipeCommand.Status,
                cancellationToken);
            if (!result.IsSuccess || result.StatusSnapshot == null)
            {
                IsWatchdogConnected = false;
                WatchdogServiceStatusText = "확인할 수 없음";
                WatchdogHealthText = "확인할 수 없음";
                WatchdogFailuresText = "—";
                WatchdogRestartsText = "—";
                WatchdogAutoRestartText = "확인할 수 없음";
                WatchdogLastHealthText = "—";
                TrayIconSource = StoppedTrayIcon;
                TrayToolTipText = "DEEPAi Service Directory | UNKNOWN | 연결 안 됨";
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    SetStatus(result.ErrorMessage, true);
                }

                return;
            }

            ApplyWatchdogSnapshot(result.StatusSnapshot);
        }

        private async Task RefreshVisibleListAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            int page = SelectedPageIndex;
            if (page != 1 && page != 2)
            {
                return;
            }

            if (!await _listRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                if (page == 1)
                {
                    await RefreshPendingPageCoreAsync(
                        cancellationToken,
                        manualProbe);
                }
                else
                {
                    await RefreshServicePageCoreAsync(
                        cancellationToken,
                        manualProbe);
                }
            }
            finally
            {
                _listRefreshGate.Release();
            }
        }

        private async Task RefreshPendingPageCoreAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            try
            {
                AdminPage<AdminPendingItem> page = await _adminClient.GetPendingAsync(
                    _pendingCursor,
                    cancellationToken);
                ApplyPendingPage(page, manualProbe);
            }
            catch (AdminApiException exception) when (
                exception.IsConflict && _pendingCursor != null)
            {
                ResetPendingPaging();
                try
                {
                    AdminPage<AdminPendingItem> page =
                        await _adminClient.GetPendingAsync(null, cancellationToken);
                    ApplyPendingPage(page, manualProbe);
                    SetStatus(
                        "승인 대기 목록이 변경되어 첫 페이지부터 다시 표시합니다.",
                        false);
                }
                catch (AdminApiException retryException)
                {
                    HandleAdminFailure(retryException);
                }
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task RefreshServicePageCoreAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            try
            {
                AdminPage<AdminServiceItem> page = await _adminClient.GetServicesAsync(
                    _serviceCursor,
                    cancellationToken);
                ApplyServicePage(page, manualProbe);
            }
            catch (AdminApiException exception) when (
                exception.IsConflict && _serviceCursor != null)
            {
                ResetServicePaging();
                try
                {
                    AdminPage<AdminServiceItem> page =
                        await _adminClient.GetServicesAsync(null, cancellationToken);
                    ApplyServicePage(page, manualProbe);
                    SetStatus(
                        "등록 서비스 목록이 변경되어 첫 페이지부터 다시 표시합니다.",
                        false);
                }
                catch (AdminApiException retryException)
                {
                    HandleAdminFailure(retryException);
                }
            }
            catch (AdminApiException exception)
            {
                HandleAdminFailure(exception);
            }
        }

        private async Task RefreshLoggingAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            if (!await _loggingRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                try
                {
                    AdminLoggingSettings settings =
                        await _adminClient.GetLoggingSettingsAsync(cancellationToken);
                    LogRetentionDaysText = settings.LogRetentionDays.ToString(
                        CultureInfo.InvariantCulture);
                    MarkAdminSuccess(manualProbe);
                }
                catch (AdminApiException exception)
                {
                    HandleAdminFailure(exception);
                }
            }
            finally
            {
                _loggingRefreshGate.Release();
            }
        }

        private void ApplySyncStatus(
            AdminSyncStatus status,
            bool manualProbe)
        {
            _syncStatus = status ?? throw new ArgumentNullException(nameof(status));
            SyncStateText = status.PairingStateText;
            SyncConnectionText = status.Enabled ? "정상" : "비활성";
            SyncConnectionBrush = status.Enabled ? Brushes.SeaGreen : Brushes.DarkOrange;
            PeerEndpointText = status.PeerEndpoint ?? "—";
            PeerEndpointInput = status.PeerEndpoint ?? PeerEndpointInput;
            PeerInstanceIdText = status.PeerInstanceId.HasValue
                ? status.PeerInstanceId.Value.ToString("D")
                : "—";
            KeyEpochText = status.KeyEpoch.HasValue
                ? status.KeyEpoch.Value.ToString(CultureInfo.InvariantCulture)
                : "—";
            LastSyncText = FormatUtc(status.LastSyncUtc);
            LastResultText = status.LastResult;
            ClockSkewText = status.ClockSkewSeconds.HasValue
                ? status.ClockSkewSeconds.Value.ToString(
                    CultureInfo.CurrentCulture) + "초"
                : "—";
            PairingIdText = status.PairingId.HasValue
                ? status.PairingId.Value.ToString("D")
                : "—";
            PairingSasText = status.Sas ?? "—";
            PairingExpiryText = status.PairingExpiresUtc.HasValue
                ? FormatUtc(status.PairingExpiresUtc)
                : FormatUtc(status.CommitExpiresUtc);
            PairingProgressText = FormatPairingProgress(status);
            PeerNotificationText = FormatPeerNotification(status);
            PeerNotificationWarning = status.HasUnconfirmedPeerNotification
                ? "로컬 처리는 완료됐지만 상대 피어의 "
                    + status.LastPeerNotificationOperation.ToString().ToUpperInvariant()
                    + " 확인을 받지 못했습니다. 상대 상태를 확인하십시오."
                : null;
            MarkAdminSuccess(manualProbe);
            RaiseCommandStates();
        }

        private void ApplyWatchdogSnapshot(WatchdogStatusSnapshot snapshot)
        {
            IsWatchdogConnected = true;
            string serviceStatus = FormatServiceStatus(snapshot.ServiceStatus);
            string health = FormatHealthStatus(snapshot.HealthStatus);
            string autoRestart = FormatAutoRestartStatus(snapshot.AutoRestartStatus);
            string lastHealth = snapshot.LastHealthUtc.HasValue
                ? snapshot.LastHealthUtc.Value.ToString(
                    "yyyy-MM-ddTHH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture)
                : "NOT_RUN";

            WatchdogServiceStatusText = serviceStatus;
            WatchdogHealthText = health;
            WatchdogFailuresText = snapshot.ConsecutiveFailures.ToString(
                CultureInfo.CurrentCulture);
            WatchdogRestartsText = snapshot.RestartCountInTenMinutes.ToString(
                CultureInfo.CurrentCulture);
            WatchdogAutoRestartText = autoRestart;
            WatchdogLastHealthText = lastHealth;
            bool running = snapshot.ServiceStatus == WatchdogServiceStatus.Running;
            TrayIconSource = running ? RunningTrayIcon : StoppedTrayIcon;
            string toolTip = "DEEPAi Service Directory | " + serviceStatus
                + " | HEALTH=" + health
                + " | F=" + snapshot.ConsecutiveFailures.ToString(
                    CultureInfo.InvariantCulture)
                + " | R10=" + snapshot.RestartCountInTenMinutes.ToString(
                    CultureInfo.InvariantCulture)
                + " | AR=" + autoRestart
                + " | LH=" + lastHealth;
            TrayToolTipText = toolTip.Length <= 127
                ? toolTip
                : "DEEPAi Service Directory | " + serviceStatus
                    + " | HEALTH=" + health;
        }

        private void ApplyPendingPage(
            AdminPage<AdminPendingItem> page,
            bool manualProbe)
        {
            PendingItems.Clear();
            foreach (AdminPendingItem item in page.Items)
            {
                PendingItems.Add(item);
            }

            SelectedPending = null;
            _pendingTotalCount = page.TotalCount;
            _pendingNextCursor = page.NextCursor;
            OnPropertyChanged(nameof(PendingCountText));
            OnPropertyChanged(nameof(HasPendingCapacityWarning));
            OnPropertyChanged(nameof(PendingCapacityWarningText));
            OnPropertyChanged(nameof(PendingPageText));
            MarkAdminSuccess(manualProbe);
            RaiseCommandStates();
        }

        private void ApplyServicePage(
            AdminPage<AdminServiceItem> page,
            bool manualProbe)
        {
            Services.Clear();
            foreach (AdminServiceItem item in page.Items)
            {
                Services.Add(item);
            }

            SelectedService = null;
            _serviceTotalCount = page.TotalCount;
            _serviceNextCursor = page.NextCursor;
            OnPropertyChanged(nameof(ServiceCountText));
            OnPropertyChanged(nameof(ServicePageText));
            MarkAdminSuccess(manualProbe);
            RaiseCommandStates();
        }

        private void ResetPendingPaging()
        {
            _pendingCursor = null;
            _pendingNextCursor = null;
            _pendingPreviousCursors.Clear();
            _pendingPageNumber = 1;
            OnPropertyChanged(nameof(PendingPageText));
            RaiseCommandStates();
        }

        private void ResetServicePaging()
        {
            _serviceCursor = null;
            _serviceNextCursor = null;
            _servicePreviousCursors.Clear();
            _servicePageNumber = 1;
            OnPropertyChanged(nameof(ServicePageText));
            RaiseCommandStates();
        }

        private void HandleAdminFailure(AdminApiException exception)
        {
            if (exception.StatusCode == (System.Net.HttpStatusCode)429)
            {
                if (exception.RetryAfter.HasValue)
                {
                    DateTimeOffset retryNotBefore = DateTimeOffset.Now
                        + exception.RetryAfter.Value;
                    if (retryNotBefore > _adminRetryNotBefore)
                    {
                        _adminRetryNotBefore = retryNotBefore;
                    }
                }
                else
                {
                    _automaticAdminPollingSuspended = true;
                    SetStatus(
                        "요청 제한의 해제 시각을 확인할 수 없어 자동 갱신을 중지했습니다. 새로 고침으로 수동 재확인하십시오.",
                        true);
                    return;
                }

                SetStatus(exception.Message, true);
                return;
            }

            if (!exception.StatusCode.HasValue
                || exception.StatusCode.Value == System.Net.HttpStatusCode.Unauthorized
                || exception.StatusCode.Value == System.Net.HttpStatusCode.Forbidden)
            {
                IsAdminConnected = false;
                SyncConnectionText = "확인할 수 없음";
                SyncConnectionBrush = Brushes.Gray;
            }
            else
            {
                IsAdminConnected = true;
            }

            SetStatus(exception.Message, true);
        }

        private void RequestImmediateRefresh()
        {
            CancellationToken token;
            lock (_pollingStateGate)
            {
                if (_pollingCancellation == null)
                {
                    return;
                }

                token = _pollingCancellation.Token;
            }

            RefreshForNavigationAsync(token);
        }

        private async void RefreshForNavigationAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (SelectedPageIndex == 1 || SelectedPageIndex == 2)
                {
                    await RefreshVisibleListAsync(cancellationToken, true);
                }
                else if (SelectedPageIndex == 4)
                {
                    await RefreshLoggingAsync(cancellationToken, true);
                }
                else
                {
                    await RefreshSyncAsync(cancellationToken, true);
                    await RefreshWatchdogAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                HandleUnexpectedException(exception);
            }
        }

        private async Task DelayForAdminRetryAsync(
            CancellationToken cancellationToken)
        {
            while (true)
            {
                TimeSpan remaining = _adminRetryNotBefore - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                await Task.Delay(remaining, cancellationToken);
            }
        }

        private void MarkAdminSuccess(bool manualProbe)
        {
            IsAdminConnected = true;
            if (manualProbe)
            {
                _automaticAdminPollingSuspended = false;
            }

            if (DateTimeOffset.Now >= _adminRetryNotBefore)
            {
                _adminRetryNotBefore = default(DateTimeOffset);
            }
        }

        private static string FormatPairingProgress(AdminSyncStatus status)
        {
            if (status.PairingState == AdminPairingState.SasPending
                || status.PairingState == AdminPairingState.BothConfirmed)
            {
                return "SAS 확인 · 로컬 "
                    + FormatConfirmation(status.LocalConfirmed)
                    + " · 상대 "
                    + FormatConfirmation(status.RemoteConfirmed)
                    + FormatRemaining(status.PairingRemainingSeconds);
            }

            if (status.PairingState == AdminPairingState.PairedPendingCommit)
            {
                return "Commit · 로컬 "
                    + FormatConfirmation(status.LocalCommitConfirmed)
                    + " · 상대 "
                    + FormatConfirmation(status.RemoteCommitConfirmed);
            }

            if (status.PairingRemainingSeconds.HasValue)
            {
                return "페어링 진행 중"
                    + FormatRemaining(status.PairingRemainingSeconds);
            }

            return "—";
        }

        private static string FormatPeerNotification(AdminSyncStatus status)
        {
            string value = status.LastPeerNotificationOperation
                .ToString()
                .ToUpperInvariant()
                + " / "
                + FormatPeerNotificationResult(
                    status.LastPeerNotificationResult);
            if (status.LastPeerNotificationUtc.HasValue)
            {
                value += " · " + FormatUtc(status.LastPeerNotificationUtc);
            }

            return value;
        }

        private static string FormatPeerNotificationResult(
            AdminPeerNotificationResult result)
        {
            switch (result)
            {
                case AdminPeerNotificationResult.NotRun:
                    return "NOT_RUN";
                case AdminPeerNotificationResult.Confirmed:
                    return "CONFIRMED";
                case AdminPeerNotificationResult.Unconfirmed:
                    return "UNCONFIRMED";
                case AdminPeerNotificationResult.NotRequired:
                    return "NOT_REQUIRED";
                default:
                    return "UNKNOWN";
            }
        }

        private static string FormatConfirmation(bool? value)
        {
            return !value.HasValue ? "—" : value.Value ? "확인" : "대기";
        }

        private static string FormatRemaining(int? seconds)
        {
            return seconds.HasValue
                ? " · " + seconds.Value.ToString(CultureInfo.CurrentCulture) + "초 남음"
                : string.Empty;
        }

        private static string FormatUtc(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString(
                    "yyyy-MM-dd HH:mm:ss 'UTC'",
                    CultureInfo.InvariantCulture)
                : "—";
        }

        private static string FormatServiceStatus(WatchdogServiceStatus status)
        {
            switch (status)
            {
                case WatchdogServiceStatus.Stopped:
                    return "STOPPED";
                case WatchdogServiceStatus.StartPending:
                    return "START_PENDING";
                case WatchdogServiceStatus.StopPending:
                    return "STOP_PENDING";
                case WatchdogServiceStatus.Running:
                    return "RUNNING";
                case WatchdogServiceStatus.ContinuePending:
                    return "CONTINUE_PENDING";
                case WatchdogServiceStatus.PausePending:
                    return "PAUSE_PENDING";
                case WatchdogServiceStatus.Paused:
                    return "PAUSED";
                default:
                    return "UNKNOWN";
            }
        }

        private static string FormatHealthStatus(WatchdogHealthStatus status)
        {
            switch (status)
            {
                case WatchdogHealthStatus.NotRun:
                    return "NOT_RUN";
                case WatchdogHealthStatus.Ok:
                    return "OK";
                case WatchdogHealthStatus.Failed:
                    return "FAILED";
                default:
                    return "UNKNOWN";
            }
        }

        private static string FormatAutoRestartStatus(
            WatchdogAutoRestartStatus status)
        {
            switch (status)
            {
                case WatchdogAutoRestartStatus.Enabled:
                    return "ENABLED";
                case WatchdogAutoRestartStatus.Suppressed:
                    return "SUPPRESSED";
                default:
                    return "UNKNOWN";
            }
        }
    }
}
