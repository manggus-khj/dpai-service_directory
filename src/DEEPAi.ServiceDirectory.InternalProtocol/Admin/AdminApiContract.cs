using System;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static class AdminApiContract
    {
        public const string XmlNamespace = "urn:deepai:service-directory:admin";
        public const int MaximumBodyBytes = 16 * 1024;
        public const int MaximumXmlDepth = 16;
        public const int PageSize = 250;
        public const int MinimumLogRetentionDays = 1;
        public const int MaximumLogRetentionDays = 1095;
        public const int RegistrationModeDurationSeconds = 60 * 60;
        public const int MaximumFailureReasonCharacters = 512;
        public const string RegistrationModePath =
            "/admin/registration-mode";
        public const string OpenRegistrationModePath =
            "/admin/registration-mode/open";
        public const string CloseRegistrationModePath =
            "/admin/registration-mode/close";

        public static readonly Uri BaseAddress =
            new Uri("http://127.0.0.1:21000/", UriKind.Absolute);
    }

    public sealed class AdminProtocolException : Exception
    {
        public AdminProtocolException(string message)
            : base(message)
        {
        }

        public AdminProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
