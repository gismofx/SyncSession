using System.Threading.Tasks;
using SyncSession.Core.Interfaces;

namespace SyncSession.Server.Services;

/// <summary>
/// Default tenant validator that allows all access.
/// Consumers should override with custom logic for production multi-tenant scenarios
/// (database lookups, role-based access, parent/child tenant hierarchies, etc.)
/// </summary>
public class DefaultDirectWriteTenantValidator : IDirectWriteTenantValidator
{
    /// <inheritdoc/>
    public Task<bool> IsAuthorizedAsync(string userId, string tenantId)
    {
        // Default: allow all. Override via DI for production validation.
        return Task.FromResult(true);
    }
}
