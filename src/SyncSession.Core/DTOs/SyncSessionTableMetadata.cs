using System;

namespace SyncSession.Core.DTOs;

/// <summary>
/// Represents metadata for a single table within a sync session.
/// </summary>
public class SyncSessionTableMetadata
{
    /// <summary>
    /// Gets or sets the business table name.
    /// </summary>
    /// <value>The table name (e.g., <c>Customers</c>, <c>Orders</c>).</value>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the temp table name where data is staged.
    /// </summary>
    /// <value>The temp table name (e.g., <c>TempPushCustomers</c> or <c>TempPull_Customers_abc123</c>).</value>
    public string TempTableName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this table uses a shared temp table.
    /// </summary>
    /// <value><c>true</c> for shared tables; <c>false</c> for dedicated tables.</value>
    public bool UsesSharedTable { get; set; }

    /// <summary>
    /// Gets or sets the total record count in the temp table.
    /// </summary>
    /// <value>The record count for pull operations, or <c>null</c> for push operations where the client controls the data.</value>
    public int? TotalRecords { get; set; }
}
