using System;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal enum CertificateAuthorityRole
    {
        ActiveIssuer = 1,
        Standby = 2
    }

    internal enum CertificateAuthorityRotationPhase
    {
        Stable = 1,
        Published = 2,
        Activated = 3
    }

    internal enum CertificateAuthoritySlot
    {
        A = 1,
        B = 2
    }

    internal enum CertificateAuthorityLiveRole
    {
        Current = 1,
        Next = 2,
        Retiring = 3
    }

    internal sealed class CertificateAuthorityLiveState
    {
        private readonly byte[] _caSpkiSha256;

        internal CertificateAuthorityLiveState(
            CertificateAuthoritySlot slot,
            CertificateAuthorityLiveRole role,
            CertificateSerialNumber caSerialNumber,
            byte[] caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong crlNumber)
        {
            if (!Enum.IsDefined(typeof(CertificateAuthoritySlot), slot))
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            if (!Enum.IsDefined(typeof(CertificateAuthorityLiveRole), role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            if (!caSerialNumber.IsValid)
            {
                throw new ArgumentException(
                    "CA serial number must be valid.",
                    nameof(caSerialNumber));
            }

            if (caSpkiSha256 == null || caSpkiSha256.Length != 32)
            {
                throw new ArgumentException(
                    "CA SPKI SHA-256 must contain exactly 32 bytes.",
                    nameof(caSpkiSha256));
            }

            CertificateAuthorityState.EnsureUtc(
                notBeforeUtc,
                nameof(notBeforeUtc));
            CertificateAuthorityState.EnsureUtc(
                notAfterUtc,
                nameof(notAfterUtc));
            if (notAfterUtc <= notBeforeUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(notAfterUtc));
            }

            if (crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(crlNumber));
            }

            Slot = slot;
            Role = role;
            CaSerialNumber = caSerialNumber;
            _caSpkiSha256 = (byte[])caSpkiSha256.Clone();
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            CrlNumber = crlNumber;
        }

        internal CertificateAuthoritySlot Slot { get; }

        internal CertificateAuthorityLiveRole Role { get; }

        internal CertificateSerialNumber CaSerialNumber { get; }

        internal DateTime NotBeforeUtc { get; }

        internal DateTime NotAfterUtc { get; }

        internal ulong CrlNumber { get; }

        internal byte[] GetCaSpkiSha256()
        {
            return (byte[])_caSpkiSha256.Clone();
        }

        internal CertificateAuthorityLiveState WithCrlNumber(
            ulong crlNumber)
        {
            if (crlNumber < CrlNumber)
            {
                throw new ArgumentOutOfRangeException(nameof(crlNumber));
            }

            return new CertificateAuthorityLiveState(
                Slot,
                Role,
                CaSerialNumber,
                _caSpkiSha256,
                NotBeforeUtc,
                NotAfterUtc,
                crlNumber);
        }
    }

    internal sealed class CertificateAuthorityState
    {
        private static readonly TimeSpan MinimumRotationPeriod =
            TimeSpan.FromDays(30);

        internal CertificateAuthorityState(
            Guid siteId,
            Guid issuerInstanceId,
            CertificateAuthorityRole role,
            CertificateSerialNumber caSerialNumber,
            byte[] caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong pkiRevision,
            ulong crlNumber,
            DateTime? lastBackupUtc)
            : this(
                siteId,
                issuerInstanceId,
                role,
                caSerialNumber,
                caSpkiSha256,
                notBeforeUtc,
                notAfterUtc,
                1,
                pkiRevision,
                crlNumber,
                lastBackupUtc)
        {
        }

        internal CertificateAuthorityState(
            Guid siteId,
            Guid issuerInstanceId,
            CertificateAuthorityRole role,
            CertificateSerialNumber caSerialNumber,
            byte[] caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong trustRevision,
            ulong pkiRevision,
            ulong crlNumber,
            DateTime? lastBackupUtc)
            : this(
                siteId,
                issuerInstanceId,
                role,
                CertificateAuthorityRotationPhase.Stable,
                trustRevision,
                pkiRevision,
                CertificateAuthoritySlot.A,
                null,
                null,
                null,
                null,
                null,
                new CertificateAuthorityLiveState(
                    CertificateAuthoritySlot.A,
                    CertificateAuthorityLiveRole.Current,
                    caSerialNumber,
                    caSpkiSha256,
                    notBeforeUtc,
                    notAfterUtc,
                    crlNumber),
                null,
                lastBackupUtc.HasValue ? (ulong?)trustRevision : null,
                lastBackupUtc)
        {
        }

        internal CertificateAuthorityState(
            Guid siteId,
            Guid issuerInstanceId,
            CertificateAuthorityRole role,
            CertificateAuthorityRotationPhase rotationPhase,
            ulong trustRevision,
            ulong pkiRevision,
            CertificateAuthoritySlot currentSlot,
            Guid? rotationId,
            DateTime? publishedUtc,
            DateTime? activationNotBeforeUtc,
            DateTime? activatedUtc,
            DateTime? retirementNotBeforeUtc,
            CertificateAuthorityLiveState currentAuthority,
            CertificateAuthorityLiveState otherAuthority,
            ulong? lastBackupTrustRevision,
            DateTime? lastBackupUtc)
        {
            if (siteId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Site ID must not be empty.",
                    nameof(siteId));
            }

            if (issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Issuer instance ID must not be empty.",
                    nameof(issuerInstanceId));
            }

            if (!Enum.IsDefined(typeof(CertificateAuthorityRole), role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            if (!Enum.IsDefined(
                    typeof(CertificateAuthorityRotationPhase),
                    rotationPhase)
                || !Enum.IsDefined(
                    typeof(CertificateAuthoritySlot),
                    currentSlot))
            {
                throw new ArgumentOutOfRangeException(nameof(rotationPhase));
            }

            if (currentAuthority == null
                || currentAuthority.Role
                    != CertificateAuthorityLiveRole.Current
                || currentAuthority.Slot != currentSlot)
            {
                throw new ArgumentException(
                    "The current authority must match CurrentSlot.",
                    nameof(currentAuthority));
            }

            if (trustRevision == 0 || pkiRevision == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkiRevision),
                    "Provisioned PKI high-water values must be positive.");
            }

            EnsureOptionalUtc(publishedUtc, nameof(publishedUtc));
            EnsureOptionalUtc(
                activationNotBeforeUtc,
                nameof(activationNotBeforeUtc));
            EnsureOptionalUtc(activatedUtc, nameof(activatedUtc));
            EnsureOptionalUtc(
                retirementNotBeforeUtc,
                nameof(retirementNotBeforeUtc));
            ValidateRotation(
                rotationPhase,
                currentAuthority,
                otherAuthority,
                rotationId,
                publishedUtc,
                activationNotBeforeUtc,
                activatedUtc,
                retirementNotBeforeUtc);

            if (lastBackupTrustRevision.HasValue
                != lastBackupUtc.HasValue)
            {
                throw new ArgumentException(
                    "Backup revision and time must be present together.");
            }

            if (lastBackupUtc.HasValue)
            {
                EnsureUtc(lastBackupUtc.Value, nameof(lastBackupUtc));
                if (!lastBackupTrustRevision.HasValue
                    || lastBackupTrustRevision.Value == 0
                    || lastBackupTrustRevision.Value > trustRevision)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(lastBackupTrustRevision));
                }
            }

            SiteId = siteId;
            IssuerInstanceId = issuerInstanceId;
            Role = role;
            RotationPhase = rotationPhase;
            TrustRevision = trustRevision;
            PkiRevision = pkiRevision;
            CurrentSlot = currentSlot;
            RotationId = rotationId;
            PublishedUtc = publishedUtc;
            ActivationNotBeforeUtc = activationNotBeforeUtc;
            ActivatedUtc = activatedUtc;
            RetirementNotBeforeUtc = retirementNotBeforeUtc;
            CurrentAuthority = currentAuthority;
            OtherAuthority = otherAuthority;
            LastBackupTrustRevision = lastBackupTrustRevision;
            LastBackupUtc = lastBackupUtc;
        }

        internal Guid SiteId { get; }

        internal Guid IssuerInstanceId { get; }

        internal CertificateAuthorityRole Role { get; }

        internal CertificateAuthorityRotationPhase RotationPhase { get; }

        internal CertificateAuthoritySlot CurrentSlot { get; }

        internal Guid? RotationId { get; }

        internal DateTime? PublishedUtc { get; }

        internal DateTime? ActivationNotBeforeUtc { get; }

        internal DateTime? ActivatedUtc { get; }

        internal DateTime? RetirementNotBeforeUtc { get; }

        internal CertificateAuthorityLiveState CurrentAuthority { get; }

        internal CertificateAuthorityLiveState OtherAuthority { get; }

        internal CertificateSerialNumber CaSerialNumber =>
            CurrentAuthority.CaSerialNumber;

        internal DateTime NotBeforeUtc => CurrentAuthority.NotBeforeUtc;

        internal DateTime NotAfterUtc => CurrentAuthority.NotAfterUtc;

        internal ulong TrustRevision { get; }

        internal ulong PkiRevision { get; }

        internal ulong CrlNumber => CurrentAuthority.CrlNumber;

        internal ulong? LastBackupTrustRevision { get; }

        internal DateTime? LastBackupUtc { get; }

        internal byte[] GetCaSpkiSha256()
        {
            return CurrentAuthority.GetCaSpkiSha256();
        }

        internal CertificateAuthorityState WithHighWater(
            ulong pkiRevision,
            ulong crlNumber)
        {
            if (pkiRevision < PkiRevision
                || crlNumber < CrlNumber
                || (pkiRevision == PkiRevision
                    && crlNumber == CrlNumber))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkiRevision),
                    "At least one PKI high-water value must increase and neither may decrease.");
            }

            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                Role,
                RotationPhase,
                TrustRevision,
                pkiRevision,
                CurrentSlot,
                RotationId,
                PublishedUtc,
                ActivationNotBeforeUtc,
                ActivatedUtc,
                RetirementNotBeforeUtc,
                CurrentAuthority.WithCrlNumber(crlNumber),
                OtherAuthority,
                LastBackupTrustRevision,
                LastBackupUtc);
        }

        internal CertificateAuthorityState WithLastBackupUtc(
            DateTime lastBackupUtc)
        {
            EnsureUtc(lastBackupUtc, nameof(lastBackupUtc));
            if (LastBackupUtc.HasValue
                && lastBackupUtc < LastBackupUtc.Value)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastBackupUtc),
                    "Last backup time must not decrease.");
            }

            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                Role,
                RotationPhase,
                TrustRevision,
                PkiRevision,
                CurrentSlot,
                RotationId,
                PublishedUtc,
                ActivationNotBeforeUtc,
                ActivatedUtc,
                RetirementNotBeforeUtc,
                CurrentAuthority,
                OtherAuthority,
                TrustRevision,
                lastBackupUtc);
        }

        internal CertificateAuthorityState Publish(
            Guid rotationId,
            DateTime publishedUtc,
            CertificateAuthorityLiveState nextAuthority)
        {
            if (RotationPhase != CertificateAuthorityRotationPhase.Stable
                || rotationId == Guid.Empty
                || nextAuthority == null
                || nextAuthority.Role != CertificateAuthorityLiveRole.Next
                || nextAuthority.Slot == CurrentSlot
                || nextAuthority.CrlNumber != 1
                || TrustRevision == ulong.MaxValue
                || PkiRevision == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The CA rotation cannot be published from the current state.");
            }

            EnsureUtc(publishedUtc, nameof(publishedUtc));
            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                Role,
                CertificateAuthorityRotationPhase.Published,
                TrustRevision + 1,
                PkiRevision + 1,
                CurrentSlot,
                rotationId,
                publishedUtc,
                publishedUtc.Add(MinimumRotationPeriod),
                null,
                null,
                CurrentAuthority,
                nextAuthority,
                LastBackupTrustRevision,
                LastBackupUtc);
        }

        internal CertificateAuthorityState CancelPublished(Guid rotationId)
        {
            if (RotationPhase
                    != CertificateAuthorityRotationPhase.Published
                || !RotationId.HasValue
                || RotationId.Value != rotationId
                || TrustRevision == ulong.MaxValue
                || PkiRevision == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The published CA rotation cannot be cancelled.");
            }

            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                Role,
                CertificateAuthorityRotationPhase.Stable,
                TrustRevision + 1,
                PkiRevision + 1,
                CurrentSlot,
                null,
                null,
                null,
                null,
                null,
                CurrentAuthority,
                null,
                LastBackupTrustRevision,
                LastBackupUtc);
        }

        internal bool IsCurrentRevisionBackedUp =>
            LastBackupTrustRevision.HasValue
            && LastBackupTrustRevision.Value >= TrustRevision;

        private static void ValidateRotation(
            CertificateAuthorityRotationPhase phase,
            CertificateAuthorityLiveState currentAuthority,
            CertificateAuthorityLiveState otherAuthority,
            Guid? rotationId,
            DateTime? publishedUtc,
            DateTime? activationNotBeforeUtc,
            DateTime? activatedUtc,
            DateTime? retirementNotBeforeUtc)
        {
            if (phase == CertificateAuthorityRotationPhase.Stable)
            {
                if (otherAuthority != null
                    || rotationId.HasValue
                    || publishedUtc.HasValue
                    || activationNotBeforeUtc.HasValue
                    || activatedUtc.HasValue
                    || retirementNotBeforeUtc.HasValue)
                {
                    throw new ArgumentException(
                        "STABLE CA rotation state is inconsistent.");
                }

                return;
            }

            if (otherAuthority == null
                || otherAuthority.Slot == currentAuthority.Slot
                || otherAuthority.CaSerialNumber
                    == currentAuthority.CaSerialNumber
                || !rotationId.HasValue
                || rotationId.Value == Guid.Empty
                || !publishedUtc.HasValue
                || !activationNotBeforeUtc.HasValue
                || activationNotBeforeUtc.Value
                    < publishedUtc.Value.Add(MinimumRotationPeriod))
            {
                throw new ArgumentException(
                    "Rotating CA state is missing required publication data.");
            }

            byte[] currentSpki = currentAuthority.GetCaSpkiSha256();
            byte[] otherSpki = otherAuthority.GetCaSpkiSha256();
            try
            {
                if (FixedTimeEquals(currentSpki, otherSpki))
                {
                    throw new ArgumentException(
                        "Live CA authorities must use different keys.");
                }
            }
            finally
            {
                Array.Clear(currentSpki, 0, currentSpki.Length);
                Array.Clear(otherSpki, 0, otherSpki.Length);
            }

            if (phase == CertificateAuthorityRotationPhase.Published)
            {
                if (otherAuthority.Role
                        != CertificateAuthorityLiveRole.Next
                    || activatedUtc.HasValue
                    || retirementNotBeforeUtc.HasValue)
                {
                    throw new ArgumentException(
                        "PUBLISHED CA rotation state is inconsistent.");
                }

                return;
            }

            if (otherAuthority.Role
                    != CertificateAuthorityLiveRole.Retiring
                || !activatedUtc.HasValue
                || !retirementNotBeforeUtc.HasValue
                || activatedUtc.Value < activationNotBeforeUtc.Value
                || retirementNotBeforeUtc.Value
                    < activatedUtc.Value.Add(MinimumRotationPeriod))
            {
                throw new ArgumentException(
                    "ACTIVATED CA rotation state is inconsistent.");
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
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

        private static void EnsureOptionalUtc(
            DateTime? value,
            string parameterName)
        {
            if (value.HasValue)
            {
                EnsureUtc(value.Value, parameterName);
            }
        }

        internal static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA state timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }
        }
    }
}
