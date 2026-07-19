using System;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal interface IWatchdogRuntime : IDisposable
    {
        Task Completion { get; }

        void Start();

        bool Stop(TimeSpan timeout);
    }

    internal sealed class WatchdogRuntimeHost : IWatchdogRuntime
    {
        internal static readonly TimeSpan MonitorInterval =
            TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan ServiceControlDeadline =
            TimeSpan.FromMilliseconds(2500);
        internal static readonly TimeSpan ResponseWriteReserve =
            TimeSpan.FromMilliseconds(100);

        private const string ControlFailureReason =
            "서비스 제어 작업을 완료할 수 없습니다.";
        private const string StatusFailureReason =
            "서비스 상태를 확인할 수 없습니다.";

        private readonly object _operationGate = new object();
        private readonly object _lifecycleGate = new object();
        private readonly IMainServiceController _mainServiceController;
        private readonly IWatchdogHealthProbe _healthProbe;
        private readonly IWatchdogClock _clock;
        private readonly WatchdogMonitorPolicy _monitorPolicy;
        private readonly WatchdogPipeServer _pipeServer;
        private readonly Func<CancellationToken, Task> _monitorWorker;
        private readonly Func<CancellationToken, Task> _pipeWorker;
        private CancellationTokenSource _cancellation;
        private Task _completion;
        private bool _started;
        private bool _disposed;
        private bool _lastAutomaticRestartFailed;

        private WatchdogRuntimeHost(
            IMainServiceController mainServiceController,
            IWatchdogHealthProbe healthProbe,
            IWatchdogClock clock,
            WatchdogMonitorPolicy monitorPolicy,
            WatchdogPipeClientAuthorization pipeAuthorization,
            SecurityAuditEventLogger securityAuditLogger)
        {
            _mainServiceController = mainServiceController
                ?? throw new ArgumentNullException(
                    nameof(mainServiceController));
            _healthProbe = healthProbe
                ?? throw new ArgumentNullException(nameof(healthProbe));
            _clock = clock
                ?? throw new ArgumentNullException(nameof(clock));
            _monitorPolicy = monitorPolicy
                ?? throw new ArgumentNullException(nameof(monitorPolicy));
            _pipeServer = new WatchdogPipeServer(
                pipeAuthorization
                    ?? throw new ArgumentNullException(
                        nameof(pipeAuthorization)),
                securityAuditLogger
                    ?? throw new ArgumentNullException(
                        nameof(securityAuditLogger)),
                HandlePipeCommand);
            _monitorWorker = RunMonitorAsync;
            _pipeWorker = _pipeServer.RunAsync;
        }

        internal WatchdogRuntimeHost(
            Func<CancellationToken, Task> monitorWorker,
            Func<CancellationToken, Task> pipeWorker)
        {
            _monitorWorker = monitorWorker
                ?? throw new ArgumentNullException(nameof(monitorWorker));
            _pipeWorker = pipeWorker
                ?? throw new ArgumentNullException(nameof(pipeWorker));
        }

        internal Task Completion
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _completion;
                }
            }
        }

        Task IWatchdogRuntime.Completion => Completion;

        internal bool LastAutomaticRestartFailed
        {
            get
            {
                lock (_operationGate)
                {
                    return _lastAutomaticRestartFailed;
                }
            }
        }

        internal bool IsDisposed
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _disposed;
                }
            }
        }

        internal static WatchdogRuntimeHost CreateDefault(
            string mainServiceName,
            string watchdogServiceName)
        {
            var pipeAuthorization =
                WatchdogPipeClientAuthorization.Create(
                    watchdogServiceName);
            var securityAuditLogger = new SecurityAuditEventLogger(
                Guid.NewGuid());
            return new WatchdogRuntimeHost(
                new MainServiceController(mainServiceName),
                new WatchdogHealthProbe(),
                new SystemWatchdogClock(),
                new WatchdogMonitorPolicy(),
                pipeAuthorization,
                securityAuditLogger);
        }

        internal void Start()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    throw new InvalidOperationException(
                        "The watchdog runtime is already started.");
                }

                _cancellation = new CancellationTokenSource();
                Task monitor = null;
                Task pipe = null;
                try
                {
                    monitor = _monitorWorker(_cancellation.Token);
                    if (monitor == null)
                    {
                        throw new InvalidOperationException(
                            "The watchdog monitor did not return a completion task.");
                    }

                    pipe = _pipeWorker(_cancellation.Token);
                    if (pipe == null)
                    {
                        throw new InvalidOperationException(
                            "The watchdog pipe server did not return a completion task.");
                    }

                    _completion = ObserveWorkersAsync(
                        monitor,
                        pipe,
                        _cancellation);
                    _started = true;
                }
                catch (Exception workerStartFailure)
                {
                    Exception cancellationFailure = null;
                    try
                    {
                        _cancellation.Cancel();
                    }
                    catch (Exception exception)
                    {
                        cancellationFailure = exception;
                    }

                    if (monitor != null && pipe != null)
                    {
                        _completion = Task.WhenAll(monitor, pipe);
                    }
                    else
                    {
                        _completion = monitor ?? pipe;
                    }

                    if (_completion == null)
                    {
                        _cancellation.Dispose();
                        _cancellation = null;
                    }
                    else
                    {
                        // A worker started before its sibling failed. Retain
                        // lifecycle ownership so Dispose must drain that task.
                        _started = true;
                    }

                    if (cancellationFailure != null)
                    {
                        throw new AggregateException(
                            "A watchdog worker failed to start and cancellation also failed.",
                            workerStartFailure,
                            cancellationFailure);
                    }

                    throw;
                }
            }
        }

        void IWatchdogRuntime.Start()
        {
            Start();
        }

        internal bool Stop(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            Task completion;
            lock (_lifecycleGate)
            {
                if (!_started)
                {
                    return true;
                }

                _cancellation.Cancel();
                completion = _completion;
            }

            try
            {
                return completion == null || completion.Wait(timeout);
            }
            catch (AggregateException)
            {
                // Wait observes a completed canceled or faulted worker by
                // throwing. Stop's result describes whether all workers
                // terminated within the deadline; Completion retains the
                // separate success/fault outcome for the service observer.
                return completion.IsCompleted;
            }
        }

        bool IWatchdogRuntime.Stop(TimeSpan timeout)
        {
            return Stop(timeout);
        }

        public void Dispose()
        {
            Dispose(TimeSpan.FromSeconds(5));
        }

        internal void Dispose(TimeSpan timeout)
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            bool stopped = Stop(timeout);
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!stopped
                    || (_completion != null && !_completion.IsCompleted))
                {
                    throw new InvalidOperationException(
                        "The watchdog runtime cannot be disposed while workers are still running.");
                }

                _cancellation?.Dispose();
                _cancellation = null;
                _started = false;
                _disposed = true;
            }
        }

        private async Task RunMonitorAsync(
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan cycleStarted = _clock.MonotonicNow;
                bool healthSucceeded = _healthProbe.Check();
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset completedUtc = _clock.UtcNow;

                lock (_operationGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WatchdogServiceStatus serviceStatus;
                    bool statusAvailable =
                        _mainServiceController.TryGetStatus(
                            out serviceStatus);
                    cancellationToken.ThrowIfCancellationRequested();
                    WatchdogMonitorDecision decision =
                        _monitorPolicy.RecordObservation(
                            statusAvailable
                                && serviceStatus
                                    == WatchdogServiceStatus.Running,
                            healthSucceeded,
                            completedUtc,
                            _clock.MonotonicNow);
                    if (decision.ShouldRestart)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _lastAutomaticRestartFailed =
                            !_mainServiceController.TryRestart(
                                ServiceControlDeadline);
                    }
                }

                TimeSpan elapsed = _clock.MonotonicNow - cycleStarted;
                TimeSpan delay = MonitorInterval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private byte[] HandlePipeCommand(
            WatchdogPipeCommand command,
            WatchdogPipeResponseDeadline responseDeadline)
        {
            if (responseDeadline == null)
            {
                throw new ArgumentNullException(nameof(responseDeadline));
            }

            TimeSpan lockBudget = responseDeadline.Remaining
                - ResponseWriteReserve;
            if (lockBudget <= TimeSpan.Zero
                || !Monitor.TryEnter(_operationGate, lockBudget))
            {
                return WatchdogPipeCodec.EncodeErrorResponse(
                    ControlFailureReason);
            }

            try
            {
                TimeSpan controlBudget = responseDeadline.Remaining
                    - ResponseWriteReserve;
                if (controlBudget <= TimeSpan.Zero)
                {
                    return WatchdogPipeCodec.EncodeErrorResponse(
                        ControlFailureReason);
                }

                if (controlBudget > ServiceControlDeadline)
                {
                    controlBudget = ServiceControlDeadline;
                }

                bool succeeded;
                switch (command)
                {
                    case WatchdogPipeCommand.Start:
                        succeeded = _mainServiceController.TryStart(
                            controlBudget);
                        if (succeeded)
                        {
                            _monitorPolicy.RecordManualStartOrRestart(
                                _clock.MonotonicNow);
                            _lastAutomaticRestartFailed = false;
                            return WatchdogPipeCodec
                                .EncodeControlSuccessResponse(command);
                        }

                        return WatchdogPipeCodec.EncodeErrorResponse(
                            ControlFailureReason);

                    case WatchdogPipeCommand.Stop:
                        succeeded = _mainServiceController.TryStop(
                            controlBudget);
                        if (succeeded)
                        {
                            _monitorPolicy.RecordManualStop(
                                _clock.MonotonicNow);
                            return WatchdogPipeCodec
                                .EncodeControlSuccessResponse(command);
                        }

                        return WatchdogPipeCodec.EncodeErrorResponse(
                            ControlFailureReason);

                    case WatchdogPipeCommand.Restart:
                        succeeded = _mainServiceController.TryRestart(
                            controlBudget);
                        if (succeeded)
                        {
                            _monitorPolicy.RecordManualStartOrRestart(
                                _clock.MonotonicNow);
                            _lastAutomaticRestartFailed = false;
                            return WatchdogPipeCodec
                                .EncodeControlSuccessResponse(command);
                        }

                        return WatchdogPipeCodec.EncodeErrorResponse(
                            ControlFailureReason);

                    case WatchdogPipeCommand.Status:
                        WatchdogServiceStatus status;
                        if (!_mainServiceController.TryGetStatus(out status))
                        {
                            return WatchdogPipeCodec.EncodeErrorResponse(
                                StatusFailureReason);
                        }

                        WatchdogStatusSnapshot snapshot =
                            _monitorPolicy.CreateStatusSnapshot(
                                status,
                                _clock.MonotonicNow);
                        return WatchdogPipeCodec.EncodeStatusSuccessResponse(
                            command,
                            snapshot);

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(command),
                            command,
                            "A defined watchdog command is required.");
                }
            }
            finally
            {
                Monitor.Exit(_operationGate);
            }
        }

        private static async Task ObserveWorkersAsync(
            Task monitor,
            Task pipe,
            CancellationTokenSource cancellation)
        {
            await Task.WhenAny(monitor, pipe).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
            }

            await Task.WhenAll(monitor, pipe).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(WatchdogRuntimeHost));
            }
        }
    }
}
