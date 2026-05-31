using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs.Push;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Integration tests for client/server protocol version negotiation (Session 27e).
/// Verifies that the server correctly rejects incompatible protocol versions with
/// 426 Upgrade Required and accepts the current protocol version.
///
/// Uses raw HttpClient (not HttpSyncServerApi) to test the server-side controller
/// path in isolation, independent of client-side header injection logic.
/// </summary>
[Collection("MariaDB Collection")]
public class ProtocolVersionTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;

    public ProtocolVersionTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(ProtocolVersionTests));
    }

    public async Task DisposeAsync() => await Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal valid BeginPush body — we only need the request to reach the
    /// protocol check; we don't care whether the session is actually created.
    /// </summary>
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

    // ── BeginPush — rejection cases ───────────────────────────────────────────

    [Fact]
    public async Task BeginPush_MissingProtocolHeader_Returns426()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        // No protocol header — treated as version 0 on the server
        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        ((int)response.StatusCode).Should().Be(426);
    }

    [Fact]
    public async Task BeginPush_ObsoleteProtocolVersion_Returns426WithVersionDetails()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add(SyncProtocolVersion.ProtocolHeader, "0");

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        ((int)response.StatusCode).Should().Be(426);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("serverMinVersion").GetInt32()
            .Should().Be(SyncProtocolVersion.MinSupported);
        body.GetProperty("serverCurrentVersion").GetInt32()
            .Should().Be(SyncProtocolVersion.Current);
        body.GetProperty("clientVersion").GetInt32()
            .Should().Be(0);
    }

    [Fact]
    public async Task BeginPush_FutureProtocolVersion_Returns426()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        // A version beyond Current is also incompatible (server can't speak it)
        client.DefaultRequestHeaders.Add(
            SyncProtocolVersion.ProtocolHeader,
            (SyncProtocolVersion.Current + 99).ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        ((int)response.StatusCode).Should().Be(426);
    }

    // ── BeginPush — acceptance ────────────────────────────────────────────────

    [Fact]
    public async Task BeginPush_CurrentProtocolVersion_DoesNotReturn426()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add(
            SyncProtocolVersion.ProtocolHeader,
            SyncProtocolVersion.Current.ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        // Any non-426 response confirms the protocol check passed.
        // (200 = session created, 400 = validation error — both are fine here)
        ((int)response.StatusCode).Should().NotBe(426);
    }

    // ── BeginPull — rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task BeginPull_MissingProtocolHeader_Returns426()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/pull/begin", MinimalBeginPullRequest());

        ((int)response.StatusCode).Should().Be(426);
    }

    [Fact]
    public async Task BeginPull_CurrentProtocolVersion_DoesNotReturn426()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add(
            SyncProtocolVersion.ProtocolHeader,
            SyncProtocolVersion.Current.ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/pull/begin", MinimalBeginPullRequest());

        ((int)response.StatusCode).Should().NotBe(426);
    }

    // ── Other endpoints not gated ─────────────────────────────────────────────

    [Fact]
    public async Task PushBatch_WithoutProtocolHeader_IsNotBlocked()
    {
        // Only Begin endpoints are gated — batch/complete/status must NOT check the header.
        // This prevents mid-session failures if the header were ever dropped in transit.
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/sync/push/batch", new
        {
            SessionId = Guid.NewGuid(),
            TableName = "Customers",
            Records = Array.Empty<object>()
        });

        ((int)response.StatusCode).Should().NotBe(426);
    }
}
