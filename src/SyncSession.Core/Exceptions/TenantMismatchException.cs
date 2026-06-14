using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Thrown when a sync is requested for a tenant that does not match the tenant the engine
/// was configured/built with. A fail-closed guard against cross-tenant operations when the
/// logged-in user changes without the engine being rebuilt.
/// </summary>
public class TenantMismatchException : SyncException
{
    /// <summary>The tenant the engine was configured with.</summary>
    public Guid? ConfiguredTenantId { get; }

    /// <summary>The tenant the caller expected this sync to run against.</summary>
    public Guid? ExpectedTenantId { get; }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="configuredTenantId">Tenant the engine was built with.</param>
    /// <param name="expectedTenantId">Tenant the caller expected this sync to run against.</param>
    public TenantMismatchException(Guid? configuredTenantId, Guid? expectedTenantId)
        : base($"Tenant mismatch: the engine is configured for tenant '{configuredTenantId}', " +
               $"but the sync was requested for tenant '{expectedTenantId}'. Rebuild the engine " +
               $"for the current user/tenant before syncing.")
    {
        ConfiguredTenantId = configuredTenantId;
        ExpectedTenantId = expectedTenantId;
    }
}
