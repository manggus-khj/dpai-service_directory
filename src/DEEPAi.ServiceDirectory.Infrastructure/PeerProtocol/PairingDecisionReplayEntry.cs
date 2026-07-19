using System;
using System.Diagnostics;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    // Retains only an already-authenticated request and its signed response.
    // K0, pair root, transcript material and derived keys are never retained
    // here.  The entry exists solely to satisfy exact decision replay after
    // the transient pairing state has been securely disposed.
    internal sealed class PairingDecisionReplayEntry : IDisposable
    {
        private readonly long _deadlineTimestamp;
        private readonly byte[] _requestBody;
        private readonly byte[] _requestMac;
        private readonly byte[] _responseBody;
        private readonly byte[] _responseMac;
        private bool _disposed;

        internal PairingDecisionReplayEntry(
            Guid pairingId,
            Guid peerInstanceId,
            string peerEndpoint,
            PeerPairingDecisionValue decision,
            long deadlineTimestamp,
            byte[] requestBody,
            byte[] requestMac,
            int responseStatusCode,
            byte[] responseBody,
            byte[] responseMac)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The replay pairing ID must not be empty.",
                    nameof(pairingId));
            }

            if (peerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The replay peer instance ID must not be empty.",
                    nameof(peerInstanceId));
            }

            if (string.IsNullOrEmpty(peerEndpoint))
            {
                throw new ArgumentException(
                    "The replay peer endpoint is required.",
                    nameof(peerEndpoint));
            }

            if (!Enum.IsDefined(
                    typeof(PeerPairingDecisionValue),
                    decision))
            {
                throw new ArgumentOutOfRangeException(nameof(decision));
            }

            if (deadlineTimestamp <= Stopwatch.GetTimestamp())
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deadlineTimestamp),
                    "The replay deadline must still be active.");
            }

            ValidateBody(requestBody, nameof(requestBody));
            ValidateMac(requestMac, nameof(requestMac));
            if (responseStatusCode < 100 || responseStatusCode > 599)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(responseStatusCode));
            }

            ValidateBody(responseBody, nameof(responseBody));
            ValidateMac(responseMac, nameof(responseMac));

            PairingId = pairingId;
            PeerInstanceId = peerInstanceId;
            PeerEndpoint = peerEndpoint;
            Decision = decision;
            ResponseStatusCode = responseStatusCode;
            _deadlineTimestamp = deadlineTimestamp;
            _requestBody = (byte[])requestBody.Clone();
            _requestMac = (byte[])requestMac.Clone();
            _responseBody = (byte[])responseBody.Clone();
            _responseMac = (byte[])responseMac.Clone();
        }

        internal Guid PairingId { get; }

        internal Guid PeerInstanceId { get; }

        internal string PeerEndpoint { get; }

        internal PeerPairingDecisionValue Decision { get; }

        internal int ResponseStatusCode { get; }

        internal bool IsExpired
        {
            get
            {
                ThrowIfDisposed();
                return Stopwatch.GetTimestamp() >= _deadlineTimestamp;
            }
        }

        internal bool TryCopyResponse(
            byte[] requestBody,
            byte[] requestMac,
            out byte[] responseBody,
            out byte[] responseMac)
        {
            ThrowIfDisposed();
            responseBody = null;
            responseMac = null;
            if (!FixedTimeEquals(_requestBody, requestBody)
                || requestMac == null
                || requestMac.Length
                    != PairingCryptography.AuthenticationCodeLength
                || !PairingCryptography.FixedTimeEquals32(
                    _requestMac,
                    requestMac))
            {
                return false;
            }

            responseBody = (byte[])_responseBody.Clone();
            responseMac = (byte[])_responseMac.Clone();
            return true;
        }

        internal static long CreateDeadline(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero
                || remaining > PairingNegotiationStateMachine
                    .PairingWindowLifetime)
            {
                throw new ArgumentOutOfRangeException(nameof(remaining));
            }

            long now = Stopwatch.GetTimestamp();
            double deltaValue = Math.Ceiling(
                remaining.TotalSeconds * Stopwatch.Frequency);
            if (deltaValue < 1d)
            {
                deltaValue = 1d;
            }

            long delta = checked((long)deltaValue);
            return checked(now + delta);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Array.Clear(_requestBody, 0, _requestBody.Length);
            Array.Clear(_requestMac, 0, _requestMac.Length);
            Array.Clear(_responseBody, 0, _responseBody.Length);
            Array.Clear(_responseMac, 0, _responseMac.Length);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private static void ValidateBody(
            byte[] value,
            string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException(
                    "A replay body must not be empty.",
                    parameterName);
            }
        }

        private static void ValidateMac(
            byte[] value,
            string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length
                != PairingCryptography.AuthenticationCodeLength)
            {
                throw new ArgumentException(
                    "A replay MAC must contain exactly 32 bytes.",
                    parameterName);
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null
                || left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingDecisionReplayEntry));
            }
        }
    }
}
