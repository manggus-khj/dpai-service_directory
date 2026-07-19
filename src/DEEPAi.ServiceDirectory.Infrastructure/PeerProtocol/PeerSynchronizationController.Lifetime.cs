using System;
using System.Threading;
using System.Threading.Tasks;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private readonly ManualResetEventSlim _backgroundWorkDrained =
            new ManualResetEventSlim(true);
        private readonly TaskCompletionSource<object> _completion =
            new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        private int _backgroundWorkCount;
        private Exception _fatalException;
        private bool _stopping;
        private bool _stopped;

        public Task Completion => _completion.Task;

        public Exception FatalException
        {
            get
            {
                lock (_gate)
                {
                    return _fatalException;
                }
            }
        }

        public void BeginStop()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_stopped)
                {
                    return;
                }

                if (!_stopping)
                {
                    _stopping = true;
                    _started = false;
                    _periodicTimer.Change(
                        Timeout.InfiniteTimeSpan,
                        Timeout.InfiniteTimeSpan);
                }
            }

            // Abort active HttpWebRequest instances and reject all later
            // sends before any component begins waiting. This keeps SCM stop
            // bounded even when a peer is offline in the middle of a control
            // or exchange call.
            _transport.CancelPendingRequests();
        }

        public bool WaitForStop(TimeSpan drainTimeout)
        {
            if (drainTimeout < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drainTimeout));
            }

            BeginStop();
            if (!_backgroundWorkDrained.Wait(drainTimeout))
            {
                return false;
            }

            lock (_gate)
            {
                if (_backgroundWorkCount != 0)
                {
                    return false;
                }

                DisposeTransientPairingLocked();
                DisposePairingDecisionReplayLocked();
                DisposeSessionLocked();
                DisposePairAuthenticationLocked();
                _stopped = true;
                if (_fatalException == null)
                {
                    _completion.TrySetResult(null);
                }

                return true;
            }
        }

        public bool Stop(TimeSpan drainTimeout)
        {
            return WaitForStop(drainTimeout);
        }

        private bool TryQueueBackgroundWork(
            WaitCallback callback,
            object state)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            lock (_gate)
            {
                if (_disposed || _stopping || _stopped)
                {
                    return false;
                }

                _backgroundWorkCount = checked(
                    _backgroundWorkCount + 1);
                if (_backgroundWorkCount == 1)
                {
                    _backgroundWorkDrained.Reset();
                }
            }

            bool queued = false;
            try
            {
                queued = ThreadPool.QueueUserWorkItem(
                    workState =>
                    {
                        try
                        {
                            callback(workState);
                        }
                        catch (Exception exception)
                        {
                            RecordFatalBackgroundFailure(exception);
                        }
                        finally
                        {
                            CompleteBackgroundWork();
                        }
                    },
                    state);
                return queued;
            }
            finally
            {
                if (!queued)
                {
                    CompleteBackgroundWork();
                }
            }
        }

        private void CompleteBackgroundWork()
        {
            lock (_gate)
            {
                if (_backgroundWorkCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Peer background work accounting underflowed.");
                }

                _backgroundWorkCount--;
                if (_backgroundWorkCount == 0)
                {
                    _backgroundWorkDrained.Set();
                }
            }
        }

        private void RecordFatalBackgroundFailure(Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            bool cancelTransport = false;
            lock (_gate)
            {
                if (_disposed || _stopped || _fatalException != null)
                {
                    return;
                }

                _fatalException = exception;
                _stopping = true;
                _started = false;
                _periodicTimer.Change(
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
                cancelTransport = true;
            }

            if (cancelTransport)
            {
                Exception reportedFailure = exception;
                try
                {
                    _transport.CancelPendingRequests();
                }
                catch (Exception cancellationFailure)
                {
                    reportedFailure = new AggregateException(
                        "Peer background work failed and transport cancellation also failed.",
                        exception,
                        cancellationFailure);
                    lock (_gate)
                    {
                        if (ReferenceEquals(
                                _fatalException,
                                exception))
                        {
                            _fatalException = reportedFailure;
                        }
                    }
                }

                _completion.TrySetException(reportedFailure);
            }
        }
    }
}
