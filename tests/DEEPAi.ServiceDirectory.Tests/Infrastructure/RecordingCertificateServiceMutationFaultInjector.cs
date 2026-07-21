using System.Collections.Generic;
using System.IO;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    internal sealed class RecordingCertificateServiceMutationFaultInjector
        : ICertificateServiceMutationFaultInjector
    {
        private readonly List<CertificateServiceMutationFaultPoint>
            _observedPoints =
                new List<CertificateServiceMutationFaultPoint>();
        private CertificateServiceMutationFaultPoint? _armedPoint;

        internal IReadOnlyList<CertificateServiceMutationFaultPoint>
            ObservedPoints => _observedPoints.AsReadOnly();

        internal CertificateSerialNumber? LastSerialNumber { get; private set; }

        internal void Arm(CertificateServiceMutationFaultPoint point)
        {
            _armedPoint = point;
        }

        internal void Clear()
        {
            _observedPoints.Clear();
            LastSerialNumber = null;
            _armedPoint = null;
        }

        public void OnFault(
            CertificateServiceMutationFaultPoint faultPoint,
            CertificateServiceMutationOperation operation,
            CertificateSerialNumber? serialNumber)
        {
            _observedPoints.Add(faultPoint);
            if (serialNumber.HasValue)
            {
                LastSerialNumber = serialNumber;
            }

            if (_armedPoint == faultPoint)
            {
                _armedPoint = null;
                throw new IOException(
                    "Injected certificate service fault: "
                    + faultPoint
                    + ".");
            }
        }
    }
}
