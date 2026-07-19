using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class ProductCodeTests
    {
        [TestMethod]
        public void TryCreateTrimsAndNormalizesAsciiValue()
        {
            ProductCode productCode;

            bool created = ProductCode.TryCreate(" a1b2 ", out productCode);

            Assert.IsTrue(created);
            Assert.IsTrue(productCode.IsValid);
            Assert.AreEqual("A1B2", productCode.Value);
        }

        [TestMethod]
        public void TryCreateRejectsInvalidValues()
        {
            string[] invalidValues =
            {
                null,
                string.Empty,
                "ABC",
                "ABCDE",
                "AB-1",
                "AB\u017f1",
                "한글12",
                "ＡＢ12"
            };

            foreach (string invalidValue in invalidValues)
            {
                ProductCode productCode;

                bool created = ProductCode.TryCreate(invalidValue, out productCode);

                Assert.IsFalse(created, "Value should have been rejected: " + invalidValue);
                Assert.IsFalse(productCode.IsValid);
            }
        }
    }
}
