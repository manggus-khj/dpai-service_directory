using System;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.Tray.Clients;

namespace DEEPAi.ServiceDirectory.Tray.ViewModels
{
    public sealed partial class StatusMonitorViewModel
    {
        public async Task CreateCaBackupFromUiAsync(
            string password,
            string passwordConfirmation)
        {
            if (string.IsNullOrEmpty(password))
            {
                SetStatus("CA 백업 암호를 입력하십시오.", true);
                return;
            }

            if (!StringComparer.Ordinal.Equals(
                password,
                passwordConfirmation))
            {
                SetStatus("CA 백업 암호 확인이 일치하지 않습니다.", true);
                return;
            }

            if (!await _certificateRefreshGate.WaitAsync(
                0,
                _lifetimeCancellation.Token))
            {
                SetStatus("다른 인증서 관리 요청이 처리 중입니다.", true);
                return;
            }

            try
            {
                try
                {
                    AdminServerCaBackupResponse backup =
                        await _adminClient.CreateCaBackupAsync(
                            password,
                            _lifetimeCancellation.Token);
                    LastCaBackupText = backup.FileName
                        + " · SHA-256 "
                        + backup.Sha256;
                    MarkAdminSuccess(true);
                    SetStatus("암호화된 Site CA 백업을 생성했습니다.", false);
                    await RefreshCertificateAdministrationCoreAsync(
                        _lifetimeCancellation.Token,
                        false);
                }
                catch (AdminApiException exception)
                {
                    HandleAdminFailure(exception);
                }
            }
            finally
            {
                _certificateRefreshGate.Release();
            }
        }

        private async Task RefreshCertificateAdministrationAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            if (!await _certificateRefreshGate.WaitAsync(
                0,
                cancellationToken))
            {
                return;
            }

            try
            {
                await RefreshCertificateAdministrationCoreAsync(
                    cancellationToken,
                    manualProbe);
            }
            finally
            {
                _certificateRefreshGate.Release();
            }
        }

        private async Task RefreshCertificateAdministrationCoreAsync(
            CancellationToken cancellationToken,
            bool manualProbe)
        {
            try
            {
                AdminServerCaStatusResponse status =
                    await _adminClient.GetCaStatusAsync(cancellationToken);
                ApplyCaStatus(status);
                if (status.State == AdminCaState.NotProvisioned)
                {
                    ResetCertificatePaging();
                    Certificates.Clear();
                    _certificateTotalCount = 0;
                    OnPropertyChanged(nameof(CertificatePageText));
                }
                else
                {
                    await RefreshCertificatePageCoreAsync(
                        cancellationToken);
                }

                MarkAdminSuccess(manualProbe);
            }
            catch (AdminApiException exception) when (
                exception.IsConflict && _certificateCursor != null)
            {
                ResetCertificatePaging();
                try
                {
                    await RefreshCertificatePageCoreAsync(
                        cancellationToken);
                    MarkAdminSuccess(manualProbe);
                    SetStatus(
                        "인증서 원장이 변경되어 첫 페이지부터 다시 표시합니다.",
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

        private async Task RefreshCertificatePageCoreAsync(
            CancellationToken cancellationToken)
        {
            AdminServerCertificatesResponse page =
                await _adminClient.GetCertificatesAsync(
                    _certificateCursor,
                    cancellationToken);
            ApplyCertificatePage(page);
        }

        private async Task RevokeCertificateAsync()
        {
            AdminServerCertificateItem selected = SelectedCertificate;
            if (selected == null)
            {
                return;
            }

            if (!_confirmDelete(
                selected.SerialNumber
                + " 인증서를 "
                + SelectedRevocationReason.ToString()
                + " 사유로 폐기하시겠습니까?\n이 작업은 취소할 수 없습니다."))
            {
                return;
            }

            if (!await _certificateRefreshGate.WaitAsync(
                0,
                _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                try
                {
                    AdminServerCertificateRevocationResponse result =
                        await _adminClient.RevokeCertificateAsync(
                            selected.SerialNumber,
                            SelectedRevocationReason,
                            _lifetimeCancellation.Token);
                    MarkAdminSuccess(true);
                    SetStatus(
                        result.Replayed
                            ? "이미 같은 사유로 폐기된 인증서입니다."
                            : "인증서를 폐기하고 CRL을 갱신했습니다.",
                        false);
                    ResetCertificatePaging();
                    await RefreshCertificateAdministrationCoreAsync(
                        _lifetimeCancellation.Token,
                        false);
                }
                catch (AdminApiException exception)
                {
                    HandleAdminFailure(exception);
                }
            }
            finally
            {
                _certificateRefreshGate.Release();
            }
        }

        private async Task PreviousCertificatePageAsync()
        {
            if (_certificatePreviousCursors.Count == 0
                || !await _certificateRefreshGate.WaitAsync(
                    0,
                    _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                string targetCursor = _certificatePreviousCursors.Peek();
                AdminServerCertificatesResponse page =
                    await _adminClient.GetCertificatesAsync(
                        targetCursor,
                        _lifetimeCancellation.Token);
                _certificatePreviousCursors.Pop();
                _certificateCursor = targetCursor;
                _certificatePageNumber--;
                ApplyCertificatePage(page);
                MarkAdminSuccess(true);
            }
            catch (AdminApiException exception)
            {
                await HandleCertificateNavigationFailureAsync(exception);
            }
            finally
            {
                _certificateRefreshGate.Release();
            }
        }

        private async Task NextCertificatePageAsync()
        {
            string targetCursor = _certificateNextCursor;
            if (string.IsNullOrEmpty(targetCursor)
                || !await _certificateRefreshGate.WaitAsync(
                    0,
                    _lifetimeCancellation.Token))
            {
                return;
            }

            try
            {
                AdminServerCertificatesResponse page =
                    await _adminClient.GetCertificatesAsync(
                        targetCursor,
                        _lifetimeCancellation.Token);
                _certificatePreviousCursors.Push(_certificateCursor);
                _certificateCursor = targetCursor;
                _certificatePageNumber++;
                ApplyCertificatePage(page);
                MarkAdminSuccess(true);
            }
            catch (AdminApiException exception)
            {
                await HandleCertificateNavigationFailureAsync(exception);
            }
            finally
            {
                _certificateRefreshGate.Release();
            }
        }

        private async Task HandleCertificateNavigationFailureAsync(
            AdminApiException exception)
        {
            if (exception.IsConflict)
            {
                ResetCertificatePaging();
                try
                {
                    await RefreshCertificateAdministrationCoreAsync(
                        _lifetimeCancellation.Token,
                        true);
                    SetStatus(
                        "인증서 원장이 변경되어 첫 페이지부터 다시 표시합니다.",
                        false);
                }
                catch (AdminApiException retryException)
                {
                    HandleAdminFailure(retryException);
                }

                return;
            }

            HandleAdminFailure(exception);
        }

        private void ApplyCaStatus(AdminServerCaStatusResponse status)
        {
            _caStatus = status ?? throw new ArgumentNullException(
                nameof(status));
            OnPropertyChanged(nameof(CaStateText));
            OnPropertyChanged(nameof(CaRoleText));
            OnPropertyChanged(nameof(CaSiteIdText));
            OnPropertyChanged(nameof(CaSerialText));
            OnPropertyChanged(nameof(CaRevisionText));
            OnPropertyChanged(nameof(CaCrlNumberText));
            OnPropertyChanged(nameof(CaRevisionAndCrlText));
            OnPropertyChanged(nameof(CaLastBackupText));
            RaiseCommandStates();
        }

        private void ApplyCertificatePage(
            AdminServerCertificatesResponse page)
        {
            if (page == null)
            {
                throw new ArgumentNullException(nameof(page));
            }

            Certificates.Clear();
            foreach (AdminServerCertificateItem item in page.Items)
            {
                Certificates.Add(item);
            }

            SelectedCertificate = null;
            _certificateTotalCount = page.TotalCount;
            _certificateNextCursor = page.NextCursor;
            OnPropertyChanged(nameof(CertificatePageText));
            RaiseCommandStates();
        }

        private void ResetCertificatePaging()
        {
            _certificatePreviousCursors.Clear();
            _certificateCursor = null;
            _certificateNextCursor = null;
            _certificatePageNumber = 1;
            OnPropertyChanged(nameof(CertificatePageText));
            RaiseCommandStates();
        }
    }
}
