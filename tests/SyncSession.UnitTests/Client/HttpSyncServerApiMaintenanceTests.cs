using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SyncSession.Client.Http;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Unit tests for maintenance-mode (503) handling in HttpSyncServerApi.
/// The server states why it refused and how long to wait; before this, the client threw
/// EnsureSuccessStatusCode's resource string and both facts were lost.
/// </summary>
public class HttpSyncServerApiMaintenanceTests
{
    private const string BaseUrl = "https://sync.example.com/api";
    private static readonly Guid DeviceId = Guid.NewGuid();
    private const string GatedReason = "Server is in maintenance mode. Retry after 60 seconds.";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpSyncServerApi BuildApi(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(response)), BaseUrl, DeviceId);

    private static List<TableSyncInfo> OneTable() =>
        [new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 0 }];

    /// <summary>The shape push/begin and pull/begin return when gated: the DTO, as JSON.</summary>
    private static HttpResponseMessage GatedJson(string reason = GatedReason, string? retryAfter = "60")
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent.Create(new PullSessionBeginResponse
            {
                Success = false,
                ErrorMessage = reason
            })
        };
        if (retryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    /// <summary>What a 503 from something in front of the app looks like: not our body.</summary>
    private static HttpResponseMessage GatedByProxy(string body = "<html><body>503</body></html>") =>
        new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(body) };

    // ── Tests — the gated begin calls ─────────────────────────────────────────

    [Fact]
    public async Task BeginPullAsync_Receives503_ThrowsSyncMaintenanceException()
    {
        var api = BuildApi(GatedJson());

        var act = () => api.BeginPullAsync(["Customers"]);

        await act.Should().ThrowAsync<SyncMaintenanceException>();
    }

    [Fact]
    public async Task BeginPushAsync_Receives503_ThrowsSyncMaintenanceException()
    {
        var api = BuildApi(GatedJson());

        var act = () => api.BeginPushAsync(OneTable());

        await act.Should().ThrowAsync<SyncMaintenanceException>();
    }

    [Fact]
    public async Task BeginPullAsync_Receives503_CarriesTheServersOwnReason()
    {
        var api = BuildApi(GatedJson());

        var ex = await Assert.ThrowsAsync<SyncMaintenanceException>(
            () => api.BeginPullAsync(["Customers"]));

        ex.ServerMessage.Should().Be(GatedReason);
        ex.Message.Should().Contain("maintenance mode");
    }

    [Fact]
    public async Task BeginPullAsync_Receives503_HonoursRetryAfterHeader()
    {
        var api = BuildApi(GatedJson(retryAfter: "30"));

        var ex = await Assert.ThrowsAsync<SyncMaintenanceException>(
            () => api.BeginPullAsync(["Customers"]));

        ex.RetryAfterSeconds.Should().Be(30);
    }

    [Fact]
    public async Task BeginPullAsync_Receives503WithoutRetryAfter_FallsBackToDefault()
    {
        var api = BuildApi(GatedJson(retryAfter: null));

        var ex = await Assert.ThrowsAsync<SyncMaintenanceException>(
            () => api.BeginPullAsync(["Customers"]));

        ex.RetryAfterSeconds.Should().Be(SyncMaintenanceException.DefaultRetryAfterSeconds);
    }

    [Fact]
    public async Task BeginPullAsync_Receives503FromAProxy_ReportsNoServerMessage()
    {
        var api = BuildApi(GatedByProxy());

        var ex = await Assert.ThrowsAsync<SyncMaintenanceException>(
            () => api.BeginPullAsync(["Customers"]));

        ex.ServerMessage.Should().BeNull("an HTML error page is not a reason the sync server gave");
        ex.RetryAfterSeconds.Should().Be(SyncMaintenanceException.DefaultRetryAfterSeconds);
    }

    // ── The wire casing the real server uses ──────────────────────────────────

    /// <summary>
    /// The server sends PascalCase — <c>SyncSessionExtensions.cs:110</c> sets
    /// <c>PropertyNamingPolicy = null</c>. Found the hard way: the camelCase fake above passed
    /// while the same case against the real server returned no reason at all.
    /// </summary>
    [Fact]
    public async Task BeginPullAsync_Receives503WithPascalCaseBody_CarriesTheServersReason()
    {
        var gated = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"Success\":false,\"ErrorMessage\":\"" + GatedReason + "\"}",
                Encoding.UTF8,
                "application/json")
        };
        gated.Headers.TryAddWithoutValidation("Retry-After", "60");

        var ex = await Assert.ThrowsAsync<SyncMaintenanceException>(
            () => BuildApi(gated).BeginPullAsync(["Customers"]));

        ex.ServerMessage.Should().Be(GatedReason);
    }

    // ── The seed endpoint answers in plain text, not JSON (SyncController.cs:454-460) ──

    [Fact]
    public async Task StreamSeedAsync_Receives503PlainText_ThrowsWithTheServersReason()
    {
        var gated = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(GatedReason)
        };
        gated.Headers.TryAddWithoutValidation("Retry-After", "60");

        var seed = new HttpSeedServerApi(
            new HttpClient(new StubHandler(gated)),
            BaseUrl,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpSeedServerApi>.Instance);

        var ex = await Assert.ThrowsAsync<SyncMaintenanceException>(async () =>
        {
            await foreach (var _ in seed.StreamSeedAsync(Guid.NewGuid(), DeviceId)) { }
        });

        ex.ServerMessage.Should().Be(GatedReason);
        ex.RetryAfterSeconds.Should().Be(60);
    }

    // ── Control: nothing else changes shape ───────────────────────────────────

    [Fact]
    public async Task BeginPullAsync_Receives500_StillThrowsHttpRequestException()
    {
        var api = BuildApi(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => api.BeginPullAsync(["Customers"]);

        await act.Should().ThrowAsync<HttpRequestException>(
            "only 503 is the gate — other failures must keep failing as they did");
    }

    // ── Stub handler ──────────────────────────────────────────────────────────

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
