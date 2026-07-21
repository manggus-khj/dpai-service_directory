using System;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public enum AdminCaRotationPhase
    {
        Stable = 1,
        Published = 2,
        Activated = 3
    }

    public enum AdminCaRotationAuthorityRole
    {
        Current = 1,
        Next = 2,
        Retiring = 3
    }

    public enum AdminCaRotationReadiness
    {
        Ready = 1,
        NotReady = 2,
        NotRequired = 3
    }

    public sealed class AdminCancelCaRotationRequest
    {
        internal AdminCancelCaRotationRequest(Guid rotationId)
        {
            if (rotationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Rotation ID must not be empty.",
                    nameof(rotationId));
            }

            RotationId = rotationId;
        }

        public Guid RotationId { get; }
    }

    public sealed class AdminCaRotationAuthority
    {
        public AdminCaRotationAuthority(
            AdminCaRotationAuthorityRole role,
            string caSerialNumber,
            string caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong crlNumber)
        {
            if (!Enum.IsDefined(
                    typeof(AdminCaRotationAuthorityRole),
                    role)
                || !CertificateSerialNumber.TryCreate(
                    caSerialNumber,
                    out CertificateSerialNumber ignoredSerial)
                || !AdminCertificateModelValidation.IsCanonicalSha256(
                    caSpkiSha256)
                || notBeforeUtc.Kind != DateTimeKind.Utc
                || notAfterUtc.Kind != DateTimeKind.Utc
                || notAfterUtc <= notBeforeUtc
                || crlNumber == 0)
            {
                throw new ArgumentException(
                    "Admin CA rotation authority is invalid.");
            }

            Role = role;
            CaSerialNumber = caSerialNumber;
            CaSpkiSha256 = caSpkiSha256;
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            CrlNumber = crlNumber;
        }

        public AdminCaRotationAuthorityRole Role { get; }

        public string CaSerialNumber { get; }

        public string CaSpkiSha256 { get; }

        public DateTime NotBeforeUtc { get; }

        public DateTime NotAfterUtc { get; }

        public ulong CrlNumber { get; }
    }

    public sealed class AdminServerCaRotationResponse
    {
        private static readonly TimeSpan MinimumTransitionPeriod =
            TimeSpan.FromDays(30);

        public AdminServerCaRotationResponse(
            AdminCaRotationPhase phase,
            ulong trustRevision,
            Guid? rotationId,
            DateTime? publishedUtc,
            DateTime? activationNotBeforeUtc,
            DateTime? activatedUtc,
            DateTime? retirementNotBeforeUtc,
            AdminCaRotationAuthority currentAuthority,
            AdminCaRotationAuthority otherAuthority,
            bool currentRevisionBackupReady,
            AdminCaRotationReadiness peerReadiness,
            AdminCaRotationReadiness directoryLeafReadiness,
            int retiringLeafCount,
            bool activationReady,
            bool completionReady)
        {
            if (!Enum.IsDefined(typeof(AdminCaRotationPhase), phase)
                || trustRevision == 0
                || currentAuthority == null
                || currentAuthority.Role
                    != AdminCaRotationAuthorityRole.Current
                || !Enum.IsDefined(
                    typeof(AdminCaRotationReadiness),
                    peerReadiness)
                || !Enum.IsDefined(
                    typeof(AdminCaRotationReadiness),
                    directoryLeafReadiness)
                || directoryLeafReadiness
                    == AdminCaRotationReadiness.NotRequired
                || retiringLeafCount < 0)
            {
                throw new ArgumentException(
                    "Admin CA rotation response is invalid.");
            }

            RequireUtc(publishedUtc, nameof(publishedUtc));
            RequireUtc(
                activationNotBeforeUtc,
                nameof(activationNotBeforeUtc));
            RequireUtc(activatedUtc, nameof(activatedUtc));
            RequireUtc(
                retirementNotBeforeUtc,
                nameof(retirementNotBeforeUtc));
            ValidatePhase(
                phase,
                rotationId,
                publishedUtc,
                activationNotBeforeUtc,
                activatedUtc,
                retirementNotBeforeUtc,
                otherAuthority,
                retiringLeafCount,
                activationReady,
                completionReady);

            Phase = phase;
            TrustRevision = trustRevision;
            RotationId = rotationId;
            PublishedUtc = publishedUtc;
            ActivationNotBeforeUtc = activationNotBeforeUtc;
            ActivatedUtc = activatedUtc;
            RetirementNotBeforeUtc = retirementNotBeforeUtc;
            CurrentAuthority = currentAuthority;
            OtherAuthority = otherAuthority;
            CurrentRevisionBackupReady = currentRevisionBackupReady;
            PeerReadiness = peerReadiness;
            DirectoryLeafReadiness = directoryLeafReadiness;
            RetiringLeafCount = retiringLeafCount;
            ActivationReady = activationReady;
            CompletionReady = completionReady;
        }

        public AdminCaRotationPhase Phase { get; }

        public ulong TrustRevision { get; }

        public Guid? RotationId { get; }

        public DateTime? PublishedUtc { get; }

        public DateTime? ActivationNotBeforeUtc { get; }

        public DateTime? ActivatedUtc { get; }

        public DateTime? RetirementNotBeforeUtc { get; }

        public AdminCaRotationAuthority CurrentAuthority { get; }

        public AdminCaRotationAuthority OtherAuthority { get; }

        public bool CurrentRevisionBackupReady { get; }

        public AdminCaRotationReadiness PeerReadiness { get; }

        public AdminCaRotationReadiness DirectoryLeafReadiness { get; }

        public int RetiringLeafCount { get; }

        public bool ActivationReady { get; }

        public bool CompletionReady { get; }

        private static void ValidatePhase(
            AdminCaRotationPhase phase,
            Guid? rotationId,
            DateTime? publishedUtc,
            DateTime? activationNotBeforeUtc,
            DateTime? activatedUtc,
            DateTime? retirementNotBeforeUtc,
            AdminCaRotationAuthority otherAuthority,
            int retiringLeafCount,
            bool activationReady,
            bool completionReady)
        {
            if (phase == AdminCaRotationPhase.Stable)
            {
                if (rotationId.HasValue
                    || publishedUtc.HasValue
                    || activationNotBeforeUtc.HasValue
                    || activatedUtc.HasValue
                    || retirementNotBeforeUtc.HasValue
                    || otherAuthority != null
                    || retiringLeafCount != 0
                    || activationReady
                    || completionReady)
                {
                    throw new ArgumentException(
                        "A STABLE CA rotation response is inconsistent.");
                }

                return;
            }

            if (!rotationId.HasValue || rotationId.Value == Guid.Empty
                || !publishedUtc.HasValue
                || !activationNotBeforeUtc.HasValue
                || otherAuthority == null
                || activationNotBeforeUtc.Value
                    < publishedUtc.Value.Add(MinimumTransitionPeriod))
            {
                throw new ArgumentException(
                    "A rotating CA response is missing publication state.");
            }

            if (phase == AdminCaRotationPhase.Published)
            {
                if (otherAuthority.Role
                        != AdminCaRotationAuthorityRole.Next
                    || activatedUtc.HasValue
                    || retirementNotBeforeUtc.HasValue
                    || retiringLeafCount != 0
                    || completionReady)
                {
                    throw new ArgumentException(
                        "A PUBLISHED CA rotation response is inconsistent.");
                }

                return;
            }

            if (otherAuthority.Role
                    != AdminCaRotationAuthorityRole.Retiring
                || !activatedUtc.HasValue
                || !retirementNotBeforeUtc.HasValue
                || activatedUtc.Value < activationNotBeforeUtc.Value
                || retirementNotBeforeUtc.Value
                    < activatedUtc.Value.Add(MinimumTransitionPeriod)
                || activationReady)
            {
                throw new ArgumentException(
                    "An ACTIVATED CA rotation response is inconsistent.");
            }
        }

        private static void RequireUtc(DateTime? value, string name)
        {
            if (value.HasValue && value.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA rotation timestamps must use DateTimeKind.Utc.",
                    name);
            }
        }
    }
}
