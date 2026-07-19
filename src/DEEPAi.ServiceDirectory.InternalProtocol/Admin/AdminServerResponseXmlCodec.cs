using System;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static partial class AdminServerResponseXmlCodec
    {
        private const string UtcTimestampFormat =
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XNamespace Namespace =
            AdminApiContract.XmlNamespace;

        public static byte[] SerializeServicesResponse(
            AdminServerServicesResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var services = new XElement(Namespace + "Services");
            foreach (AdminServerServiceItem item in response.Items)
            {
                services.Add(CreateServiceItemElement(item));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(services);
            root.Add(
                new XElement(
                    Namespace + "TotalCount",
                    FormatInt32(response.TotalCount)));
            if (response.NextCursor != null)
            {
                root.Add(
                    new XElement(
                        Namespace + "NextCursor",
                        response.NextCursor));
            }

            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseServicesResponse(body));
        }

        public static byte[] SerializePendingResponse(
            AdminServerPendingResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var pendingItems = new XElement(Namespace + "PendingItems");
            foreach (AdminServerPendingItem item in response.Items)
            {
                pendingItems.Add(CreatePendingItemElement(item));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(pendingItems);
            root.Add(
                new XElement(
                    Namespace + "TotalCount",
                    FormatInt32(response.TotalCount)));
            if (response.NextCursor != null)
            {
                root.Add(
                    new XElement(
                        Namespace + "NextCursor",
                        response.NextCursor));
            }

            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParsePendingResponse(body));
        }

        public static byte[] SerializeSyncStatusResponse(
            AdminServerSyncStatusResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var syncStatus = new XElement(
                Namespace + "SyncStatus",
                new XElement(
                    Namespace + "Enabled",
                    FormatBoolean(response.Enabled)),
                new XElement(
                    Namespace + "PairingState",
                    response.PairingState.ToString()));
            AddOptional(syncStatus, "PeerEndpoint", response.PeerEndpoint);
            AddOptionalGuid(
                syncStatus,
                "PeerInstanceId",
                response.PeerInstanceId);
            AddOptionalUInt64(syncStatus, "KeyEpoch", response.KeyEpoch);
            AddOptionalGuid(syncStatus, "PairingId", response.PairingId);
            AddOptionalUtc(
                syncStatus,
                "PairingExpiresUtc",
                response.PairingExpiresUtc);
            AddOptionalInt32(
                syncStatus,
                "PairingRemainingSeconds",
                response.PairingRemainingSeconds);
            AddOptional(syncStatus, "Sas", response.Sas);
            AddOptionalBoolean(
                syncStatus,
                "LocalConfirmed",
                response.LocalConfirmed);
            AddOptionalBoolean(
                syncStatus,
                "RemoteConfirmed",
                response.RemoteConfirmed);
            AddOptionalUtc(
                syncStatus,
                "CommitExpiresUtc",
                response.CommitExpiresUtc);
            AddOptionalBoolean(
                syncStatus,
                "LocalCommitConfirmed",
                response.LocalCommitConfirmed);
            AddOptionalBoolean(
                syncStatus,
                "RemoteCommitConfirmed",
                response.RemoteCommitConfirmed);
            AddOptionalUtc(
                syncStatus,
                "LastSyncUtc",
                response.LastSyncUtc);
            syncStatus.Add(
                new XElement(
                    Namespace + "LastResult",
                    response.LastResult));
            AddOptionalInt64(
                syncStatus,
                "ClockSkewSeconds",
                response.ClockSkewSeconds);
            syncStatus.Add(
                new XElement(
                    Namespace + "LastPeerNotificationOperation",
                    FormatNotificationOperation(
                        response.LastPeerNotificationOperation)),
                new XElement(
                    Namespace + "LastPeerNotificationResult",
                    FormatNotificationResult(
                        response.LastPeerNotificationResult)));
            AddOptionalUtc(
                syncStatus,
                "LastPeerNotificationUtc",
                response.LastPeerNotificationUtc);

            XElement root = CreateSuccessEnvelope();
            root.Add(syncStatus);
            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseSyncResponse(body));
        }

        public static byte[] SerializeSyncDisableResponse(
            AdminServerSyncDisableResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(
                new XElement(
                    Namespace + "SyncDisableResult",
                    new XElement(
                        Namespace + "LocalPairingState",
                        response.LocalPairingState.ToString()),
                    new XElement(
                        Namespace + "PeerNotificationOperation",
                        FormatNotificationOperation(
                            response.PeerNotificationOperation)),
                    new XElement(
                        Namespace + "PeerNotificationResult",
                        FormatNotificationResult(
                            response.PeerNotificationResult)),
                    new XElement(
                        Namespace + "PeerNotificationUtc",
                        FormatUtc(response.PeerNotificationUtc))));

            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseSyncDisableResponse(body));
        }

        public static byte[] SerializeLoggingResponse(
            AdminServerLoggingResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            XElement root = CreateSuccessEnvelope();
            root.Add(
                new XElement(
                    Namespace + "LoggingSettings",
                    new XElement(
                        Namespace + "LogRetentionDays",
                        FormatInt32(response.LogRetentionDays))));

            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseLoggingResponse(body));
        }

        public static byte[] SerializeUnitResponse(
            AdminServerUnitResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return SerializeAndValidate(
                CreateSuccessEnvelope(),
                body => AdminXmlCodec.ParseUnitResponse(body));
        }

        public static byte[] SerializeErrorResponse(
            AdminServerErrorResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var root = new XElement(
                Namespace + "Response",
                new XElement(Namespace + "Result", "ERROR"),
                new XElement(
                    Namespace + "Code",
                    FormatInt32(response.NumericCode)),
                new XElement(Namespace + "Message", response.Message));

            return SerializeAndValidate(
                root,
                body => AdminXmlCodec.ParseUnitResponse(body));
        }

        private static XElement CreateSuccessEnvelope()
        {
            return new XElement(
                Namespace + "Response",
                new XElement(Namespace + "Result", "OK"),
                new XElement(Namespace + "Code", "0"),
                new XElement(Namespace + "Message", string.Empty));
        }

        private static XElement CreateServiceItemElement(
            AdminServerServiceItem item)
        {
            XElement element = CreateServiceDefinitionElement(
                "Service",
                item.Definition);
            element.Add(
                new XElement(
                    Namespace + "LastModifiedUtc",
                    FormatUtc(item.LastModifiedUtc)),
                new XElement(
                    Namespace + "Deleted",
                    FormatBoolean(item.Deleted)));
            if (item.DeletedUtc.HasValue)
            {
                element.Add(
                    new XElement(
                        Namespace + "DeletedUtc",
                        FormatUtc(item.DeletedUtc.Value)));
            }

            return element;
        }

        private static XElement CreatePendingItemElement(
            AdminServerPendingItem item)
        {
            var element = new XElement(
                Namespace + "PendingItem",
                new XElement(
                    Namespace + "Id",
                    FormatGuid(item.Id)),
                new XElement(
                    Namespace + "Type",
                    item.Type == AdminPendingRequestType.New
                        ? "New"
                        : "Modify"),
                new XElement(
                    Namespace + "RequestedUtc",
                    FormatUtc(item.RequestedUtc)),
                new XElement(Namespace + "SourceIP", item.SourceIp),
                CreateServiceDefinitionElement(
                    "Requested",
                    item.Requested));
            if (item.Current != null)
            {
                element.Add(
                    CreateServiceDefinitionElement(
                        "Current",
                        item.Current));
            }

            return element;
        }

        private static XElement CreateServiceDefinitionElement(
            string elementName,
            AdminServerServiceDefinition definition)
        {
            return new XElement(
                Namespace + elementName,
                new XElement(Namespace + "Name", definition.Name),
                new XElement(
                    Namespace + "ProductCode",
                    definition.ProductCode),
                new XElement(
                    Namespace + "ServerAddress",
                    definition.ServerAddress),
                new XElement(
                    Namespace + "Port",
                    FormatInt32(definition.Port)));
        }

        private static void AddOptional(
            XElement parent,
            string name,
            string value)
        {
            if (value != null)
            {
                parent.Add(new XElement(Namespace + name, value));
            }
        }

        private static void AddOptionalGuid(
            XElement parent,
            string name,
            Guid? value)
        {
            if (value.HasValue)
            {
                parent.Add(
                    new XElement(
                        Namespace + name,
                        FormatGuid(value.Value)));
            }
        }

        private static void AddOptionalUtc(
            XElement parent,
            string name,
            DateTime? value)
        {
            if (value.HasValue)
            {
                parent.Add(
                    new XElement(
                        Namespace + name,
                        FormatUtc(value.Value)));
            }
        }

        private static void AddOptionalUInt64(
            XElement parent,
            string name,
            ulong? value)
        {
            if (value.HasValue)
            {
                parent.Add(
                    new XElement(
                        Namespace + name,
                        value.Value.ToString(
                            CultureInfo.InvariantCulture)));
            }
        }

        private static void AddOptionalInt32(
            XElement parent,
            string name,
            int? value)
        {
            if (value.HasValue)
            {
                parent.Add(
                    new XElement(
                        Namespace + name,
                        FormatInt32(value.Value)));
            }
        }

        private static void AddOptionalInt64(
            XElement parent,
            string name,
            long? value)
        {
            if (value.HasValue)
            {
                parent.Add(
                    new XElement(
                        Namespace + name,
                        value.Value.ToString(
                            CultureInfo.InvariantCulture)));
            }
        }

        private static void AddOptionalBoolean(
            XElement parent,
            string name,
            bool? value)
        {
            if (value.HasValue)
            {
                parent.Add(
                    new XElement(
                        Namespace + name,
                        FormatBoolean(value.Value)));
            }
        }

        private static string FormatGuid(Guid value)
        {
            if (value == Guid.Empty)
            {
                throw new AdminProtocolException(
                    "Admin response GUID values cannot be empty.");
            }

            return value.ToString("D").ToLowerInvariant();
        }

        private static string FormatUtc(DateTime value)
        {
            AdminServerResponseValidation.EnsureUtc(value, nameof(value));
            return value.ToString(
                UtcTimestampFormat,
                CultureInfo.InvariantCulture);
        }

        private static string FormatInt32(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatBoolean(bool value)
        {
            return value ? "true" : "false";
        }

        private static string FormatNotificationOperation(
            AdminPeerNotificationOperation operation)
        {
            switch (operation)
            {
                case AdminPeerNotificationOperation.None:
                    return "NONE";
                case AdminPeerNotificationOperation.Release:
                    return "RELEASE";
                case AdminPeerNotificationOperation.Revoke:
                    return "REVOKE";
                default:
                    throw new AdminProtocolException(
                        "The Admin notification operation is invalid.");
            }
        }

        private static string FormatNotificationResult(
            AdminPeerNotificationResult result)
        {
            switch (result)
            {
                case AdminPeerNotificationResult.NotRun:
                    return "NOT_RUN";
                case AdminPeerNotificationResult.Confirmed:
                    return "CONFIRMED";
                case AdminPeerNotificationResult.Unconfirmed:
                    return "UNCONFIRMED";
                case AdminPeerNotificationResult.NotRequired:
                    return "NOT_REQUIRED";
                default:
                    throw new AdminProtocolException(
                        "The Admin notification result is invalid.");
            }
        }

        private static byte[] SerializeAndValidate(
            XElement root,
            Action<byte[]> validator)
        {
            byte[] body = StrictUtf8.GetBytes(
                root.ToString(SaveOptions.DisableFormatting));
            if (body.Length > AdminApiContract.MaximumBodyBytes)
            {
                throw new AdminProtocolException(
                    "The Admin response exceeds the 16 KiB body limit.");
            }

            validator(body);
            return body;
        }
    }
}
