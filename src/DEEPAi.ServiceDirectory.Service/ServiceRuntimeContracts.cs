using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;

namespace DEEPAi.ServiceDirectory.Service
{
    public sealed class ServiceDirectoryApplicationContext
    {
        internal ServiceDirectoryApplicationContext(
            StateMutationCoordinator stateCoordinator,
            ServiceDirectoryRuntimeConfigurationState configurationState,
            ServiceDirectoryConfiguration configuration,
            SystemFileLogger systemFileLogger,
            SecurityAuditEventLogger securityAuditLogger,
            ICertificateAuthorityAdministration
                certificateAuthorityAdministration)
        {
            StateCoordinator = stateCoordinator
                ?? throw new ArgumentNullException(
                    nameof(stateCoordinator));
            ConfigurationState = configurationState
                ?? throw new ArgumentNullException(
                    nameof(configurationState));
            InitialConfiguration = configuration
                ?? throw new ArgumentNullException(nameof(configuration));
            SystemFileLogger = systemFileLogger
                ?? throw new ArgumentNullException(
                    nameof(systemFileLogger));
            SecurityAuditLogger = securityAuditLogger
                ?? throw new ArgumentNullException(
                    nameof(securityAuditLogger));
            CertificateAuthorityAdministration =
                certificateAuthorityAdministration
                ?? throw new ArgumentNullException(
                    nameof(certificateAuthorityAdministration));
        }

        public StateMutationCoordinator StateCoordinator { get; }

        public IAdminConfigurationState ConfigurationState
        {
            get;
        }

        internal ServiceDirectoryRuntimeConfigurationState
            RuntimeConfigurationState =>
            (ServiceDirectoryRuntimeConfigurationState)ConfigurationState;

        public ServiceDirectoryConfiguration InitialConfiguration { get; }

        public SystemFileLogger SystemFileLogger { get; }

        internal SecurityAuditEventLogger SecurityAuditLogger { get; }

        public ICertificateAuthorityAdministration
            CertificateAuthorityAdministration { get; }
    }

    // The composition root owns persistence and listener primitives. The
    // application factory receives only verified, already-loaded state and
    // must return the real Admin handler before any listener is opened.
    public interface IServiceDirectoryApplicationFactory
    {
        IAdminHttpRequestHandler CreateAdminHandler(
            ServiceDirectoryApplicationContext context);
    }

    // Implement this additional contract only when the authenticated Peer
    // wire state/session owner is available. If it is absent, the shared host
    // keeps every /api/sync/* route closed with a bodyless 404.
    public interface IServiceDirectoryPeerApplicationFactory
    {
        IPeerHttpRequestHandler CreatePeerHandler(
            ServiceDirectoryApplicationContext context);
    }

    public sealed class ServiceDirectoryApplicationFactory
        : IServiceDirectoryApplicationFactory,
        IServiceDirectoryPeerApplicationFactory
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ServiceDirectoryApplicationContext,
            PeerSynchronizationController> _pendingControllers =
            new Dictionary<ServiceDirectoryApplicationContext,
                PeerSynchronizationController>();

        public ServiceDirectoryApplicationFactory()
        {
        }

        public IAdminHttpRequestHandler CreateAdminHandler(
            ServiceDirectoryApplicationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var controller = new PeerSynchronizationController(
                context.RuntimeConfigurationState,
                context.StateCoordinator,
                context.SystemFileLogger,
                context.SecurityAuditLogger);
            AdminApplicationHttpRequestHandler handler = null;
            try
            {
                handler = new AdminApplicationHttpRequestHandler(
                    context.StateCoordinator,
                    context.ConfigurationState,
                    context.SystemFileLogger,
                    controller,
                    context.CertificateAuthorityAdministration);
                lock (_gate)
                {
                    _pendingControllers.Add(context, controller);
                }

                return handler;
            }
            catch
            {
                if (handler != null)
                {
                    handler.Dispose();
                }

                controller.Dispose();
                throw;
            }
        }

        public IPeerHttpRequestHandler CreatePeerHandler(
            ServiceDirectoryApplicationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            lock (_gate)
            {
                PeerSynchronizationController controller;
                if (!_pendingControllers.TryGetValue(
                        context,
                        out controller))
                {
                    throw new InvalidOperationException(
                        "The Admin handler must be created before the Peer handler for the same runtime context.");
                }

                _pendingControllers.Remove(context);
                return controller;
            }
        }
    }

    public interface IServiceDirectoryRuntimeFactory
    {
        ServiceDirectoryRuntimeHost Create();
    }

    public sealed class ServiceDirectoryRuntimeFactory
        : IServiceDirectoryRuntimeFactory
    {
        private readonly string _dataRootPath;
        private readonly IServiceDirectoryApplicationFactory
            _applicationFactory;
        private readonly IInstalledListenerAddressValidator
            _listenerAddressValidator;

        public ServiceDirectoryRuntimeFactory(
            IServiceDirectoryApplicationFactory applicationFactory)
            : this(
                ServiceDirectoryRuntimeComposition.GetInstalledDataRootPath(),
                applicationFactory,
                new InstalledListenerAddressValidator())
        {
        }

        internal ServiceDirectoryRuntimeFactory(
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

            _dataRootPath = dataRootPath;
            _applicationFactory = applicationFactory
                ?? throw new ArgumentNullException(
                    nameof(applicationFactory));
            _listenerAddressValidator = listenerAddressValidator
                ?? throw new ArgumentNullException(
                    nameof(listenerAddressValidator));
        }

        public ServiceDirectoryRuntimeHost Create()
        {
            return ServiceDirectoryRuntimeComposition.Create(
                _dataRootPath,
                _applicationFactory,
                _listenerAddressValidator);
        }
    }

    internal interface IServiceDirectoryApplicationLifetime : IDisposable
    {
        Task Completion { get; }

        Exception FatalException { get; }

        void Start();

        void BeginStop();

        bool WaitForStop(TimeSpan drainTimeout);

        bool Stop(TimeSpan drainTimeout);
    }

    internal sealed class ApplicationComponentLifetime
        : IServiceDirectoryApplicationLifetime
    {
        private readonly IDisposable _adminLifetime;
        private readonly IDisposable _peerLifetime;
        private readonly PeerSynchronizationController _peerController;
        private readonly IDisposable _certificateAuthorityLifetime;
        private readonly TaskCompletionSource<object> _completion =
            new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        private bool _stopSignaled;
        private bool _stopped;
        private bool _disposed;

        internal ApplicationComponentLifetime(
            IAdminHttpRequestHandler adminHandler,
            IPeerHttpRequestHandler peerHandler,
            IDisposable certificateAuthorityLifetime)
        {
            _adminLifetime = adminHandler as IDisposable;
            _peerLifetime = peerHandler as IDisposable;
            _peerController = peerHandler
                as PeerSynchronizationController;
            _certificateAuthorityLifetime =
                certificateAuthorityLifetime;
            if (ReferenceEquals(_adminLifetime, _peerLifetime))
            {
                _peerLifetime = null;
            }

            if (_peerController != null)
            {
                _peerController.Completion.ContinueWith(
                    CompleteFromPeer,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        public Task Completion => _completion.Task;

        public Exception FatalException => _peerController == null
            ? null
            : _peerController.FatalException;

        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ApplicationComponentLifetime));
            }

            if (_stopSignaled || _stopped)
            {
                throw new InvalidOperationException(
                    "Application components have already been stopped.");
            }

            if (_started)
            {
                return;
            }

            _started = true;
            if (_peerController != null)
            {
                _peerController.Start();
            }
        }

        public void BeginStop()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ApplicationComponentLifetime));
            }

            if (_stopped || _stopSignaled)
            {
                return;
            }

            if (_peerController != null)
            {
                _peerController.BeginStop();
            }

            _stopSignaled = true;
        }

        public bool WaitForStop(TimeSpan drainTimeout)
        {
            if (drainTimeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drainTimeout));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ApplicationComponentLifetime));
            }

            if (_stopped)
            {
                return true;
            }

            BeginStop();
            if (_peerController != null
                && !_peerController.WaitForStop(drainTimeout))
            {
                return false;
            }

            _stopped = true;
            _completion.TrySetResult(null);
            return true;
        }

        public bool Stop(TimeSpan drainTimeout)
        {
            return WaitForStop(drainTimeout);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_started && !_stopped)
            {
                throw new InvalidOperationException(
                    "Application components must drain before disposal.");
            }

            var failures = new List<Exception>(3);
            DisposeOne(_peerLifetime, failures);
            DisposeOne(_adminLifetime, failures);
            DisposeOne(_certificateAuthorityLifetime, failures);
            _disposed = true;
            _completion.TrySetResult(null);
            if (failures.Count != 0)
            {
                throw new AggregateException(
                    "Application component disposal failed.",
                    failures);
            }
        }

        private void CompleteFromPeer(Task peerCompletion)
        {
            if (peerCompletion.IsFaulted)
            {
                AggregateException failure = peerCompletion.Exception;
                if (failure == null)
                {
                    _completion.TrySetException(
                        new InvalidOperationException(
                            "Peer synchronization failed without an exception."));
                }
                else
                {
                    _completion.TrySetException(
                        failure.Flatten().InnerExceptions);
                }
            }
            else if (peerCompletion.IsCanceled)
            {
                _completion.TrySetCanceled();
            }
            else
            {
                _completion.TrySetResult(null);
            }
        }

        private static void DisposeOne(
            IDisposable lifetime,
            ICollection<Exception> failures)
        {
            if (lifetime == null)
            {
                return;
            }

            try
            {
                lifetime.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }
}
