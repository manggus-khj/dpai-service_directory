using System;
using System.IO;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;

namespace DEEPAi.ServiceDirectory.Service
{
    internal static class ServiceDirectoryRuntimeComposition
    {
        internal static ServiceDirectoryRuntimeHost Create(
            string dataRootPath,
            IServiceDirectoryApplicationFactory applicationFactory,
            IInstalledListenerAddressValidator listenerAddressValidator)
        {
            if (string.IsNullOrWhiteSpace(dataRootPath))
            {
                throw new ArgumentException(
                    "The service data root path is required.",
                    nameof(dataRootPath));
            }

            if (applicationFactory == null)
            {
                throw new ArgumentNullException(nameof(applicationFactory));
            }

            if (listenerAddressValidator == null)
            {
                throw new ArgumentNullException(
                    nameof(listenerAddressValidator));
            }

            string fullDataRootPath = Path.GetFullPath(dataRootPath);
            ServiceDirectoryRuntimeConfigurationState configurationState =
                null;
            ServiceDirectoryHttpListenerHost listenerHost = null;
            IDisposable applicationLifetime = null;
            IServiceDirectoryApplicationLifetime runtimeApplicationLifetime =
                null;
            CertificateAuthorityRuntimeAdministration
                certificateAuthorityAdministration = null;
            try
            {
                var mutationGate = new StateMutationGate();
                // This owner recovers the one shared fixed-target journal and
                // atomically validates config.xml, peer.dat, directory.xml,
                // and pending.xml before publishing configuration.
                configurationState =
                    new ServiceDirectoryRuntimeConfigurationState(
                        fullDataRootPath,
                        mutationGate);
                ServiceDirectoryConfiguration configuration =
                    configurationState.GetCurrent();
                var stateStore = new XmlServiceDirectoryStateStore(
                    fullDataRootPath);
                StateMutationCoordinator stateCoordinator =
                    OpenStateCoordinator(stateStore, mutationGate);
                certificateAuthorityAdministration =
                    new CertificateAuthorityRuntimeAdministration(
                        fullDataRootPath,
                        mutationGate,
                        configuration.InstanceId);

                ServiceDirectoryListenerAddress listenerAddress;
                if (!ServiceDirectoryListenerAddress.TryCreate(
                        configuration.ListenAddress,
                        out listenerAddress)
                    || !StringComparer.Ordinal.Equals(
                        configuration.ListenAddress,
                        listenerAddress.CanonicalAddress))
                {
                    throw new InvalidDataException(
                        "config.xml contains a non-canonical ListenAddress.");
                }

                listenerAddressValidator.Validate(listenerAddress);

                // Construction performs the required read-only exact
                // registry-key check. No listener is started before this
                // succeeds.
                var securityAuditLogger = new SecurityAuditEventLogger(
                    configuration.InstanceId);
                var systemFileLogger = new SystemFileLogger(
                    fullDataRootPath);
                var applicationContext =
                    new ServiceDirectoryApplicationContext(
                        stateCoordinator,
                        configurationState,
                        configuration,
                        systemFileLogger,
                        securityAuditLogger,
                        certificateAuthorityAdministration);
                IAdminHttpRequestHandler adminHandler =
                    applicationFactory.CreateAdminHandler(
                        applicationContext);
                if (adminHandler == null)
                {
                    throw new InvalidOperationException(
                        "The application factory did not provide an Admin request handler.");
                }

                applicationLifetime = adminHandler as IDisposable;
                IPeerHttpRequestHandler peerHandler = null;
                var peerFactory = applicationFactory
                    as IServiceDirectoryPeerApplicationFactory;
                if (peerFactory != null)
                {
                    peerHandler = peerFactory.CreatePeerHandler(
                        applicationContext);
                    if (peerHandler == null)
                    {
                        throw new InvalidOperationException(
                            "The Peer application factory did not provide a request handler.");
                    }
                }

                runtimeApplicationLifetime =
                    new ApplicationComponentLifetime(
                        adminHandler,
                        peerHandler,
                        certificateAuthorityAdministration);
                applicationLifetime = runtimeApplicationLifetime;
                certificateAuthorityAdministration = null;

                var sharedExternalConcurrencyLimiter =
                    new ExternalRequestConcurrencyLimiter();
                var externalAdapter = new ExternalHttpAdapter(
                    stateCoordinator,
                    listenerAddress,
                    sharedExternalConcurrencyLimiter,
                    securityAuditLogger);
                var adminAdapter = new AdminHttpAdapter(
                    adminHandler,
                    securityAuditLogger);
                var watchdogAdapter = new WatchdogHealthHttpAdapter(
                    sharedExternalConcurrencyLimiter,
                    securityAuditLogger);
                PeerHttpAdapter peerAdapter = peerHandler == null
                    ? null
                    : new PeerHttpAdapter(
                        peerHandler,
                        listenerAddress,
                        securityAuditLogger);
                listenerHost = new ServiceDirectoryHttpListenerHost(
                    listenerAddress,
                    externalAdapter,
                    adminAdapter,
                    watchdogAdapter,
                    peerAdapter);

                var runtime = new ServiceDirectoryRuntimeHost(
                    listenerHost,
                    configurationState,
                    runtimeApplicationLifetime,
                    systemFileLogger,
                    configuration.InstanceId,
                    configuration.LogRetentionDays);
                listenerHost = null;
                configurationState = null;
                applicationLifetime = null;
                runtimeApplicationLifetime = null;
                return runtime;
            }
            catch (Exception creationFailure)
            {
                Exception listenerCleanupFailure = null;
                Exception applicationCleanupFailure = null;
                Exception configurationCleanupFailure = null;
                Exception certificateAuthorityCleanupFailure = null;
                if (listenerHost != null)
                {
                    try
                    {
                        listenerHost.Dispose();
                    }
                    catch (Exception exception)
                    {
                        listenerCleanupFailure = exception;
                    }
                }

                if (applicationLifetime != null)
                {
                    try
                    {
                        applicationLifetime.Dispose();
                    }
                    catch (Exception exception)
                    {
                        applicationCleanupFailure = exception;
                    }
                }

                if (configurationState != null)
                {
                    try
                    {
                        configurationState.Dispose();
                    }
                    catch (Exception exception)
                    {
                        configurationCleanupFailure = exception;
                    }
                }

                if (certificateAuthorityAdministration != null)
                {
                    try
                    {
                        certificateAuthorityAdministration.Dispose();
                    }
                    catch (Exception exception)
                    {
                        certificateAuthorityCleanupFailure = exception;
                    }
                }

                if (listenerCleanupFailure != null
                    || applicationCleanupFailure != null
                    || configurationCleanupFailure != null
                    || certificateAuthorityCleanupFailure != null)
                {
                    var failures = new System.Collections.Generic.List<Exception>
                    {
                        creationFailure
                    };
                    if (listenerCleanupFailure != null)
                    {
                        failures.Add(listenerCleanupFailure);
                    }

                    if (applicationCleanupFailure != null)
                    {
                        failures.Add(applicationCleanupFailure);
                    }

                    if (configurationCleanupFailure != null)
                    {
                        failures.Add(configurationCleanupFailure);
                    }

                    if (certificateAuthorityCleanupFailure != null)
                    {
                        failures.Add(certificateAuthorityCleanupFailure);
                    }

                    throw new AggregateException(
                        "Runtime composition and cleanup failed.",
                        failures);
                }

                throw;
            }
        }

        internal static string GetInstalledDataRootPath()
        {
            string commonApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonApplicationData))
            {
                throw new InvalidOperationException(
                    "The common application data path is unavailable.");
            }

            return Path.Combine(
                commonApplicationData,
                "DEEPAi",
                "ServiceDirectory");
        }

        private static StateMutationCoordinator OpenStateCoordinator(
            XmlServiceDirectoryStateStore stateStore,
            StateMutationGate mutationGate)
        {
            StateCoordinatorOpenResult stateOpen =
                StateMutationCoordinator.Open(stateStore, mutationGate);
            if (stateOpen == null)
            {
                throw new InvalidOperationException(
                    "The state coordinator returned no open result.");
            }

            if (!stateOpen.IsSuccess)
            {
                throw new InvalidDataException(
                    "Service directory state load failed: "
                    + stateOpen.FailureCode.ToString());
            }

            return stateOpen.Coordinator;
        }
    }
}
