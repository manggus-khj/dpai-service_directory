using System;
using System.IO;
using System.Security.Cryptography;

namespace DEEPAi.ServiceDirectory.Infrastructure.Protocol
{
    public enum BoundedBodyReadFailureCode
    {
        None = 0,
        DeclaredLengthTooLarge,
        ActualLengthTooLarge,
        DeclaredLengthMismatch,
        IoFailure
    }

    public sealed class BoundedRequestBody
    {
        private readonly byte[] _contents;

        internal BoundedRequestBody(byte[] contents)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            _contents = (byte[])contents.Clone();
        }

        public int Length => _contents.Length;

        public byte[] ComputeSha256()
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return algorithm.ComputeHash(_contents);
            }
        }

        internal Stream OpenRead()
        {
            return new MemoryStream(
                _contents,
                0,
                _contents.Length,
                false,
                false);
        }
    }

    public sealed class BoundedBodyReadResult
    {
        private BoundedBodyReadResult(
            bool isSuccess,
            BoundedRequestBody body,
            BoundedBodyReadFailureCode failureCode)
        {
            if (isSuccess)
            {
                if (body == null || failureCode != BoundedBodyReadFailureCode.None)
                {
                    throw new ArgumentException("A successful body result must contain only a body.");
                }
            }
            else if (body != null
                || failureCode == BoundedBodyReadFailureCode.None
                || !Enum.IsDefined(typeof(BoundedBodyReadFailureCode), failureCode))
            {
                throw new ArgumentException("A failed body result must contain only a defined failure code.");
            }

            IsSuccess = isSuccess;
            Body = body;
            FailureCode = failureCode;
        }

        public bool IsSuccess { get; }

        public BoundedRequestBody Body { get; }

        public BoundedBodyReadFailureCode FailureCode { get; }

        internal static BoundedBodyReadResult Success(BoundedRequestBody body)
        {
            return new BoundedBodyReadResult(
                true,
                body,
                BoundedBodyReadFailureCode.None);
        }

        internal static BoundedBodyReadResult Failure(
            BoundedBodyReadFailureCode failureCode)
        {
            return new BoundedBodyReadResult(false, null, failureCode);
        }
    }

    public sealed class BoundedRequestBodyReader
    {
        private const int ReadBufferSize = 8192;

        public BoundedBodyReadResult ReadStandard(
            Stream input,
            long declaredContentLength)
        {
            return Read(input, declaredContentLength, XmlInputLimits.StandardBodyBytes);
        }

        public BoundedBodyReadResult ReadSyncExchange(
            Stream input,
            long declaredContentLength)
        {
            return Read(input, declaredContentLength, XmlInputLimits.SyncExchangeBodyBytes);
        }

        private static BoundedBodyReadResult Read(
            Stream input,
            long declaredContentLength,
            int maximumBytes)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (!input.CanRead)
            {
                throw new ArgumentException("The request body stream must be readable.", nameof(input));
            }

            if (declaredContentLength < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(declaredContentLength),
                    "The declared content length must be -1 or non-negative.");
            }

            if (declaredContentLength > maximumBytes)
            {
                return BoundedBodyReadResult.Failure(
                    BoundedBodyReadFailureCode.DeclaredLengthTooLarge);
            }

            int initialCapacity = declaredContentLength >= 0
                ? (int)declaredContentLength
                : Math.Min(maximumBytes, ReadBufferSize);
            long readLimit = (long)maximumBytes + 1L;
            var buffer = new byte[Math.Min(ReadBufferSize, maximumBytes)];

            try
            {
                using (var collected = new MemoryStream(initialCapacity))
                {
                    long totalBytesRead = 0;
                    while (totalBytesRead < readLimit)
                    {
                        int requestedBytes = (int)Math.Min(
                            buffer.Length,
                            readLimit - totalBytesRead);
                        int bytesRead = input.Read(buffer, 0, requestedBytes);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        collected.Write(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;
                    }

                    if (totalBytesRead > maximumBytes)
                    {
                        return BoundedBodyReadResult.Failure(
                            BoundedBodyReadFailureCode.ActualLengthTooLarge);
                    }

                    if (declaredContentLength >= 0
                        && totalBytesRead != declaredContentLength)
                    {
                        return BoundedBodyReadResult.Failure(
                            BoundedBodyReadFailureCode.DeclaredLengthMismatch);
                    }

                    return BoundedBodyReadResult.Success(
                        new BoundedRequestBody(collected.ToArray()));
                }
            }
            catch (IOException)
            {
                return BoundedBodyReadResult.Failure(
                    BoundedBodyReadFailureCode.IoFailure);
            }
        }
    }
}
