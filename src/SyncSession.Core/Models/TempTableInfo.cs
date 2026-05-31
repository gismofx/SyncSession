namespace SyncSession.Core.Models;

/// <summary>
/// Temp table metadata — name and strategy (shared vs dedicated).
/// </summary>
/// <remarks>
/// Used to identify temp tables during cleanup operations.
/// UsesSharedTable = true means multiple sessions share this table.
/// UsesSharedTable = false means dedicated table for single session.
/// </remarks>
public readonly struct TempTableInfo
{
    /// <summary>
    /// Name of the temporary table (e.g., "TempPushCustomers" or "TempPush_Customers_abc123").
    /// </summary>
    public string TempTableName { get; init; }
    
    /// <summary>
    /// True if this is a shared table (multiple sessions), false if dedicated (single session).
    /// </summary>
    public bool UsesSharedTable { get; init; }
}
