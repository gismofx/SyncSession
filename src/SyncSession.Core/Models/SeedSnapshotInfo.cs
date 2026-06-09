namespace SyncSession.Core.Models;

/// <summary>
/// Lightweight projection of a seed snapshot used for orphan detection queries.
/// Contains only the identifying fields needed to locate and clean up orphaned seed tables.
/// </summary>
public class SeedSnapshotInfo
{
    /// <summary>Unique identifier for the seed operation.</summary>
    public Guid SeedId { get; set; }
    /// <summary>Device that initiated the seed.</summary>
    public Guid DeviceId { get; set; }
    /// <summary>Tenant that was being seeded.</summary>
    public Guid TenantId { get; set; }
}
