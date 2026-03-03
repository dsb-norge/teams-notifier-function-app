using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Models;

public class TeamLookupEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "teamlookup";
    public string RowKey { get; set; } = string.Empty; // Team thread ID (19:...@thread.tacv2)
    public string TeamGuid { get; set; } = string.Empty; // AAD Group GUID
    public string? TeamName { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
