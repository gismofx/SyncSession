using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Http;

/// <summary>
/// HTTP-based implementation of <see cref="ISeedServerApi"/>.
/// Consumes the NDJSON stream from <c>GET /v1/sync/seed/{tenantId}</c>.
/// </summary>
public sealed class HttpSeedServerApi : ISeedServerApi
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger<HttpSeedServerApi> _logger;

    /// <summary>Initializes a new instance of <see cref="HttpSeedServerApi"/>.</summary>
    public HttpSeedServerApi(HttpClient httpClient, string baseUrl, ILogger<HttpSeedServerApi> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SeedLine> StreamSeedAsync(
        Guid tenantId,
        Guid deviceId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/v1/sync/seed/{tenantId}?deviceId={deviceId}";
        _logger.LogDebug("Opening seed stream: {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(
            new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Fast path for row lines — skip full deserialization
            if (line.Contains("\"type\":\"row\"", StringComparison.Ordinal))
            {
                yield return new SeedLine { Type = "row", RawLine = line };
                continue;
            }

            // Fast path for rows bundle lines — skip full deserialization
            if (line.Contains("\"type\":\"rows\"", StringComparison.Ordinal))
            {
                yield return new SeedLine { Type = "rows", RawLine = line };
                continue;
            }

            // Full deserialization for control lines (begin, table, table_end, end, preparing)
            SeedLine seedLine;
            try
            {
                seedLine = JsonSerializer.Deserialize<SeedLine>(line)
                    ?? throw new InvalidOperationException("Null seed line deserialized.");
                seedLine.RawLine = line;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize seed line: {Line}", line);
                throw;
            }

            yield return seedLine;
        }
    }

    /// <inheritdoc/>
    public async Task AcknowledgeSeedAsync(SeedAcknowledgeRequest request, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/v1/sync/seed/acknowledge";
        var body = JsonSerializer.Serialize(request);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        _logger.LogDebug("Acknowledging seed for device {DeviceId} status={Status}", request.DeviceId, request.Status);
        var response = await _httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
    }
}
