using System;
using System.Globalization;
using System.Xml.Linq;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static partial class AdminServerXmlCodec
    {
        public static void ParsePrepareCaRotationRequest(byte[] body)
        {
            LoadSchemaValidatedRoot(body, "PrepareCaRotation");
        }

        public static AdminCancelCaRotationRequest
            ParseCancelCaRotationRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "CancelCaRotation");
            return new AdminCancelCaRotationRequest(
                ReadRequiredCanonicalGuid(root, "RotationId"));
        }
    }

    public static partial class AdminServerResponseXmlCodec
    {
        public static byte[] SerializeCaRotationResponse(
            AdminServerCaRotationResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var rotation = new XElement(
                Namespace + "CaRotation",
                new XElement(
                    Namespace + "Phase",
                    FormatRotationPhase(response.Phase)),
                new XElement(
                    Namespace + "TrustRevision",
                    response.TrustRevision.ToString(
                        CultureInfo.InvariantCulture)));
            AddOptionalGuid(rotation, "RotationId", response.RotationId);
            AddOptionalUtc(
                rotation,
                "PublishedUtc",
                response.PublishedUtc);
            AddOptionalUtc(
                rotation,
                "ActivationNotBeforeUtc",
                response.ActivationNotBeforeUtc);
            AddOptionalUtc(rotation, "ActivatedUtc", response.ActivatedUtc);
            AddOptionalUtc(
                rotation,
                "RetirementNotBeforeUtc",
                response.RetirementNotBeforeUtc);
            rotation.Add(CreateAuthorityElement(
                "CurrentAuthority",
                response.CurrentAuthority));
            if (response.OtherAuthority != null)
            {
                rotation.Add(CreateAuthorityElement(
                    "OtherAuthority",
                    response.OtherAuthority));
            }

            rotation.Add(
                new XElement(
                    Namespace + "CurrentRevisionBackupReady",
                    FormatBoolean(response.CurrentRevisionBackupReady)),
                new XElement(
                    Namespace + "PeerReadiness",
                    FormatReadiness(response.PeerReadiness)),
                new XElement(
                    Namespace + "DirectoryLeafReadiness",
                    FormatReadiness(response.DirectoryLeafReadiness)),
                new XElement(
                    Namespace + "RetiringLeafCount",
                    response.RetiringLeafCount.ToString(
                        CultureInfo.InvariantCulture)),
                new XElement(
                    Namespace + "ActivationReady",
                    FormatBoolean(response.ActivationReady)),
                new XElement(
                    Namespace + "CompletionReady",
                    FormatBoolean(response.CompletionReady)));

            XElement root = CreateSuccessEnvelope();
            root.Add(rotation);
            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseCaRotationResponse(body));
        }

        private static XElement CreateAuthorityElement(
            string name,
            AdminCaRotationAuthority authority)
        {
            return new XElement(
                Namespace + name,
                new XElement(
                    Namespace + "Role",
                    FormatAuthorityRole(authority.Role)),
                new XElement(
                    Namespace + "CaSerialNumber",
                    authority.CaSerialNumber),
                new XElement(
                    Namespace + "CaSpkiSha256",
                    authority.CaSpkiSha256),
                new XElement(
                    Namespace + "NotBeforeUtc",
                    FormatUtc(authority.NotBeforeUtc)),
                new XElement(
                    Namespace + "NotAfterUtc",
                    FormatUtc(authority.NotAfterUtc)),
                new XElement(
                    Namespace + "CrlNumber",
                    authority.CrlNumber.ToString(
                        CultureInfo.InvariantCulture)));
        }

        private static string FormatRotationPhase(
            AdminCaRotationPhase phase)
        {
            switch (phase)
            {
                case AdminCaRotationPhase.Stable:
                    return "STABLE";
                case AdminCaRotationPhase.Published:
                    return "PUBLISHED";
                case AdminCaRotationPhase.Activated:
                    return "ACTIVATED";
                default:
                    throw new AdminProtocolException(
                        "Admin CA rotation phase is invalid.");
            }
        }

        private static string FormatAuthorityRole(
            AdminCaRotationAuthorityRole role)
        {
            switch (role)
            {
                case AdminCaRotationAuthorityRole.Current:
                    return "CURRENT";
                case AdminCaRotationAuthorityRole.Next:
                    return "NEXT";
                case AdminCaRotationAuthorityRole.Retiring:
                    return "RETIRING";
                default:
                    throw new AdminProtocolException(
                        "Admin CA rotation authority role is invalid.");
            }
        }

        private static string FormatReadiness(
            AdminCaRotationReadiness readiness)
        {
            switch (readiness)
            {
                case AdminCaRotationReadiness.Ready:
                    return "READY";
                case AdminCaRotationReadiness.NotReady:
                    return "NOT_READY";
                case AdminCaRotationReadiness.NotRequired:
                    return "NOT_REQUIRED";
                default:
                    throw new AdminProtocolException(
                        "Admin CA rotation readiness is invalid.");
            }
        }
    }

    public static partial class AdminXmlCodec
    {
        public static byte[] SerializePrepareCaRotation()
        {
            return Serialize(new XElement(
                Namespace + "PrepareCaRotation"));
        }

        public static byte[] SerializeCancelCaRotation(Guid rotationId)
        {
            if (rotationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Rotation ID must not be empty.",
                    nameof(rotationId));
            }

            return Serialize(new XElement(
                Namespace + "CancelCaRotation",
                new XElement(
                    Namespace + "RotationId",
                    rotationId.ToString("D").ToLowerInvariant())));
        }

        public static AdminResponse<AdminServerCaRotationResponse>
            ParseCaRotationResponse(byte[] body)
        {
            return ParseResponse(
                body,
                "CaRotation",
                (root, rotation) => new AdminServerCaRotationResponse(
                    ParseRotationPhase(
                        ReadRequiredString(rotation, "Phase")),
                    ReadRequiredUInt64(rotation, "TrustRevision"),
                    ReadOptionalGuid(rotation, "RotationId"),
                    ReadOptionalUtc(rotation, "PublishedUtc"),
                    ReadOptionalUtc(
                        rotation,
                        "ActivationNotBeforeUtc"),
                    ReadOptionalUtc(rotation, "ActivatedUtc"),
                    ReadOptionalUtc(
                        rotation,
                        "RetirementNotBeforeUtc"),
                    ParseAuthority(
                        ReadRequiredElement(
                            rotation,
                            "CurrentAuthority")),
                    ParseOptionalAuthority(rotation, "OtherAuthority"),
                    ReadRequiredBoolean(
                        rotation,
                        "CurrentRevisionBackupReady"),
                    ParseReadiness(ReadRequiredString(
                        rotation,
                        "PeerReadiness")),
                    ParseReadiness(ReadRequiredString(
                        rotation,
                        "DirectoryLeafReadiness")),
                    ReadRequiredNonNegativeInt(
                        rotation,
                        "RetiringLeafCount"),
                    ReadRequiredBoolean(rotation, "ActivationReady"),
                    ReadRequiredBoolean(rotation, "CompletionReady")));
        }

        private static AdminCaRotationAuthority ParseAuthority(
            XElement authority)
        {
            return new AdminCaRotationAuthority(
                ParseAuthorityRole(ReadRequiredString(authority, "Role")),
                ReadRequiredString(authority, "CaSerialNumber"),
                ReadRequiredString(authority, "CaSpkiSha256"),
                ReadRequiredUtc(authority, "NotBeforeUtc"),
                ReadRequiredUtc(authority, "NotAfterUtc"),
                ReadRequiredUInt64(authority, "CrlNumber"));
        }

        private static AdminCaRotationAuthority ParseOptionalAuthority(
            XElement parent,
            string name)
        {
            XElement authority = parent.Element(Namespace + name);
            return authority == null ? null : ParseAuthority(authority);
        }

        private static AdminCaRotationPhase ParseRotationPhase(string value)
        {
            switch (value)
            {
                case "STABLE":
                    return AdminCaRotationPhase.Stable;
                case "PUBLISHED":
                    return AdminCaRotationPhase.Published;
                case "ACTIVATED":
                    return AdminCaRotationPhase.Activated;
                default:
                    throw new AdminProtocolException(
                        "Admin CA rotation phase is invalid.");
            }
        }

        private static AdminCaRotationAuthorityRole ParseAuthorityRole(
            string value)
        {
            switch (value)
            {
                case "CURRENT":
                    return AdminCaRotationAuthorityRole.Current;
                case "NEXT":
                    return AdminCaRotationAuthorityRole.Next;
                case "RETIRING":
                    return AdminCaRotationAuthorityRole.Retiring;
                default:
                    throw new AdminProtocolException(
                        "Admin CA rotation authority role is invalid.");
            }
        }

        private static AdminCaRotationReadiness ParseReadiness(string value)
        {
            switch (value)
            {
                case "READY":
                    return AdminCaRotationReadiness.Ready;
                case "NOT_READY":
                    return AdminCaRotationReadiness.NotReady;
                case "NOT_REQUIRED":
                    return AdminCaRotationReadiness.NotRequired;
                default:
                    throw new AdminProtocolException(
                        "Admin CA rotation readiness is invalid.");
            }
        }
    }
}
