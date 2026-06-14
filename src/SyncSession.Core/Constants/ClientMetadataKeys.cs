namespace SyncSession.Core.Constants;

/// <summary>
/// Well-known keys for the client-side metadata key/value store (the <c>LocalSyncMetadata</c>
/// table). Keys are code-owned constants used verbatim, so the column is case-sensitive.
/// </summary>
public static class ClientMetadataKeys
{
    /// <summary>
    /// The tenant this local database is bound to, stored as a GUID string. Written at seed and
    /// asserted on every multi-tenant sync to prevent a different tenant from syncing into a
    /// database that already holds another tenant's data.
    /// </summary>
    public const string BoundTenantId = "BoundTenantId";
}
