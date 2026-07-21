using System;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal interface IExternalRegistrationLogSink
    {
        void WriteCreated(ProductCode productCode);

        void WriteUpdated(ProductCode productCode);
    }

    internal sealed class SystemExternalRegistrationLogSink
        : IExternalRegistrationLogSink
    {
        private readonly SystemFileLogger _logger;
        private readonly IAdminConfigurationState _configurationState;

        internal SystemExternalRegistrationLogSink(
            SystemFileLogger logger,
            IAdminConfigurationState configurationState)
        {
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
            _configurationState = configurationState
                ?? throw new ArgumentNullException(
                    nameof(configurationState));
        }

        public void WriteCreated(ProductCode productCode)
        {
            Write(productCode, false);
        }

        public void WriteUpdated(ProductCode productCode)
        {
            Write(productCode, true);
        }

        private void Write(ProductCode productCode, bool updated)
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            if (configuration == null)
            {
                throw new InvalidOperationException(
                    "The runtime configuration owner returned no current value for registration logging.");
            }

            try
            {
                if (updated)
                {
                    _logger.WriteRegisteredServiceUpdated(
                        productCode,
                        configuration.LogRetentionDays);
                }
                else
                {
                    _logger.WriteRegisteredServiceCreated(
                        productCode,
                        configuration.LogRetentionDays);
                }
            }
            catch (SystemLogRetentionAfterWriteException)
            {
                // The required event is already durable. Retention cleanup is
                // retried by the next log write or settings update.
            }
        }
    }

    internal sealed class LoggingExternalCertificateService
        : IExternalCertificateService
    {
        private readonly IExternalCertificateService _inner;
        private readonly IExternalRegistrationLogSink _log;

        internal LoggingExternalCertificateService(
            IExternalCertificateService inner,
            IExternalRegistrationLogSink log)
        {
            _inner = inner
                ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public ExternalTrustInfo GetTrustInfo()
        {
            return _inner.GetTrustInfo();
        }

        public byte[] GetCertificateRevocationList()
        {
            return _inner.GetCertificateRevocationList();
        }

        public ExternalRegistrationServiceResult Register(
            ExternalRegistrationRequest request,
            DateTime utcNow)
        {
            ExternalRegistrationServiceResult result =
                _inner.Register(request, utcNow);
            if (result == null)
            {
                throw new InvalidOperationException(
                    "The certificate service returned no registration result.");
            }

            if (result.Status != ExternalRegistrationServiceStatus.Registered
                && result.Status !=
                    ExternalRegistrationServiceStatus.Reregistered)
            {
                return result;
            }

            ProductCode productCode;
            if (result.Service == null
                || !ProductCode.TryCreate(
                    result.Service.ProductCode,
                    out productCode))
            {
                throw new InvalidOperationException(
                    "A committed registration result has no canonical product code.");
            }

            if (result.Status ==
                ExternalRegistrationServiceStatus.Reregistered)
            {
                _log.WriteUpdated(productCode);
            }
            else
            {
                _log.WriteCreated(productCode);
            }

            return result;
        }

        public ExternalRegistrationServiceResult Renew(
            ExternalCertificateRenewalRequest request,
            DateTime utcNow)
        {
            return _inner.Renew(request, utcNow);
        }
    }
}
