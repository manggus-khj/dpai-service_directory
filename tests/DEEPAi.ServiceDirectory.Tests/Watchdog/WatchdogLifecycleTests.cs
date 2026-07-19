using System;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class WatchdogLifecycleTests
    {
        [TestMethod]
        public void RuntimeDisposeDoesNotCommitWhileWorkersAreRunning()
        {
            var releaseWorkers = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime = new WatchdogRuntimeHost(
                cancellationToken => releaseWorkers.Task,
                cancellationToken => releaseWorkers.Task);
            runtime.Start();

            Assert.ThrowsExactly<InvalidOperationException>(
                () => runtime.Dispose(TimeSpan.FromMilliseconds(10)));
            Assert.IsFalse(runtime.IsDisposed);

            releaseWorkers.SetResult(null);
            Assert.IsTrue(runtime.Completion.Wait(TimeSpan.FromSeconds(2)));

            runtime.Dispose(TimeSpan.FromSeconds(1));
            Assert.IsTrue(runtime.IsDisposed);
        }

        [TestMethod]
        public void WorkerStartFailureCancelsAndRetainsStartedWorkerForCleanup()
        {
            var monitorCompletion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var cancellationObserved =
                new ManualResetEventSlim(false))
            {
                var startFailure = new InvalidOperationException(
                    "pipe start failed");
                var runtime = new WatchdogRuntimeHost(
                    cancellationToken =>
                    {
                        cancellationToken.Register(
                            () =>
                            {
                                cancellationObserved.Set();
                                monitorCompletion.TrySetCanceled();
                            });
                        return monitorCompletion.Task;
                    },
                    cancellationToken => throw startFailure);

                InvalidOperationException observed =
                    Assert.ThrowsExactly<InvalidOperationException>(
                        () => runtime.Start());

                Assert.AreSame(startFailure, observed);
                Assert.IsTrue(
                    cancellationObserved.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsNotNull(runtime.Completion);
                Assert.IsTrue(runtime.Completion.IsCompleted);
                Assert.IsFalse(runtime.IsDisposed);

                runtime.Dispose(TimeSpan.FromSeconds(1));
                Assert.IsTrue(runtime.IsDisposed);
            }
        }

        [TestMethod]
        public void CompletedRuntimeDoesNotRequestStopInsideStart()
        {
            var runtime = new ControllableRuntime();
            runtime.Fail(new InvalidOperationException("worker failed"));
            int startReturned = 0;
            int stopRequestedBeforeStartReturned = 0;
            using (var stopRequested = new ManualResetEventSlim(false))
            {
                var service = new WatchdogWindowsService(
                    () => runtime,
                    () => { },
                    () =>
                    {
                        if (Volatile.Read(ref startReturned) == 0)
                        {
                            Interlocked.Exchange(
                                ref stopRequestedBeforeStartReturned,
                                1);
                        }

                        stopRequested.Set();
                    },
                    TimeSpan.FromMilliseconds(10));

                service.StartRuntime();
                Interlocked.Exchange(ref startReturned, 1);

                Assert.IsTrue(
                    stopRequested.Wait(TimeSpan.FromSeconds(2)),
                    "The completed runtime was not forwarded to the service stop path.");
                Assert.AreEqual(
                    0,
                    Volatile.Read(
                        ref stopRequestedBeforeStartReturned));

                service.StopRuntime();
                Assert.IsTrue(runtime.IsDisposed);
                Assert.IsFalse(service.HasRuntime);
            }
        }

        [TestMethod]
        public void FailedServiceStopRequestRetainsCompletionForRetry()
        {
            var runtime = new ControllableRuntime();
            runtime.Fail(new InvalidOperationException("worker failed"));
            int stopRequests = 0;
            using (var retried = new ManualResetEventSlim(false))
            {
                var service = new WatchdogWindowsService(
                    () => runtime,
                    () => { },
                    () =>
                    {
                        if (Interlocked.Increment(ref stopRequests) == 1)
                        {
                            throw new InvalidOperationException(
                                "SCM stop request failed");
                        }

                        retried.Set();
                    },
                    TimeSpan.FromMilliseconds(10));

                service.StartRuntime();

                Assert.IsTrue(
                    retried.Wait(TimeSpan.FromSeconds(2)),
                    "The failed SCM stop request was not retried.");
                Assert.IsTrue(Volatile.Read(ref stopRequests) >= 2);

                service.StopRuntime();
                Assert.IsTrue(runtime.IsDisposed);
                Assert.IsFalse(service.HasRuntime);
            }
        }

        [TestMethod]
        public void StopTimeoutRetainsRuntimeUntilCompletionDisposesIt()
        {
            var runtime = new ControllableRuntime();
            int stopRequests = 0;
            var service = new WatchdogWindowsService(
                () => runtime,
                () => { },
                () => Interlocked.Increment(ref stopRequests),
                TimeSpan.FromMilliseconds(10));
            service.StartRuntime();

            service.StopRuntime();

            Assert.IsTrue(service.HasRuntime);
            Assert.IsFalse(runtime.IsDisposed);

            runtime.Complete();
            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => runtime.IsDisposed && !service.HasRuntime,
                    TimeSpan.FromSeconds(2)),
                "The timed-out runtime was not disposed after its workers completed.");
            Assert.AreEqual(0, Volatile.Read(ref stopRequests));
        }

        [TestMethod]
        public void StopRequestedDuringStartIsAppliedAfterRuntimePublication()
        {
            using (var startEntered = new ManualResetEventSlim(false))
            using (var allowStart = new ManualResetEventSlim(false))
            {
                var runtime = new BlockingStartRuntime(
                    startEntered,
                    allowStart);
                var service = new WatchdogWindowsService(
                    () => runtime,
                    () => { },
                    () => { },
                    TimeSpan.FromMilliseconds(10));

                Task start = Task.Run(() => service.StartRuntime());
                Assert.IsTrue(
                    startEntered.Wait(TimeSpan.FromSeconds(2)),
                    "The runtime did not enter its start operation.");

                service.StopRuntime();
                allowStart.Set();

                Assert.IsTrue(start.Wait(TimeSpan.FromSeconds(2)));
                Assert.AreEqual(1, runtime.StopCount);
                Assert.IsTrue(runtime.IsDisposed);
                Assert.IsFalse(service.HasRuntime);
            }
        }

        private sealed class ControllableRuntime : IWatchdogRuntime
        {
            private readonly TaskCompletionSource<object> _completion =
                new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;

            public Task Completion => _completion.Task;

            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void Start()
            {
            }

            public bool Stop(TimeSpan timeout)
            {
                return _completion.Task.IsCompleted;
            }

            public void Dispose()
            {
                if (!_completion.Task.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "Workers must complete before disposal.");
                }

                Interlocked.Exchange(ref _disposed, 1);
            }

            internal void Complete()
            {
                _completion.TrySetResult(null);
            }

            internal void Fail(Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        private sealed class BlockingStartRuntime : IWatchdogRuntime
        {
            private readonly ManualResetEventSlim _startEntered;
            private readonly ManualResetEventSlim _allowStart;
            private readonly TaskCompletionSource<object> _completion =
                new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;
            private int _stopCount;

            internal BlockingStartRuntime(
                ManualResetEventSlim startEntered,
                ManualResetEventSlim allowStart)
            {
                _startEntered = startEntered;
                _allowStart = allowStart;
            }

            public Task Completion => _completion.Task;

            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            internal int StopCount => Volatile.Read(ref _stopCount);

            public void Start()
            {
                _startEntered.Set();
                if (!_allowStart.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException(
                        "The test did not release the runtime start operation.");
                }
            }

            public bool Stop(TimeSpan timeout)
            {
                Interlocked.Increment(ref _stopCount);
                _completion.TrySetResult(null);
                return true;
            }

            public void Dispose()
            {
                if (!_completion.Task.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "Workers must complete before disposal.");
                }

                Interlocked.Exchange(ref _disposed, 1);
            }
        }
    }
}
