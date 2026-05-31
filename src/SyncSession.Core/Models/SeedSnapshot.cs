namespace SyncSession.Core.Models;

public class SeedSnapshot
{
    public Guid SeedId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid TenantId { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }
}
