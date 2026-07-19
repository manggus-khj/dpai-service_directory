using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal sealed partial class PairingNegotiationStateMachine
    {
        private void EnsureWindowActive()
        {
            long now = GetMonotonicTimestamp();
            ExpireIfNeeded(now);
            if (_state == PairingNegotiationState.Unpaired)
            {
                throw new InvalidOperationException(
                    "No active pairing window exists.");
            }

            if (_state != PairingNegotiationState.BothConfirmed
                && now >= _deadlineTimestamp)
            {
                ResetToUnpaired();
                throw new InvalidOperationException(
                    "The pairing window has expired.");
            }
        }

        private void ExpireIfNeeded(long now)
        {
            if (_state != PairingNegotiationState.Unpaired
                && _state != PairingNegotiationState.BothConfirmed
                && now >= _deadlineTimestamp)
            {
                ResetToUnpaired();
            }
        }

        private long GetMonotonicTimestamp()
        {
            long timestamp = _timestampProvider();
            if (timestamp < 0)
            {
                throw new InvalidOperationException(
                    "The monotonic timestamp provider returned a negative value.");
            }

            if (timestamp < _lastObservedTimestamp)
            {
                return _lastObservedTimestamp;
            }

            _lastObservedTimestamp = timestamp;
            return timestamp;
        }

        private long AddDuration(long timestamp, TimeSpan duration)
        {
            double durationTicks = Math.Ceiling(
                duration.TotalSeconds * _timestampFrequency);
            if (durationTicks >= long.MaxValue - timestamp)
            {
                return long.MaxValue;
            }

            return timestamp + (long)durationTicks;
        }

        private void FailIfBeforeBothConfirmed()
        {
            if (_state != PairingNegotiationState.BothConfirmed)
            {
                ResetToUnpaired();
            }
        }

        private void ResetToUnpaired()
        {
            ClearOwnedNegotiationMaterial();
            _peerEndpoint = null;
            _deadlineTimestamp = 0;
            _helloAttempts = 0;
            _state = PairingNegotiationState.Unpaired;
        }

        private void ClearOwnedNegotiationMaterial()
        {
            if (_secretContext != null)
            {
                _secretContext.Dispose();
                _secretContext = null;
            }

            Clear(_initiatorNonce);
            Clear(_initiatorPublicKey);
            Clear(_transcriptHash);
            Clear(_localConfirmationMac);
            Clear(_remoteConfirmationMac);
            Clear(_sas);
            Clear(_localDecisionBody);
            Clear(_localDecisionMac);
            Clear(_remoteDecisionBody);
            Clear(_remoteDecisionMac);
            Clear(_remoteDecisionResponseBody);
            Clear(_remoteDecisionResponseMac);

            _initiatorNonce = null;
            _initiatorPublicKey = null;
            _transcriptHash = null;
            _localConfirmationMac = null;
            _remoteConfirmationMac = null;
            _sas = null;
            _localDecisionRequest = null;
            _localDecisionBody = null;
            _localDecisionMac = null;
            _remoteDecisionRequest = null;
            _remoteDecisionBody = null;
            _remoteDecisionMac = null;
            _remoteDecisionResponseBody = null;
            _remoteDecisionResponseMac = null;
            _localRole = null;
            _pairingId = Guid.Empty;
            _initiatorInstanceId = Guid.Empty;
            _peerInstanceId = Guid.Empty;
            _initiatorEndpoint = null;
            _initiatorLastPeerKeyEpoch = 0;
            _keyEpoch = 0;
        }

        private static PairingConfirmationDirection ToCryptographyRole(
            PeerPairingRole role)
        {
            switch (role)
            {
                case PeerPairingRole.Initiator:
                    return PairingConfirmationDirection.Initiator;
                case PeerPairingRole.Responder:
                    return PairingConfirmationDirection.Responder;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static PeerPairingRole OppositeRole(PeerPairingRole role)
        {
            switch (role)
            {
                case PeerPairingRole.Initiator:
                    return PeerPairingRole.Responder;
                case PeerPairingRole.Responder:
                    return PeerPairingRole.Initiator;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static int CompareCanonicalInstanceIds(Guid left, Guid right)
        {
            string canonicalLeft = left.ToString("D").ToLowerInvariant();
            string canonicalRight = right.ToString("D").ToLowerInvariant();
            return StringComparer.Ordinal.Compare(
                canonicalLeft,
                canonicalRight);
        }

        private static string ValidateCanonicalEndpoint(
            string endpoint,
            string parameterName)
        {
            string canonical;
            if (!AdminPeerEndpoint.TryNormalize(endpoint, out canonical)
                || !StringComparer.Ordinal.Equals(endpoint, canonical))
            {
                throw new ArgumentException(
                    "The pairing endpoint must be canonical.",
                    parameterName);
            }

            return canonical;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
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

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private static void Clear(char[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingNegotiationStateMachine));
            }
        }
    }
}
