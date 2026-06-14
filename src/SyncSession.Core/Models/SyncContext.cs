using System;

namespace SyncSession.Core.Models;

/// <summary>
/// Optional per-call context for a sync operation. Lets callers supply the current
/// user/tenant identity at sync time without rebuilding the engine.
/// </summary>
public sealed class SyncContext
{
    /// <summary>
    /// The tenant this sync is expected to run against. When set and it does not match the
    /// tenant the engine was configured with, the engine throws
    /// <see cref="Exceptions.TenantMismatchException"/> and performs no I/O. Use on clients
    /// where the logged-in user can change without the engine being rebuilt (e.g. shared
    /// workstations) to fail closed instead of syncing under the wrong tenant.
    /// </summary>
    public Guid? ExpectedTenantId { get; init; }

    /// <summary>
    /// Overrides <see cref="ClientSyncConfiguration.UserDisplayName"/> for this sync's audit
    /// record (server-side SyncSessions). Falls back to the configured value when <c>null</c>.
    /// </summary>
    public string? UserDisplayName { get; init; }
}
