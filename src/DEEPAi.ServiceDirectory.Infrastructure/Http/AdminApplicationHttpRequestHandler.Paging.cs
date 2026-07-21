using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed partial class AdminApplicationHttpRequestHandler
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        public AdminHandlerResult<AdminServerServicesResponse> GetServices(
            AdminServicesQuery query)
        {
            ThrowIfDisposed();
            if (query == null)
            {
                return Failure<AdminServerServicesResponse>(
                    AdminServerErrorCode.BadRequest);
            }

            DirectorySnapshot snapshot;
            if (!_stateCoordinator.TryGetReadySnapshot(out snapshot))
            {
                return Failure<AdminServerServicesResponse>(
                    AdminServerErrorCode.Internal);
            }

            List<ServiceRecord> records = snapshot.Records.Values
                .Where(record => query.IncludeDeleted || !record.Deleted)
                .OrderBy(
                    record => record.Definition.ProductCode.Value,
                    StringComparer.Ordinal)
                .ToList();
            byte[] fingerprint = ComputeServicesFingerprint(
                snapshot,
                records,
                query.IncludeDeleted);
            try
            {
                int offset;
                if (!TryResolveOffset(
                        query.Cursor,
                        AdminCursorKind.Services,
                        query.IncludeDeleted,
                        fingerprint,
                        records.Count,
                        out offset))
                {
                    return Failure<AdminServerServicesResponse>(
                        AdminServerErrorCode.Conflict);
                }

                int maximumCount = Math.Min(
                    query.PageSize,
                    records.Count - offset);
                var items = new List<AdminServerServiceItem>(maximumCount);
                for (int index = 0; index < maximumCount; index++)
                {
                    items.Add(ToServiceItem(records[offset + index]));
                }

                AdminServerServicesResponse response;
                if (!TryCreateCanonicalPage(
                        items,
                        records.Count,
                        count => CreateNextCursor(
                            AdminCursorKind.Services,
                            query.IncludeDeleted,
                            offset,
                            count,
                            records.Count,
                            fingerprint),
                        (pageItems, totalCount, nextCursor) =>
                            new AdminServerServicesResponse(
                                pageItems,
                                totalCount,
                                nextCursor),
                        AdminServerResponseXmlCodec
                            .SerializeServicesResponse,
                        out response))
                {
                    return Failure<AdminServerServicesResponse>(
                        AdminServerErrorCode.Internal);
                }

                return AdminHandlerResult<AdminServerServicesResponse>
                    .Success(response);
            }
            finally
            {
                Array.Clear(fingerprint, 0, fingerprint.Length);
            }
        }

        private bool TryResolveOffset(
            string cursor,
            AdminCursorKind kind,
            bool includeDeleted,
            byte[] fingerprint,
            int totalCount,
            out int offset)
        {
            offset = 0;
            if (cursor == null)
            {
                return true;
            }

            return _cursorCodec.TryRead(
                    cursor,
                    kind,
                    includeDeleted,
                    fingerprint,
                    out offset)
                && offset < totalCount;
        }

        private string CreateNextCursor(
            AdminCursorKind kind,
            bool includeDeleted,
            int offset,
            int count,
            int totalCount,
            byte[] fingerprint)
        {
            int nextOffset = checked(offset + count);
            return nextOffset < totalCount
                ? _cursorCodec.Create(
                    kind,
                    includeDeleted,
                    nextOffset,
                    fingerprint)
                : null;
        }

        private static bool TryCreateCanonicalPage<TItem, TResponse>(
            IReadOnlyList<TItem> maximumItems,
            int totalCount,
            Func<int, string> nextCursorFactory,
            Func<IReadOnlyList<TItem>, int, string, TResponse>
                responseFactory,
            Func<TResponse, byte[]> serializer,
            out TResponse response)
            where TItem : class
            where TResponse : class
        {
            if (maximumItems == null)
            {
                throw new ArgumentNullException(nameof(maximumItems));
            }

            if (nextCursorFactory == null)
            {
                throw new ArgumentNullException(nameof(nextCursorFactory));
            }

            if (responseFactory == null)
            {
                throw new ArgumentNullException(nameof(responseFactory));
            }

            if (serializer == null)
            {
                throw new ArgumentNullException(nameof(serializer));
            }

            if (maximumItems.Count == 0)
            {
                response = responseFactory(
                    new TItem[0],
                    totalCount,
                    null);
                return IsCanonicalResponseWithinBodyLimit(
                    response,
                    serializer);
            }

            TResponse maximumResponse = CreatePageResponse(
                maximumItems,
                maximumItems.Count,
                totalCount,
                nextCursorFactory,
                responseFactory);
            if (IsCanonicalResponseWithinBodyLimit(
                    maximumResponse,
                    serializer))
            {
                response = maximumResponse;
                return true;
            }

            // The maximum candidate was tried first because the last page
            // omits NextCursor. If it does not fit, every smaller candidate
            // has the same fixed-length opaque cursor, so canonical byte size
            // increases strictly with each additional item.
            int lowerCount = 1;
            int upperCount = maximumItems.Count - 1;
            TResponse largestResponse = null;
            while (lowerCount <= upperCount)
            {
                int candidateCount = lowerCount
                    + ((upperCount - lowerCount) / 2);
                TResponse candidate = CreatePageResponse(
                    maximumItems,
                    candidateCount,
                    totalCount,
                    nextCursorFactory,
                    responseFactory);
                if (IsCanonicalResponseWithinBodyLimit(
                        candidate,
                        serializer))
                {
                    largestResponse = candidate;
                    lowerCount = candidateCount + 1;
                }
                else
                {
                    upperCount = candidateCount - 1;
                }
            }

            // A nonempty snapshot cannot be represented by an empty page:
            // its cursor would not advance and the response model rejects
            // that shape. Let the caller map a one-item serialization failure
            // to the existing INTERNAL contract.
            response = largestResponse;
            return response != null;
        }

        private static TResponse CreatePageResponse<TItem, TResponse>(
            IReadOnlyList<TItem> maximumItems,
            int count,
            int totalCount,
            Func<int, string> nextCursorFactory,
            Func<IReadOnlyList<TItem>, int, string, TResponse>
                responseFactory)
            where TItem : class
            where TResponse : class
        {
            var pageItems = new List<TItem>(count);
            for (int index = 0; index < count; index++)
            {
                pageItems.Add(maximumItems[index]);
            }

            return responseFactory(
                pageItems,
                totalCount,
                nextCursorFactory(count));
        }

        private static bool IsCanonicalResponseWithinBodyLimit<TResponse>(
            TResponse response,
            Func<TResponse, byte[]> serializer)
            where TResponse : class
        {
            try
            {
                byte[] body = serializer(response);
                return body != null
                    && body.Length <= AdminApiContract.MaximumBodyBytes;
            }
            catch (AdminProtocolException)
            {
                return false;
            }
        }

        private static AdminServerServiceItem ToServiceItem(
            ServiceRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            return new AdminServerServiceItem(
                ToServiceDefinition(record.Definition),
                record.LastModifiedUtc,
                record.Deleted,
                record.DeletedUtc);
        }

        private static AdminServerServiceDefinition ToServiceDefinition(
            ServiceDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            return new AdminServerServiceDefinition(
                definition.Name,
                definition.ProductCode.Value,
                definition.ServiceHostName,
                definition.ServiceIpv4Address,
                definition.Port);
        }

        private static byte[] ComputeServicesFingerprint(
            DirectorySnapshot snapshot,
            IReadOnlyList<ServiceRecord> records,
            bool includeDeleted)
        {
            using (var writer = new AdminFingerprintWriter())
            {
                writer.WriteString("admin-services-cursor-v1");
                writer.WriteBoolean(includeDeleted);
                writer.WriteUInt64(snapshot.LogicalClock);
                writer.WriteInt32(records.Count);
                for (int index = 0; index < records.Count; index++)
                {
                    writer.WriteServiceRecord(records[index]);
                }

                return writer.ComputeHash();
            }
        }

        private sealed class AdminFingerprintWriter : IDisposable
        {
            private readonly MemoryStream _stream = new MemoryStream();

            internal void WriteServiceRecord(ServiceRecord record)
            {
                if (record == null)
                {
                    throw new ArgumentNullException(nameof(record));
                }

                WriteServiceDefinition(record.Definition);
                WriteDateTime(record.LastModifiedUtc);
                WriteBoolean(record.Deleted);
                WriteBoolean(record.DeletedUtc.HasValue);
                if (record.DeletedUtc.HasValue)
                {
                    WriteDateTime(record.DeletedUtc.Value);
                }

                WriteUInt64(record.LogicalVersion);
                WriteGuid(record.OriginInstanceId);
            }

            internal void WriteServiceDefinition(ServiceDefinition definition)
            {
                if (definition == null)
                {
                    throw new ArgumentNullException(nameof(definition));
                }

                WriteString(definition.Name);
                WriteString(definition.ProductCode.Value);
                WriteString(definition.ServiceHostName);
                WriteString(definition.ServiceIpv4Address);
                WriteInt32(definition.Port);
            }

            internal void WriteString(string value)
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                byte[] bytes = StrictUtf8.GetBytes(value);
                try
                {
                    WriteInt32(bytes.Length);
                    _stream.Write(bytes, 0, bytes.Length);
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                }
            }

            internal void WriteGuid(Guid value)
            {
                if (value == Guid.Empty)
                {
                    throw new ArgumentException(
                        "Fingerprint GUID values cannot be empty.",
                        nameof(value));
                }

                WriteString(value.ToString("D"));
            }

            internal void WriteDateTime(DateTime value)
            {
                if (value.Kind != DateTimeKind.Utc)
                {
                    throw new ArgumentException(
                        "Fingerprint timestamps must be UTC.",
                        nameof(value));
                }

                WriteInt64(value.Ticks);
            }

            internal void WriteBoolean(bool value)
            {
                _stream.WriteByte(value ? (byte)1 : (byte)0);
            }

            internal void WriteInt32(int value)
            {
                var bytes = new byte[4];
                bytes[0] = (byte)(value >> 24);
                bytes[1] = (byte)(value >> 16);
                bytes[2] = (byte)(value >> 8);
                bytes[3] = (byte)value;
                _stream.Write(bytes, 0, bytes.Length);
            }

            internal void WriteUInt64(ulong value)
            {
                var bytes = new byte[8];
                for (int index = 7; index >= 0; index--)
                {
                    bytes[index] = (byte)value;
                    value >>= 8;
                }

                _stream.Write(bytes, 0, bytes.Length);
            }

            internal void WriteInt64(long value)
            {
                WriteUInt64(unchecked((ulong)value));
            }

            internal byte[] ComputeHash()
            {
                byte[] value = _stream.ToArray();
                try
                {
                    using (SHA256 sha = SHA256.Create())
                    {
                        return sha.ComputeHash(value);
                    }
                }
                finally
                {
                    Array.Clear(value, 0, value.Length);
                }
            }

            public void Dispose()
            {
                byte[] buffer = _stream.GetBuffer();
                Array.Clear(buffer, 0, buffer.Length);
                _stream.Dispose();
            }
        }
    }
}
