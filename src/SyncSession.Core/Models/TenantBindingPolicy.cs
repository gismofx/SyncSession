namespace SyncSession.Core.Models;

/// <summary>
/// Controls what happens when a multi-tenant sync runs against a local database that has no
/// persisted tenant binding — for example a database populated before tenant binding existed,
/// or one that was never seeded.
/// </summary>
/// <remarks>
/// A binding that is <em>present but different</em> from the configured tenant is always rejected,
/// regardless of this policy — <see cref="Adopt"/> only ever fills a <em>missing</em> binding.
/// </remarks>
public enum TenantBindingPolicy
{
    /// <summary>
    /// Fail closed: throw <c>TenantBindingMissingException</c> when the local database has no tenant
    /// binding. The default — a database should be bound at seed.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Bind the local database to the engine's configured tenant on first sync. Use when migrating
    /// databases that were populated before tenant binding existed.
    /// </summary>
    Adopt = 1
}
