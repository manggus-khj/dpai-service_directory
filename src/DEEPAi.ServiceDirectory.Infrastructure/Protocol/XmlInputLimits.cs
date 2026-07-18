namespace DEEPAi.ServiceDirectory.Infrastructure.Protocol
{
    public static class XmlInputLimits
    {
        public const int StandardBodyBytes = 16 * 1024;
        public const int SyncExchangeBodyBytes = 4 * 1024 * 1024;
        public const int MaximumDepth = 16;
    }
}
