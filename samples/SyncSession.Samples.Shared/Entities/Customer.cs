using System;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Samples.Shared.Entities;

/// <summary>
/// Multi-tenant customer entity for sample applications.
/// Each tenant has their own isolated set of customers.
/// </summary>
[SyncTable("Customers", Priority = 1)]
public class Customer : IMultiTenantSyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }

    public string? Address { get; set; } = string.Empty;

    // Multi-tenant isolation
    public Guid TenantId { get; set; } = Guid.Empty;

    // ISyncInfrastructure
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }

    // ISyncEntity
    public string ModifiedByUserId { get; set; } = "DemoUser";
    public bool IsDeleted { get; set; } = false;
}
