using System;
using System.Data;
using Dapper;

namespace SyncSession.Client.Database;

/// <summary>
/// Dapper TypeHandler for Guid ↔ SQLite TEXT conversion.
/// Ensures GUIDs are stored as human-readable strings (not BLOBs).
/// </summary>
public class SqliteGuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.Value = value.ToString();
        parameter.DbType = DbType.String;
    }

    /// <inheritdoc/>
    public override Guid Parse(object value)
    {
        return Guid.Parse(value.ToString()!);
    }
}

/// <summary>
/// Dapper TypeHandler for Guid? ↔ SQLite TEXT conversion.
/// Handles nullable GUIDs (e.g., SyncSessionId).
/// </summary>
public class SqliteNullableGuidTypeHandler : SqlMapper.TypeHandler<Guid?>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid? value)
    {
        if (value.HasValue)
        {
            parameter.Value = value.Value.ToString();
            parameter.DbType = DbType.String;
        }
        else
        {
            parameter.Value = DBNull.Value;
        }
    }

    /// <inheritdoc/>
    public override Guid? Parse(object value)
    {
        if (value is null || value is DBNull)
            return null;

        return Guid.Parse(value.ToString()!);
    }
}
