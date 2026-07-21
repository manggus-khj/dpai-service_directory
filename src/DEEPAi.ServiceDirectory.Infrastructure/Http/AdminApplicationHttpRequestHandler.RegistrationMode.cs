using System;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed partial class AdminApplicationHttpRequestHandler
    {
        public AdminHandlerResult<AdminServerRegistrationModeResponse>
            GetRegistrationMode()
        {
            ThrowIfDisposed();
            return RegistrationModeSuccess(
                _registrationModeOwner.GetSnapshot());
        }

        public AdminHandlerResult<AdminServerRegistrationModeResponse>
            OpenRegistrationMode()
        {
            ThrowIfDisposed();
            CertificateAuthorityStatus caStatus =
                _certificateAuthorityAdministration.GetStatus();
            if (caStatus == null)
            {
                return Failure<AdminServerRegistrationModeResponse>(
                    AdminServerErrorCode.Internal);
            }

            if (caStatus.State != CertificateAuthorityOperationalState.Ready
                || caStatus.Role !=
                    CertificateAuthorityIssuerRole.ActiveIssuer)
            {
                return Failure<AdminServerRegistrationModeResponse>(
                    AdminServerErrorCode.Conflict);
            }

            RegistrationModeSnapshot snapshot =
                _registrationModeOwner.Open();
            return snapshot.State == RegistrationModeState.Claimed
                ? Failure<AdminServerRegistrationModeResponse>(
                    AdminServerErrorCode.Conflict)
                : RegistrationModeSuccess(snapshot);
        }

        public AdminHandlerResult<AdminServerRegistrationModeResponse>
            CloseRegistrationMode()
        {
            ThrowIfDisposed();
            RegistrationModeSnapshot snapshot =
                _registrationModeOwner.Close();
            return snapshot.State == RegistrationModeState.Claimed
                ? Failure<AdminServerRegistrationModeResponse>(
                    AdminServerErrorCode.Conflict)
                : RegistrationModeSuccess(snapshot);
        }

        private static AdminHandlerResult<
            AdminServerRegistrationModeResponse> RegistrationModeSuccess(
                RegistrationModeSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var status = new AdminRegistrationModeStatus(
                ToAdminState(snapshot.State),
                snapshot.OpenedUtc,
                snapshot.ExpiresUtc,
                snapshot.RemainingSeconds);
            return AdminHandlerResult<AdminServerRegistrationModeResponse>
                .Success(
                    new AdminServerRegistrationModeResponse(
                        status,
                        ToAdminLastRegistration(snapshot.LastResult)));
        }

        private static AdminLastRegistration ToAdminLastRegistration(
            RegistrationModeLastResult result)
        {
            if (result == null)
            {
                return null;
            }

            if (result.Outcome ==
                RegistrationModeCompletionOutcome.Failed)
            {
                return AdminLastRegistration.CreateFailure(
                    result.CompletedUtc,
                    result.FailureReason);
            }

            return AdminLastRegistration.CreateSuccess(
                result.CompletedUtc,
                result.Outcome ==
                    RegistrationModeCompletionOutcome.Registered
                    ? AdminRegistrationOutcome.Registered
                    : AdminRegistrationOutcome.Reregistered,
                result.ProductCode,
                result.ServiceHostName,
                result.ServiceIpv4Address,
                result.CertificateSerialNumber,
                result.CertificateNotAfterUtc.Value);
        }

        private static AdminRegistrationModeState ToAdminState(
            RegistrationModeState state)
        {
            switch (state)
            {
                case RegistrationModeState.Closed:
                    return AdminRegistrationModeState.Closed;
                case RegistrationModeState.Open:
                    return AdminRegistrationModeState.Open;
                case RegistrationModeState.Claimed:
                    return AdminRegistrationModeState.Claimed;
                default:
                    throw new InvalidOperationException(
                        "The registration mode owner returned an unknown state.");
            }
        }
    }
}
