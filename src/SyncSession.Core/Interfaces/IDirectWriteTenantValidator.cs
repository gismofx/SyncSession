using System.Threading.Tasks;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Validates tenant authorization for direct write operations.
/// Default implementation checks JWT claims; consumers can override with custom logic
/// (database lookups, role-based access, parent/child tenant hierarchies, etc.)
/// </summary>
public interface IDirectWriteTenantValidator
{
    /// <summary>
    /// Determines if the user is authorized to write data for the specified tenant.
    /// </summary>
    /// <param name="userId">User ID making the request</param>
    /// <param name="tenantId">Tenant ID the user is attempting to access</param>
    /// <returns>True if authorized, false otherwise</returns>
    Task<bool> IsAuthorizedAsync(string userId, string tenantId);
}
