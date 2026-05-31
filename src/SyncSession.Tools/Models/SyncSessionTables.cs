namespace SyncSession.Tools.Models;

/// <summary>
/// SyncSession infrastructure table names that should be excluded from
/// migration assessment results — they are not consumer business tables.
/// </summary>
public static class SyncSessionTables
{
    public static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "sessionrecords",
        "clientprocessedsessions",
        "schemaversions",
        "syncsessiontables",
        "seedsnapshots",
    };

    public static bool IsInfrastructure(string tableName) =>
        Infrastructure.Contains(tableName) ||
        tableName.StartsWith("temppush", StringComparison.OrdinalIgnoreCase) ||
        tableName.StartsWith("temppull", StringComparison.OrdinalIgnoreCase);
}
