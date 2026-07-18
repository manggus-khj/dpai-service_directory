using System;
using System.Security;
using System.Security.Principal;

namespace DEEPAi.ServiceDirectory.Infrastructure.Security
{
    public enum AdminAuthorizationStatus
    {
        Unavailable = 0,
        Authorized = 1,
        Unauthenticated = 2,
        Forbidden = 3
    }

    public sealed class AdminAuthorizationResult
    {
        private static readonly AdminAuthorizationResult AuthorizedResult =
            new AdminAuthorizationResult(AdminAuthorizationStatus.Authorized);

        private static readonly AdminAuthorizationResult UnauthenticatedResult =
            new AdminAuthorizationResult(AdminAuthorizationStatus.Unauthenticated);

        private static readonly AdminAuthorizationResult ForbiddenResult =
            new AdminAuthorizationResult(AdminAuthorizationStatus.Forbidden);

        private static readonly AdminAuthorizationResult UnavailableResult =
            new AdminAuthorizationResult(AdminAuthorizationStatus.Unavailable);

        private AdminAuthorizationResult(AdminAuthorizationStatus status)
        {
            if (!Enum.IsDefined(typeof(AdminAuthorizationStatus), status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            Status = status;
        }

        public AdminAuthorizationStatus Status { get; }

        public bool IsAuthorized => Status == AdminAuthorizationStatus.Authorized;

        internal static AdminAuthorizationResult Authorized()
        {
            return AuthorizedResult;
        }

        internal static AdminAuthorizationResult Unauthenticated()
        {
            return UnauthenticatedResult;
        }

        internal static AdminAuthorizationResult Forbidden()
        {
            return ForbiddenResult;
        }

        internal static AdminAuthorizationResult Unavailable()
        {
            return UnavailableResult;
        }
    }

    public sealed class AdminRequestAuthorizer
    {
        private const string LocalOperatorsGroupName =
            "DEEPAi-ServiceDirectory-Operators";

        public AdminAuthorizationResult Authorize(IPrincipal requestPrincipal)
        {
            if (requestPrincipal == null)
            {
                return AdminAuthorizationResult.Unauthenticated();
            }

            IIdentity requestIdentity = requestPrincipal.Identity;
            if (requestIdentity == null || !requestIdentity.IsAuthenticated)
            {
                return AdminAuthorizationResult.Unauthenticated();
            }

            var windowsIdentity = requestIdentity as WindowsIdentity;
            if (windowsIdentity == null)
            {
                return AdminAuthorizationResult.Unauthenticated();
            }

            SecurityIdentifier operatorsGroupSid;
            try
            {
                operatorsGroupSid = ResolveLocalOperatorsGroupSid();
            }
            catch (IdentityNotMappedException)
            {
                return AdminAuthorizationResult.Unavailable();
            }
            catch (SystemException exception)
                when (exception.GetType() == typeof(SystemException))
            {
                // NTAccount.Translate reports Win32 lookup failures as an exact
                // SystemException. Do not turn broader runtime failures into an
                // ordinary authorization result.
                return AdminAuthorizationResult.Unavailable();
            }

            try
            {
                var windowsPrincipal = new WindowsPrincipal(windowsIdentity);
                return windowsPrincipal.IsInRole(operatorsGroupSid)
                    ? AdminAuthorizationResult.Authorized()
                    : AdminAuthorizationResult.Forbidden();
            }
            catch (SecurityException)
            {
                // WindowsPrincipal reports token inspection failures as a security
                // failure. The caller receives no identity or exception details.
                return AdminAuthorizationResult.Unavailable();
            }
        }

        private static SecurityIdentifier ResolveLocalOperatorsGroupSid()
        {
            var localGroupAccount = new NTAccount(
                Environment.MachineName,
                LocalOperatorsGroupName);

            return (SecurityIdentifier)localGroupAccount.Translate(
                typeof(SecurityIdentifier));
        }
    }
}
