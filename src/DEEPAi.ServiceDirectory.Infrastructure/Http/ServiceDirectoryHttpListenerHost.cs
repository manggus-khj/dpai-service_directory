using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    // Owns the shared HttpListener transport only. Application state handlers
    // remain constructor-injected through their transport-neutral adapters.
    public sealed class ServiceDirectoryHttpListenerHost : IDisposable
    {
        internal const string LoopbackPrefix =
            "http://127.0.0.1:21000/";
        private const int MaximumTransportWorkers = 40;

        private static readonly TimeSpan DefaultStopDrainTimeout =
            TimeSpan.FromSeconds(5);

        private readonly object _stateGate = new object();
        private readonly IHttpListenerServer _listener;
        private readonly ServiceDirectoryListenerAddress _configuredAddress;
        private readonly Func<ExternalHttpRequestData,
            ExternalHttpResponseData> _externalProcessor;
        private readonly Func<AdminHttpRequestData,
            AdminHttpResponseData> _adminProcessor;
        private readonly Func<ExternalHttpRequestData,
            ExternalHttpResponseData> _watchdogProcessor;
        private readonly Func<PeerHttpRequestData,
            PeerHttpResponseData> _peerProcessor;
        private readonly IHttpDeadlineWaiter _deadlineWaiter;
        private readonly SemaphoreSlim _transportSlots;
        private readonly HashSet<Task> _activeRequests;
        private readonly HashSet<Task> _detachedOperations;
        private readonly TaskCompletionSource<object> _completion;

        private CancellationTokenSource _stopSource;
        private bool _started;
        private bool _stopRequested;
        private bool _acceptLoopEnded;
        private bool _disposed;
        private Exception _fatalException;

        public ServiceDirectoryHttpListenerHost(
            ServiceDirectoryListenerAddress configuredAddress,
            ExternalHttpAdapter externalAdapter,
            AdminHttpAdapter adminAdapter,
            WatchdogHealthHttpAdapter watchdogAdapter)
            : this(
                configuredAddress,
                externalAdapter,
                adminAdapter,
                watchdogAdapter,
                null)
        {
        }

        public ServiceDirectoryHttpListenerHost(
            ServiceDirectoryListenerAddress configuredAddress,
            ExternalHttpAdapter externalAdapter,
            AdminHttpAdapter adminAdapter,
            WatchdogHealthHttpAdapter watchdogAdapter,
            PeerHttpAdapter peerAdapter)
            : this(
                new SystemHttpListenerServer(),
                configuredAddress,
                GetExternalProcessor(externalAdapter),
                GetAdminProcessor(adminAdapter),
                GetWatchdogProcessor(watchdogAdapter),
                peerAdapter == null
                    ? GetClosedPeerProcessor()
                    : GetPeerProcessor(peerAdapter),
                new SystemHttpDeadlineWaiter())
        {
        }

        internal ServiceDirectoryHttpListenerHost(
            IHttpListenerServer listener,
            ServiceDirectoryListenerAddress configuredAddress,
            Func<ExternalHttpRequestData,
                ExternalHttpResponseData> externalProcessor,
            Func<AdminHttpRequestData,
                AdminHttpResponseData> adminProcessor,
            Func<ExternalHttpRequestData,
                ExternalHttpResponseData> watchdogProcessor,
            IHttpDeadlineWaiter deadlineWaiter)
            : this(
                listener,
                configuredAddress,
                externalProcessor,
                adminProcessor,
                watchdogProcessor,
                GetClosedPeerProcessor(),
                deadlineWaiter)
        {
        }

        internal ServiceDirectoryHttpListenerHost(
            IHttpListenerServer listener,
            ServiceDirectoryListenerAddress configuredAddress,
            Func<ExternalHttpRequestData,
                ExternalHttpResponseData> externalProcessor,
            Func<AdminHttpRequestData,
                AdminHttpResponseData> adminProcessor,
            Func<ExternalHttpRequestData,
                ExternalHttpResponseData> watchdogProcessor,
            Func<PeerHttpRequestData,
                PeerHttpResponseData> peerProcessor,
            IHttpDeadlineWaiter deadlineWaiter)
        {
            _listener = listener
                ?? throw new ArgumentNullException(nameof(listener));
            _configuredAddress = configuredAddress
                ?? throw new ArgumentNullException(
                    nameof(configuredAddress));
            _externalProcessor = externalProcessor
                ?? throw new ArgumentNullException(
                    nameof(externalProcessor));
            _adminProcessor = adminProcessor
                ?? throw new ArgumentNullException(nameof(adminProcessor));
            _watchdogProcessor = watchdogProcessor
                ?? throw new ArgumentNullException(
                    nameof(watchdogProcessor));
            _peerProcessor = peerProcessor
                ?? throw new ArgumentNullException(nameof(peerProcessor));
            _deadlineWaiter = deadlineWaiter
                ?? throw new ArgumentNullException(nameof(deadlineWaiter));
            _transportSlots = new SemaphoreSlim(
                MaximumTransportWorkers,
                MaximumTransportWorkers);
            _activeRequests = new HashSet<Task>();
            _detachedOperations = new HashSet<Task>();
            _completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _listener.Configure(
                    new[]
                    {
                        _configuredAddress.HttpPrefix,
                        LoopbackPrefix
                    },
                    HttpListenerRequestRouting.SelectAuthentication,
                    false);
            }
            catch
            {
                _listener.Dispose();
                throw;
            }
        }

        public Task Completion => _completion.Task;

        public Exception FatalException
        {
            get
            {
                lock (_stateGate)
                {
                    return _fatalException;
                }
            }
        }

        public void Start()
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    throw new InvalidOperationException(
                        "The HTTP listener host can be started only once.");
                }

                if (_stopRequested)
                {
                    throw new InvalidOperationException(
                        "A stopped HTTP listener host cannot be restarted.");
                }

                _started = true;
                _stopSource = new CancellationTokenSource();
                try
                {
                    _listener.Start();
                }
                catch (Exception exception)
                {
                    _fatalException = exception;
                    _acceptLoopEnded = true;
                    _completion.TrySetException(exception);
                    throw;
                }

                Task acceptLoop = AcceptLoopAsync(_stopSource.Token);
                acceptLoop.ContinueWith(
                    OnAcceptLoopCompleted,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        public bool Stop()
        {
            return Stop(DefaultStopDrainTimeout);
        }

        public void BeginStop()
        {
            SignalStop(null);
        }

        public bool WaitForStop(TimeSpan drainTimeout)
        {
            if (drainTimeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(drainTimeout));
            }

            try
            {
                return _completion.Task.Wait(drainTimeout);
            }
            catch (AggregateException)
            {
                // Completion faults expose FatalException while still proving
                // that listener acceptance and tracked requests drained.
                return _completion.Task.IsCompleted;
            }
        }

        public bool Stop(TimeSpan drainTimeout)
        {
            BeginStop();
            return WaitForStop(drainTimeout);
        }

        public void Dispose()
        {
            bool shouldDisposePrimitives;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            SignalStop(null);
            try
            {
                _completion.Task.Wait(DefaultStopDrainTimeout);
            }
            catch (AggregateException)
            {
                // The fault remains observable through Completion/FatalException.
            }

            _listener.Dispose();
            lock (_stateGate)
            {
                shouldDisposePrimitives = _completion.Task.IsCompleted;
            }

            if (shouldDisposePrimitives)
            {
                if (_stopSource != null)
                {
                    _stopSource.Dispose();
                }

                _transportSlots.Dispose();
            }
        }

        private async Task AcceptLoopAsync(CancellationToken stopToken)
        {
            while (!stopToken.IsCancellationRequested)
            {
                bool slotAcquired = false;
                try
                {
                    await _transportSlots.WaitAsync(stopToken)
                        .ConfigureAwait(false);
                    slotAcquired = true;

                    IHttpServerContext context = await _listener
                        .AcceptAsync()
                        .ConfigureAwait(false);
                    if (context == null)
                    {
                        throw new InvalidOperationException(
                            "The HTTP listener returned a null context.");
                    }

                    if (stopToken.IsCancellationRequested)
                    {
                        context.Abort();
                        context.Dispose();
                        break;
                    }

                    Task requestTask = ProcessContextAsync(
                        context,
                        stopToken);
                    lock (_stateGate)
                    {
                        _activeRequests.Add(requestTask);
                    }

                    slotAcquired = false;
                    _ = requestTask.ContinueWith(
                        completed => OnRequestCompleted(
                            completed,
                            context),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch (OperationCanceledException)
                    when (stopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                    when (stopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpListenerException)
                    when (stopToken.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    if (slotAcquired)
                    {
                        _transportSlots.Release();
                    }
                }
            }
        }

        private async Task ProcessContextAsync(
            IHttpServerContext context,
            CancellationToken stopToken)
        {
            RawHttpRequestTarget target;
            if (!RawHttpRequestTargetParser.TryParse(
                    context.Request.RawUrl,
                    out target))
            {
                target = new RawHttpRequestTarget("/", string.Empty);
            }

            ServiceDirectoryHttpRoute route =
                HttpListenerRequestRouting.ResolveRoute(
                    context.Request.LocalEndPoint,
                    context.Request.RemoteEndPoint,
                    target,
                    _configuredAddress);
            TimeSpan deadline = HttpListenerDeadlinePolicy.GetDeadline(
                route,
                context.Request.HttpMethod,
                target.AbsolutePath);

            try
            {
                context.Request.SetBodyReadTimeout(deadline);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is ObjectDisposedException
                || exception is InvalidOperationException
                || exception is NotSupportedException)
            {
                // The independent request timer still closes the context.
            }

            using (var deadlineSource =
                CancellationTokenSource.CreateLinkedTokenSource(stopToken))
            {
                Task operation = ExecuteRequestAsync(
                    context,
                    target,
                    route);
                Task deadlineTask = _deadlineWaiter.WaitAsync(
                    deadline,
                    deadlineSource.Token);
                Task winner = await Task.WhenAny(operation, deadlineTask)
                    .ConfigureAwait(false);

                if (operation.IsCompleted || winner == operation)
                {
                    deadlineSource.Cancel();
                    await ObserveExpectedCancellation(deadlineTask)
                        .ConfigureAwait(false);
                    await operation.ConfigureAwait(false);
                    return;
                }

                context.Abort();
                if (operation.IsCompleted)
                {
                    try
                    {
                        await operation.ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        IsExpectedAbortedOperationFailure(exception))
                    {
                        // Deadline/stop aborts are expected transport
                        // failures.
                    }
                }
                else
                {
                    // Synchronous adapter code cannot be force-cancelled.
                    // The transport context is aborted at the deadline, but
                    // runtime state must remain alive until the detached
                    // application operation actually terminates.
                    TrackDetachedOperation(operation);
                }

                if (deadlineTask.IsFaulted)
                {
                    throw Unwrap(deadlineTask.Exception);
                }
            }
        }

        private async Task ExecuteRequestAsync(
            IHttpServerContext context,
            RawHttpRequestTarget target,
            ServiceDirectoryHttpRoute route)
        {
            HttpTransportResponseData response = await Task.Factory
                .StartNew(
                    () => Dispatch(context, target, route),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .ConfigureAwait(false);
            await context.WriteResponseAsync(
                    response,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        private HttpTransportResponseData Dispatch(
            IHttpServerContext context,
            RawHttpRequestTarget target,
            ServiceDirectoryHttpRoute route)
        {
            switch (route)
            {
                case ServiceDirectoryHttpRoute.External:
                    return HttpTransportResponseData.FromExternal(
                        _externalProcessor(
                            HttpListenerRequestMapper.ToExternal(
                                context,
                                target)));
                case ServiceDirectoryHttpRoute.Admin:
                    return HttpTransportResponseData.FromAdmin(
                        _adminProcessor(
                            HttpListenerRequestMapper.ToAdmin(
                                context,
                                target)));
                case ServiceDirectoryHttpRoute.WatchdogHealth:
                    return HttpTransportResponseData.FromExternal(
                        _watchdogProcessor(
                            HttpListenerRequestMapper.ToExternal(
                                context,
                                target)));
                case ServiceDirectoryHttpRoute.Peer:
                    return HttpTransportResponseData.FromPeer(
                        _peerProcessor(
                            HttpListenerRequestMapper.ToPeer(
                                context,
                                target)));
                case ServiceDirectoryHttpRoute.NotFound:
                default:
                    return HttpTransportResponseData.Bodyless(404);
            }
        }

        private void OnRequestCompleted(
            Task requestTask,
            IHttpServerContext context)
        {
            Exception failure = null;
            try
            {
                context.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (requestTask.IsFaulted)
            {
                failure = Unwrap(requestTask.Exception);
            }

            lock (_stateGate)
            {
                _activeRequests.Remove(requestTask);
            }

            _transportSlots.Release();
            if (failure != null
                && !IsExpectedRequestCompletionFailure(failure))
            {
                SignalStop(failure);
            }

            TryCompleteIfDrained();
        }

        private void TrackDetachedOperation(Task operation)
        {
            bool detachedCapacityReached;
            lock (_stateGate)
            {
                _detachedOperations.Add(operation);
                detachedCapacityReached =
                    _detachedOperations.Count
                        >= MaximumTransportWorkers;
            }

            operation.ContinueWith(
                OnDetachedOperationCompleted,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            if (detachedCapacityReached)
            {
                SignalStop(new InvalidOperationException(
                    "The maximum number of deadline-aborted HTTP operations did not terminate."));
            }
        }

        private void OnDetachedOperationCompleted(Task operation)
        {
            Exception failure = operation.IsFaulted
                ? Unwrap(operation.Exception)
                : null;
            lock (_stateGate)
            {
                _detachedOperations.Remove(operation);
            }

            if (failure != null
                && !IsExpectedAbortedOperationFailure(failure))
            {
                SignalStop(failure);
            }

            TryCompleteIfDrained();
        }

        private void OnAcceptLoopCompleted(Task acceptLoop)
        {
            Exception failure = acceptLoop.IsFaulted
                ? Unwrap(acceptLoop.Exception)
                : null;
            lock (_stateGate)
            {
                _acceptLoopEnded = true;
            }

            if (failure != null)
            {
                SignalStop(failure);
            }

            TryCompleteIfDrained();
        }

        private void SignalStop(Exception failure)
        {
            CancellationTokenSource stopSource = null;
            bool callListenerStop = false;
            bool completeWithoutStart = false;
            lock (_stateGate)
            {
                if (failure != null && _fatalException == null)
                {
                    _fatalException = failure;
                }

                if (!_stopRequested)
                {
                    _stopRequested = true;
                    stopSource = _stopSource;
                    callListenerStop = _started;
                }

                if (!_started)
                {
                    _acceptLoopEnded = true;
                    completeWithoutStart = true;
                }
            }

            if (stopSource != null)
            {
                stopSource.Cancel();
            }

            if (callListenerStop)
            {
                try
                {
                    _listener.Stop();
                }
                catch (Exception exception) when (
                    exception is HttpListenerException
                    || exception is ObjectDisposedException
                    || exception is InvalidOperationException)
                {
                    lock (_stateGate)
                    {
                        if (_fatalException == null)
                        {
                            _fatalException = exception;
                        }
                    }
                }
            }

            if (completeWithoutStart || failure != null)
            {
                TryCompleteIfDrained();
            }
        }

        private void TryCompleteIfDrained()
        {
            Exception failure;
            lock (_stateGate)
            {
                if (!_acceptLoopEnded
                    || _activeRequests.Count != 0
                    || _detachedOperations.Count != 0)
                {
                    return;
                }

                failure = _fatalException;
            }

            if (failure == null)
            {
                _completion.TrySetResult(null);
            }
            else
            {
                _completion.TrySetException(failure);
            }
        }

        private static async Task ObserveExpectedCancellation(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static bool IsExpectedRequestCompletionFailure(
            Exception exception)
        {
            // A peer can disconnect while its response is being written.
            // The transport wrapper distinguishes that request-local failure
            // from listener lifecycle and application invariant failures.
            return exception is HttpListenerTransportException;
        }

        private static bool IsExpectedAbortedOperationFailure(
            Exception exception)
        {
            return exception is HttpListenerTransportException
                || exception is OperationCanceledException
                || exception is ObjectDisposedException;
        }

        private static Exception Unwrap(AggregateException exception)
        {
            if (exception == null)
            {
                return new InvalidOperationException(
                    "An asynchronous operation failed without an exception.");
            }

            AggregateException flattened = exception.Flatten();
            return flattened.InnerExceptions.Count == 1
                ? flattened.InnerExceptions[0]
                : flattened;
        }

        private static Func<ExternalHttpRequestData,
            ExternalHttpResponseData> GetExternalProcessor(
                ExternalHttpAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            return adapter.Process;
        }

        private static Func<AdminHttpRequestData,
            AdminHttpResponseData> GetAdminProcessor(
                AdminHttpAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            return adapter.Process;
        }

        private static Func<ExternalHttpRequestData,
            ExternalHttpResponseData> GetWatchdogProcessor(
                WatchdogHealthHttpAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            return adapter.Process;
        }

        private static Func<PeerHttpRequestData,
            PeerHttpResponseData> GetPeerProcessor(
                PeerHttpAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            return adapter.Process;
        }

        private static Func<PeerHttpRequestData,
            PeerHttpResponseData> GetClosedPeerProcessor()
        {
            return request => PeerHttpResponseData.Bodyless(404);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ServiceDirectoryHttpListenerHost));
            }
        }
    }
}
