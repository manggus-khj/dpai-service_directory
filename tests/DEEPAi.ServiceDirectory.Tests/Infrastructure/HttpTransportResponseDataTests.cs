using System;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class HttpTransportResponseDataTests
    {
        [TestMethod]
        public void ExternalResponseShapeAndBodyAreCopied()
        {
            ExternalHttpResponseData source =
                ExternalHttpResponseData.XmlError(
                    401,
                    ExternalResponseCode.InvalidApiKey);

            HttpTransportResponseData response =
                HttpTransportResponseData.FromExternal(source);

            Assert.AreEqual(401, response.StatusCode);
            Assert.AreEqual(
                ExternalApiContract.XmlContentType,
                response.ContentType);
            Assert.AreEqual(source.ContentLength, response.ContentLength);
            Assert.IsFalse(response.RetryAfterSeconds.HasValue);

            byte[] firstCopy = response.GetBody();
            byte original = firstCopy[0];
            firstCopy[0] ^= 0xff;
            Assert.AreEqual(original, response.GetBody()[0]);
        }

        [TestMethod]
        public void AdminRetryAfterShapeIsPreserved()
        {
            var sourceBody = new byte[] { 1, 2, 3 };
            AdminHttpResponseData source = AdminHttpResponseData.Xml(
                429,
                sourceBody,
                7);

            HttpTransportResponseData response =
                HttpTransportResponseData.FromAdmin(source);
            sourceBody[0] = 9;

            Assert.AreEqual(429, response.StatusCode);
            Assert.AreEqual(
                "application/xml; charset=utf-8",
                response.ContentType);
            Assert.AreEqual(3, response.ContentLength);
            Assert.IsTrue(response.RetryAfterSeconds.HasValue);
            Assert.AreEqual(7, response.RetryAfterSeconds.Value);
            CollectionAssert.AreEqual(
                new byte[] { 1, 2, 3 },
                response.GetBody());
        }

        [TestMethod]
        public void BodylessHostResponseHasNoEntityHeaders()
        {
            HttpTransportResponseData response =
                HttpTransportResponseData.Bodyless(404);

            Assert.AreEqual(404, response.StatusCode);
            Assert.AreEqual(0, response.ContentLength);
            Assert.IsNull(response.ContentType);
            Assert.IsFalse(response.RetryAfterSeconds.HasValue);
            CollectionAssert.AreEqual(
                new byte[0],
                response.GetBody());
        }

        [TestMethod]
        public void HostCannotInventOtherBodylessStatuses()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => HttpTransportResponseData.Bodyless(403));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => HttpTransportResponseData.Bodyless(500));
        }
    }
}
