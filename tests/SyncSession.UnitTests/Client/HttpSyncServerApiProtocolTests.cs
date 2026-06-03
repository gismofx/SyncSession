using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SyncSession.Client.Http;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Unit tests for HttpSyncServerApi protocol version negotiation.
/// Verifies that 426 responses are correctly translated to SyncProtocolException,
/// and that Begin calls inject the correct protocol headers.
/// </summary>
public class HttpSyncServerApiProtocolTests
{
    private const string BaseUrl = "https://sync.example.com/api";

    private static readonly Guid DeviceId = Guid.NewGuid();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an HttpSyncServerApi backed by a handler that always returns the given response.
    /// </summary>
    private static (HttpSyncServerApi Api, CapturingHandler Handler) BuildApi(HttpResponseMessage response)
    {
        var handler = new CapturingHandler(response);
        var httpClient = new HttpClient(handler);
        var api = new HttpSyncServerApi(httpClient, BaseUrl, DeviceId);
        return (api, handler);
    }

    private static HttpResponseMessage Ok426Body(int clientVersion = 0, int serverMin = 1, int serverCurrent = 1)
    {
        var body = new
        {
            error = "Upgrade Required",
            clientVersion,
            serverMinVersion = serverMin,
            serverCurrentVersion = serverCurrent,
            message = "Update the SyncSystem.Client NuGet package."
        };
        return new HttpResponseMessage((HttpStatusCode)426)
        {
            Content = JsonContent.Create(body)
        };
    }

    private static HttpResponseMessage OkBeginPushResponse()
    {
        var body = new PushSessionBeginResponse
        {
            Success = true,
            SessionId = Guid.NewGuid()
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body)
        };
    }

    private static HttpResponseMessage OkBeginPullResponse()
    {
        var body = new SyncSession.Core.DTOs.Pull.PullSessionBeginResponse
        {
            Success = true,
            PullSessionId = Guid.NewGuid()
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body)
        };
    }

    private static List<TableSyncInfo> OneTable() =>
        [new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 0 }];

    // ── Tests — 426 handling ──────────────────────────────────────────────────

    [Fact]
    public async Task BeginPushAsync_Receives426_ThrowsSyncProtocolException()
    {
        var (api, _) = BuildApi(Ok426Body(clientVersion: 0, serverMin: 1, serverCurrent: 1));

        var act = () => api.BeginPushAsync(OneTable());

        await act.Should().ThrowAsync<SyncProtocolException>();
    }

    [Fact]
    public async Task BeginPushAsync_Receives426_ExceptionCarriesVersionProperties()
    {
        var (api, _) = BuildApi(Ok426Body(clientVersion: 0, serverMin: 1, serverCurrent: 2));

        var ex = await Assert.ThrowsAsync<SyncProtocolException>(
            () => api.BeginPushAsync(OneTable()));

        ex.ClientVersion.Should().Be(0);
        ex.ServerMinVersion.Should().Be(1);
        ex.ServerCurrentVersion.Should().Be(2);
    }

    [Fact]
    public async Task BeginPullAsync_Receives426_ThrowsSyncProtocolException()
    {
        var (api, _) = BuildApi(Ok426Body(clientVersion: 0, serverMin: 1, serverCurrent: 1));

        var act = () => api.BeginPullAsync(["Customers"]);

        await act.Should().ThrowAsync<SyncProtocolException>();
    }

    [Fact]
    public async Task BeginPullAsync_Receives426_ExceptionMessageIsDescriptive()
    {
        var (api, _) = BuildApi(Ok426Body(clientVersion: 0, serverMin: 1, serverCurrent: 1));

        var ex = await Assert.ThrowsAsync<SyncProtocolException>(
            () => api.BeginPullAsync(["Customers"]));

        ex.Message.Should().Contain("protocol version")
            .And.Contain("Update the SyncSystem.Client");
    }

    // ── Tests — header injection ──────────────────────────────────────────────

    [Fact]
    public async Task BeginPushAsync_SendsProtocolVersionHeader()
    {
        var (api, handler) = BuildApi(OkBeginPushResponse());

        await api.BeginPushAsync(OneTable());

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.TryGetValues(SyncProtocolVersion.ProtocolHeader, out var values)
            .Should().BeTrue("protocol header must be present on BeginPush");
        values.Should().ContainSingle()
            .Which.Should().Be(SyncProtocolVersion.Current.ToString());
    }

    [Fact]
    public async Task BeginPushAsync_SendsPackageVersionHeader()
    {
        var (api, handler) = BuildApi(OkBeginPushResponse());

        await api.BeginPushAsync(OneTable());

        handler.LastRequest!.Headers
            .Contains(SyncProtocolVersion.PackageVersionHeader)
            .Should().BeTrue("package version header must be present on BeginPush");
    }

    [Fact]
    public async Task BeginPullAsync_SendsProtocolVersionHeader()
    {
        var (api, handler) = BuildApi(OkBeginPullResponse());

        await api.BeginPullAsync(["Customers"]);

        handler.LastRequest!.Headers.TryGetValues(SyncProtocolVersion.ProtocolHeader, out var values)
            .Should().BeTrue("protocol header must be present on BeginPull");
        values.Should().ContainSingle()
            .Which.Should().Be(SyncProtocolVersion.Current.ToString());
    }

    // ── Stub handler ─────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the last request and returns the pre-configured response.
    /// </summary>
    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }
}
