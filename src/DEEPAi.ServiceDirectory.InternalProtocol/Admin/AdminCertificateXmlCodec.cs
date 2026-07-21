using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static partial class AdminServerXmlCodec
    {
        public static AdminCreateCaBackupRequest
            ParseCreateCaBackupRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "CreateCaBackup");
            string password = ReadRequiredValue(root, "Password");
            int scalarCount = CountUnicodeScalars(password);
            int utf8Length;
            try
            {
                utf8Length = StrictUtf8.GetByteCount(password);
            }
            catch (EncoderFallbackException exception)
            {
                throw new AdminProtocolException(
                    "The CA backup password contains invalid Unicode.",
                    exception);
            }

            if (scalarCount < 12
                || scalarCount > 128
                || utf8Length > 512
                || ContainsControlCharacter(password))
            {
                throw new AdminProtocolException(
                    "The CA backup password does not satisfy the required bounds.");
            }

            return new AdminCreateCaBackupRequest(password);
        }

        public static AdminRevokeCertificateRequest
            ParseRevokeCertificateRequest(byte[] body)
        {
            XElement root = LoadSchemaValidatedRoot(
                body,
                "RevokeCertificate");
            return new AdminRevokeCertificateRequest(
                ParseOperatorReason(
                    ReadRequiredValue(root, "Reason")));
        }

        private static int CountUnicodeScalars(string value)
        {
            int count = 0;
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]))
                {
                    if (index + 1 >= value.Length
                        || !char.IsLowSurrogate(value[index + 1]))
                    {
                        return int.MaxValue;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(value[index]))
                {
                    return int.MaxValue;
                }

                count++;
            }

            return count;
        }

        private static bool ContainsControlCharacter(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static AdminCertificateRevocationReason
            ParseOperatorReason(string value)
        {
            switch (value)
            {
                case "KEY_COMPROMISE":
                    return AdminCertificateRevocationReason.KeyCompromise;
                case "CA_COMPROMISE":
                    return AdminCertificateRevocationReason.CaCompromise;
                case "AFFILIATION_CHANGED":
                    return AdminCertificateRevocationReason.AffiliationChanged;
                case "PRIVILEGE_WITHDRAWN":
                    return AdminCertificateRevocationReason.PrivilegeWithdrawn;
                case "AA_COMPROMISE":
                    return AdminCertificateRevocationReason.AaCompromise;
                default:
                    throw new AdminProtocolException(
                        "The certificate revocation reason is not allowed for an operator request.");
            }
        }
    }

    public static partial class AdminServerResponseXmlCodec
    {
        public static byte[] SerializeCaStatusResponse(
            AdminServerCaStatusResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var status = new XElement(
                Namespace + "CaStatus",
                new XElement(
                    Namespace + "State",
                    FormatCaState(response.State)));
            if (response.State != AdminCaState.NotProvisioned)
            {
                status.Add(
                    new XElement(
                        Namespace + "Role",
                        response.Role == AdminCaRole.ActiveIssuer
                            ? "ACTIVE_ISSUER"
                            : "STANDBY"),
                    new XElement(
                        Namespace + "SiteId",
                        FormatGuid(response.SiteId.Value)),
                    new XElement(
                        Namespace + "IssuerInstanceId",
                        FormatGuid(response.IssuerInstanceId.Value)),
                    new XElement(
                        Namespace + "CaSerialNumber",
                        response.CaSerialNumber),
                    new XElement(
                        Namespace + "CaSpkiSha256",
                        response.CaSpkiSha256),
                    new XElement(
                        Namespace + "NotBeforeUtc",
                        FormatUtc(response.NotBeforeUtc.Value)),
                    new XElement(
                        Namespace + "NotAfterUtc",
                        FormatUtc(response.NotAfterUtc.Value)),
                    new XElement(
                        Namespace + "PkiRevision",
                        response.PkiRevision.Value.ToString(
                            CultureInfo.InvariantCulture)),
                    new XElement(
                        Namespace + "CrlNumber",
                        response.CrlNumber.Value.ToString(
                            CultureInfo.InvariantCulture)));
                if (response.LastBackupUtc.HasValue)
                {
                    status.Add(new XElement(
                        Namespace + "LastBackupUtc",
                        FormatUtc(response.LastBackupUtc.Value)));
                }
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(status);
            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseCaStatusResponse(body));
        }

        public static byte[] SerializeCaBackupResponse(
            AdminServerCaBackupResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(new XElement(
                Namespace + "CaBackup",
                new XElement(Namespace + "FileName", response.FileName),
                new XElement(
                    Namespace + "CreatedUtc",
                    FormatUtc(response.CreatedUtc)),
                new XElement(Namespace + "Sha256", response.Sha256)));
            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseCaBackupResponse(body));
        }

        public static byte[] SerializeCertificatesResponse(
            AdminServerCertificatesResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var certificates = new XElement(Namespace + "Certificates");
            foreach (AdminServerCertificateItem item in response.Items)
            {
                var certificate = new XElement(
                    Namespace + "Certificate",
                    new XElement(
                        Namespace + "SerialNumber",
                        item.SerialNumber),
                    new XElement(
                        Namespace + "IssuerCaSerialNumber",
                        item.IssuerCaSerialNumber),
                    new XElement(
                        Namespace + "ProductCode",
                        item.ProductCode),
                    new XElement(
                        Namespace + "IssuanceKind",
                        item.IssuanceKind
                            == AdminCertificateIssuanceKind.Registration
                                ? "REGISTRATION"
                                : "RENEWAL"),
                    new XElement(
                        Namespace + "ServiceHostName",
                        item.ServiceHostName),
                    new XElement(
                        Namespace + "ServiceIpv4Address",
                        item.ServiceIpv4Address),
                    new XElement(
                        Namespace + "Status",
                        FormatCertificateStatus(item.Status)),
                    new XElement(
                        Namespace + "IssuedUtc",
                        FormatUtc(item.IssuedUtc)),
                    new XElement(
                        Namespace + "NotBeforeUtc",
                        FormatUtc(item.NotBeforeUtc)),
                    new XElement(
                        Namespace + "NotAfterUtc",
                        FormatUtc(item.NotAfterUtc)),
                    new XElement(
                        Namespace + "LeafSha256",
                        item.LeafSha256));
                if (item.ScheduledRevocationUtc.HasValue)
                {
                    certificate.Add(new XElement(
                        Namespace + "ScheduledRevocationUtc",
                        FormatUtc(item.ScheduledRevocationUtc.Value)));
                }

                if (item.RevokedUtc.HasValue)
                {
                    certificate.Add(
                        new XElement(
                            Namespace + "RevokedUtc",
                            FormatUtc(item.RevokedUtc.Value)),
                        new XElement(
                            Namespace + "RevocationReason",
                            FormatRevocationReason(
                                item.RevocationReason.Value)));
                }

                certificates.Add(certificate);
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(certificates);
            root.Add(new XElement(
                Namespace + "TotalCount",
                response.TotalCount.ToString(CultureInfo.InvariantCulture)));
            if (response.NextCursor != null)
            {
                root.Add(new XElement(
                    Namespace + "NextCursor",
                    response.NextCursor));
            }

            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseCertificatesResponse(body));
        }

        public static byte[] SerializeCertificateRevocationResponse(
            AdminServerCertificateRevocationResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(new XElement(
                Namespace + "CertificateRevocation",
                new XElement(
                    Namespace + "SerialNumber",
                    response.SerialNumber),
                new XElement(
                    Namespace + "IssuerCaSerialNumber",
                    response.IssuerCaSerialNumber),
                new XElement(
                    Namespace + "RevokedUtc",
                    FormatUtc(response.RevokedUtc)),
                new XElement(
                    Namespace + "Reason",
                    FormatRevocationReason(response.Reason)),
                new XElement(
                    Namespace + "PkiRevision",
                    response.PkiRevision.ToString(
                        CultureInfo.InvariantCulture)),
                new XElement(
                    Namespace + "CrlNumber",
                    response.CrlNumber.ToString(
                        CultureInfo.InvariantCulture)),
                new XElement(
                    Namespace + "Replayed",
                    response.Replayed ? "true" : "false")));
            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseCertificateRevocationResponse(body));
        }

        private static string FormatCaState(AdminCaState value)
        {
            switch (value)
            {
                case AdminCaState.NotProvisioned:
                    return "NOT_PROVISIONED";
                case AdminCaState.BackupRequired:
                    return "BACKUP_REQUIRED";
                case AdminCaState.Ready:
                    return "READY";
                default:
                    throw new AdminProtocolException(
                        "Admin CA state is invalid.");
            }
        }

        private static string FormatCertificateStatus(
            AdminCertificateStatus value)
        {
            switch (value)
            {
                case AdminCertificateStatus.Current:
                    return "CURRENT";
                case AdminCertificateStatus.Retiring:
                    return "RETIRING";
                case AdminCertificateStatus.Revoked:
                    return "REVOKED";
                default:
                    throw new AdminProtocolException(
                        "Admin certificate status is invalid.");
            }
        }

        private static string FormatRevocationReason(
            AdminCertificateRevocationReason value)
        {
            switch (value)
            {
                case AdminCertificateRevocationReason.KeyCompromise:
                    return "KEY_COMPROMISE";
                case AdminCertificateRevocationReason.CaCompromise:
                    return "CA_COMPROMISE";
                case AdminCertificateRevocationReason.AffiliationChanged:
                    return "AFFILIATION_CHANGED";
                case AdminCertificateRevocationReason.Superseded:
                    return "SUPERSEDED";
                case AdminCertificateRevocationReason.CessationOfOperation:
                    return "CESSATION_OF_OPERATION";
                case AdminCertificateRevocationReason.PrivilegeWithdrawn:
                    return "PRIVILEGE_WITHDRAWN";
                case AdminCertificateRevocationReason.AaCompromise:
                    return "AA_COMPROMISE";
                default:
                    throw new AdminProtocolException(
                        "Admin revocation reason is invalid.");
            }
        }
    }

    public static partial class AdminXmlCodec
    {
        public static byte[] SerializeCreateCaBackup(string password)
        {
            if (password == null)
            {
                throw new ArgumentNullException(nameof(password));
            }

            return Serialize(new XElement(
                Namespace + "CreateCaBackup",
                new XElement(Namespace + "Password", password)));
        }

        public static byte[] SerializeRevokeCertificate(
            AdminCertificateRevocationReason reason)
        {
            if (reason == AdminCertificateRevocationReason.Superseded
                || reason == AdminCertificateRevocationReason
                    .CessationOfOperation)
            {
                throw new ArgumentException(
                    "Automatic revocation reasons cannot be submitted by an operator.",
                    nameof(reason));
            }

            return Serialize(new XElement(
                Namespace + "RevokeCertificate",
                new XElement(
                    Namespace + "Reason",
                    FormatOperatorRevocationReason(reason))));
        }

        private static string FormatOperatorRevocationReason(
            AdminCertificateRevocationReason reason)
        {
            switch (reason)
            {
                case AdminCertificateRevocationReason.KeyCompromise:
                    return "KEY_COMPROMISE";
                case AdminCertificateRevocationReason.CaCompromise:
                    return "CA_COMPROMISE";
                case AdminCertificateRevocationReason.AffiliationChanged:
                    return "AFFILIATION_CHANGED";
                case AdminCertificateRevocationReason.PrivilegeWithdrawn:
                    return "PRIVILEGE_WITHDRAWN";
                case AdminCertificateRevocationReason.AaCompromise:
                    return "AA_COMPROMISE";
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        public static AdminResponse<AdminServerCaStatusResponse>
            ParseCaStatusResponse(byte[] body)
        {
            return ParseResponse(
                body,
                "CaStatus",
                (root, status) =>
                {
                    AdminCaState state = ParseCaState(
                        ReadRequiredString(status, "State"));
                    if (state == AdminCaState.NotProvisioned)
                    {
                        return new AdminServerCaStatusResponse(state);
                    }

                    return new AdminServerCaStatusResponse(
                        state,
                        ParseCaRole(ReadRequiredString(status, "Role")),
                        ReadRequiredGuid(status, "SiteId"),
                        ReadRequiredGuid(status, "IssuerInstanceId"),
                        ReadRequiredString(status, "CaSerialNumber"),
                        ReadRequiredString(status, "CaSpkiSha256"),
                        ReadRequiredUtc(status, "NotBeforeUtc"),
                        ReadRequiredUtc(status, "NotAfterUtc"),
                        ReadRequiredUInt64(status, "PkiRevision"),
                        ReadRequiredUInt64(status, "CrlNumber"),
                        ReadOptionalUtc(status, "LastBackupUtc"));
                });
        }

        public static AdminResponse<AdminServerCaBackupResponse>
            ParseCaBackupResponse(byte[] body)
        {
            return ParseResponse(
                body,
                "CaBackup",
                (root, backup) => new AdminServerCaBackupResponse(
                    ReadRequiredString(backup, "FileName"),
                    ReadRequiredUtc(backup, "CreatedUtc"),
                    ReadRequiredString(backup, "Sha256")));
        }

        public static AdminResponse<AdminServerCertificatesResponse>
            ParseCertificatesResponse(byte[] body)
        {
            return ParseResponse(
                body,
                "Certificates",
                (root, certificates) =>
                {
                    var items = new List<AdminServerCertificateItem>();
                    foreach (XElement item in certificates.Elements(
                        Namespace + "Certificate"))
                    {
                        items.Add(new AdminServerCertificateItem(
                            ReadRequiredString(item, "SerialNumber"),
                            ReadRequiredString(
                                item,
                                "IssuerCaSerialNumber"),
                            ReadRequiredString(item, "ProductCode"),
                            ParseIssuanceKind(ReadRequiredString(
                                item,
                                "IssuanceKind")),
                            ReadRequiredString(item, "ServiceHostName"),
                            ReadRequiredString(item, "ServiceIpv4Address"),
                            ParseCertificateStatus(ReadRequiredString(
                                item,
                                "Status")),
                            ReadRequiredUtc(item, "IssuedUtc"),
                            ReadRequiredUtc(item, "NotBeforeUtc"),
                            ReadRequiredUtc(item, "NotAfterUtc"),
                            ReadRequiredString(item, "LeafSha256"),
                            ReadOptionalUtc(
                                item,
                                "ScheduledRevocationUtc"),
                            ReadOptionalUtc(item, "RevokedUtc"),
                            ParseOptionalRevocationReason(item)));
                    }

                    return new AdminServerCertificatesResponse(
                        items.AsReadOnly(),
                        ReadRequiredNonNegativeInt(root, "TotalCount"),
                        ReadOptionalString(root, "NextCursor"));
                });
        }

        public static AdminResponse<AdminServerCertificateRevocationResponse>
            ParseCertificateRevocationResponse(byte[] body)
        {
            return ParseResponse(
                body,
                "CertificateRevocation",
                (root, revoked) =>
                    new AdminServerCertificateRevocationResponse(
                        ReadRequiredString(revoked, "SerialNumber"),
                        ReadRequiredString(
                            revoked,
                            "IssuerCaSerialNumber"),
                        ReadRequiredUtc(revoked, "RevokedUtc"),
                        ParseRevocationReason(ReadRequiredString(
                            revoked,
                            "Reason")),
                        ReadRequiredUInt64(revoked, "PkiRevision"),
                        ReadRequiredUInt64(revoked, "CrlNumber"),
                        ReadRequiredBoolean(revoked, "Replayed")));
        }

        private static ulong ReadRequiredUInt64(
            XElement parent,
            string name)
        {
            string text = ReadRequiredString(parent, name);
            ulong value;
            if (!ulong.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value)
                || value == 0
                || !StringComparer.Ordinal.Equals(
                    text,
                    value.ToString(CultureInfo.InvariantCulture)))
            {
                throw new AdminProtocolException(
                    "Admin XML unsigned integer is invalid: "
                    + name
                    + ".");
            }

            return value;
        }

        private static AdminCaState ParseCaState(string value)
        {
            switch (value)
            {
                case "NOT_PROVISIONED":
                    return AdminCaState.NotProvisioned;
                case "BACKUP_REQUIRED":
                    return AdminCaState.BackupRequired;
                case "READY":
                    return AdminCaState.Ready;
                default:
                    throw new AdminProtocolException(
                        "Admin CA state is invalid.");
            }
        }

        private static AdminCaRole ParseCaRole(string value)
        {
            if (StringComparer.Ordinal.Equals(value, "ACTIVE_ISSUER"))
            {
                return AdminCaRole.ActiveIssuer;
            }

            if (StringComparer.Ordinal.Equals(value, "STANDBY"))
            {
                return AdminCaRole.Standby;
            }

            throw new AdminProtocolException("Admin CA role is invalid.");
        }

        private static AdminCertificateIssuanceKind ParseIssuanceKind(
            string value)
        {
            if (StringComparer.Ordinal.Equals(value, "REGISTRATION"))
            {
                return AdminCertificateIssuanceKind.Registration;
            }

            if (StringComparer.Ordinal.Equals(value, "RENEWAL"))
            {
                return AdminCertificateIssuanceKind.Renewal;
            }

            throw new AdminProtocolException(
                "Admin certificate issuance kind is invalid.");
        }

        private static AdminCertificateStatus ParseCertificateStatus(
            string value)
        {
            switch (value)
            {
                case "CURRENT":
                    return AdminCertificateStatus.Current;
                case "RETIRING":
                    return AdminCertificateStatus.Retiring;
                case "REVOKED":
                    return AdminCertificateStatus.Revoked;
                default:
                    throw new AdminProtocolException(
                        "Admin certificate status is invalid.");
            }
        }

        private static AdminCertificateRevocationReason?
            ParseOptionalRevocationReason(XElement parent)
        {
            string value = ReadOptionalString(parent, "RevocationReason");
            return value == null
                ? (AdminCertificateRevocationReason?)null
                : ParseRevocationReason(value);
        }

        private static AdminCertificateRevocationReason
            ParseRevocationReason(string value)
        {
            switch (value)
            {
                case "KEY_COMPROMISE":
                    return AdminCertificateRevocationReason.KeyCompromise;
                case "CA_COMPROMISE":
                    return AdminCertificateRevocationReason.CaCompromise;
                case "AFFILIATION_CHANGED":
                    return AdminCertificateRevocationReason.AffiliationChanged;
                case "SUPERSEDED":
                    return AdminCertificateRevocationReason.Superseded;
                case "CESSATION_OF_OPERATION":
                    return AdminCertificateRevocationReason.CessationOfOperation;
                case "PRIVILEGE_WITHDRAWN":
                    return AdminCertificateRevocationReason.PrivilegeWithdrawn;
                case "AA_COMPROMISE":
                    return AdminCertificateRevocationReason.AaCompromise;
                default:
                    throw new AdminProtocolException(
                        "Admin certificate revocation reason is invalid.");
            }
        }
    }
}
