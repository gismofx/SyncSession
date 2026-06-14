using System.Linq;
using FluentAssertions;
using SyncSession.Client.Database;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Locks the single-source-of-truth contract for <see cref="SqliteClientSchema"/>: every SQLite
/// implementation (built-in or custom) provisions its bookkeeping tables from these statements,
/// so the set must stay complete and idempotent.
/// </summary>
public class SqliteClientSchemaTests
{
    [Fact]
    public void AllStatements_ContainsBothBookkeepingTables()
    {
        SqliteClientSchema.AllStatements.Should().HaveCount(2);
        SqliteClientSchema.AllStatements.Should().Contain(SqliteClientSchema.LocalSyncStateDdl);
        SqliteClientSchema.AllStatements.Should().Contain(SqliteClientSchema.LocalSyncMetadataDdl);
    }

    [Fact]
    public void AllStatements_AreIdempotentCreateTable()
    {
        SqliteClientSchema.AllStatements.Should()
            .OnlyContain(s => s.Contains("CREATE TABLE IF NOT EXISTS"));
    }

    [Fact]
    public void Ddl_DeclaresExpectedTables()
    {
        SqliteClientSchema.LocalSyncStateDdl.Should().Contain("LocalSyncState");
        SqliteClientSchema.LocalSyncMetadataDdl.Should().Contain("LocalSyncMetadata");
        // Metadata store is a case-sensitive key/value table.
        SqliteClientSchema.LocalSyncMetadataDdl.Should().Contain("Key");
        SqliteClientSchema.LocalSyncMetadataDdl.Should().Contain("Value");
    }
}