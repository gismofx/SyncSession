namespace SyncSession.Core.Models;

/// <summary>
/// Information about a table in a sync session
/// Used by queue processing to determine table processing order
/// </summary>
public class SessionTableInfo
{
    /// <summary>
    /// Business table name (e.g., "Customers")
    /// </summary>
    public string TableName { get; set; } = string.Empty;
    
    /// <summary>
    /// Temp table name where data is staged (e.g., "TempPushCustomers" or "TempPush_Customers_abc123")
    /// </summary>
    public string TempTableName { get; set; } = string.Empty;
    
    /// <summary>
    /// Processing priority (lower = process first)
    /// Used to respect foreign key dependencies
    /// </summary>
    public int Priority { get; set; }
    
    /// <summary>
    /// True if using a shared temp table, false for dedicated
    /// </summary>
    public bool UsesSharedTable { get; set; }
}
