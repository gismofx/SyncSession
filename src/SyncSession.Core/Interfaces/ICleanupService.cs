using System.Threading.Tasks;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Defines a contract for server-side cleanup services that periodically remove
/// stale, orphaned, or expired data to maintain database health.
/// </summary>
/// <remarks>
/// Implementations are registered with the DI container and resolved via
/// <c>IEnumerable&lt;ICleanupService&gt;</c> by the server's cleanup background service,
/// allowing new cleanup strategies to be added without modifying the background service.
/// </remarks>
public interface ICleanupService
{
    /// <summary>
    /// Executes the cleanup operation and returns the number of items removed.
    /// </summary>
    /// <returns>
    /// The number of items cleaned up (sessions purged, tables dropped, rows deleted, etc.).
    /// Returns 0 if nothing required cleanup.
    /// </returns>
    Task<int> ExecuteCleanupAsync();

    /// <summary>
    /// Returns a human-readable description of what this service cleans up,
    /// used for structured logging during cleanup cycles.
    /// </summary>
    /// <returns>A short description of the cleanup operation (e.g., "Stale session cleanup").</returns>
    string GetCleanupDescription();
}
