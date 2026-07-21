using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ExternalCertificateMutationLoggingTests
    {
        [TestMethod]
        public void RegistrationEventsFollowCommittedResultsAndSkipReplay()
        {
            var order = new List<string>();
            var inner = new RecordingCertificateService(order);
            var log = new RecordingRegistrationLogSink(order);
            var service = new LoggingExternalCertificateService(
                inner,
                log);
            ExternalRegistrationRequest request = CreateRequest();

            inner.NextStatus =
                ExternalRegistrationServiceStatus.Registered;
            service.Register(request, UtcNow);
            inner.NextStatus =
                ExternalRegistrationServiceStatus.Reregistered;
            service.Register(request, UtcNow);
            inner.NextStatus =
                ExternalRegistrationServiceStatus.Replayed;
            service.Register(request, UtcNow);

            CollectionAssert.AreEqual(
                new[]
                {
                    "commit:Registered",
                    "created:AB12",
                    "commit:Reregistered",
                    "updated:AB12",
                    "commit:Replayed"
                },
                order.ToArray());
        }

        private static readonly DateTime UtcNow = new DateTime(
            2026,
            7,
            21,
            1,
            2,
            3,
            DateTimeKind.Utc);

        private static ExternalRegistrationRequest CreateRequest()
        {
            return new ExternalRegistrationRequest(
                Guid.NewGuid(),
                "Service",
                "AB12",
                "service.internal",
                "10.20.30.40",
                21000,
                new byte[] { 0x30 });
        }

        private sealed class RecordingRegistrationLogSink
            : IExternalRegistrationLogSink
        {
            private readonly ICollection<string> _order;

            internal RecordingRegistrationLogSink(
                ICollection<string> order)
            {
                _order = order;
            }

            public void WriteCreated(ProductCode productCode)
            {
                _order.Add("created:" + productCode.Value);
            }

            public void WriteUpdated(ProductCode productCode)
            {
                _order.Add("updated:" + productCode.Value);
            }
        }

        private sealed class RecordingCertificateService
            : IExternalCertificateService
        {
            private readonly ICollection<string> _order;

            internal RecordingCertificateService(
                ICollection<string> order)
            {
                _order = order;
            }

            internal ExternalRegistrationServiceStatus NextStatus
            {
                get;
                set;
            }

            public ExternalTrustInfo GetTrustInfo()
            {
                throw new NotSupportedException();
            }

            public ExternalTrustSnapshot GetTrustSnapshot()
            {
                throw new NotSupportedException();
            }

            public byte[] GetCertificateRevocationList()
            {
                throw new NotSupportedException();
            }

            public byte[] GetCertificateRevocationList(
                string caSerialNumber)
            {
                throw new NotSupportedException();
            }

            public ExternalRegistrationServiceResult Register(
                ExternalRegistrationRequest request,
                DateTime utcNow)
            {
                _order.Add("commit:" + NextStatus);
                return ExternalRegistrationServiceResult.Success(
                    NextStatus,
                    new ExternalServiceItem(
                        request.Name,
                        request.ProductCode,
                        request.ServiceHostName,
                        request.ServiceIpv4Address,
                        request.Port,
                        utcNow),
                    new ExternalIssuedCertificate(
                        new byte[] { 0x30 },
                        new byte[] { 0x30 },
                        "1234567890ABCDEF1234567890ABCDEF",
                        utcNow.AddMinutes(-5),
                        utcNow.AddYears(1),
                        ExternalApiContract.IssuerCrlPathPrefix
                            + "01A4B5C6D7E8F90123456789ABCDEF01"));
            }

            public ExternalRegistrationServiceResult Renew(
                ExternalCertificateRenewalRequest request,
                DateTime utcNow)
            {
                throw new NotSupportedException();
            }
        }
    }
}
