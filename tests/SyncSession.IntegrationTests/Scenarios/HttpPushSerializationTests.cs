using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;
using SyncSession.Core.Services;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Verifies that records pushed over HTTP with camelCase JSON keys are correctly
/// normalized, inserted, and committed to the database.
///
/// Regression test for the camelCase normalization bug: when System.Text.Json
/// deserializes PushBatchRequest.Records, dictionary keys arrive as camelCase
/// (e.g. "id", "name", "modifiedAtUtc"). Without normalization these would
/// not match the PascalCase column names in GetValidPushColumns, causing
/// records to be silently dropped or inserts to use wrong column names.
///
/// Also validates that ISO 8601 datetime strings with timezone offsets are
/// converted to UTC before reaching MySQL, which rejects offset notation.
/// </summary>
[Collection("MariaDB Collection")]
public class HttpPushSerializationTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;

    public HttpPushSerializationTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(HttpPushSerializationTests));
    }

    public async Task DisposeAsync() => await Task.CompletedTask;

    [Fact]
    public async Task PushBatch_WithCamelCaseJson_RecordsLandInDatabase()
    {
        // Arrange
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-SyncSystem-Protocol", "1");

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var metadataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_connectionString, metadataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        var deviceId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        // Use a local datetime with offset — this is what System.Text.Json produces
        // when serializing a DateTimeOffset or a DateTime with Kind=Local.
        var modifiedAt = new DateTimeOffset(2026, 2, 22, 16, 39, 52, TimeSpan.FromHours(-5));

        // Begin push session via HTTP
        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(config,
                new Dictionary<string, int> { ["Customers"] = 1 })
        };

        var beginResponse = await client.PostAsJsonAsync("/api/v1/sync/push/begin", beginRequest);
        beginResponse.IsSuccessStatusCode.Should().BeTrue(
            because: $"begin push failed: {await beginResponse.Content.ReadAsStringAsync()}");

        var beginResult = await beginResponse.Content.ReadFromJsonAsync<PushSessionBeginResponse>();
        beginResult!.Success.Should().BeTrue();
        var sessionId = beginResult.SessionId;

        // Build batch request — records use camelCase keys (real HTTP path)
        // System.Text.Json serializes Dictionary<string, object?> keys as-is but
        // entity properties use camelCase when using JsonSerializerDefaults.Web.
        // We explicitly use camelCase here to simulate what the client library sends.
        var records = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["id"]               = customerId.ToString(),
                ["tenantId"]         = Guid.NewGuid().ToString(),
                ["name"]             = "Test Customer",
                ["email"]            = "test@example.com",
                ["phone"]            = "555-1234",
                ["address"]          = "123 Main St",
                ["modifiedByUserId"] = "user-001",
                ["isDeleted"]        = false,
                ["modifiedAtUtc"]    = modifiedAt.ToString("O")   // ISO 8601 with offset
            }
        };

        var batchRequest = new PushBatchRequest
        {
            SessionId = sessionId,
            TableName = "Customers",
            Records = records
        };

        // Act — POST batch via full HTTP stack (triggers JsonElement wrapping + camelCase keys)
        var batchResponse = await client.PostAsJsonAsync("/api/v1/sync/push/batch", batchRequest);
        batchResponse.IsSuccessStatusCode.Should().BeTrue(
            because: $"push batch failed: {await batchResponse.Content.ReadAsStringAsync()}");

        var batchResult = await batchResponse.Content.ReadFromJsonAsync<PushBatchResponse>();
        batchResult!.Success.Should().BeTrue();
        batchResult.RecordsAccepted.Should().Be(1);

        // Complete table + session
        var tableCompleteResponse = await client.PostAsJsonAsync("/api/v1/sync/push/table-complete",
            new PushTableCompleteRequest { SessionId = sessionId, TableName = "Customers", TotalRecordsSent = 1 });
        tableCompleteResponse.IsSuccessStatusCode.Should().BeTrue();

        var sessionCompleteResponse = await client.PostAsJsonAsync("/api/v1/sync/push/complete",
            new PushSessionCompleteRequest { SessionId = sessionId });
        sessionCompleteResponse.IsSuccessStatusCode.Should().BeTrue();

        // Wait for background queue processor to commit the session
        await WaitForSessionCommittedAsync(sessionId);

        // Assert — record exists in Customers table with correct values
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var row = await connection.QuerySingleOrDefaultAsync(
            "SELECT Id, Name, Email, ModifiedByUserId, ModifiedAtUtc, IsDeleted FROM Customers WHERE Id = @Id",
            new { Id = customerId.ToString() });

        ((object?)row).Should().NotBeNull("record should have been committed to Customers table");

        string name = row!.Name;
        string email = row!.Email;
        string modifiedByUserId = row!.ModifiedByUserId;
        DateTime modifiedAtUtc = row!.ModifiedAtUtc;

        name.Should().Be("Test Customer");
        email.Should().Be("test@example.com");
        modifiedByUserId.Should().Be("user-001");

        // Datetime must have been converted to UTC — MySQL stores/returns UTC DateTime
        modifiedAtUtc.Kind.Should().Be(DateTimeKind.Unspecified); // MySQL driver returns Unspecified
        modifiedAtUtc.Should().BeCloseTo(modifiedAt.UtcDateTime, TimeSpan.FromSeconds(1),
            because: "ISO 8601 offset datetime should be converted to UTC before insert");
    }

    [Fact]
    public async Task PushBatch_WithCamelCaseJson_DatetimeWithOffset_IsStoredAsUtc()
    {
        // Arrange — focuses specifically on the datetime conversion edge case
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-SyncSystem-Protocol", "1");

        var config = TestDatabaseFactory.CreateDefaultSyncConfiguration();
        var metadataCache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        var serverDb = new MySqlServerDatabase(_connectionString, metadataCache, config, NullLogger<MySqlServerDatabase>.Instance);
        var tempTableManager = new TempTableManager(serverDb, config, NullLogger<TempTableManager>.Instance);

        var deviceId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        // EST offset: -5 hours. UTC equivalent = input + 5h
        var localTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(-5));
        var expectedUtc = localTime.UtcDateTime; // 2026-01-01 17:00:00 UTC

        var beginRequest = new PushSessionBeginRequest
        {
            DeviceId = deviceId,
            Tables = TestDatabaseFactory.GetTableSyncInfos(config,
                new Dictionary<string, int> { ["Customers"] = 1 })
        };

        var beginResponse = await client.PostAsJsonAsync("/api/v1/sync/push/begin", beginRequest);
        var beginResult = await beginResponse.Content.ReadFromJsonAsync<PushSessionBeginResponse>();
        var sessionId = beginResult!.SessionId;

        var records = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["id"]               = customerId.ToString(),
                ["tenantId"]         = Guid.NewGuid().ToString(),
                ["name"]             = "Datetime Test",
                ["modifiedByUserId"] = "system",
                ["isDeleted"]        = false,
                ["modifiedAtUtc"]    = localTime.ToString("O")  // "2026-01-01T12:00:00-05:00"
            }
        };

        // Act
        await client.PostAsJsonAsync("/api/v1/sync/push/batch",
            new PushBatchRequest { SessionId = sessionId, TableName = "Customers", Records = records });
        await client.PostAsJsonAsync("/api/v1/sync/push/table-complete",
            new PushTableCompleteRequest { SessionId = sessionId, TableName = "Customers", TotalRecordsSent = 1 });
        await client.PostAsJsonAsync("/api/v1/sync/push/complete",
            new PushSessionCompleteRequest { SessionId = sessionId });

        await WaitForSessionCommittedAsync(sessionId);

        // Assert
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var storedUtc = await connection.ExecuteScalarAsync<DateTime>(
            "SELECT ModifiedAtUtc FROM Customers WHERE Id = @Id",
            new { Id = customerId.ToString() });

        storedUtc.Should().BeCloseTo(expectedUtc, TimeSpan.FromSeconds(1),
            because: "offset datetime should be stored as UTC, not local time");
    }

    private async Task WaitForSessionCommittedAsync(Guid sessionId, int maxWaitMs = 15000)
    {
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            var status = await connection.ExecuteScalarAsync<string?>(
                "SELECT Status FROM SessionRecords WHERE SessionId = @SessionId",
                new { SessionId = sessionId.ToString() });

            if (status == "Committed") return;
            if (status == "Failed")
                throw new Exception($"Session {sessionId} failed during background processing");

            await Task.Delay(500);
        }

        throw new TimeoutException($"Session {sessionId} did not reach Committed status within {maxWaitMs}ms");
    }
}
