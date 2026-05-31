using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Database;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;
/// <summary>
/// Tests for cleanup services (Session 13, updated Session 22e for Pull session support)
/// </summary>
public class CleanupTests : IAsyncLifetime
{
    private SqliteConnection? _connection;
    private SqliteServerDatabase? _database;
    private ServerSyncConfiguration? _config;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        
        await InitializeSchemaAsync();
        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var tableMetaDataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);

        _database = new SqliteServerDatabase(_connection, tableMetaDataCache, config);

        _config = new ServerSyncConfiguration();
        _config.RegisterTable<Customer>(priority: 1);
    }

    public Task DisposeAsync()
    {
        _connection?.Dispose();
        return Task.CompletedTask;
    }

    private async Task InitializeSchemaAsync()
    {
        // Infrastructure tables — sourced from TestSchemaBuilder (validated against 001_Infrastructure.sql)
        await TestSchemaBuilder.BuildAsync(_connection!);

        // Business tables
        await _connection!.ExecuteAsync(@"
            CREATE TABLE Customers (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                ModifiedAtUtc TEXT NOT NULL,
                SyncSessionId TEXT NULL,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'System'
            )");

        // Shared temp tables
        await _connection.ExecuteAsync(@"
            CREATE TABLE TempPushCustomers (
                SessionId TEXT NOT NULL,
                SequenceNumber INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                ModifiedAtUtc TEXT NOT NULL,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'System',
                IsDeleted INTEGER NOT NULL DEFAULT 0
            )");

        await _connection.ExecuteAsync(@"
            CREATE TABLE TempPullCustomers (
                SessionId TEXT NOT NULL,
                Id TEXT NOT NULL,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                SyncVersion INTEGER NOT NULL,
                ModifiedAtUtc TEXT NOT NULL,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'System',
                IsDeleted INTEGER NOT NULL,
                SyncSessionId TEXT NULL,
                PRIMARY KEY (SessionId, Id)
            )");
    }

    #region Push Session Cleanup Tests

    [Fact]
    public async Task SessionCleanup_ShouldDetectStalePushSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var staleSessionId = Guid.NewGuid();
        var staleTime = DateTime.UtcNow.AddMinutes(-45);

        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Staging', @StaleTime, @StaleTime)",
            new
            {
                SessionId = staleSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });

        // Act
        var staleSessions = await service.FindStaleSessions();

        // Assert
        staleSessions.Should().HaveCount(1);
        staleSessions[0].SessionId.Should().Be(staleSessionId);
    }

    [Fact]
    public async Task SessionCleanup_ShouldNotDetectActivePushSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var activeSessionId = Guid.NewGuid();
        var recentTime = DateTime.UtcNow.AddMinutes(-5);

        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Ready', @RecentTime, @RecentTime)",
            new
            {
                SessionId = activeSessionId.ToString(),
                RecentTime = recentTime.ToString("O")
            });

        // Act
        var staleSessions = await service.FindStaleSessions();

        // Assert
        staleSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionCleanup_ShouldCleanupStalePushSession_AndMarkAsFailed()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var staleSessionId = Guid.NewGuid();
        var staleTime = DateTime.UtcNow.AddMinutes(-45);

        // Create stale session
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Staging', @StaleTime, @StaleTime)",
            new
            {
                SessionId = staleSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });

        // Create temp table record
        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables (SessionId, TableName, TempTableName, UsesSharedTable, ProcessingPriority)
            VALUES (@SessionId, 'Customers', 'TempPushCustomers', 1, 1)",
            new { SessionId = staleSessionId.ToString() });

        // Add data to shared temp table
        await _connection.ExecuteAsync(@"
            INSERT INTO TempPushCustomers (SessionId, Id, Name, Email, ModifiedAtUtc)
            VALUES (@SessionId, @Id, 'Test', 'test@example.com', @Now)",
            new
            {
                SessionId = staleSessionId.ToString(),
                Id = Guid.NewGuid().ToString(),
                Now = DateTime.UtcNow.ToString("O")
            });

        // Act
        var cleanedCount = await service.CleanupStaleSessions();

        // Assert
        cleanedCount.Should().Be(1);

        // Verify session marked as Failed
        var session = await _connection.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT Status, ErrorMessage FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = staleSessionId.ToString() });

        ((string)session.Status).Should().Be("Failed");
        ((string)session.ErrorMessage).Should().Contain("timed out");

        // Verify temp table data deleted
        var tempRows = await _connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM TempPushCustomers WHERE SessionId = @SessionId",
            new { SessionId = staleSessionId.ToString() });

        tempRows.Should().Be(0);

        // Verify session tables deleted
        var sessionTableRows = await _connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM SyncSessionTables WHERE SessionId = @SessionId",
            new { SessionId = staleSessionId.ToString() });

        sessionTableRows.Should().Be(0);
    }

    [Fact]
    public async Task SessionCleanup_ShouldPurgeOldPushSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var oldSessionId = Guid.NewGuid();
        var oldTime = DateTime.UtcNow.AddDays(-45);

        // Create old committed session
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, SyncVersion, CreatedAtUtc, LastActivityUtc, CommittedAtUtc)
            VALUES (@SessionId, 'Push', 'Committed', 100, @OldTime, @OldTime, @OldTime)",
            new
            {
                SessionId = oldSessionId.ToString(),
                OldTime = oldTime.ToString("O")
            });

        // Add session table record
        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables (SessionId, TableName, TempTableName, UsesSharedTable, ProcessingPriority)
            VALUES (@SessionId, 'Customers', NULL, 1, 1)",
            new { SessionId = oldSessionId.ToString() });

        // Add client processed record
        await _connection.ExecuteAsync(@"
            INSERT INTO ClientProcessedSessions (DeviceId, SessionId, ProcessedAtUtc)
            VALUES (@DeviceId, @SessionId, @Now)",
            new
            {
                DeviceId = Guid.NewGuid().ToString(),
                SessionId = oldSessionId.ToString(),
                Now = DateTime.UtcNow.ToString("O")
            });

        // Act
        var purgedCount = await service.PurgeOldSessions(retentionDays: 30);

        // Assert
        purgedCount.Should().Be(1);

        var sessionExists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = oldSessionId.ToString() });
        sessionExists.Should().Be(0);

        var sessionTableExists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SyncSessionTables WHERE SessionId = @SessionId",
            new { SessionId = oldSessionId.ToString() });
        sessionTableExists.Should().Be(0);

        var processedExists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ClientProcessedSessions WHERE SessionId = @SessionId",
            new { SessionId = oldSessionId.ToString() });
        processedExists.Should().Be(0);
    }

    #endregion

    #region Retention Disabled (38B-a)

    [Fact]
    public async Task PurgeOldSessions_RetentionZero_PurgesNothing()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var oldSessionId = Guid.NewGuid();
        var oldTime = DateTime.UtcNow.AddDays(-365);
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, SyncVersion, CreatedAtUtc, LastActivityUtc, CommittedAtUtc)
            VALUES (@SessionId, 'Push', 'Committed', 100, @OldTime, @OldTime, @OldTime)",
            new { SessionId = oldSessionId.ToString(), OldTime = oldTime.ToString("O") });

        // Act — retention disabled
        var purgedCount = await service.PurgeOldSessions(retentionDays: 0);

        // Assert — nothing purged, session retained
        purgedCount.Should().Be(0);
        var exists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @Id",
            new { Id = oldSessionId.ToString() });
        exists.Should().Be(1, "with retention disabled the old session must NOT be purged");
    }

    [Fact]
    public async Task ExecuteCleanup_RetentionZero_StaleCleanupStillRuns_NoPurge()
    {
        // Arrange — config with retention disabled (default)
        var config = new ServerSyncConfiguration { SessionRetentionDays = 0 };
        config.RegisterTable<Customer>(priority: 1);
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, config);

        // A stale session (must still be failed)
        var staleId = Guid.NewGuid();
        var staleTime = DateTime.UtcNow.AddMinutes(-45);
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Staging', @StaleTime, @StaleTime)",
            new { SessionId = staleId.ToString(), StaleTime = staleTime.ToString("O") });

        // An old committed session (must NOT be purged when retention = 0)
        var oldId = Guid.NewGuid();
        var oldTime = DateTime.UtcNow.AddDays(-365);
        await _connection.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, SyncVersion, CreatedAtUtc, LastActivityUtc, CommittedAtUtc)
            VALUES (@SessionId, 'Push', 'Committed', 50, @OldTime, @OldTime, @OldTime)",
            new { SessionId = oldId.ToString(), OldTime = oldTime.ToString("O") });

        // Act
        await service.ExecuteCleanupAsync();

        // Assert — stale failed, old retained
        var staleStatus = await _connection.ExecuteScalarAsync<string>(
            "SELECT Status FROM SessionRecords WHERE SessionId = @Id", new { Id = staleId.ToString() });
        staleStatus.Should().Be("Failed", "stale-session cleanup must run regardless of retention setting");

        var oldExists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @Id", new { Id = oldId.ToString() });
        oldExists.Should().Be(1, "retention = 0 must not purge old sessions");
    }

    #endregion

    #region Pull Session Cleanup Tests (Session 22e)

    [Fact]
    public async Task SessionCleanup_ShouldDetectStalePullSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var stalePullSessionId = Guid.NewGuid();
        var staleTime = DateTime.UtcNow.AddMinutes(-45);

        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Pull', 'Pulling', @StaleTime, @StaleTime)",
            new
            {
                SessionId = stalePullSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });

        // Act
        var staleSessions = await service.FindStaleSessions();

        // Assert
        staleSessions.Should().HaveCount(1);
        staleSessions[0].SessionId.Should().Be(stalePullSessionId);
        staleSessions[0].SessionType.Should().Be("Pull");
        staleSessions[0].Status.Should().Be("Pulling");
    }

    [Fact]
    public async Task SessionCleanup_ShouldNotDetectActivePullSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var activePullSessionId = Guid.NewGuid();
        var recentTime = DateTime.UtcNow.AddMinutes(-5);

        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Pull', 'Pulling', @RecentTime, @RecentTime)",
            new
            {
                SessionId = activePullSessionId.ToString(),
                RecentTime = recentTime.ToString("O")
            });

        // Act
        var staleSessions = await service.FindStaleSessions();

        // Assert
        staleSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionCleanup_ShouldCleanupStalePullSession_WithSharedTempTable()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var stalePullSessionId = Guid.NewGuid();
        var staleTime = DateTime.UtcNow.AddMinutes(-45);

        // Create stale pull session
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Pull', 'Pulling', @StaleTime, @StaleTime)",
            new
            {
                SessionId = stalePullSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });

        // Create SyncSessionTables record (pull sessions now track these)
        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables (SessionId, TableName, TempTableName, UsesSharedTable, ProcessingPriority, ActualRecordCount)
            VALUES (@SessionId, 'Customers', 'TempPullCustomers', 1, 1, 1)",
            new { SessionId = stalePullSessionId.ToString() });

        // Add data to shared pull temp table
        // Note: CreatedAtUtc was removed from TempPull* tables in migration 004
        await _connection.ExecuteAsync(@"
            INSERT INTO TempPullCustomers (SessionId, Id, Name, Email, SyncVersion, ModifiedAtUtc, IsDeleted)
            VALUES (@PullSessionId, @Id, 'PullTest', 'pull@example.com', 1, @Now, 0)",
            new
            {
                PullSessionId = stalePullSessionId.ToString(),
                Id = Guid.NewGuid().ToString(),
                Now = DateTime.UtcNow.ToString("O")
            });

        // Act
        var cleanedCount = await service.CleanupStaleSessions();

        // Assert
        cleanedCount.Should().Be(1);

        // Verify session marked as Failed
        var session = await _connection.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT Status, ErrorMessage FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = stalePullSessionId.ToString() });

        ((string)session.Status).Should().Be("Failed");
        ((string)session.ErrorMessage).Should().Contain("timed out");

        // Verify pull temp table data deleted
        var tempRows = await _connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM TempPullCustomers WHERE SessionId = @PullSessionId",
            new { PullSessionId = stalePullSessionId.ToString() });

        tempRows.Should().Be(0);

        // Verify session tables deleted
        var sessionTableRows = await _connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM SyncSessionTables WHERE SessionId = @SessionId",
            new { SessionId = stalePullSessionId.ToString() });

        sessionTableRows.Should().Be(0);
    }

    [Fact]
    public async Task SessionCleanup_ShouldPurgeOldCompletedPullSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var oldPullSessionId = Guid.NewGuid();
        var oldTime = DateTime.UtcNow.AddDays(-45);

        // Create old completed pull session
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc, CommittedAtUtc)
            VALUES (@SessionId, 'Pull', 'Completed', @OldTime, @OldTime, @OldTime)",
            new
            {
                SessionId = oldPullSessionId.ToString(),
                OldTime = oldTime.ToString("O")
            });

        // Act
        var purgedCount = await service.PurgeOldSessions(retentionDays: 30);

        // Assert
        purgedCount.Should().Be(1);

        var sessionExists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SessionRecords WHERE SessionId = @Id",
            new { Id = oldPullSessionId.ToString() });
        sessionExists.Should().Be(0);
    }

    #endregion

    #region Mixed Push/Pull Cleanup Tests (Session 22e)

    [Fact]
    public async Task SessionCleanup_ShouldDetectBothPushAndPullStaleSessions()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var staleTime = DateTime.UtcNow.AddMinutes(-45);

        var stalePushSessionId = Guid.NewGuid();
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Staging', @StaleTime, @StaleTime)",
            new
            {
                SessionId = stalePushSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });

        var stalePullSessionId = Guid.NewGuid();
        await _connection.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Pull', 'Pulling', @StaleTime, @StaleTime)",
            new
            {
                SessionId = stalePullSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });

        // Act
        var staleSessions = await service.FindStaleSessions();

        // Assert
        staleSessions.Should().HaveCount(2);
        staleSessions.Should().Contain(s => s.SessionType == "Push");
        staleSessions.Should().Contain(s => s.SessionType == "Pull");
    }

    [Fact]
    public async Task SessionCleanup_ShouldCleanupMixedStaleSessionsCorrectly()
    {
        // Arrange
        var service = new SessionCleanupService(_database!, NullLogger<SessionCleanupService>.Instance, _config!);

        var staleTime = DateTime.UtcNow.AddMinutes(-45);

        // Create stale push session with shared push temp data
        var stalePushSessionId = Guid.NewGuid();
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Staging', @StaleTime, @StaleTime)",
            new
            {
                SessionId = stalePushSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });
        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables (SessionId, TableName, TempTableName, UsesSharedTable, ProcessingPriority)
            VALUES (@SessionId, 'Customers', 'TempPushCustomers', 1, 1)",
            new { SessionId = stalePushSessionId.ToString() });
        await _connection.ExecuteAsync(@"
            INSERT INTO TempPushCustomers (SessionId, Id, Name, Email, ModifiedAtUtc)
            VALUES (@SessionId, @Id, 'PushTest', 'push@example.com', @Now)",
            new
            {
                SessionId = stalePushSessionId.ToString(),
                Id = Guid.NewGuid().ToString(),
                Now = DateTime.UtcNow.ToString("O")
            });

        // Create stale pull session with shared pull temp data
        var stalePullSessionId = Guid.NewGuid();
        await _connection.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Pull', 'Pulling', @StaleTime, @StaleTime)",
            new
            {
                SessionId = stalePullSessionId.ToString(),
                StaleTime = staleTime.ToString("O")
            });
        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables (SessionId, TableName, TempTableName, UsesSharedTable, ProcessingPriority, ActualRecordCount)
            VALUES (@SessionId, 'Customers', 'TempPullCustomers', 1, 1, 1)",
            new { SessionId = stalePullSessionId.ToString() });
        await _connection.ExecuteAsync(@"
            INSERT INTO TempPullCustomers (SessionId, Id, Name, Email, SyncVersion, ModifiedAtUtc, IsDeleted)
            VALUES (@PullSessionId, @Id, 'PullTest', 'pull@example.com', 1, @Now, 0)",
            new
            {
                PullSessionId = stalePullSessionId.ToString(),
                Id = Guid.NewGuid().ToString(),
                Now = DateTime.UtcNow.ToString("O")
            });

        // Act
        var cleanedCount = await service.CleanupStaleSessions();

        // Assert
        cleanedCount.Should().Be(2);

        // Both marked Failed
        var pushSession = await _connection.QuerySingleAsync<dynamic>(
            "SELECT Status FROM SessionRecords WHERE SessionId = @Id",
            new { Id = stalePushSessionId.ToString() });
        ((string)pushSession.Status).Should().Be("Failed");

        var pullSession = await _connection.QuerySingleAsync<dynamic>(
            "SELECT Status FROM SessionRecords WHERE SessionId = @Id",
            new { Id = stalePullSessionId.ToString() });
        ((string)pullSession.Status).Should().Be("Failed");

        // Push temp data cleaned via SessionId column
        var pushTempRows = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TempPushCustomers WHERE SessionId = @Id",
            new { Id = stalePushSessionId.ToString() });
        pushTempRows.Should().Be(0);

        // Pull temp data cleaned via SessionId column
        var pullTempRows = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TempPullCustomers WHERE SessionId = @Id",
            new { Id = stalePullSessionId.ToString() });
        pullTempRows.Should().Be(0);
    }

    #endregion

    #region Shared Table Cleanup Tests

    [Fact]
    public async Task SharedTableCleanup_ShouldCleanupOldRows()
    {
        // Arrange
        var service = new SharedTableCleanupService(_database!, NullLogger<SharedTableCleanupService>.Instance, _config!);

        // Add old row (30 hours old) - must have a matching SessionRecord
        var oldTime = DateTime.UtcNow.AddHours(-30);
        var oldSessionId = Guid.NewGuid().ToString();
        await _connection!.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Committed', @OldTime, @OldTime)",
            new { SessionId = oldSessionId, OldTime = oldTime.ToString("O") });
        await _connection.ExecuteAsync(@"
            INSERT INTO TempPushCustomers (SessionId, Id, Name, Email, ModifiedAtUtc)
            VALUES (@SessionId, @Id, 'Old', 'old@example.com', @OldTime)",
            new { SessionId = oldSessionId, Id = Guid.NewGuid().ToString(), OldTime = oldTime.ToString("O") });

        // Add recent row (1 hour old) - must have a matching SessionRecord
        var recentTime = DateTime.UtcNow.AddHours(-1);
        var recentSessionId = Guid.NewGuid().ToString();
        await _connection.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Committed', @RecentTime, @RecentTime)",
            new { SessionId = recentSessionId, RecentTime = recentTime.ToString("O") });
        await _connection.ExecuteAsync(@"
            INSERT INTO TempPushCustomers (SessionId, Id, Name, Email, ModifiedAtUtc)
            VALUES (@SessionId, @Id, 'Recent', 'recent@example.com', @RecentTime)",
            new { SessionId = recentSessionId, Id = Guid.NewGuid().ToString(), RecentTime = recentTime.ToString("O") });

        // Act - Cleanup rows older than 24 hours
        var deletedCount = await service.CleanupSharedTempTablesAsync(olderThanHours: 24);

        // Assert
        deletedCount.Should().Be(1);

        var remainingRows = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TempPushCustomers");
        remainingRows.Should().Be(1);

        var remainingRow = await _connection.QuerySingleAsync<dynamic>(
            "SELECT Name FROM TempPushCustomers");
        ((string)remainingRow.Name).Should().Be("Recent");
    }

    #endregion

    #region Temp Table Cleanup Tests (Dedicated Tables)

    [Fact]
    public async Task TempTableCleanup_ShouldFindDedicatedTables()
    {
        // Arrange
        var service = new TempTableCleanupService(_database!, NullLogger<TempTableCleanupService>.Instance, _config!);

        await _connection!.ExecuteAsync(@"
            CREATE TABLE TempPush_Customers_ABC123 (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            )");

        // Act
        var dedicatedTables = await service.FindDedicatedTempTables();

        // Assert
        dedicatedTables.Should().Contain("TempPush_Customers_ABC123");
        dedicatedTables.Should().NotContain("TempPushCustomers");
    }

    [Fact]
    public async Task TempTableCleanup_ShouldFindOrphanedTables()
    {
        // Arrange
        var service = new TempTableCleanupService(_database!, NullLogger<TempTableCleanupService>.Instance, _config!);

        await _connection!.ExecuteAsync(@"
            CREATE TABLE TempPush_Customers_ORPHANED (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            )");

        await _connection.ExecuteAsync(@"
            CREATE TABLE TempPush_Customers_ACTIVE (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            )");

        var activeSessionId = Guid.NewGuid();
        await _connection.ExecuteAsync(@"
            INSERT INTO SessionRecords (SessionId, SessionType, Status, CreatedAtUtc, LastActivityUtc)
            VALUES (@SessionId, 'Push', 'Processing', @Now, @Now)",
            new
            {
                SessionId = activeSessionId.ToString(),
                Now = DateTime.UtcNow.ToString("O")
            });

        await _connection.ExecuteAsync(@"
            INSERT INTO SyncSessionTables (SessionId, TableName, TempTableName, UsesSharedTable, ProcessingPriority)
            VALUES (@SessionId, 'Customers', 'TempPush_Customers_ACTIVE', 0, 1)",
            new { SessionId = activeSessionId.ToString() });

        // Act
        var orphanedTables = await service.FindOrphanedTempTables();

        // Assert
        orphanedTables.Should().Contain("TempPush_Customers_ORPHANED");
        orphanedTables.Should().NotContain("TempPush_Customers_ACTIVE");
    }

    [Fact]
    public async Task TempTableCleanup_ShouldDropOrphanedTables()
    {
        // Arrange
        var service = new TempTableCleanupService(_database!, NullLogger<TempTableCleanupService>.Instance, _config!);

        await _connection!.ExecuteAsync(@"
            CREATE TABLE TempPull_Orders_ORPHAN123 (
                Id TEXT PRIMARY KEY,
                OrderNumber TEXT NOT NULL
            )");

        // Act
        var droppedCount = await service.DropOrphanedTempTables();

        // Assert
        droppedCount.Should().Be(1);

        var tableExists = await _connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'TempPull_Orders_ORPHAN123'");
        tableExists.Should().Be(0);
    }

    [Fact]
    public async Task TempTableCleanup_ShouldGetStatistics()
    {
        // Arrange
        var service = new TempTableCleanupService(_database!, NullLogger<TempTableCleanupService>.Instance, _config!);

        await _connection!.ExecuteAsync(@"
            CREATE TABLE TempPush_Items_XYZ789 (
                Id TEXT PRIMARY KEY
            )");

        for (int i = 0; i < 5; i++)
        {
            await _connection.ExecuteAsync(@"
                INSERT INTO TempPushCustomers (SessionId, Id, Name, Email, ModifiedAtUtc)
                VALUES (@SessionId, @Id, 'Test', 'test@example.com', @Now)",
                new
                {
                    SessionId = Guid.NewGuid().ToString(),
                    Id = Guid.NewGuid().ToString(),
                    Now = DateTime.UtcNow.ToString("O")
                });
        }

        // Act
        var stats = await service.GetTempTableStatistics();

        // Assert
        stats.TotalDedicatedTables.Should().Be(1);
        stats.OrphanedTables.Should().Be(1);
        stats.SharedTableRowCounts["TempPushCustomers"].Should().Be(5);
    }

    #endregion
}
