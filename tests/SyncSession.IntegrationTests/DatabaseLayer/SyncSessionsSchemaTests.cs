using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.Constants;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Server.Database;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// Verifies the SessionRecords schema includes audit columns (38l)
/// and that SyncActivityLog has been removed from the infrastructure.
/// </summary>
[Collection("MariaDB Collection")]
public class SessionRecordsSchemaTests
{
    private readonly MariaDbFixture _fixture;
    private readonly TestDatabaseFactory _dbFactory;

    public SessionRecordsSchemaTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task SessionRecords_HasAuditColumns_AfterMigration()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SessionRecords_HasAuditColumns_AfterMigration));

        await using var conn = new MySqlConnection(connectionString);
        var dbName = new MySqlConnectionStringBuilder(connectionString).Database;

        // Act — query column names on SessionRecords
        var columns = (await conn.QueryAsync<string>(
            @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'SessionRecords'",
            new { db = dbName })).ToHashSet();

        // Assert — original columns still present
        columns.Should().Contain("SessionId");
        columns.Should().Contain("TenantId");
        columns.Should().Contain("SessionType");
        columns.Should().Contain("Status");
        columns.Should().Contain("SyncVersion");
        columns.Should().Contain("CreatedAtUtc");
        columns.Should().Contain("CommittedAtUtc");
        columns.Should().Contain("ErrorMessage");

        // Assert — new audit columns (38l)
        columns.Should().Contain("DeviceId", "SyncSessions must have DeviceId after 38l migration");
        columns.Should().Contain("UserId", "SyncSessions must have UserId after 38l migration");
        columns.Should().Contain("UserDisplayName", "SyncSessions must have UserDisplayName after 38l migration");
        columns.Should().Contain("TotalRows", "SyncSessions must have TotalRows after 38l migration");
        columns.Should().Contain("RowCountsJson", "SyncSessions must have RowCountsJson after 38l migration");
    }

    [Fact]
    public async Task SyncActivityLog_DoesNotExist_AfterMigration()
    {
        // Arrange
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(SyncActivityLog_DoesNotExist_AfterMigration));

        await using var conn = new MySqlConnection(connectionString);
        var dbName = new MySqlConnectionStringBuilder(connectionString).Database;

        // Act
        var tableExists = await conn.QuerySingleAsync<int>(
            @"SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES
              WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'SyncActivityLog'",
            new { db = dbName });

        // Assert
        tableExists.Should().Be(0, "SyncActivityLog should not exist — removed in 38l");
    }

    #region Database Layer — Audit Column Read/Write (38l Step 3)

    private async Task<MySqlServerDatabase> CreateServerDbAsync(string testName)
    {
        var connStr = await _dbFactory.CreateDatabaseAsync(testName);
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var cache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        return new MySqlServerDatabase(connStr, cache, config, NullLogger<MySqlServerDatabase>.Instance);
    }

    [Fact]
    public async Task CreateSessionAsync_WithAuditFields_RoundTrips()
    {
        var db = await CreateServerDbAsync(nameof(CreateSessionAsync_WithAuditFields_RoundTrips));
        var sessionId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await db.CreateSessionAsync(new SessionRecord
        {
            SessionId = sessionId,
            TenantId = tenantId,
            DeviceId = deviceId,
            UserId = "user-abc",
            UserDisplayName = "Jane Doe",
            SessionType = SyncConstants.SESSION_TYPE_PUSH,
            Status = SyncConstants.STATUS_STAGING,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
        });

        var session = await db.GetSessionAsync(sessionId);

        session.Should().NotBeNull();
        session!.DeviceId.Should().Be(deviceId);
        session.UserId.Should().Be("user-abc");
        session.UserDisplayName.Should().Be("Jane Doe");
        session.TotalRows.Should().Be(0);
        session.RowCountsJson.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSessionStatusAsync_SetsTotalRowsAndRowCountsJson()
    {
        var db = await CreateServerDbAsync(nameof(UpdateSessionStatusAsync_SetsTotalRowsAndRowCountsJson));
        var sessionId = Guid.NewGuid();

        await db.CreateSessionAsync(new SessionRecord
        {
            SessionId = sessionId,
            SessionType = SyncConstants.SESSION_TYPE_PUSH,
            Status = SyncConstants.STATUS_STAGING,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
        });

        await db.UpdateSessionStatusAsync(
            sessionId,
            SyncConstants.STATUS_COMMITTED,
            totalRows: 500,
            rowCountsJson: "{\"Customers\":200,\"Orders\":300}");

        var session = await db.GetSessionAsync(sessionId);

        session.Should().NotBeNull();
        session!.Status.Should().Be("Committed");
        session.TotalRows.Should().Be(500);
        session.RowCountsJson.Should().Be("{\"Customers\":200,\"Orders\":300}");
        session.CommittedAtUtc.Should().NotBeNull("CommittedAtUtc should be set on Committed status");
    }

    [Fact]
    public async Task UpdateSessionStatusAsync_Completed_SetsCommittedAtUtc()
    {
        var db = await CreateServerDbAsync(nameof(UpdateSessionStatusAsync_Completed_SetsCommittedAtUtc));
        var sessionId = Guid.NewGuid();

        await db.CreateSessionAsync(new SessionRecord
        {
            SessionId = sessionId,
            SessionType = SyncConstants.SESSION_TYPE_PULL,
            Status = SyncConstants.STATUS_STAGING,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
        });

        await db.UpdateSessionStatusAsync(sessionId, SyncConstants.STATUS_COMPLETED, totalRows: 100);

        var session = await db.GetSessionAsync(sessionId);

        session.Should().NotBeNull();
        session!.Status.Should().Be("Completed");
        session.CommittedAtUtc.Should().NotBeNull("CommittedAtUtc should be set for Completed (pull) status too");
        session.TotalRows.Should().Be(100);
    }

    [Fact]
    public async Task CreateSessionAsync_SeedType_TerminalStatus_Works()
    {
        var db = await CreateServerDbAsync(nameof(CreateSessionAsync_SeedType_TerminalStatus_Works));
        var sessionId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await db.CreateSessionAsync(new SessionRecord
        {
            SessionId = sessionId,
            TenantId = tenantId,
            DeviceId = deviceId,
            UserId = "seed-user",
            UserDisplayName = "Seed User",
            SessionType = SyncConstants.SESSION_TYPE_SEED,
            Status = SyncConstants.STATUS_COMPLETED,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            LastActivityUtc = DateTime.UtcNow,
            CommittedAtUtc = DateTime.UtcNow,
            TotalRows = 12000,
            RowCountsJson = "{\"Patients\":5000,\"History\":7000}",
        });

        var session = await db.GetSessionAsync(sessionId);

        session.Should().NotBeNull();
        session!.SessionType.Should().Be("Seed");
        session.Status.Should().Be("Completed");
        session.UserId.Should().Be("seed-user");
        session.UserDisplayName.Should().Be("Seed User");
        session.TotalRows.Should().Be(12000);
        session.RowCountsJson.Should().Contain("Patients");
        session.CommittedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task FindUnseenSessionIds_DoesNotReturnSeedSessions()
    {
        var db = await CreateServerDbAsync(nameof(FindUnseenSessionIds_DoesNotReturnSeedSessions));
        var deviceId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // Create a committed push session (should be found)
        var pushId = Guid.NewGuid();
        await db.CreateSessionAsync(new SessionRecord
        {
            SessionId = pushId,
            TenantId = tenantId,
            SessionType = SyncConstants.SESSION_TYPE_PUSH,
            Status = SyncConstants.STATUS_COMMITTED,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
            CommittedAtUtc = DateTime.UtcNow,
        });

        // Create a seed session (should NOT be found — Status='Completed', not 'Committed')
        var seedId = Guid.NewGuid();
        await db.CreateSessionAsync(new SessionRecord
        {
            SessionId = seedId,
            TenantId = tenantId,
            SessionType = SyncConstants.SESSION_TYPE_SEED,
            Status = SyncConstants.STATUS_COMPLETED,
            CreatedAtUtc = DateTime.UtcNow,
            LastActivityUtc = DateTime.UtcNow,
            CommittedAtUtc = DateTime.UtcNow,
        });

        var unseen = (await db.FindUnseenSessionIdsAsync(deviceId)).ToList();

        unseen.Should().Contain(pushId, "committed push session should be found");
        unseen.Should().NotContain(seedId, "seed session (Completed) should NOT be found by pull query");
    }

    #endregion
}
