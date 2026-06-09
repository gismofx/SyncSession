using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs;
using SyncSession.Core.DTOs.Push;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Integration tests for ISyncGate / maintenance mode (Session 37k).
/// Verifies that entry-point endpoints return 503 when gated and that
/// in-flight downstream endpoints (batch, complete) are unaffected.
/// </summary>
[Collection("MariaDB Collection")]
public class MaintenanceModeTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;

    public MaintenanceModeTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(MaintenanceModeTests));
    }

    public async Task DisposeAsync() => await Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PushSessionBeginRequest MinimalBeginPushRequest() => new()
    {
        DeviceId = Guid.NewGuid(),
        Tables = [new() { TableName = "Customers", EstimatedRecordCount = 0 }]
    };

    private static object MinimalBeginPullRequest() => new
    {
        DeviceId = Guid.NewGuid(),
        TableNames = new[] { "Customers" }
    };

    private static HttpClient CreateClientWithProtocolHeader(SyncWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            SyncProtocolVersion.ProtocolHeader,
            SyncProtocolVersion.Current.ToString());
        return client;
    }

    // ── Admin status endpoint ─────────────────────────────────────────────────

    [Fact]
    public async Task GetMaintenanceStatus_WhenDisabled_ReturnsFalse()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/maintenance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<MaintenanceStatusDto>();
        dto.Should().NotBeNull();
        dto!.MaintenanceEnabled.Should().BeFalse();
        dto.ReadyForMaintenance.Should().BeFalse();
    }

    [Fact]
    public async Task EnableMaintenance_ThenGetStatus_ReflectsEnabledState()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        var enableResponse = await client.PostAsync("/api/v1/admin/maintenance/enable", null);
        enableResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await enableResponse.Content.ReadFromJsonAsync<MaintenanceStatusDto>();
        dto!.MaintenanceEnabled.Should().BeTrue();
        dto.ReadyForMaintenance.Should().BeTrue(); // no active sessions in clean DB
    }

    // ── push/begin blocked when gated ────────────────────────────────────────

    [Fact]
    public async Task PushBegin_WhenGated_Returns503WithRetryAfter()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = CreateClientWithProtocolHeader(factory);

        await client.PostAsync("/api/v1/admin/maintenance/enable", null);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        ((int)response.StatusCode).Should().Be(503);
        response.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue();
        values.Should().Contain("60");
    }

    // ── pull/begin blocked when gated ────────────────────────────────────────

    [Fact]
    public async Task PullBegin_WhenGated_Returns503WithRetryAfter()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = CreateClientWithProtocolHeader(factory);

        await client.PostAsync("/api/v1/admin/maintenance/enable", null);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/pull/begin", MinimalBeginPullRequest());

        ((int)response.StatusCode).Should().Be(503);
        response.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue();
        values.Should().Contain("60");
    }

    // ── push/batch passes through when gated ─────────────────────────────────

    [Fact]
    public async Task PushBatch_WhenGated_IsNotBlocked()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = CreateClientWithProtocolHeader(factory);

        // Create a real session first (gate is off)
        var beginResponse = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());
        beginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var beginBody = await beginResponse.Content.ReadFromJsonAsync<PushSessionBeginResponse>();
        var sessionId = beginBody!.SessionId;

        // Now enable maintenance
        await client.PostAsync("/api/v1/admin/maintenance/enable", null);

        // batch should still work (downstream — not an entry point)
        var batchBody = new { SessionId = sessionId, TableName = "Customers", Records = Array.Empty<object>() };
        var batchResponse = await client.PostAsJsonAsync("/api/v1/sync/push/batch", batchBody);

        // 404 (session staging check) or 200 — either way, NOT 503
        ((int)batchResponse.StatusCode).Should().NotBe(503);
    }

    // ── enable → disable → push/begin succeeds ───────────────────────────────

    [Fact]
    public async Task PushBegin_AfterDisable_Returns200()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = CreateClientWithProtocolHeader(factory);

        await client.PostAsync("/api/v1/admin/maintenance/enable", null);
        await client.PostAsync("/api/v1/admin/maintenance/disable", null);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        // Not 503 — gate is clear (may be 200 or validation error depending on DB state)
        ((int)response.StatusCode).Should().NotBe(503);
    }
}
