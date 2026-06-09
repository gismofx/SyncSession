using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Exceptions;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;

namespace SyncSession.Client.Http;

/// <summary>
/// HTTP-based implementation of <see cref="ISyncServerApi"/> for client-side use.
/// Communicates with SyncSession.Server via REST API endpoints.
/// </summary>
public class HttpSyncServerApi : ISyncServerApi
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly Guid _deviceId;

    // Resolved once at construction — informational only, never used for decisions.
    private static readonly string _packageVersion =
        typeof(HttpSyncServerApi).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// Initializes a new instance of <see cref="HttpSyncServerApi"/>.
    /// </summary>
    /// <param name="httpClient">Configured <see cref="HttpClient"/> for server communication.</param>
    /// <param name="baseUrl">Base URL of the SyncSession server (e.g., <c>https://host/api</c>).</param>
    /// <param name="deviceId">Unique identifier for this device, sent with every sync request.</param>
    public HttpSyncServerApi(HttpClient httpClient, string baseUrl, Guid deviceId)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _deviceId = deviceId != Guid.Empty ? deviceId : throw new ArgumentException("DeviceId cannot be empty.", nameof(deviceId));
    }

    /// <summary>
    /// Sends a POST request with protocol version headers on Begin-class calls.
    /// Handles 426 Upgrade Required by throwing <see cref="SyncProtocolException"/>.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithProtocolHeadersAsync<T>(string url, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(SyncProtocolVersion.ProtocolHeader,
            SyncProtocolVersion.Current.ToString());
        request.Headers.Add(SyncProtocolVersion.PackageVersionHeader, _packageVersion);

        var response = await _httpClient.SendAsync(request);

        if ((int)response.StatusCode == 426)
        {
            var body426 = await TryRead426Body(response);
            throw new SyncProtocolException(
                body426.ClientVersion,
                body426.ServerMinVersion,
                body426.ServerCurrentVersion);
        }

        return response;
    }

    private static async Task<(int ClientVersion, int ServerMinVersion, int ServerCurrentVersion)>
        TryRead426Body(HttpResponseMessage response)
    {
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (
                doc.TryGetProperty("clientVersion",       out var cv)  ? cv.GetInt32()  : 0,
                doc.TryGetProperty("serverMinVersion",    out var min) ? min.GetInt32() : 0,
                doc.TryGetProperty("serverCurrentVersion",out var cur) ? cur.GetInt32() : 0
            );
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    #region Push Operations

    
    /// <inheritdoc/>
    public async Task<Guid> BeginPushAsync(List<TableSyncInfo> tables, Guid? tenantId = null, string? userDisplayName = null)
    {
        var request = new PushSessionBeginRequest
        {
            DeviceId = _deviceId,
            TenantId = tenantId,
            Tables = tables,
            UserDisplayName = userDisplayName,
        };

        var response = await PostWithProtocolHeadersAsync(
            $"{_baseUrl}/v1/sync/push/begin", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PushSessionBeginResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize push session begin response");

        if (!result.Success)
            throw new InvalidOperationException($"Push session begin failed: {result.ErrorMessage}");

        return result.SessionId;
    }

    
    /// <inheritdoc/>
    public async Task PushBatchAsync<T>(Guid sessionId, IEnumerable<T> records) where T : ISyncEntity
    {
        var recordList = records.ToList();
        if (!recordList.Any())
            return;

        var tableName = TableNameResolver.GetTableName<T>();

        // Send typed records directly — server filters to valid push columns
        var request = new PushBatchRequest<T>
        {
            SessionId = sessionId,
            TableName = tableName,
            Records = recordList
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/sync/push/batch", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PushBatchResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize push batch response");

        if (!result.Success)
            throw new InvalidOperationException($"Push batch failed: {result.ErrorMessage}");
    }

    
    /// <inheritdoc/>
    public async Task CompleteTableAsync(Guid sessionId, string tableName, int totalRecordsSent)
    {
        var request = new PushTableCompleteRequest
        {
            SessionId = sessionId,
            TableName = tableName,
            TotalRecordsSent = totalRecordsSent
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/sync/push/table-complete", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PushTableCompleteResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize table-complete response");

        if (!result.Success)
            throw new InvalidOperationException($"Table-complete failed for '{tableName}': {result.ErrorMessage}");

        if (!result.CountMatches)
            throw new InvalidOperationException(
                $"Record count mismatch for table '{tableName}': " +
                $"client sent {totalRecordsSent}, server received {result.ActualRecordCount}. " +
                $"Push session aborted — session will be cleaned up by the server.");
    }

    /// <inheritdoc/>
    public async Task CompletePushAsync(Guid sessionId)
    {
        var request = new PushSessionCompleteRequest { SessionId = sessionId };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/sync/push/complete", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PushSessionCompleteResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize push complete response");

        if (!result.Success)
            throw new InvalidOperationException($"Push session complete failed: {result.ErrorMessage}");
    }

    
    /// <inheritdoc/>
    public async Task<PushSessionStatusResponse> GetPushStatusAsync(Guid sessionId)
    {
        var response = await _httpClient.GetAsync($"{_baseUrl}/v1/sync/push/status/{sessionId}");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PushSessionStatusResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize push status response");
    }

    #endregion

    #region Pull Operations

    
    /// <inheritdoc/>
    public async Task<PullSessionBeginResponse> BeginPullAsync(List<string> tableNames, Guid? tenantId = null, string? userDisplayName = null)
    {
        var request = new PullSessionBeginRequest
        {
            DeviceId = _deviceId,
            TenantId = tenantId,
            TableNames = tableNames,
            UserDisplayName = userDisplayName,
        };

        var response = await PostWithProtocolHeadersAsync(
            $"{_baseUrl}/v1/sync/pull/begin", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PullSessionBeginResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize pull session begin response");

        if (!result.Success)
            throw new InvalidOperationException($"Pull session begin failed: {result.ErrorMessage}");

        return result;
    }

    
    /// <inheritdoc/>
    public async Task<(IEnumerable<T> Records, bool HasMore, int TotalRecords)> PullBatchAsync<T>(
        Guid pullSessionId,
        int offset,
        int limit) where T : ISyncEntity
    {
        var request = new PullBatchRequest
        {
            PullSessionId = pullSessionId,
            TableName = TableNameResolver.GetTableName<T>(),
            Offset = offset,
            Limit = limit
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/sync/pull/batch", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PullBatchResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize pull batch response");

        if (!result.Success)
            throw new InvalidOperationException($"Pull batch failed: {result.ErrorMessage}");

        var records = result.Records
            .Select(dict => EntityReflectionHelper.DictionaryToEntity<T>(dict))
            .ToList();

        return (records, result.HasMore, result.TotalRecords);
    }

    
    /// <inheritdoc/>
    public async Task CompletePullAsync(Guid pullSessionId, List<Guid> processedSessionIds)
    {
        var request = new PullSessionCompleteRequest
        {
            PullSessionId = pullSessionId,
            DeviceId = _deviceId,
            ProcessedSessionIds = processedSessionIds
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/sync/pull/complete", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PullSessionCompleteResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize pull complete response");

        if (!result.Success)
            throw new InvalidOperationException($"Pull session complete failed: {result.ErrorMessage}");
    }

    #endregion

}
