using System;

namespace SyncSession.Core.Attributes;

/// <summary>
/// Marks a class as a syncable table and specifies sync metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class SyncTableAttribute : Attribute
{
    /// <summary>
    /// Name of the database table. If null, the class name is used during discovery.
    /// </summary>
    public string? TableName { get; }
    
    /// <summary>
    /// Processing priority (lower values are processed first).
    /// Use this to ensure parent tables are synchronized before child tables.
    /// Example: Customers (1), Orders (2), OrderItems (3).
    /// </summary>
    public int Priority { get; set; }
    
    /// <summary>
    /// Initializes a new instance of <see cref="SyncTableAttribute"/> with an explicit table name.
    /// </summary>
    /// <param name="tableName">The name of the database table.</param>
    public SyncTableAttribute(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));
        TableName = tableName;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SyncTableAttribute"/> using the class name as the table name.
    /// </summary>
    public SyncTableAttribute()
    {
        TableName = null; // resolved to class name during discovery
    }
}

/// <summary>
/// Customizes how a property is mapped to a database column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public class SyncColumnAttribute : Attribute
{
    /// <summary>
    /// Override the column name. Leave <c>null</c> to use the property name as the column name.
    /// </summary>
    public string? ColumnName { get; set; }
    
    /// <summary>
    /// Exclude this property from synchronization (local-only data).
    /// </summary>
    public bool Ignore { get; set; }
}
