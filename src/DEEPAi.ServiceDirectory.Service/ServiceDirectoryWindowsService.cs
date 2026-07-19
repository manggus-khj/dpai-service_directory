using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace DEEPAi.ServiceDirectory.Service
{
    public sealed class ServiceDirectoryWindowsService : ServiceBase
    {
        public const string MainServiceName =
            "DEEPAi.ServiceDirectory";
        public const string MainServiceDisplayName =
            "DEEPAi Service Directory";

        private const int ErrorExceptionInService = 1064;
        private const int ErrorServiceRequestTimeout = 1053;
        private const int StopAdditionalTimeMilliseconds = 40000;
        private static readonly TimeSpan StopDrainTimeout =
            TimeSpan.FromSeconds(40);
        private static readonly TimeSpan RuntimeCompletionRetryDelay =
            TimeSpan.FromMilliseconds(100);

        private readonly object _gate = new object();
        private readonly IServiceDirectoryRuntimeFactory _runtimeFactory;
        private ServiceDirectoryRuntimeHost _runtime;
        private string _stopReason = "SCM_STOP";
        private bool _serviceStartInProgress;
        private bool _serviceStopRequested;
        private bool _runtimeStopInProgress;
        private bool _runtimeCompletionHandling;
        private Task _pendingRuntimeCompletion;

        public ServiceDirectoryWindowsService(
            IServiceDirectoryRuntimeFactory runtimeFactory)
        {
            _runtimeFactory = runtimeFactory
                ?? throw new ArgumentNullException(nameof(runtimeFactory));
            ServiceName = MainServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            ServiceIdentityVerifier.EnsureExpectedVirtualServiceAccount(
                MainServiceName);
            ServiceDirectoryRuntimeHost runtime =
                _runtimeFactory.Create();
            if (runtime == null)
            {
                throw new InvalidOperationException(
                    "The runtime factory did not create a service runtime.");
            }

            lock (_gate)
            {
                _serviceStartInProgress = true;
                _serviceStopRequested = false;
                _stopReason = "SCM_STOP";
            }

            try
            {
                runtime.Start();
                lock (_gate)
                {
                    if (_runtime != null)
                    {
                        throw new InvalidOperationException(
                            "The main service runtime is already active.");
                    }

                    _runtime = runtime;
                }

                runtime.Completion.ContinueWith(
                    completed => QueueRuntimeCompletion(
                        runtime,
                        completed),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            catch (Exception startupFailure)
            {
                Exception cleanupFailure = null;
                try
                {
                    runtime.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                if (cleanupFailure != null)
                {
                    throw new AggregateException(
                        "Service startup and runtime cleanup both failed.",
                        startupFailure,
                        cleanupFailure);
                }

                throw;
            }
            finally
            {
                ReleaseServiceStart(runtime);
            }
        }

        protected override void OnStop()
        {
            RequestAdditionalTime(StopAdditionalTimeMilliseconds);
            StopRuntime(null);
        }

        protected override void OnShutdown()
        {
            StopRuntime("SYSTEM_SHUTDOWN");
            base.OnShutdown();
        }

        private void StopRuntime(string explicitReason)
        {
            ServiceDirectoryRuntimeHost runtime;
            string reason;
            lock (_gate)
            {
                runtime = _runtime;
                _serviceStopRequested = true;
                if (runtime != null)
                {
                    _runtimeStopInProgress = true;
                }

                if (explicitReason != null)
                {
                    _stopReason = explicitReason;
                }

                reason = _stopReason;
            }

            if (runtime == null)
            {
                return;
            }

            bool stopped;
            try
            {
                stopped = runtime.Stop(reason, StopDrainTimeout);
            }
            catch (Exception stopFailure)
            {
                ExitCode = ErrorExceptionInService;
                bool disposed;
                Exception cleanupFailure = TryDisposeStoppedRuntime(
                    runtime,
                    out disposed);
                if (disposed)
                {
                    ClearRuntimeReference(runtime);
                }
                else
                {
                    ReleaseRuntimeStopAfterFailure(runtime);
                }

                if (cleanupFailure != null)
                {
                    throw new AggregateException(
                        "Service stop and runtime cleanup both failed.",
                        stopFailure,
                        cleanupFailure);
                }

                throw;
            }

            if (!stopped)
            {
                ExitCode = ErrorServiceRequestTimeout;
                ReleaseRuntimeStopAfterFailure(runtime);
                throw new System.TimeoutException(
                    "The service runtime did not drain within the SCM stop timeout.");
            }

            try
            {
                runtime.Dispose();
                ClearRuntimeReference(runtime);
            }
            catch
            {
                ExitCode = ErrorExceptionInService;
                ReleaseRuntimeStopAfterFailure(runtime);
                throw;
            }
        }

        private void OnRuntimeCompleted(
            ServiceDirectoryRuntimeHost runtime,
            Task completion)
        {
            bool serviceStopRequested;
            lock (_gate)
            {
                if (!ReferenceEquals(_runtime, runtime))
                {
                    return;
                }

                if (_serviceStartInProgress
                    || _runtimeStopInProgress
                    || _runtimeCompletionHandling)
                {
                    _pendingRuntimeCompletion = completion;
                    return;
                }

                _runtimeCompletionHandling = true;
                _pendingRuntimeCompletion = null;
                serviceStopRequested = _serviceStopRequested;
                if (!serviceStopRequested)
                {
                    _stopReason = "RUNTIME_FAILURE";
                }
            }

            try
            {
                if (completion.IsFaulted)
                {
                    // Reading Exception observes the component fault before
                    // requesting a controlled SCM stop.
                    AggregateException observed = completion.Exception;
                    if (observed == null && !serviceStopRequested)
                    {
                        ExitCode = ErrorExceptionInService;
                    }
                }

                if (serviceStopRequested)
                {
                    try
                    {
                        // The SCM or shutdown path already requested this
                        // stop. Retry only the retained runtime drain with the
                        // original reason and timeout diagnosis intact.
                        StopRuntime(null);
                    }
                    catch (Exception)
                    {
                        // StopRuntime owns ExitCode and retained-runtime retry
                        // state for timeout and cleanup failures.
                    }

                    return;
                }

                ExitCode = ErrorExceptionInService;
                try
                {
                    Stop();
                }
                catch (Exception)
                {
                    // The SCM stop path already records a non-zero exit code.
                    // A retained runtime is re-queued below after the active
                    // stop attempt releases its lifecycle guard.
                    lock (_gate)
                    {
                        if (ReferenceEquals(_runtime, runtime))
                        {
                            _pendingRuntimeCompletion = completion;
                        }
                    }
                }
            }
            finally
            {
                Task pendingCompletion = null;
                lock (_gate)
                {
                    if (ReferenceEquals(_runtime, runtime))
                    {
                        _runtimeCompletionHandling = false;
                        if (!_runtimeStopInProgress
                            && _pendingRuntimeCompletion != null)
                        {
                            pendingCompletion =
                                _pendingRuntimeCompletion;
                            _pendingRuntimeCompletion = null;
                        }
                    }
                    else
                    {
                        _runtimeCompletionHandling = false;
                        _pendingRuntimeCompletion = null;
                    }
                }

                if (pendingCompletion != null)
                {
                    QueueRuntimeCompletion(
                        runtime,
                        pendingCompletion);
                }
            }
        }

        private static Exception TryDisposeStoppedRuntime(
            ServiceDirectoryRuntimeHost runtime,
            out bool disposed)
        {
            disposed = false;
            try
            {
                runtime.Dispose();
                disposed = true;
                return null;
            }
            catch (InvalidOperationException)
            {
                // A listener that did not drain must remain undisposed until
                // process termination closes its OS handles.
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private void ClearRuntimeReference(
            ServiceDirectoryRuntimeHost runtime)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _runtime = null;
                    _serviceStartInProgress = false;
                    _serviceStopRequested = false;
                    _runtimeStopInProgress = false;
                    _runtimeCompletionHandling = false;
                    _pendingRuntimeCompletion = null;
                }
            }
        }

        private void ReleaseRuntimeStopAfterFailure(
            ServiceDirectoryRuntimeHost runtime)
        {
            Task completionToQueue = null;
            lock (_gate)
            {
                if (!ReferenceEquals(_runtime, runtime))
                {
                    return;
                }

                _runtimeStopInProgress = false;
                if (_pendingRuntimeCompletion == null
                    && runtime.Completion.IsCompleted)
                {
                    _pendingRuntimeCompletion = runtime.Completion;
                }

                if (!_runtimeCompletionHandling
                    && _pendingRuntimeCompletion != null)
                {
                    completionToQueue = _pendingRuntimeCompletion;
                    _pendingRuntimeCompletion = null;
                }
            }

            if (completionToQueue != null)
            {
                QueueRuntimeCompletion(runtime, completionToQueue);
            }
        }

        private void ReleaseServiceStart(
            ServiceDirectoryRuntimeHost runtime)
        {
            bool stopRequested = false;
            Task completionToQueue = null;
            lock (_gate)
            {
                _serviceStartInProgress = false;
                if (ReferenceEquals(_runtime, runtime))
                {
                    if (_serviceStopRequested
                        && !_runtimeStopInProgress)
                    {
                        stopRequested = true;
                    }
                    else if (!_runtimeStopInProgress
                        && !_runtimeCompletionHandling
                        && _pendingRuntimeCompletion != null)
                    {
                        completionToQueue = _pendingRuntimeCompletion;
                        _pendingRuntimeCompletion = null;
                    }
                }
            }

            if (stopRequested)
            {
                StopRuntime(null);
                return;
            }

            if (completionToQueue != null)
            {
                QueueRuntimeCompletion(runtime, completionToQueue);
            }
        }

        private void QueueRuntimeCompletion(
            ServiceDirectoryRuntimeHost runtime,
            Task completion)
        {
            try
            {
                Task.Delay(RuntimeCompletionRetryDelay).ContinueWith(
                    completed => OnRuntimeCompleted(runtime, completion),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_runtime, runtime))
                    {
                        _pendingRuntimeCompletion = completion;
                    }
                }

                ExitCode = ErrorExceptionInService;
            }
        }
    }
}
