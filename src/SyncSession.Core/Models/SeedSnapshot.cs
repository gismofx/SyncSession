namespace SyncSession.Core.Models;

/// <summary>
/// Represents an active seed operation row in the SeedSnapshots tracking table.
/// Used by the server to manage seed lifecycle and detect orphaned seeds.
/// </summary>
public class SeedSnapshot
{
    /// <summary>Unique identifier for this seed operation (also used as the snapshot table name suffix).</summary>
    public Guid SeedId { get; set; }
    /// <summary>Device that initiated the seed.</summary>
    public Guid DeviceId { get; set; }
    /// <summary>Tenant being seeded.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Current status of the seed operation (see <see cref="SeedSnapshotStatus"/>).</summary>
    public string Status { get; set; } = "Active";
    /// <summary>UTC timestamp when this seed operation was created.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>UTC timestamp of the last keep-alive update; used to detect orphaned seeds.</summary>
    public DateTime LastActivityUtc { get; set; }
}
