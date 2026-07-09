using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Core.DTOs.Push;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// End-to-end reproduction of the CreationDate timezone-shift bug (Session 40 diagnosis).
///
/// A user-entered business date (Order.OrderDate — the sample analogue of DVMApp's
/// History.CreationDate) is pushed as a JSON string and must land in the production table exactly
/// as entered: a pure, timezone-free wall-clock value.
///
/// The server temp-insert path (MySqlServerDatabase.InsertBatchIntoTempTableAsync ->
/// EntityReflectionHelper.UnwrapJsonElements -> UnwrapJsonElement) coerces any date-shaped JSON
/// string via `dto.UtcDateTime`, converting it to UTC and shifting the stored value. This test
/// drives a value through the real begin -> temp insert -> queue upsert -> production-table path
/// and asserts the wall-clock survives.
///
/// The pushed OrderDate carries an explicit -05:00 offset so the shift is deterministic on any
/// host timezone (the no-offset + non-UTC-server variant is covered by the matching unit test).
///
/// EXPECTED STATE: FAILS against the current implementation — proving the bug through temp -> master.
/// </summary>
[Collection("MariaDB Collection")]
public class CreationDateTimeZonePreservationIntegrationTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public CreationDateTimeZonePreservationIntegrationTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task Push_BusinessDate_PreservesWallClock_ThroughTempToProductionTable()
    {
        // The exact wall-clock the user entered: 2026-07-06 7:29:02 PM. Must survive to the master table.
        var enteredWallClock = new DateTime(2026, 7, 6, 19, 29, 2, DateTimeKind.Unspecified);

        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(Push_BusinessDate_PreservesWallClock_ThroughTempToProductionTable));

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var metadataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(connectionString, metadataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);
        var sessionTracker = new SessionTracker(serverDb, tempTableManager, NullLogger<SessionTracker>.Instance);
        var queueProcessor = new SyncQueueProcessor(serverDb, tempTableManager, NullLogger<SyncQueueProcessor>.Instance);

        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // The Customer satisfies the Order's foreign key; both go in one session so the queue
        // processor upserts Customers (priority 1) before Orders (priority 2).
        var customerRow = TestDataGenerator.CreateCustomerDict(id: customerId, name: "FK Owner");

        var orderRow = TestDataGenerator.CreateOrderDict(customerId, id: orderId);
        // Replace OrderDate with the exact production wire shape: a JSON string with NO offset
        // (clients serialize via ToString("s")). This is what makes UnwrapJsonElement run its
        // date branch, where the server used to fabricate its own local offset.
        orderRow["OrderDate"] = JsonSerializer.Deserialize<JsonElement>("\"2026-07-06T19:29:02\"");

        // ModifiedAtUtc is written client-side as DateTime.UtcNow and serialized without a 'Z'.
        // It must be stored as-is, and can never end up later than the session's commit time.
        var clientModifiedAtUtc = new DateTime(2026, 7, 6, 23, 29, 19, DateTimeKind.Unspecified);
        orderRow["ModifiedAtUtc"] = JsonSerializer.Deserialize<JsonElement>("\"2026-07-06T23:29:19\"");


        // Push through the real production flow: begin -> temp insert -> ready -> queue upsert.
        var begin = new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = TestDatabaseFactory.GetTableSyncInfos(
                config, new Dictionary<string, int> { ["Customers"] = 1, ["Orders"] = 1 })
        };
        var sessionId = (await sessionTracker.CreatePushSessionAsync(begin)).SessionId;

        // Force the server process into US Central (UTC-5) for the temp-table insert, where
        // UnwrapJsonElement runs. Without this the test would pass vacuously on a UTC host,
        // because an offset-less string parsed in UTC produces no shift.
        using (ForceLocalTimeZone(-5))
        {
            await tempTableManager.InsertBatchAsync(sessionId, "Customers", new List<Dictionary<string, object?>> { customerRow });
            await sessionTracker.CompleteTableAsync(sessionId, "Customers", totalRecordsSent: 1);

            await tempTableManager.InsertBatchAsync(sessionId, "Orders", new List<Dictionary<string, object?>> { orderRow });
            await sessionTracker.CompleteTableAsync(sessionId, "Orders", totalRecordsSent: 1);
        }

        await sessionTracker.MarkSessionReadyAsync(sessionId);
        await queueProcessor.ProcessReadySessionsAsync(default);

        // Assert - the production Orders table must hold the entered wall-clock, timezone stripped.
        using var connection = await _dbFactory.GetConnectionAsync();
        var stored = await connection.QuerySingleAsync<DateTime>(
            "SELECT OrderDate FROM Orders WHERE Id = @Id", new { Id = orderId });

        stored.Should().Be(enteredWallClock,
            "a user-entered business date must be stored exactly as entered, with no timezone conversion");

        // Assert - ModifiedAtUtc preserved as sent, and never later than the session commit.
        var storedModified = await connection.QuerySingleAsync<DateTime>(
            "SELECT ModifiedAtUtc FROM Orders WHERE Id = @Id", new { Id = orderId });
        var committedAtUtc = await connection.QuerySingleAsync<DateTime>(
            "SELECT CommittedAtUtc FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = sessionId.ToString() });

        storedModified.Should().Be(clientModifiedAtUtc,
            "the client-supplied UTC timestamp must be stored as-is, not shifted by the server offset");
        storedModified.Should().BeBefore(committedAtUtc,
            "a record cannot be modified after the sync session that carried it committed");
    }

    // -- helpers --------------------------------------------------------------

    private static IDisposable ForceLocalTimeZone(double offsetHours)
        => new LocalTimeZoneOverride(TimeZoneInfo.CreateCustomTimeZone(
            $"TEST_UTC{offsetHours}", TimeSpan.FromHours(offsetHours),
            "Test fixed-offset zone", "Test fixed-offset zone"));

    private sealed class LocalTimeZoneOverride : IDisposable
    {
        private readonly object _cachedData;
        private readonly FieldInfo _localField;
        private readonly TimeZoneInfo _original;

        public LocalTimeZoneOverride(TimeZoneInfo tz)
        {
            var cachedDataField = typeof(TimeZoneInfo).GetField(
                "s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("TimeZoneInfo.s_cachedData not found.");
            _cachedData = cachedDataField.GetValue(null)!;
            _localField = _cachedData.GetType().GetField(
                "_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("CachedData._localTimeZone not found.");

            _original = TimeZoneInfo.Local;
            _localField.SetValue(_cachedData, tz);
        }

        public void Dispose() => _localField.SetValue(_cachedData, _original);
    }
}

