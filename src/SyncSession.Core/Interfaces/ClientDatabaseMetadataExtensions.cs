using System;
using System.Threading.Tasks;
using SyncSession.Core.Constants;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Typed helpers over the <see cref="IClientDatabase"/> metadata key/value store. The database
/// implementations only store raw strings; this keeps the string↔object mapping (and the
/// well-known keys) in a single place, shared by the sync engine and the seed writer.
/// </summary>
public static class ClientDatabaseMetadataExtensions
{
    /// <summary>
    /// Gets the tenant this local database is bound to, or <c>null</c> when there is no binding
    /// (or the stored value is not a valid GUID).
    /// </summary>
    public static async Task<Guid?> GetBoundTenantAsync(this IClientDatabase database)
    {
        var raw = await database.GetClientMetadataAsync(ClientMetadataKeys.BoundTenantId);
        return Guid.TryParse(raw, out var tenantId) ? tenantId : (Guid?)null;
    }

    /// <summary>Binds this local database to the given tenant.</summary>
    public static Task SetBoundTenantAsync(this IClientDatabase database, Guid tenantId)
        => database.SetClientMetadataAsync(ClientMetadataKeys.BoundTenantId, tenantId.ToString());
}
