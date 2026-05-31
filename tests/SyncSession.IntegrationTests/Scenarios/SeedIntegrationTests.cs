using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Client.Http;
using SyncSession.Client.Seeding;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Integration tests for the seed streaming endpoint and its surrounding machinery:
/// snapshot table lifecycle, post-seed delta pull correctness, acknowledge endpoint,
/// and orphan cleanup.
/// Each test gets an isolated MariaDB database via <see cref="MariaDbFixture"/>.
/// </summary>
[Collection("MariaDB Collection")]
public class SeedIntegrationTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;

    /// <inheritdoc cref="SeedIntegrationTests"/>
    public SeedIntegrationTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(SeedIntegrationTests));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a factory + HTTP client. Caller must dispose the factory.</summary>
    private (SyncWebApplicationFactory factory, HttpClient client) CreateClient()
    {
        var factory = new SyncWebApplicationFactory(_connectionString);
        return (factory, factory.CreateClient());
    }

    /// <summary>
    /// Seeds Customer records directly into the server database via
    /// <see cref="IDirectWriteService"/> and returns the tenant ID used.
    /// </summary>
    private static async Task<Guid> SeedCustomerAsync(SyncWebApplicationFactory factory, Guid tenantId, int count = 1)
    {
        using var scope = factory.Services.CreateScope();
        var writeService = scope.ServiceProvider.GetRequiredService<IDirectWriteService>();

        for (int i = 0; i < count; i++)
        {
            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = $"Seed Customer {i}",
                Email = $"seed{i}@test.com",
                Phone = "555-0000",
                Address = "123 Test St",
                ModifiedByUserId = "seed-test",
                IsDeleted = false
            };
            await writeService.WriteAsync(customer, "seed-test", tenantId.ToString());
        }

        return tenantId;
    }

    /// <summary>Reads all NDJSON lines from the seed stream response body.</summary>
    private static async Task<List<SeedLine>> ReadAllSeedLinesAsync(HttpResponseMessage response)
    {
        var lines = new List<SeedLine>();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines.Add(JsonSerializer.Deserialize<SeedLine>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
        return lines;
    }

    /// <summary>Calls POST /api/v1/sync/seed/acknowledge and asserts 204 No Content.</summary>
    private static async Task AcknowledgeSeedAsync(HttpClient client, Guid deviceId, Guid tenantId)
    {
        var body = new SeedAcknowledgeRequest { DeviceId = deviceId, TenantId = tenantId };
        var response = await client.PostAsJsonAsync("/api/v1/sync/seed/acknowledge", body);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Tests 1–3 ─────────────────────────────────────────────────────────────

    /// <summary>Verifies GET returns 200 and Content-Type: application/x-ndjson.</summary>
    [Fact]
    public async Task SeedEndpoint_Returns200_WithNdjsonContentType()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.GetAsync(
            $"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-ndjson");

        // Drain the stream so server-side cleanup can complete
        await response.Content.ReadAsStreamAsync().ContinueWith(t => t.Result.DisposeAsync());
    }

    /// <summary>Verifies begin envelope contains correct tenantId and end envelope contains a valid UTC anchor.</summary>
    [Fact]
    public async Task SeedEndpoint_StreamContainsBeginAndEndEnvelopes()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        await SeedCustomerAsync(factory, tenantId, 1);

        var response = await client.GetAsync($"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}");
        var lines = await ReadAllSeedLinesAsync(response);

        var begin = lines.FirstOrDefault(l => l.Type == "begin");
        var end   = lines.FirstOrDefault(l => l.Type == "end");

        begin.Should().NotBeNull("stream must open with a 'begin' line");
        begin!.TenantId.Should().Be(tenantId.ToString());

        end.Should().NotBeNull("stream must close with an 'end' line");
        end!.Anchor.Should().NotBeNull();
        end.Anchor!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>Row count in stream must equal SELECT COUNT(*) WHERE TenantId for the seeded tenant.</summary>
    [Fact]
    public async Task SeedEndpoint_RowCountsMatchDatabase()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        const int expectedCount = 3;
        await SeedCustomerAsync(factory, tenantId, expectedCount);

        var response = await client.GetAsync($"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}");
        var lines = await ReadAllSeedLinesAsync(response);

        var rowCount = lines
            .Where(l => l.Table == "Customers")
            .Sum(l => l.Type == "rows" ? (l.Rows?.Count ?? 0) : (l.Type == "row" ? 1 : 0));

        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var dbCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Customers WHERE TenantId = @TenantId",
            new { TenantId = tenantId.ToString() });

        rowCount.Should().Be(dbCount);
        dbCount.Should().Be(expectedCount);
    }

    // ── Tests 4–6 ─────────────────────────────────────────────────────────────

    /// <summary>SeedSnapshots row with Status=Active must exist before the first row line is emitted.</summary>
    [Fact]
    public async Task SeedEndpoint_SnapshotRowPresentBeforeRowsEmitted()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        await SeedCustomerAsync(factory, tenantId, 5);

        using var response = await client.GetAsync(
            $"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? snapshotStatus = null;
        while (!reader.EndOfStream)
        {
            var raw = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = JsonSerializer.Deserialize<SeedLine>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            if (line.Type == "rows" || line.Type == "row")
            {
                // Check snapshot exists at this point
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync();
                snapshotStatus = await conn.ExecuteScalarAsync<string?>(
                    "SELECT Status FROM SeedSnapshots WHERE DeviceId = @DeviceId AND TenantId = @TenantId",
                    new { DeviceId = deviceId.ToString(), TenantId = tenantId.ToString() });
                break;
            }
        }

        // Drain remaining stream
        while (!reader.EndOfStream) await reader.ReadLineAsync();

        snapshotStatus.Should().Be("Active",
            "SeedSnapshots row must be Active before first row is emitted");
    }

    /// <summary>SeedSnapshots row and all SeedSnap_* tables for the seed must be deleted after a full successful stream.</summary>
    [Fact]
    public async Task SeedEndpoint_SnapshotCleanedUpAfterSuccessfulStream()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        await SeedCustomerAsync(factory, tenantId, 2);

        var response = await client.GetAsync($"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}");
        // Consume entire stream to trigger server-side finally block
        await ReadAllSeedLinesAsync(response);

        // Poll up to 2 s — the iterator finally block runs after the last yield,
        // which may be slightly after the client finishes reading.
        int snapshotCount = 1;
        string? seedIdStr = null;
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(200);
            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            // Capture the seedId on first pass so we can assert its table is gone
            seedIdStr ??= await conn.ExecuteScalarAsync<string?>(
                "SELECT SeedId FROM SeedSnapshots WHERE DeviceId = @DeviceId",
                new { DeviceId = deviceId.ToString() });

            snapshotCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM SeedSnapshots WHERE DeviceId = @DeviceId",
                new { DeviceId = deviceId.ToString() });

            if (snapshotCount == 0) break;
        }

        snapshotCount.Should().Be(0, "SeedSnapshots row must be deleted after successful stream");

        // If we captured a seedId, verify its specific snapshot tables are also gone
        if (seedIdStr != null)
        {
            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();
            // SeedSnap tables use seedId with 'N' format (no dashes)
            var seedIdNoDashes = seedIdStr.Replace("-", "");
            var specificSnapCount = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name LIKE @Pattern",
                new { Pattern = $"SeedSnap_%_{seedIdNoDashes}" });
            specificSnapCount.Should().Be(0, "snapshot tables for this seedId must be dropped after successful stream");
        }
    }

    /// <summary>SeedSnapshots.Status must transition to Failed when the stream is cancelled mid-flight.</summary>
    [Fact]
    public async Task SeedEndpoint_SnapshotStatusFailedOnCancellation()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        await SeedCustomerAsync(factory, tenantId, 10);

        // Call SeedService directly via DI — avoids WebApplicationFactory in-memory
        // transport, which doesn't reliably propagate RequestAborted on client cancellation.
        using var scope = factory.Services.CreateScope();
        var seedService = scope.ServiceProvider.GetRequiredService<ISeedService>();
        using var cts = new CancellationTokenSource();

        int rowsSeen = 0;
        try
        {
            await foreach (var line in seedService.StreamSeedAsync(tenantId, deviceId, cts.Token))
            {
                if ((line.Type == "rows" || line.Type == "row") && ++rowsSeen >= 1)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException) { /* expected */ }

        // Give the finally block a moment to execute UpdateSeedSnapshotStatusAsync
        await Task.Delay(200);

        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var status = await conn.ExecuteScalarAsync<string?>(
            "SELECT Status FROM SeedSnapshots WHERE DeviceId = @DeviceId AND TenantId = @TenantId",
            new { DeviceId = deviceId.ToString(), TenantId = tenantId.ToString() });

        status.Should().Be("Failed",
            "SeedService finally block must mark snapshot as Failed when iteration is cancelled");
    }

    // ── Tests 7–8 ─────────────────────────────────────────────────────────────

    /// <summary>POST /seed/acknowledge must bulk-insert committed sessions into ClientProcessedSessions for the device.</summary>
    [Fact]
    public async Task SeedEndpoint_AcknowledgeSeed_PopulatesClientProcessedSessions()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        // Create at least one committed session by seeding a customer
        await SeedCustomerAsync(factory, tenantId, 1);

        // Full seed stream
        var response = await client.GetAsync($"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}");
        await ReadAllSeedLinesAsync(response);

        // Acknowledge
        await AcknowledgeSeedAsync(client, deviceId, tenantId);

        // Verify ClientProcessedSessions populated
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var processedCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ClientProcessedSessions WHERE DeviceId = @DeviceId",
            new { DeviceId = deviceId.ToString() });

        processedCount.Should().BeGreaterThan(0,
            "acknowledge must bulk-insert committed sessions into ClientProcessedSessions");
    }

    /// <summary>After seed+acknowledge, only sessions committed after the anchor should appear in the next pull.</summary>
    [Fact]
    public async Task SeedEndpoint_PostSeedDeltaPull_ReturnsOnlyNewSessions()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        // Step 1: create pre-seed record (session A auto-committed by DirectWriteService)
        using var scope = factory.Services.CreateScope();
        var writeService = scope.ServiceProvider.GetRequiredService<IDirectWriteService>();
        var preSeedCustomer = new Customer
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            Name = "Pre-Seed", Email = "pre@test.com", Phone = "555-0001",
            Address = "A", ModifiedByUserId = "test", IsDeleted = false
        };
        await writeService.WriteAsync(preSeedCustomer, "test", tenantId.ToString());

        // Get session A's ID from DB for later assertion
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var sessionAId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT SyncSessionId FROM Customers WHERE Id = @Id",
            new { Id = preSeedCustomer.Id.ToString() });

        // Step 2: seed + acknowledge
        var seedResponse = await client.GetAsync($"/api/v1/sync/seed/{tenantId}?deviceId={deviceId}");
        await ReadAllSeedLinesAsync(seedResponse);
        await AcknowledgeSeedAsync(client, deviceId, tenantId);

        // Step 3: create post-seed record (session B)
        var postSeedCustomer = new Customer
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            Name = "Post-Seed", Email = "post@test.com", Phone = "555-0002",
            Address = "B", ModifiedByUserId = "test", IsDeleted = false
        };
        await writeService.WriteAsync(postSeedCustomer, "test", tenantId.ToString());

        var sessionBId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT SyncSessionId FROM Customers WHERE Id = @Id",
            new { Id = postSeedCustomer.Id.ToString() });

        // Step 4: assert session A processed, session B not
        var sessionAProcessed = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ClientProcessedSessions WHERE DeviceId = @DeviceId AND SessionId = @SessionId",
            new { DeviceId = deviceId.ToString(), SessionId = sessionAId.ToString() });

        var sessionBProcessed = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ClientProcessedSessions WHERE DeviceId = @DeviceId AND SessionId = @SessionId",
            new { DeviceId = deviceId.ToString(), SessionId = sessionBId.ToString() });

        sessionAProcessed.Should().Be(1,
            "session A (pre-seed) must be in ClientProcessedSessions after acknowledge");
        sessionBProcessed.Should().Be(0,
            "session B (post-seed) must NOT be in ClientProcessedSessions — it is the delta");
    }

    // ── Tests 9–10 ────────────────────────────────────────────────────────────

    /// <summary>TempTableCleanupService must drop orphaned SeedSnap_* tables and delete their SeedSnapshots rows.</summary>
    [Fact]
    public async Task SeedEndpoint_OrphanSeedSnapshot_CleanedByTempTableCleanupService()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var seedId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        // Manually insert a stale SeedSnapshots row (5h old — exceeds the 4h default)
        var staleTime = DateTime.UtcNow.AddHours(-5);
        await conn.ExecuteAsync(
            @"INSERT INTO SeedSnapshots (SeedId, DeviceId, TenantId, Status, CreatedAtUtc, LastActivityUtc)
              VALUES (@SeedId, @DeviceId, @TenantId, 'Active', @StaleTime, @StaleTime)",
            new {
                SeedId = seedId.ToString(),
                DeviceId = deviceId.ToString(),
                TenantId = tenantId.ToString(),
                StaleTime = staleTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff")
            });

        // Manually create a matching snapshot table
        var snapTableName = $"SeedSnap_Customers_{seedId:N}";
        await conn.ExecuteAsync(
            $"CREATE TABLE `{snapTableName}` (Id VARCHAR(36) PRIMARY KEY, Name VARCHAR(255))");

        // Insert a row so the table actually exists in information_schema
        await conn.ExecuteAsync(
            $"INSERT INTO `{snapTableName}` (Id, Name) VALUES (@Id, 'Test')",
            new { Id = Guid.NewGuid().ToString() });

        // Run TempTableCleanupService — not registered as concrete type; instantiate directly
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IServerDatabase>();
        var config = scope.ServiceProvider.GetRequiredService<ServerSyncConfiguration>();
        var cleanupService = new TempTableCleanupService(db, NullLogger<TempTableCleanupService>.Instance, config);
        await cleanupService.ExecuteCleanupAsync();

        // Assert: snapshot row and table are both gone
        var snapshotCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SeedSnapshots WHERE SeedId = @SeedId",
            new { SeedId = seedId.ToString() });

        var tableCount = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @TableName",
            new { TableName = snapTableName });

        snapshotCount.Should().Be(0, "orphaned SeedSnapshots row must be deleted by cleanup service");
        tableCount.Should().Be(0, "orphaned SeedSnap_* table must be dropped by cleanup service");
    }

    /// <summary>GET without ?deviceId query parameter must return 400 Bad Request.</summary>
    [Fact]
    public async Task SeedEndpoint_MissingDeviceId_ReturnsBadRequest()
    {
        var tenantId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        // Call without ?deviceId query parameter
        var response = await client.GetAsync($"/api/v1/sync/seed/{tenantId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 38B-f: Automatic seed-failure logging ─────────────────────────────────

    /// <summary>A local write failure during seed must persist a Failed SessionRecord row
    /// (SessionType='Seed') with a non-null ErrorMessage, via the real acknowledge endpoint.</summary>
    [Fact]
    public async Task SeedClient_LocalWriteFailure_WritesFailedSessionRow()
    {
        var tenantId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var (factory, client) = CreateClient();
        using var _ = factory;
        using var __ = client;

        // Seed real server-side data so the stream actually emits rows for the writer to choke on.
        await SeedCustomerAsync(factory, tenantId, 3);

        // Real HTTP seed stream + a writer that throws on first write.
        var serverApi = new HttpSeedServerApi(
            client, string.Empty, NullLogger<HttpSeedServerApi>.Instance);
        var seedClient = new SeedClient(
            serverApi, NullLogger<SeedClient>.Instance);

        var dbError = new InvalidOperationException("disk I/O error: client write failed");
        var throwingWriter = new ThrowingSeedWriter(dbError);

        var act = async () => await seedClient.SeedAsync(tenantId, deviceId, throwingWriter);
        await act.Should().ThrowAsync<SeedInterruptedException>();

        // The failure-path acknowledge writes a Seed/Failed session row with the reason.
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(string Status, string? ErrorMessage)>(
            @"SELECT Status, ErrorMessage FROM SessionRecords
              WHERE SessionType = 'Seed' AND DeviceId = @DeviceId
              ORDER BY CreatedAtUtc DESC LIMIT 1",
            new { DeviceId = deviceId.ToString() });

        row.Status.Should().Be("Failed", "a failed local write must persist a Failed seed session");
        row.ErrorMessage.Should().NotBeNullOrEmpty("the failure reason must be recorded");
        row.ErrorMessage.Should().Contain(dbError.Message);
    }

    /// <summary>ISeedDatabaseWriter stub that throws on the first row write — simulates a local DB insert failure.
    /// Implements the raw path too, since the seed stream uses 'rows' bundles for small tables.</summary>
    private sealed class ThrowingSeedWriter : ISeedDatabaseWriter, IRawSeedDatabaseWriter
    {
        private readonly Exception _toThrow;
        public ThrowingSeedWriter(Exception toThrow) => _toThrow = toThrow;

        public Task BeginTableAsync(string tableName, int totalRows, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task WriteRowsAsync(string tableName,
            IReadOnlyList<Dictionary<string, object?>> rows, CancellationToken ct = default)
            => throw _toThrow;

        public Task<int> WriteRowsRawAsync(string tableName,
            IReadOnlyList<string> rawLines, CancellationToken ct = default)
            => throw _toThrow;

        public Task EndTableAsync(string tableName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
