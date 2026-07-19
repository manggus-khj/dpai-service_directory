using System;

namespace DEEPAi.ServiceDirectory.Application.State
{
    // One process-wide serialization boundary for every durable state target
    // that participates in the shared recovery journal. Network I/O and XML
    // parsing must finish before entering this gate.
    public sealed class StateMutationGate
    {
        private readonly object _gate = new object();

        public T Execute<T>(Func<T> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            lock (_gate)
            {
                return operation();
            }
        }

        public void Execute(Action operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            lock (_gate)
            {
                operation();
            }
        }
    }
}
