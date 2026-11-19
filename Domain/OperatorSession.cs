using System;

namespace RFIDLocal.Domain
{
    public sealed class OperatorSession
    {
        public string PlatformUrl { get; set; }
        public string OperatorEmail { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public long ExpiresAt { get; set; }
    }

    public sealed class PlatformEvent
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string EventDate { get; set; }
        public override string ToString() { return Name + " · " + EventDate + " · " + Status; }
    }
}
