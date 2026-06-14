using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Thrown when a multi-tenant sync runs against a local database that has no persisted tenant
/// binding and <c>TenantBindingPolicy</c> is <c>Reject</c> (the default). Seed the database
/// (which binds it to a tenant), or set <c>TenantBindingPolicy.Adopt</c> to bind the configured
/// tenant on first sync when migrating a database populated before tenant binding existed.
/// </summary>
public class TenantBindingMissingException : SyncException
{
    /// <summary>The tenant the engine is configured for.</summary>
    public Guid ConfiguredTenantId { get; }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="configuredTenantId">The tenant the engine is configured for.</param>
    public TenantBindingMissingException(Guid configuredTenantId)
        : base($"The local database has no tenant binding and TenantBindingPolicy is Reject. " +
               $"The engine is configured for tenant '{configuredTenantId}'. Seed the database " +
               $"(which binds it to a tenant), or set TenantBindingPolicy.Adopt to bind this tenant " +
               $"on first sync.")
    {
        ConfiguredTenantId = configuredTenantId;
    }
}
