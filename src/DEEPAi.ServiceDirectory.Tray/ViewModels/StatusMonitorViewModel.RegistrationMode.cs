using System;
using System.Globalization;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Tray.ViewModels
{
    public sealed partial class StatusMonitorViewModel
    {
        public string RegistrationModeSummaryText =>
            _registrationModeResponse == null
                ? "—"
                : FormatRegistrationModeState(
                    _registrationModeResponse.RegistrationMode.State);

        public string RegistrationModeStateText =>
            _registrationModeResponse == null
                ? "확인할 수 없음"
                : FormatRegistrationModeState(
                    _registrationModeResponse.RegistrationMode.State);

        public string RegistrationModeCountdownText
        {
            get
            {
                int? remaining = _registrationModeResponse
                    ?.RegistrationMode.RemainingSeconds;
                if (!remaining.HasValue)
                {
                    return "--:--:--";
                }

                int hours = remaining.Value / 3600;
                int minutes = (remaining.Value % 3600) / 60;
                int seconds = remaining.Value % 60;
                return hours.ToString("00", CultureInfo.InvariantCulture)
                    + ":"
                    + minutes.ToString("00", CultureInfo.InvariantCulture)
                    + ":"
                    + seconds.ToString("00", CultureInfo.InvariantCulture);
            }
        }

        public string RegistrationModeWindowText
        {
            get
            {
                if (_registrationModeResponse == null)
                {
                    return "메인 서비스에서 상태를 조회하지 못했습니다.";
                }

                AdminRegistrationModeStatus mode =
                    _registrationModeResponse.RegistrationMode;
                if (mode.State == AdminRegistrationModeState.Open)
                {
                    return "시작 "
                        + FormatLocalTimestamp(mode.OpenedUtc.Value)
                        + " · 종료 "
                        + FormatLocalTimestamp(mode.ExpiresUtc.Value);
                }

                return mode.State == AdminRegistrationModeState.Claimed
                    ? "첫 유효 요청이 등록·발급 처리를 진행 중입니다."
                    : "등록 창이 닫혀 있습니다.";
            }
        }

        public string LastRegistrationOutcomeText =>
            FormatLastRegistrationOutcome(
                _registrationModeResponse?.LastRegistration);

        public string LastRegistrationCompletedText
        {
            get
            {
                AdminLastRegistration last =
                    _registrationModeResponse?.LastRegistration;
                return last == null
                    ? "완료 기록 없음"
                    : "완료 " + FormatLocalTimestamp(last.CompletedUtc);
            }
        }

        public string LastRegistrationServiceText
        {
            get
            {
                AdminLastRegistration last =
                    _registrationModeResponse?.LastRegistration;
                if (last == null)
                {
                    return "서비스: —";
                }

                if (last.Outcome == AdminRegistrationOutcome.Failed)
                {
                    return "사유: " + last.FailureReason;
                }

                return "서비스: "
                    + last.ProductCode
                    + " · "
                    + last.ServiceHostName
                    + " · "
                    + last.ServiceIpv4Address;
            }
        }

        public string LastRegistrationCertificateText
        {
            get
            {
                AdminLastRegistration last =
                    _registrationModeResponse?.LastRegistration;
                if (last == null
                    || last.Outcome == AdminRegistrationOutcome.Failed)
                {
                    return "인증서: —";
                }

                return "인증서: "
                    + last.CertificateSerialNumber
                    + " · 만료 "
                    + FormatLocalTimestamp(
                        last.CertificateNotAfterUtc.Value);
            }
        }

        private bool CanOpenRegistrationMode()
        {
            return IsAdminConnected
                && _registrationModeResponse != null
                && _registrationModeResponse.RegistrationMode.State
                    == AdminRegistrationModeState.Closed;
        }

        private bool CanCloseRegistrationMode()
        {
            return IsAdminConnected
                && _registrationModeResponse != null
                && _registrationModeResponse.RegistrationMode.State
                    == AdminRegistrationModeState.Open;
        }

        private static string FormatRegistrationModeState(
            AdminRegistrationModeState state)
        {
            switch (state)
            {
                case AdminRegistrationModeState.Closed:
                    return "CLOSED";
                case AdminRegistrationModeState.Open:
                    return "OPEN";
                case AdminRegistrationModeState.Claimed:
                    return "CLAIMED";
                default:
                    return "UNKNOWN";
            }
        }

        private static string FormatLastRegistrationOutcome(
            AdminLastRegistration last)
        {
            if (last == null)
            {
                return "아직 완료된 등록이 없습니다.";
            }

            switch (last.Outcome)
            {
                case AdminRegistrationOutcome.Registered:
                    return "REGISTERED · 신규 등록 완료";
                case AdminRegistrationOutcome.Reregistered:
                    return "REREGISTERED · 재등록 완료";
                case AdminRegistrationOutcome.Failed:
                    return "FAILED · 등록 처리 실패";
                default:
                    return "알 수 없는 등록 결과";
            }
        }

        private static string FormatLocalTimestamp(DateTime utcValue)
        {
            if (utcValue.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "UI timestamps must use UTC input.",
                    nameof(utcValue));
            }

            return utcValue.ToLocalTime().ToString(
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.CurrentCulture);
        }
    }
}
