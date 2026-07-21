using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public enum ExternalCaRotationPhase
    {
        Stable = 1,
        Published = 2,
        Activated = 3
    }

    public enum ExternalTrustAuthorityRole
    {
        Current = 1,
        Next = 2,
        Retiring = 3
    }

    public sealed class ExternalTrustAuthority
    {
        private readonly byte[] _caCertificate;
        private readonly byte[] _caSpkiSha256;

        public ExternalTrustAuthority(
            ExternalTrustAuthorityRole role,
            string caSerialNumber,
            byte[] caCertificate,
            byte[] caSpkiSha256,
            string crlUri,
            DateTime notBeforeUtc,
            DateTime notAfterUtc)
        {
            if (!Enum.IsDefined(typeof(ExternalTrustAuthorityRole), role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            Role = role;
            CaSerialNumber =
                ExternalApiModelValidation.RequireSerialNumber(
                    caSerialNumber,
                    nameof(caSerialNumber));
            _caCertificate = ExternalApiModelValidation.CloneRequiredBytes(
                caCertificate,
                0,
                ExternalApiContract.MaximumCaCertificateBytes,
                nameof(caCertificate));
            _caSpkiSha256 = ExternalApiModelValidation.CloneRequiredBytes(
                caSpkiSha256,
                ExternalApiContract.Sha256Bytes,
                0,
                nameof(caSpkiSha256));
            if (!ExternalApiContract.IsIssuerCrlPath(
                    crlUri,
                    CaSerialNumber))
            {
                throw new ArgumentException(
                    "The authority CRL URI must contain its exact CA serial number.",
                    nameof(crlUri));
            }

            CrlUri = crlUri;
            NotBeforeUtc = ExternalApiModelValidation.RequireUtc(
                notBeforeUtc,
                nameof(notBeforeUtc));
            NotAfterUtc = ExternalApiModelValidation.RequireUtc(
                notAfterUtc,
                nameof(notAfterUtc));
            if (NotAfterUtc <= NotBeforeUtc)
            {
                throw new ArgumentException(
                    "The authority validity interval is invalid.",
                    nameof(notAfterUtc));
            }
        }

        public ExternalTrustAuthorityRole Role { get; }

        public string CaSerialNumber { get; }

        public byte[] CaCertificate => (byte[])_caCertificate.Clone();

        public byte[] CaSpkiSha256 => (byte[])_caSpkiSha256.Clone();

        public string CrlUri { get; }

        public DateTime NotBeforeUtc { get; }

        public DateTime NotAfterUtc { get; }
    }

    public sealed class ExternalTrustBundle
    {
        public static readonly TimeSpan MinimumTransitionPeriod =
            TimeSpan.FromDays(30);

        private readonly IReadOnlyList<ExternalTrustAuthority> _authorities;

        public ExternalTrustBundle(
            Guid siteId,
            ulong trustRevision,
            Guid? rotationId,
            ExternalCaRotationPhase phase,
            DateTime? publishedUtc,
            DateTime? activationNotBeforeUtc,
            DateTime? activatedUtc,
            DateTime? retirementNotBeforeUtc,
            IEnumerable<ExternalTrustAuthority> authorities)
        {
            SiteId = ExternalApiModelValidation.RequireNonEmptyGuid(
                siteId,
                nameof(siteId));
            if (trustRevision == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trustRevision));
            }

            if (!Enum.IsDefined(typeof(ExternalCaRotationPhase), phase))
            {
                throw new ArgumentOutOfRangeException(nameof(phase));
            }

            var materialized = new List<ExternalTrustAuthority>(
                authorities ?? throw new ArgumentNullException(
                    nameof(authorities)));
            if (materialized.Count < 1 || materialized.Count > 2)
            {
                throw new ArgumentException(
                    "A trust bundle must contain one or two authorities.",
                    nameof(authorities));
            }

            foreach (ExternalTrustAuthority authority in materialized)
            {
                if (authority == null)
                {
                    throw new ArgumentException(
                        "A trust bundle cannot contain a null authority.",
                        nameof(authorities));
                }
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
                materialized);

            TrustRevision = trustRevision;
            RotationId = rotationId;
            Phase = phase;
            PublishedUtc = publishedUtc;
            ActivationNotBeforeUtc = activationNotBeforeUtc;
            ActivatedUtc = activatedUtc;
            RetirementNotBeforeUtc = retirementNotBeforeUtc;
            _authorities = new ReadOnlyCollection<ExternalTrustAuthority>(
                materialized);
        }

        public Guid SiteId { get; }

        public ulong TrustRevision { get; }

        public Guid? RotationId { get; }

        public ExternalCaRotationPhase Phase { get; }

        public DateTime? PublishedUtc { get; }

        public DateTime? ActivationNotBeforeUtc { get; }

        public DateTime? ActivatedUtc { get; }

        public DateTime? RetirementNotBeforeUtc { get; }

        public IReadOnlyList<ExternalTrustAuthority> Authorities =>
            _authorities;

        private static void ValidatePhase(
            ExternalCaRotationPhase phase,
            Guid? rotationId,
            DateTime? publishedUtc,
            DateTime? activationNotBeforeUtc,
            DateTime? activatedUtc,
            DateTime? retirementNotBeforeUtc,
            IReadOnlyList<ExternalTrustAuthority> authorities)
        {
            if (authorities[0].Role != ExternalTrustAuthorityRole.Current)
            {
                throw new ArgumentException(
                    "The first trust authority must be CURRENT.",
                    nameof(authorities));
            }

            if (phase == ExternalCaRotationPhase.Stable)
            {
                if (rotationId.HasValue
                    || publishedUtc.HasValue
                    || activationNotBeforeUtc.HasValue
                    || activatedUtc.HasValue
                    || retirementNotBeforeUtc.HasValue
                    || authorities.Count != 1)
                {
                    throw new ArgumentException(
                        "A STABLE trust bundle has inconsistent rotation fields.");
                }

                return;
            }

            if (!rotationId.HasValue || rotationId.Value == Guid.Empty
                || !publishedUtc.HasValue
                || !activationNotBeforeUtc.HasValue
                || authorities.Count != 2
                || activationNotBeforeUtc.Value
                    < publishedUtc.Value.Add(MinimumTransitionPeriod))
            {
                throw new ArgumentException(
                    "A rotating trust bundle is missing required publication state.");
            }

            if (phase == ExternalCaRotationPhase.Published)
            {
                if (activatedUtc.HasValue
                    || retirementNotBeforeUtc.HasValue
                    || authorities[1].Role
                        != ExternalTrustAuthorityRole.Next)
                {
                    throw new ArgumentException(
                        "A PUBLISHED trust bundle has inconsistent rotation fields.");
                }

                return;
            }

            if (!activatedUtc.HasValue
                || !retirementNotBeforeUtc.HasValue
                || activatedUtc.Value < activationNotBeforeUtc.Value
                || retirementNotBeforeUtc.Value
                    < activatedUtc.Value.Add(MinimumTransitionPeriod)
                || authorities[1].Role
                    != ExternalTrustAuthorityRole.Retiring)
            {
                throw new ArgumentException(
                    "An ACTIVATED trust bundle has inconsistent rotation fields.");
            }
        }

        private static void RequireUtc(DateTime? value, string name)
        {
            if (value.HasValue && value.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Trust bundle timestamps must use DateTimeKind.Utc.",
                    name);
            }
        }
    }
}
