using System;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Samples.Shared.Entities;

/// <summary>
/// Multi-tenant order entity (header).
/// Contains OrderItems which reference Products.
/// </summary>
[SyncTable("Orders", Priority = 2)]
public class Order : IMultiTenantSyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public Guid CustomerId { get; set; } = Guid.Empty;
    public string OrderNumber { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime OrderDate { get; set; }
    public string Status { get; set; } = "Pending";

    // Multi-tenant isolation
    public Guid TenantId { get; set; } = Guid.Empty;

    // ISyncInfrastructure
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }

    // ISyncEntity
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; } = false;
}
