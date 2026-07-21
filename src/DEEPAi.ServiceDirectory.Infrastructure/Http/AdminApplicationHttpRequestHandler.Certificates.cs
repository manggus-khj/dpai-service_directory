using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed partial class AdminApplicationHttpRequestHandler
    {
        public AdminHandlerResult<AdminServerCaStatusResponse> GetCaStatus()
        {
            ThrowIfDisposed();
            try
            {
                CertificateAuthorityStatus status =
                    _certificateAuthorityAdministration.GetStatus();
                if (status == null)
                {
                    return Failure<AdminServerCaStatusResponse>(
                        AdminServerErrorCode.Internal);
                }

                if (status.State ==
                    CertificateAuthorityOperationalState.NotProvisioned)
                {
                    return AdminHandlerResult<AdminServerCaStatusResponse>
                        .Success(new AdminServerCaStatusResponse(
                            AdminCaState.NotProvisioned));
                }

                byte[] spkiSha256 = status.GetCaSpkiSha256();
                try
                {
                    return AdminHandlerResult<AdminServerCaStatusResponse>
                        .Success(new AdminServerCaStatusResponse(
                            MapCaState(status.State),
                            MapCaRole(status.Role.Value),
                            status.SiteId.Value,
                            status.IssuerInstanceId.Value,
                            status.CaSerialNumber,
                            Convert.ToBase64String(spkiSha256),
                            status.NotBeforeUtc.Value,
                            status.NotAfterUtc.Value,
                            status.PkiRevision.Value,
                            status.CrlNumber.Value,
                            status.LastBackupUtc));
                }
                finally
                {
                    if (spkiSha256 != null)
                    {
                        Array.Clear(spkiSha256, 0, spkiSha256.Length);
                    }
                }
            }
            catch (Exception exception) when (IsPkiInfrastructureFailure(
                exception))
            {
                return Failure<AdminServerCaStatusResponse>(
                    AdminServerErrorCode.Internal);
            }
        }

        public AdminHandlerResult<AdminServerCaBackupResponse> CreateCaBackup(
            AdminCreateCaBackupRequest request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                return Failure<AdminServerCaBackupResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            try
            {
                CertificateAuthorityBackupResult backup =
                    _certificateAuthorityAdministration.CreateBackup(
                        request.Password,
                        GetUtcNow());
                byte[] sha256 = backup.GetSha256();
                try
                {
                    return AdminHandlerResult<AdminServerCaBackupResponse>
                        .Success(new AdminServerCaBackupResponse(
                            backup.FileName,
                            backup.CreatedUtc,
                            Convert.ToBase64String(sha256)));
                }
                finally
                {
                    Array.Clear(sha256, 0, sha256.Length);
                }
            }
            catch (ArgumentException)
            {
                return Failure<AdminServerCaBackupResponse>(
                    AdminServerErrorCode.BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Failure<AdminServerCaBackupResponse>(
                    AdminServerErrorCode.Conflict);
            }
            catch (Exception exception) when (IsPkiInfrastructureFailure(
                exception))
            {
                return Failure<AdminServerCaBackupResponse>(
                    AdminServerErrorCode.Internal);
            }
        }

        public AdminHandlerResult<AdminServerCertificatesResponse>
            GetCertificates(AdminCertificatesQuery query)
        {
            ThrowIfDisposed();
            if (query == null)
            {
                return Failure<AdminServerCertificatesResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            CertificateLedgerSnapshot snapshot;
            try
            {
                snapshot = _certificateAuthorityAdministration
                    .GetLedgerSnapshot();
            }
            catch (InvalidOperationException)
            {
                return Failure<AdminServerCertificatesResponse>(
                    AdminServerErrorCode.Conflict);
            }
            catch (Exception exception) when (IsPkiInfrastructureFailure(
                exception))
            {
                return Failure<AdminServerCertificatesResponse>(
                    AdminServerErrorCode.Internal);
            }

            if (snapshot == null)
            {
                return Failure<AdminServerCertificatesResponse>(
                    AdminServerErrorCode.Internal);
            }

            List<CertificateLedgerEntry> entries = snapshot.EntriesBySerial
                .Values
                .OrderBy(
                    entry => entry.SerialNumber.Hex,
                    StringComparer.Ordinal)
                .ToList();
            byte[] fingerprint = ComputeCertificateFingerprint(
                snapshot,
                entries);
            try
            {
                int offset;
                if (!TryResolveOffset(
                    query.Cursor,
                    AdminCursorKind.Certificates,
                    false,
                    fingerprint,
                    entries.Count,
                    out offset))
                {
                    return Failure<AdminServerCertificatesResponse>(
                        AdminServerErrorCode.Conflict);
                }

                int maximumCount = Math.Min(
                    query.PageSize,
                    entries.Count - offset);
                var items = new List<AdminServerCertificateItem>(
                    maximumCount);
                for (int index = 0; index < maximumCount; index++)
                {
                    items.Add(ToCertificateItem(entries[offset + index]));
                }

                AdminServerCertificatesResponse response;
                if (!TryCreateCanonicalPage(
                    items,
                    entries.Count,
                    count => CreateNextCursor(
                        AdminCursorKind.Certificates,
                        false,
                        offset,
                        count,
                        entries.Count,
                        fingerprint),
                    (pageItems, totalCount, nextCursor) =>
                        new AdminServerCertificatesResponse(
                            pageItems,
                            totalCount,
                            nextCursor),
                    AdminServerResponseXmlCodec.SerializeCertificatesResponse,
                    out response))
                {
                    return Failure<AdminServerCertificatesResponse>(
                        AdminServerErrorCode.Internal);
                }

                return AdminHandlerResult<AdminServerCertificatesResponse>
                    .Success(response);
            }
            finally
            {
                Array.Clear(fingerprint, 0, fingerprint.Length);
            }
        }

        public AdminHandlerResult<AdminServerCaRotationResponse>
            GetCaRotation()
        {
            ThrowIfDisposed();
            return ExecuteCaRotation(
                owner => owner.GetRotationStatus(GetUtcNow()));
        }

        public AdminHandlerResult<AdminServerCaRotationResponse>
            PrepareCaRotation()
        {
            ThrowIfDisposed();
            return ExecuteCaRotation(
                owner => owner.PrepareRotation(GetUtcNow()));
        }

        public AdminHandlerResult<AdminServerCaRotationResponse>
            CancelCaRotation(AdminCancelCaRotationRequest request)
        {
            ThrowIfDisposed();
            if (request == null)
            {
                return Failure<AdminServerCaRotationResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            return ExecuteCaRotation(owner => owner.CancelRotation(
                request.RotationId,
                GetUtcNow()));
        }

        private AdminHandlerResult<AdminServerCaRotationResponse>
            ExecuteCaRotation(
                Func<ICertificateAuthorityRotationAdministration,
                    AdminServerCaRotationResponse> action)
        {
            var owner = _certificateAuthorityAdministration
                as ICertificateAuthorityRotationAdministration;
            if (owner == null)
            {
                return Failure<AdminServerCaRotationResponse>(
                    AdminServerErrorCode.Conflict);
            }

            try
            {
                AdminServerCaRotationResponse response = action(owner);
                return response == null
                    ? Failure<AdminServerCaRotationResponse>(
                        AdminServerErrorCode.Internal)
                    : AdminHandlerResult<AdminServerCaRotationResponse>
                        .Success(response);
            }
            catch (ArgumentException)
            {
                return Failure<AdminServerCaRotationResponse>(
                    AdminServerErrorCode.BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Failure<AdminServerCaRotationResponse>(
                    AdminServerErrorCode.Conflict);
            }
            catch (Exception exception) when (IsPkiInfrastructureFailure(
                exception))
            {
                return Failure<AdminServerCaRotationResponse>(
                    AdminServerErrorCode.Internal);
            }
        }

        public AdminHandlerResult<AdminServerCertificateRevocationResponse>
            RevokeCertificate(
                string serialNumber,
                AdminRevokeCertificateRequest request)
        {
            ThrowIfDisposed();
            CertificateSerialNumber parsedSerial;
            if (request == null
                || !CertificateSerialNumber.TryCreate(
                    serialNumber,
                    out parsedSerial))
            {
                return Failure<AdminServerCertificateRevocationResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            try
            {
                CertificateRevocationResult result =
                    _certificateAuthorityAdministration.Revoke(
                        parsedSerial.Hex,
                        MapRevocationReason(request.Reason),
                        GetUtcNow());
                if (!result.Replayed)
                {
                    _synchronizationController.ScheduleDirectoryChanged();
                }

                return AdminHandlerResult<
                    AdminServerCertificateRevocationResponse>.Success(
                        new AdminServerCertificateRevocationResponse(
                            result.SerialNumber,
                            result.IssuerCaSerialNumber,
                            result.RevokedUtc,
                            MapRevocationReason(result.Reason),
                            result.PkiRevision,
                            result.CrlNumber,
                            result.Replayed));
            }
            catch (KeyNotFoundException)
            {
                return Failure<AdminServerCertificateRevocationResponse>(
                    AdminServerErrorCode.NotFound);
            }
            catch (ArgumentException)
            {
                return Failure<AdminServerCertificateRevocationResponse>(
                    AdminServerErrorCode.BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Failure<AdminServerCertificateRevocationResponse>(
                    AdminServerErrorCode.Conflict);
            }
            catch (Exception exception) when (IsPkiInfrastructureFailure(
                exception))
            {
                return Failure<AdminServerCertificateRevocationResponse>(
                    AdminServerErrorCode.Internal);
            }
        }

        private static AdminServerCertificateItem ToCertificateItem(
            CertificateLedgerEntry entry)
        {
            byte[] leafSha256 = entry.GetLeafCertificateSha256();
            try
            {
                return new AdminServerCertificateItem(
                    entry.SerialNumber.Hex,
                    entry.IssuerCaSerialNumber.Hex,
                    entry.ProductCode.Value,
                    entry.IssuanceKind == CertificateIssuanceKind.Registration
                        ? AdminCertificateIssuanceKind.Registration
                        : AdminCertificateIssuanceKind.Renewal,
                    entry.ServiceIdentity.ServiceHostName,
                    entry.ServiceIdentity.ServiceIpv4Address,
                    MapCertificateStatus(entry.Status),
                    entry.IssuedUtc,
                    entry.NotBeforeUtc,
                    entry.NotAfterUtc,
                    Convert.ToBase64String(leafSha256),
                    entry.ScheduledRevocationUtc,
                    entry.RevokedUtc,
                    entry.RevocationReason.HasValue
                        ? (AdminCertificateRevocationReason?)
                            MapRevocationReason(entry.RevocationReason.Value)
                        : null);
            }
            finally
            {
                Array.Clear(leafSha256, 0, leafSha256.Length);
            }
        }

        private static byte[] ComputeCertificateFingerprint(
            CertificateLedgerSnapshot snapshot,
            IReadOnlyList<CertificateLedgerEntry> entries)
        {
            using (var writer = new AdminFingerprintWriter())
            {
                writer.WriteString("admin-certificates-cursor-v1");
                writer.WriteUInt64(snapshot.PkiRevision);
                writer.WriteUInt64(snapshot.CrlNumber);
                writer.WriteInt32(entries.Count);
                for (int index = 0; index < entries.Count; index++)
                {
                    CertificateLedgerEntry entry = entries[index];
                    writer.WriteString(entry.SerialNumber.Hex);
                    writer.WriteString(entry.ProductCode.Value);
                    writer.WriteGuid(entry.IssuanceRequestId);
                    writer.WriteInt32((int)entry.IssuanceKind);
                    writer.WriteString(entry.ServiceIdentity.ServiceHostName);
                    writer.WriteString(
                        entry.ServiceIdentity.ServiceIpv4Address);
                    WriteHash(writer, entry.GetCsrSha256());
                    WriteHash(writer, entry.GetRequestPayloadSha256());
                    WriteHash(
                        writer,
                        entry.GetSubjectPublicKeyInfoSha256());
                    WriteHash(writer, entry.GetLeafCertificateSha256());
                    writer.WriteDateTime(entry.IssuedUtc);
                    writer.WriteDateTime(entry.NotBeforeUtc);
                    writer.WriteDateTime(entry.NotAfterUtc);
                    writer.WriteInt32((int)entry.Status);
                    WriteOptionalDateTime(
                        writer,
                        entry.ScheduledRevocationUtc);
                    WriteOptionalDateTime(writer, entry.RevokedUtc);
                    writer.WriteInt32(entry.RevocationReason.HasValue
                        ? (int)entry.RevocationReason.Value
                        : -1);
                }

                return writer.ComputeHash();
            }
        }

        private static void WriteHash(
            AdminFingerprintWriter writer,
            byte[] hash)
        {
            try
            {
                writer.WriteString(Convert.ToBase64String(hash));
            }
            finally
            {
                Array.Clear(hash, 0, hash.Length);
            }
        }

        private static void WriteOptionalDateTime(
            AdminFingerprintWriter writer,
            DateTime? value)
        {
            writer.WriteBoolean(value.HasValue);
            if (value.HasValue)
            {
                writer.WriteDateTime(value.Value);
            }
        }

        private static AdminCaState MapCaState(
            CertificateAuthorityOperationalState value)
        {
            switch (value)
            {
                case CertificateAuthorityOperationalState.BackupRequired:
                    return AdminCaState.BackupRequired;
                case CertificateAuthorityOperationalState.Ready:
                    return AdminCaState.Ready;
                default:
                    throw new InvalidDataException(
                        "Provisioned CA state is invalid.");
            }
        }

        private static AdminCaRole MapCaRole(
            CertificateAuthorityIssuerRole value)
        {
            switch (value)
            {
                case CertificateAuthorityIssuerRole.ActiveIssuer:
                    return AdminCaRole.ActiveIssuer;
                case CertificateAuthorityIssuerRole.Standby:
                    return AdminCaRole.Standby;
                default:
                    throw new InvalidDataException("CA role is invalid.");
            }
        }

        private static AdminCertificateStatus MapCertificateStatus(
            CertificateLedgerStatus value)
        {
            switch (value)
            {
                case CertificateLedgerStatus.Current:
                    return AdminCertificateStatus.Current;
                case CertificateLedgerStatus.Retiring:
                    return AdminCertificateStatus.Retiring;
                case CertificateLedgerStatus.Revoked:
                    return AdminCertificateStatus.Revoked;
                default:
                    throw new InvalidDataException(
                        "Certificate ledger status is invalid.");
            }
        }

        private static CertificateRevocationReason MapRevocationReason(
            AdminCertificateRevocationReason value)
        {
            switch (value)
            {
                case AdminCertificateRevocationReason.KeyCompromise:
                    return CertificateRevocationReason.KeyCompromise;
                case AdminCertificateRevocationReason.CaCompromise:
                    return CertificateRevocationReason.CaCompromise;
                case AdminCertificateRevocationReason.AffiliationChanged:
                    return CertificateRevocationReason.AffiliationChanged;
                case AdminCertificateRevocationReason.PrivilegeWithdrawn:
                    return CertificateRevocationReason.PrivilegeWithdrawn;
                case AdminCertificateRevocationReason.AaCompromise:
                    return CertificateRevocationReason.AaCompromise;
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static AdminCertificateRevocationReason MapRevocationReason(
            CertificateRevocationReason value)
        {
            switch (value)
            {
                case CertificateRevocationReason.KeyCompromise:
                    return AdminCertificateRevocationReason.KeyCompromise;
                case CertificateRevocationReason.CaCompromise:
                    return AdminCertificateRevocationReason.CaCompromise;
                case CertificateRevocationReason.AffiliationChanged:
                    return AdminCertificateRevocationReason
                        .AffiliationChanged;
                case CertificateRevocationReason.Superseded:
                    return AdminCertificateRevocationReason.Superseded;
                case CertificateRevocationReason.CessationOfOperation:
                    return AdminCertificateRevocationReason
                        .CessationOfOperation;
                case CertificateRevocationReason.PrivilegeWithdrawn:
                    return AdminCertificateRevocationReason
                        .PrivilegeWithdrawn;
                case CertificateRevocationReason.AaCompromise:
                    return AdminCertificateRevocationReason.AaCompromise;
                default:
                    throw new InvalidDataException(
                        "Certificate revocation reason is invalid.");
            }
        }

        private static bool IsPkiInfrastructureFailure(Exception exception)
        {
            return exception is IOException
                || exception is UnauthorizedAccessException
                || exception is SecurityException
                || exception is CryptographicException;
        }
    }
}
