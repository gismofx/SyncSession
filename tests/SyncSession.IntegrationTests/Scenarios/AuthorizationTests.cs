using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs.Push;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Integration tests for authorization enforcement on SyncSession endpoints (Session 37k).
///
/// Two factory configurations are tested:
///   requireAuthorization: false — both SyncAccess and SyncAdminAccess are allow-all;
///                                 no credentials required on any endpoint.
///   requireAuthorization: true  — TestAuthHandler is the auth scheme; requests without
///                                 the X-Test-TenantId header are unauthenticated → 401.
///
/// TestAuthHandler authenticates any request that carries X-Test-TenantId.
/// SyncAdminAccess is defined as RequireAuthenticatedUser() in the test factory,
/// mirroring a consumer that hasn't yet added a role claim restriction.
/// </summary>
[Collection("MariaDB Collection")]
public class AuthorizationTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;

    public AuthorizationTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(AuthorizationTests));
    }

    public async Task DisposeAsync() => await Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PushSessionBeginRequest MinimalBeginPushRequest() => new()
    {
        DeviceId = Guid.NewGuid(),
        Tables = [new() { TableName = "Customers", EstimatedRecordCount = 0 }]
    };

    /// <summary>
    /// Adds the protocol version header required for sync entry-point endpoints.
    /// Without it the server returns 426, masking the auth result we're testing.
    /// </summary>
    private static void AddProtocolHeader(HttpClient client) =>
        client.DefaultRequestHeaders.Add(
            SyncProtocolVersion.ProtocolHeader,
            SyncProtocolVersion.Current.ToString());

    /// <summary>
    /// Simulates an authenticated user by providing the tenant header that
    /// TestAuthHandler converts into a ClaimsPrincipal.
    /// </summary>
    private static void AddAuthHeader(HttpClient client) =>
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.TenantIdHeader,
            Guid.NewGuid().ToString());

    // ── RequireAuthorization = false (allow-all) ──────────────────────────────

    [Fact]
    public async Task SyncEndpoint_AuthDisabled_NoCredentials_IsNotRejected()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: false);
        using var client = factory.CreateClient();
        AddProtocolHeader(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        // Auth is off — 401 must not occur regardless of credentials
        ((int)response.StatusCode).Should().NotBe(401);
    }

    [Fact]
    public async Task AdminEndpoint_AuthDisabled_NoCredentials_IsNotRejected()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/admin/maintenance/enable", null);

        ((int)response.StatusCode).Should().NotBe(401);
    }

    // ── RequireAuthorization = true — sync endpoints ──────────────────────────

    [Fact]
    public async Task SyncEndpoint_AuthEnabled_NoCredentials_Returns401()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        using var client = factory.CreateClient();
        AddProtocolHeader(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SyncEndpoint_AuthEnabled_WithCredentials_IsNotRejected()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        using var client = factory.CreateClient();
        AddProtocolHeader(client);
        AddAuthHeader(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/sync/push/begin", MinimalBeginPushRequest());

        // Authenticated — must not be 401 (may be 200, 400, or other depending on DB state)
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    // ── RequireAuthorization = true — admin endpoints ─────────────────────────

    [Fact]
    public async Task AdminEndpoint_AuthEnabled_NoCredentials_Returns401()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/admin/maintenance/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_AuthEnabled_WithCredentials_Returns200()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        using var client = factory.CreateClient();
        AddAuthHeader(client);

        var response = await client.PostAsync("/api/v1/admin/maintenance/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminGetStatus_AuthEnabled_NoCredentials_Returns401()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/maintenance");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
