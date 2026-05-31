using System;

namespace SyncSession.Client.Seeding;

/// <summary>
/// Thrown by <see cref="SeedClient.SeedAsync"/> when the seed stream is interrupted
/// before the <c>end</c> line is received (network drop, server error, etc.).
/// </summary>
/// <remarks>
/// There is no resume — the consumer must restart seeding from the beginning.
/// Any partial data written to the client database should be rolled back or discarded
/// before retrying; <see cref="ISeedDatabaseWriter"/> implementations are responsible
/// for managing this cleanup.
/// </remarks>
public sealed class SeedInterruptedException : Exception
{
    /// <summary>Table that was being streamed when the interruption occurred, if known.</summary>
    public string? TableName { get; }

    /// <inheritdoc cref="SeedInterruptedException"/>
    public SeedInterruptedException(string message, string? tableName = null, Exception? inner = null)
        : base(message, inner)
    {
        TableName = tableName;
    }
}
