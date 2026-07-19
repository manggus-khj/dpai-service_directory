namespace DEEPAi.ServiceDirectory.Domain
{
    public enum DomainErrorCode
    {
        BadRequest = 1000,
        NotFound = 1001,
        Conflict = 1002,
        LimitExceeded = 1004,
        RevisionCollision = 2005,
        DirectoryCapacity = 2006,
        LogicalClockExhausted = 2007,
        Internal = 3000
    }
}
