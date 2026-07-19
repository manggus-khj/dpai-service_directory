using System;
using System.Security;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    public sealed class WatchdogWindowsService : ServiceBase
    {
        public const string WatchdogServiceName =
            "DEEPAi.ServiceDirectory.Watchdog";
        public const string MainServiceName =
            "DEEPAi.ServiceDirectory";

        private const int ErrorExceptionInService = 1064;
        private const int ErrorServiceRequestTimeout = 1053;
        private static readonly TimeSpan StopDrainTimeout =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RuntimeCompletionRetryDelay =
            TimeSpan.FromMilliseconds(100);

        private readonly object _gate = new object();
        private readonly Func<IWatchdogRuntime> _runtimeFactory;
        private readonly Action _identityVerifier;
        private readonly Action _requestServiceStop;
        private readonly TimeSpan _stopDrainTimeout;
        private IWatchdogRuntime _runtime;
        private bool _serviceStartInProgress;
        private bool _serviceStopRequested;
        private bool _runtimeStopInProgress;
        private bool _runtimeCompletionHandling;
        private Task _pendingRuntimeCompletion;

        public WatchdogWindowsService()
            : this(
                () => WatchdogRuntimeHost.CreateDefault(
                    MainServiceName,
                    WatchdogServiceName),
                EnsureExpectedVirtualServiceAccount,
                null,
                StopDrainTimeout)
        {
        }

        internal WatchdogWindowsService(
            Func<IWatchdogRuntime> runtimeFactory,
            Action identityVerifier,
            Action requestServiceStop,
            TimeSpan stopDrainTimeout)
        {
            _runtimeFactory = runtimeFactory
                ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _identityVerifier = identityVerifier
                ?? throw new ArgumentNullException(nameof(identityVerifier));
            if (stopDrainTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopDrainTimeout));
            }

            _requestServiceStop = requestServiceStop;
            _stopDrainTimeout = stopDrainTimeout;
            ServiceName = WatchdogServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            StartRuntime();
        }

        internal void StartRuntime()
        {
            _identityVerifier();
            IWatchdogRuntime runtime = _runtimeFactory();
            if (runtime == null)
            {
                throw new InvalidOperationException(
                    "The watchdog runtime factory returned null.");
            }

            lock (_gate)
            {
                _serviceStartInProgress = true;
                _serviceStopRequested = false;
            }

            try
            {
                runtime.Start();
                Task completion = runtime.Completion;
                if (completion == null)
                {
                    throw new InvalidOperationException(
                        "The watchdog runtime did not expose a completion task.");
                }

                lock (_gate)
                {
                    if (_runtime != null)
                    {
                        throw new InvalidOperationException(
                            "The watchdog runtime is already active.");
                    }

                    _runtime = runtime;
                }

                completion.ContinueWith(
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

                ClearRuntimeReference(runtime);
                if (cleanupFailure != null)
                {
                    throw new AggregateException(
                        "Watchdog startup and runtime cleanup both failed.",
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
            StopRuntime();
        }

        internal void StopRuntime()
        {
            IWatchdogRuntime runtime;
            lock (_gate)
            {
                runtime = _runtime;
                _serviceStopRequested = true;
                if (runtime != null)
                {
                    if (_runtimeStopInProgress)
                    {
                        return;
                    }

                    _runtimeStopInProgress = true;
                }
            }

            if (runtime == null)
            {
                return;
            }

            bool stopped;
            try
            {
                stopped = runtime.Stop(_stopDrainTimeout);
            }
            catch
            {
                ExitCode = ErrorExceptionInService;
                ReleaseRuntimeStopAfterFailure(runtime);
                throw;
            }

            if (!stopped)
            {
                ExitCode = ErrorServiceRequestTimeout;
                ReleaseRuntimeStopAfterFailure(runtime);
                return;
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

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        internal bool HasRuntime
        {
            get
            {
                lock (_gate)
                {
                    return _runtime != null;
                }
            }
        }

        private void OnRuntimeCompleted(
            IWatchdogRuntime runtime,
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
            }

            try
            {
                if (completion.IsFaulted)
                {
                    // Observe the worker failure before requesting the SCM
                    // stop or disposing a runtime whose stop already timed out.
                    AggregateException observed = completion.Exception;
                    if (observed == null)
                    {
                        ExitCode = ErrorExceptionInService;
                    }
                }

                if (serviceStopRequested)
                {
                    try
                    {
                        runtime.Dispose();
                        ClearRuntimeReference(runtime);
                    }
                    catch
                    {
                        ExitCode = ErrorExceptionInService;
                    }

                    return;
                }

                ExitCode = ErrorExceptionInService;
                try
                {
                    RequestServiceStop();
                }
                catch (Exception)
                {
                    // Do not lose the only terminal signal when the SCM stop
                    // request itself fails. The finally block re-queues it
                    // after the lifecycle guard has been released.
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
                            pendingCompletion = _pendingRuntimeCompletion;
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
                    QueueRuntimeCompletion(runtime, pendingCompletion);
                }
            }
        }

        private void ReleaseRuntimeStopAfterFailure(
            IWatchdogRuntime runtime)
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
                    && runtime.Completion != null
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

        private void ReleaseServiceStart(IWatchdogRuntime runtime)
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
                StopRuntime();
                return;
            }

            if (completionToQueue != null)
            {
                QueueRuntimeCompletion(runtime, completionToQueue);
            }
        }

        private void ClearRuntimeReference(IWatchdogRuntime runtime)
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

        private void QueueRuntimeCompletion(
            IWatchdogRuntime runtime,
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

        private void RequestServiceStop()
        {
            if (_requestServiceStop == null)
            {
                Stop();
                return;
            }

            _requestServiceStop();
        }

        private static void EnsureExpectedVirtualServiceAccount()
        {
            try
            {
                var account = new NTAccount(
                    "NT SERVICE",
                    WatchdogServiceName);
                var expectedSid = (SecurityIdentifier)account.Translate(
                    typeof(SecurityIdentifier));
                using (WindowsIdentity identity =
                    WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                {
                    if (identity.User == null)
                    {
                        throw new InvalidOperationException(
                            "The watchdog service identity has no Windows SID.");
                    }

                    if (!identity.User.Equals(expectedSid))
                    {
                        throw new InvalidOperationException(
                            "The watchdog service must run as its dedicated "
                            + "Windows virtual service account.");
                    }
                }
            }
            catch (IdentityNotMappedException exception)
            {
                throw new InvalidOperationException(
                    "The watchdog Windows virtual service account could not "
                    + "be resolved.",
                    exception);
            }
            catch (SecurityException exception)
            {
                throw new InvalidOperationException(
                    "The watchdog service identity could not be verified.",
                    exception);
            }
        }
    }
}
