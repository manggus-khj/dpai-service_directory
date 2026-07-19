using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;

namespace DEEPAi.ServiceDirectory.Service
{
    public sealed class ServiceDirectoryRuntimeHost : IDisposable
    {
        private readonly object _gate = new object();
        private readonly ServiceDirectoryHttpListenerHost _listenerHost;
        private readonly ServiceDirectoryRuntimeConfigurationState
            _configurationState;
        private readonly IServiceDirectoryApplicationLifetime
            _applicationLifetime;
        private readonly SystemFileLogger _systemFileLogger;
        private readonly Guid _instanceId;
        private readonly int _initialLogRetentionDays;
        private readonly TaskCompletionSource<object> _completion =
            new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _started;
        private bool _listenerStarted;
        private bool _listenerStopped;
        private bool _applicationStopped;
        private bool _stopLogged;
        private bool _disposed;

        internal ServiceDirectoryRuntimeHost(
            ServiceDirectoryHttpListenerHost listenerHost,
            ServiceDirectoryRuntimeConfigurationState configurationState,
            IServiceDirectoryApplicationLifetime applicationLifetime,
            SystemFileLogger systemFileLogger,
            Guid instanceId,
            int logRetentionDays)
        {
            _listenerHost = listenerHost
                ?? throw new ArgumentNullException(nameof(listenerHost));
            _configurationState = configurationState
                ?? throw new ArgumentNullException(
                    nameof(configurationState));
            _applicationLifetime = applicationLifetime
                ?? throw new ArgumentNullException(
                    nameof(applicationLifetime));
            _systemFileLogger = systemFileLogger
                ?? throw new ArgumentNullException(
                    nameof(systemFileLogger));
            if (instanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The service instance ID must not be empty.",
                    nameof(instanceId));
            }

            if (logRetentionDays < SystemFileLogger.MinimumRetentionDays
                || logRetentionDays
                    > SystemFileLogger.MaximumRetentionDays)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logRetentionDays));
            }

            _instanceId = instanceId;
            _initialLogRetentionDays = logRetentionDays;
            ObserveComponentCompletion(_listenerHost.Completion);
            ObserveComponentCompletion(_applicationLifetime.Completion);
        }

        public Task Completion => _completion.Task;

        public Exception FatalException =>
            _listenerHost.FatalException
            ?? _applicationLifetime.FatalException;

        public void Start()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_started || _listenerStopped)
                {
                    throw new InvalidOperationException(
                        "The service runtime can be started only once.");
                }

                _systemFileLogger.ApplyRetention(
                    _initialLogRetentionDays);
                _listenerHost.Start();
                _listenerStarted = true;
                bool startEventWritten = false;
                try
                {
                    try
                    {
                        _systemFileLogger.WriteServiceStarted(
                            _instanceId,
                            _initialLogRetentionDays);
                        startEventWritten = true;
                    }
                    catch (SystemLogRetentionAfterWriteException)
                    {
                        startEventWritten = true;
                    }

                    _applicationLifetime.Start();
                    _started = true;
                }
                catch (Exception startupFailure)
                {
                    Exception cleanupFailure =
                        StopAfterFailedStart(startEventWritten);
                    if (cleanupFailure != null)
                    {
                        throw new AggregateException(
                            "Service startup and listener cleanup both failed.",
                            startupFailure,
                            cleanupFailure);
                    }

                    throw;
                }
            }
        }

        public bool Stop(string reason, TimeSpan drainTimeout)
        {
            if (drainTimeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drainTimeout));
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_started)
                {
                    return _listenerStopped;
                }

                var stopwatch = Stopwatch.StartNew();
                if (!_listenerStopped)
                {
                    _listenerHost.BeginStop();
                }

                if (!_applicationStopped)
                {
                    _applicationLifetime.BeginStop();
                }

                if (!_listenerStopped)
                {
                    _listenerStopped = _listenerHost.WaitForStop(
                        Remaining(drainTimeout, stopwatch.Elapsed));
                    if (!_listenerStopped)
                    {
                        return false;
                    }
                }

                if (!_applicationStopped)
                {
                    TimeSpan remaining = Remaining(
                        drainTimeout,
                        stopwatch.Elapsed);
                    _applicationStopped = _applicationLifetime
                        .WaitForStop(remaining);
                    if (!_applicationStopped)
                    {
                        return false;
                    }
                }

                if (!_stopLogged)
                {
                    int currentRetentionDays = _configurationState
                        .GetLastKnownLogRetentionDays();
                    WriteServiceStopped(
                        reason,
                        currentRetentionDays);
                }

                _started = false;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if ((_listenerStarted && !_listenerStopped)
                    || (_started && !_applicationStopped))
                {
                    throw new InvalidOperationException(
                        "The service runtime must be stopped before disposal.");
                }

                Exception listenerFailure = null;
                Exception applicationFailure = null;
                Exception configurationFailure = null;
                try
                {
                    _listenerHost.Dispose();
                }
                catch (Exception exception)
                {
                    listenerFailure = exception;
                }

                if (_applicationLifetime != null)
                {
                    try
                    {
                        _applicationLifetime.Dispose();
                    }
                    catch (Exception exception)
                    {
                        applicationFailure = exception;
                    }
                }

                try
                {
                    _configurationState.Dispose();
                }
                catch (Exception exception)
                {
                    configurationFailure = exception;
                }

                _disposed = true;
                if (listenerFailure != null
                    || applicationFailure != null
                    || configurationFailure != null)
                {
                    var failures = new System.Collections.Generic.List<Exception>();
                    if (listenerFailure != null)
                    {
                        failures.Add(listenerFailure);
                    }

                    if (applicationFailure != null)
                    {
                        failures.Add(applicationFailure);
                    }

                    if (configurationFailure != null)
                    {
                        failures.Add(configurationFailure);
                    }

                    throw new AggregateException(
                        "Service runtime disposal failed.",
                        failures);
                }
            }

            GC.SuppressFinalize(this);
        }

        private Exception StopAfterFailedStart(bool startEventWritten)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                TimeSpan cleanupTimeout = TimeSpan.FromSeconds(5);
                _listenerHost.BeginStop();
                _applicationLifetime.BeginStop();
                bool stopped = _listenerHost.WaitForStop(
                    Remaining(cleanupTimeout, stopwatch.Elapsed));
                _listenerStopped = stopped;
                if (!stopped)
                {
                    return new TimeoutException(
                        "The listener did not drain after startup failed.");
                }

                _applicationStopped = _applicationLifetime.WaitForStop(
                    Remaining(
                        cleanupTimeout,
                        stopwatch.Elapsed));
                if (!_applicationStopped)
                {
                    return new TimeoutException(
                        "Peer background work did not drain after startup failed.");
                }

                if (startEventWritten)
                {
                    WriteServiceStopped(
                        "START_FAILURE",
                        _initialLogRetentionDays);
                }

                return null;
            }
            catch (Exception cleanupFailure)
            {
                return cleanupFailure;
            }
        }

        private static TimeSpan Remaining(
            TimeSpan timeout,
            TimeSpan elapsed)
        {
            return elapsed >= timeout
                ? TimeSpan.Zero
                : timeout - elapsed;
        }

        private void WriteServiceStopped(
            string reason,
            int retentionDays)
        {
            try
            {
                _systemFileLogger.WriteServiceStopped(
                    reason,
                    retentionDays);
                _stopLogged = true;
            }
            catch (SystemLogRetentionAfterWriteException)
            {
                // The stop record is already durable. Retention cleanup can
                // be retried on the next startup without turning a completed
                // service stop into an SCM failure or writing a duplicate.
                _stopLogged = true;
            }
        }

        private void ObserveComponentCompletion(Task componentCompletion)
        {
            componentCompletion.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                    {
                        AggregateException failure = completed.Exception;
                        if (failure == null)
                        {
                            _completion.TrySetException(
                                new InvalidOperationException(
                                    "A runtime component failed without an exception."));
                        }
                        else
                        {
                            _completion.TrySetException(
                                failure.Flatten().InnerExceptions);
                        }
                    }
                    else if (completed.IsCanceled)
                    {
                        _completion.TrySetCanceled();
                    }
                    else
                    {
                        _completion.TrySetResult(null);
                    }
                },
                TaskScheduler.Default);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ServiceDirectoryRuntimeHost));
            }
        }
    }
}
