using System;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Samples.Shared.Entities;

/// <summary>
/// Non-tenant product entity (shared reference data).
/// All tenants see the same product catalog.
/// </summary>
[SyncTable("Products", Priority = 1)]
public class Product : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public string Name { get; set; } = string.Empty;
    public string SKU { get; set; } = string.Empty;
    public decimal Price { get; set; }

    // No TenantId - shared across all tenants

    // ISyncInfrastructure
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }

    // ISyncEntity
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; } = false;
}
