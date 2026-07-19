using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class StateFileChange
    {
        internal StateFileChange(
            StateFileTarget target,
            bool beforeExists,
            byte[] beforeBytes,
            bool afterExists,
            byte[] afterBytes)
        {
            StateFileTargets.Get(target);
            ValidateImage(beforeExists, beforeBytes, nameof(beforeBytes));
            ValidateImage(afterExists, afterBytes, nameof(afterBytes));
            if (!beforeExists && !afterExists)
            {
                throw new ArgumentException(
                    "A state file change must contain a before or after image.");
            }

            Target = target;
            BeforeExists = beforeExists;
            BeforeBytes = Clone(beforeBytes);
            AfterExists = afterExists;
            AfterBytes = Clone(afterBytes);
        }

        internal StateFileTarget Target { get; }

        internal bool BeforeExists { get; }

        internal byte[] BeforeBytes { get; }

        internal bool AfterExists { get; }

        internal byte[] AfterBytes { get; }

        private static void ValidateImage(
            bool exists,
            byte[] bytes,
            string parameterName)
        {
            if (exists && bytes == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (!exists && bytes != null)
            {
                throw new ArgumentException(
                    "A non-existent state image cannot contain bytes.",
                    parameterName);
            }
        }

        private static byte[] Clone(byte[] bytes)
        {
            return bytes == null ? null : (byte[])bytes.Clone();
        }
    }
}
