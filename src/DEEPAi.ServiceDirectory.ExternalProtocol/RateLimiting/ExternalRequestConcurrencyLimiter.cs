using System;
using System.Threading;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting
{
    public sealed class ExternalRequestConcurrencyLimiter
    {
        public const int MaximumConcurrentRequests = 32;

        private int _activeRequests;

        public bool TryAcquire(out IDisposable lease)
        {
            while (true)
            {
                int current = Volatile.Read(ref _activeRequests);
                if (current >= MaximumConcurrentRequests)
                {
                    lease = null;
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _activeRequests,
                        current + 1,
                        current) == current)
                {
                    lease = new RequestLease(this);
                    return true;
                }
            }
        }

        internal int ActiveRequests => Volatile.Read(ref _activeRequests);

        private void Release()
        {
            int remaining = Interlocked.Decrement(ref _activeRequests);
            if (remaining < 0)
            {
                Interlocked.Increment(ref _activeRequests);
                throw new InvalidOperationException(
                    "The external request concurrency lease was released too many times.");
            }
        }

        private sealed class RequestLease : IDisposable
        {
            private ExternalRequestConcurrencyLimiter _owner;

            internal RequestLease(
                ExternalRequestConcurrencyLimiter owner)
            {
                _owner = owner
                    ?? throw new ArgumentNullException(nameof(owner));
            }

            public void Dispose()
            {
                ExternalRequestConcurrencyLimiter owner =
                    Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    owner.Release();
                }
            }
        }
    }
}
