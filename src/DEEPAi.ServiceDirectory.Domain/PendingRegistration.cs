using System;
using System.Net;

namespace DEEPAi.ServiceDirectory.Domain
{
    public enum PendingRequestType
    {
        New,
        Modify
    }

    public sealed class PendingRegistration
    {
        public PendingRegistration(
            Guid id,
            PendingRequestType type,
            DateTime requestedUtc,
            string sourceIp,
            ServiceDefinition requested,
            DirectoryBaseRevision baseRevision)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Pending ID must not be empty.", nameof(id));
            }

            if (requestedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Requested time must use DateTimeKind.Utc.", nameof(requestedUtc));
            }

            if (requested == null)
            {
                throw new ArgumentNullException(nameof(requested));
            }

            if (baseRevision == null)
            {
                throw new ArgumentNullException(nameof(baseRevision));
            }

            IPAddress parsedSourceIp;
            if (!IPAddress.TryParse(sourceIp, out parsedSourceIp))
            {
                throw new ArgumentException("Source IP must be an IPv4 or IPv6 literal.", nameof(sourceIp));
            }

            bool validNewBase = type == PendingRequestType.New
                && baseRevision.Kind != BaseRevisionKind.Active;
            bool validModifyBase = type == PendingRequestType.Modify
                && baseRevision.Kind == BaseRevisionKind.Active;
            if (!validNewBase && !validModifyBase)
            {
                throw new ArgumentException("Pending request type does not match its base revision.", nameof(type));
            }

            if (baseRevision.Record != null
                && baseRevision.Record.Definition.ProductCode != requested.ProductCode)
            {
                throw new ArgumentException(
                    "The pending request and base revision must use the same product code.",
                    nameof(baseRevision));
            }

            Id = id;
            Type = type;
            RequestedUtc = requestedUtc;
            SourceIp = parsedSourceIp.ToString();
            Requested = requested;
            BaseRevision = baseRevision;
        }

        public Guid Id { get; }

        public PendingRequestType Type { get; }

        public DateTime RequestedUtc { get; }

        public string SourceIp { get; }

        public ServiceDefinition Requested { get; }

        public DirectoryBaseRevision BaseRevision { get; }
    }
}
