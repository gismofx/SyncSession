namespace SyncSession.Core.Interfaces;

/// <summary>
/// Extends ISyncEntity with multi-tenant isolation support.
/// Entities implementing this interface will automatically have tenant-based filtering
/// applied during all sync operations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Common Scenario:</b> Each client/user belongs to a single tenant.
/// The sync engine automatically filters all queries by the client's tenant.
/// </para>
/// <para>
/// <b>Admin Scenario:</b> Some users (admins) can access multiple tenants.
/// Use explicit tenant context override when syncing as an admin.
/// </para>
/// <para>
/// <b>Mixed Tables:</b> Not all entities need to be multi-tenant.
/// Reference data (products, categories) can implement ISyncEntity directly.
/// Transactional data (orders, customers) typically implements IMultiTenantSyncEntity.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [SyncTable("Customers", Priority = 1)]
/// public class Customer : IMultiTenantSyncEntity
/// {
///     public Guid Id { get; set; }
///     public Guid TenantId { get; set; }  // Automatically filtered
///     public string Name { get; set; }
///     public string Email { get; set; }
///     
///     // ISyncEntity business properties
///     public bool IsDeleted { get; set; }
///     public string ModifiedByUserId { get; set; }
///     
///     // ISyncInfrastructure (auto-managed)
///     public bool IsDirty { get; set; }
///     public DateTime? ModifiedAtUtc { get; set; }
///     public Guid? SyncSessionId { get; set; }
/// }
/// </code>
/// </example>
public interface IMultiTenantSyncEntity : ISyncEntity
{
    /// <summary>
    /// Tenant identifier for data isolation.
    /// All sync operations (push/pull) automatically filter by this value.
    /// </summary>
    /// <remarks>
    /// <b>Server:</b> Validates client can only access their assigned tenant(s)
    /// <b>Client:</b> Only syncs records matching the current tenant context
    /// <b>Security:</b> Prevents cross-tenant data leakage
    /// </remarks>
    Guid TenantId { get; set; }
}
