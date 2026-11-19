using System;

namespace RFIDLocal.Domain
{
    public sealed class LocalEvent
    {
        public string EventId { get; set; }
        public string Name { get; set; }
        public DateTime EventDate { get; set; }
        public string TimeZone { get; set; }
        public string Status { get; set; }
        public string Source { get; set; }
        public bool HasAuthority { get; set; }
        public int ParticipantCount { get; set; }
        public int ReceiptCount { get; set; }
        public string DatabasePath { get; set; }
        public string RemoteEventId { get; set; }
        public string RemoteOperationId { get; set; }
        public string PublicationState { get; set; }
        public long LastRemoteRevision { get; set; }
        public string LastRemoteTransferId { get; set; }

        public override string ToString()
        {
            return Name + " · " + EventDate.ToString("yyyy-MM-dd") + " · " + Status;
        }
    }

    public sealed class LocalDevice
    {
        public string DeviceId { get; set; }
        public string Credential { get; set; }
        public int MinimumRssi { get; set; }
    }
}
