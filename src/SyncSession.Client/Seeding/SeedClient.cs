using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Seeding;

/// <summary>
/// Reads the NDJSON seed stream from <see cref="ISeedServerApi"/>, batches rows,
/// and delegates writes to an <see cref="ISeedDatabaseWriter"/>.
/// </summary>
/// <remarks>
/// Use <see cref="ClientDatabaseSeedWriter"/> for standard SQLite clients,
/// or implement <see cref="ISeedDatabaseWriter"/> directly for custom stores (e.g. wa-sqlite).
/// On stream interruption, <see cref="SeedInterruptedException"/> is thrown.
/// There is no resume — restart seeding from the beginning.
/// </remarks>
public sealed class SeedClient
{
    /// <summary>Default number of rows accumulated before calling <see cref="ISeedDatabaseWriter.WriteRowsAsync"/>.</summary>
    public const int DefaultBatchSize = 500;

    private readonly ISeedServerApi _serverApi;
    private readonly ILogger<SeedClient> _logger;

    /// <summary>Initializes a new instance of <see cref="SeedClient"/>.</summary>
    /// <param name="serverApi">Server API implementation (typically <c>HttpSeedServerApi</c>).</param>
    /// <param name="logger">Logger.</param>
    public SeedClient(ISeedServerApi serverApi, ILogger<SeedClient> logger)
    {
        _serverApi = serverApi ?? throw new ArgumentNullException(nameof(serverApi));
        _logger = logger;
    }

    /// <summary>
    /// Streams the full seed payload from the server and writes it to the client database.
    /// </summary>
    /// <param name="tenantId">Tenant to seed.</param>
    /// <param name="deviceId">Device performing the seed. Used to acknowledge processed sessions after streaming completes.</param>
    /// <param name="writer">Database writer supplied by the consumer.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="batchSize">Rows accumulated before each <see cref="ISeedDatabaseWriter.WriteRowsAsync"/> call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="bundlesPerWrite">
    /// Number of bundle lines to accumulate before flushing to <see cref="IRawSeedDatabaseWriter.WriteRowsRawAsync"/>.
    /// Each bundle contains the server's <c>RowsBundleSize</c> rows (default 100).
    /// Default 5 = 500 rows per write. Lower = more frequent progress updates; higher = fewer Worker round-trips.
    /// </param>
    /// <returns>
    /// <see cref="SeedResult"/> containing the anchor timestamp and per-table row counts.
    /// Pass <see cref="SeedResult.Anchor"/> to the first incremental pull.
    /// </returns>
    /// <remarks>
    /// On failure or cancellation this method self-reports a <c>Failed</c>/<c>Cancelled</c>
    /// seed session to the server (best-effort) before rethrowing, so a failure row is always
    /// recorded. Consumers should therefore NOT also call <see cref="ReportSeedOutcomeAsync"/>
    /// after catching an exception from this method — doing so would double-log.
    /// Use <see cref="ReportSeedOutcomeAsync"/> only for cancellations decided outside a
    /// <see cref="SeedAsync"/> call. A failed server acknowledge after a locally-committed seed
    /// is logged but not thrown — the local seed is the authoritative outcome.
    /// </remarks>
    /// <exception cref="SeedInterruptedException">
    /// Thrown when the stream ends without an <c>end</c> line (network drop, server error).
    /// </exception>
    public async Task<SeedResult> SeedAsync(
        Guid tenantId,
        Guid deviceId,
        ISeedDatabaseWriter writer,
        IProgress<SeedProgress>? progress = null,
        int batchSize = DefaultBatchSize,
        int bundlesPerWrite = 5,
        string? userId = null,
        string? userDisplayName = null,
        CancellationToken ct = default)
    {
        var rowCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<Dictionary<string, object?>>(batchSize);
        var rawBatch = new List<string>(batchSize);
        string? currentTable = null;
        int currentTableTotal = -1;
        int tablesComplete = 0;
        int totalTables = 0;
        int preparingCount = 0;
        DateTime anchor = default;
        bool streamEnded = false;
        bool beginReceived = false;
        var startedAtUtc = DateTime.UtcNow;

        // Detect raw path — skips Dictionary allocation and re-serialization
        var rawWriter = writer as IRawSeedDatabaseWriter;

        // Timing instrumentation — measure stream read vs write separately
        var swRead  = new Stopwatch();
        var swWrite = new Stopwatch();
        long totalReadMs = 0, totalWriteMs = 0, batchCount = 0;
        swRead.Start(); // starts measuring stream read immediately

        try
        {
            await foreach (var line in _serverApi.StreamSeedAsync(tenantId, deviceId, ct))
            {
                switch (line.Type)
                {
                    case "preparing":
                        // Server is building snapshot tables — report progress before data starts
                        preparingCount++;
                        progress?.Report(new SeedProgress(
                            line.Table ?? "...", preparingCount, -1, 0, totalTables,
                            $"Preparing snapshot: {line.Table}"));
                        break;

                    case "begin":
                        beginReceived = true;
                        totalTables = line.Tables?.Count ?? 0;
                        _logger.LogInformation(
                            "Seed stream opened: tenantId={TenantId}, tables={Count}, anchor={Anchor:O}",
                            tenantId, totalTables, line.GeneratedAt);
                        break;

                    case "table":
                        if (!beginReceived)
                            throw new InvalidOperationException("Received 'table' line before 'begin' in seed stream.");
                        currentTable = line.Table!;
                        currentTableTotal = line.Total ?? -1;
                        rowCounts[currentTable] = 0;
                        batch.Clear();
                        rawBatch.Clear();
                        await writer.BeginTableAsync(currentTable, currentTableTotal, ct);
                        break;

                    case "row":
                        if (!beginReceived)
                            throw new InvalidOperationException("Received 'row' line before 'begin' in seed stream.");

                        if (rawWriter != null)
                        {
                            if (line.RawLine != null) rawBatch.Add(line.RawLine);

                            if (rawBatch.Count >= batchSize)
                            {
                                swRead.Stop(); totalReadMs += swRead.ElapsedMilliseconds;
                                swWrite.Restart();
                                await rawWriter.WriteRowsRawAsync(currentTable!, rawBatch, ct);
                                swWrite.Stop(); totalWriteMs += swWrite.ElapsedMilliseconds;
                                batchCount++;
#if DEBUG
                                Console.WriteLine($"[SEED-TIMING] {currentTable} batch {batchCount}: read={swRead.ElapsedMilliseconds}ms write={swWrite.ElapsedMilliseconds}ms");
#endif
                                swRead.Restart();
                                rowCounts[currentTable!] += rawBatch.Count;
                                progress?.Report(new SeedProgress(currentTable!, rowCounts[currentTable!], currentTableTotal, tablesComplete, totalTables));
                                rawBatch.Clear();
                            }
                        }
                        else
                        {
                            if (line.Data != null) batch.Add(line.Data);
                            if (batch.Count >= batchSize)
                            {
                                await writer.WriteRowsAsync(currentTable!, batch, ct);
                                rowCounts[currentTable!] += batch.Count;
                                progress?.Report(new SeedProgress(currentTable!, rowCounts[currentTable!], currentTableTotal, tablesComplete, totalTables));
                                batch.Clear();
                            }
                        }
                        break;

                    case "rows":
                        if (!beginReceived)
                            throw new InvalidOperationException("Received 'rows' line before 'begin' in seed stream.");

                        if (rawWriter != null)
                        {
                            // Each 'rows' line is a bundle — add the raw line once, worker will expand it
                            if (line.RawLine != null) rawBatch.Add(line.RawLine);

                            if (rawBatch.Count >= bundlesPerWrite)
                            {
                                swRead.Stop(); totalReadMs += swRead.ElapsedMilliseconds;
                                swWrite.Restart();
                                var rowsWritten = await rawWriter.WriteRowsRawAsync(currentTable!, rawBatch, ct);
                                swWrite.Stop(); totalWriteMs += swWrite.ElapsedMilliseconds;
                                batchCount++;
                                if (rowsWritten == 0 && rawBatch.Count > 0)
                                    rowsWritten = rawBatch.Count * 100; // fallback: server default RowsBundleSize
#if DEBUG
                                Console.WriteLine($"[SEED-TIMING] {currentTable} batch {batchCount}: read={swRead.ElapsedMilliseconds}ms write={swWrite.ElapsedMilliseconds}ms ({rowsWritten} rows)");
#endif
                                swRead.Restart();
                                rowCounts[currentTable!] += rowsWritten;
                                progress?.Report(new SeedProgress(currentTable!, rowCounts[currentTable!], currentTableTotal, tablesComplete, totalTables));
                                rawBatch.Clear();
                            }
                        }
                        break;

                    case "table_end":
                        // Flush remaining rows
                        if (rawWriter != null)
                        {
                            if (rawBatch.Count > 0)
                            {
                                swRead.Stop();
                                totalReadMs += swRead.ElapsedMilliseconds;
                                swWrite.Restart();
                                var flushRowsWritten = await rawWriter.WriteRowsRawAsync(currentTable!, rawBatch, ct);
                                swWrite.Stop();
                                totalWriteMs += swWrite.ElapsedMilliseconds;
                                batchCount++;
                                if (flushRowsWritten == 0 && rawBatch.Count > 0)
                                    flushRowsWritten = rawBatch.Count * 100; // fallback: server default RowsBundleSize
                                if (currentTableTotal > 0)
                                    flushRowsWritten = Math.Min(flushRowsWritten, currentTableTotal - rowCounts[currentTable!]);
                                rowCounts[currentTable!] += flushRowsWritten;
                                swRead.Restart();
                                rawBatch.Clear();
                            }
#if DEBUG
                            Console.WriteLine($"[SEED-TIMING] {currentTable} COMPLETE: totalRead={totalReadMs}ms totalWrite={totalWriteMs}ms batches={batchCount} rows={rowCounts.GetValueOrDefault(currentTable!)}");
#endif
                            totalReadMs = 0; totalWriteMs = 0; batchCount = 0;
                        }
                        else
                        {
                            if (batch.Count > 0)
                            {
                                await writer.WriteRowsAsync(currentTable!, batch, ct);
                                rowCounts[currentTable!] += batch.Count;
                                batch.Clear();
                            }
                        }
                        await writer.EndTableAsync(currentTable!, ct);
                        tablesComplete++;
                        progress?.Report(new SeedProgress(
                            currentTable!, rowCounts[currentTable!],
                            currentTableTotal, tablesComplete, totalTables));
                        _logger.LogDebug("Seed: table {Table} complete ({Count} rows)",
                            currentTable, rowCounts[currentTable!]);
                        break;

                    case "end":
                        anchor = line.Anchor ?? DateTime.UtcNow;
                        streamEnded = true;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Self-report cancellation so a Cancelled session row is always written,
            // even if the consumer only wired the happy path. Best-effort, never masks.
            await ReportOutcomeBestEffortAsync(
                tenantId, deviceId, userId, userDisplayName, startedAtUtc, rowCounts,
                SyncConstants.STATUS_CANCELLED, "Seed cancelled.");
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // Malformed stream (out-of-order line — these guards fire before 'end', so the
            // generic catch below won't see them). Self-report failure, then wrap so callers
            // get a single, consistent SeedInterruptedException for any seed failure.
            await ReportOutcomeBestEffortAsync(
                tenantId, deviceId, userId, userDisplayName, startedAtUtc, rowCounts,
                SyncConstants.STATUS_FAILED, DescribeException(ex));
            throw new SeedInterruptedException(
                $"Seed stream malformed while streaming table '{currentTable}'.",
                currentTable, ex);
        }
        catch (Exception ex) when (!streamEnded)
        {
            // Stream drop or local write failure mid-seed. Report the real cause
            // (unwrapped from any reflection TargetInvocationException) before rethrow.
            await ReportOutcomeBestEffortAsync(
                tenantId, deviceId, userId, userDisplayName, startedAtUtc, rowCounts,
                SyncConstants.STATUS_FAILED, DescribeException(ex));
            throw new SeedInterruptedException(
                $"Seed stream interrupted while streaming table '{currentTable}'.",
                currentTable, ex);
        }

        if (!streamEnded)
        {
            await ReportOutcomeBestEffortAsync(
                tenantId, deviceId, userId, userDisplayName, startedAtUtc, rowCounts,
                SyncConstants.STATUS_FAILED, "Seed stream ended without an 'end' line.");
            throw new SeedInterruptedException(
                "Seed stream ended without an 'end' line.", currentTable);
        }

        // Commit is after the stream loop, so the catch above (gated on !streamEnded)
        // cannot cover it. Custom writers may do real work here — guard it explicitly.
        try
        {
            await writer.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await ReportOutcomeBestEffortAsync(
                tenantId, deviceId, userId, userDisplayName, startedAtUtc, rowCounts,
                SyncConstants.STATUS_FAILED, DescribeException(ex));
            throw;
        }

        _logger.LogInformation("Acknowledging seed with server for device {DeviceId}", deviceId);
        var totalRows = rowCounts.Values.Sum();
        try
        {
            await _serverApi.AcknowledgeSeedAsync(new SeedAcknowledgeRequest
            {
                DeviceId        = deviceId,
                TenantId        = tenantId,
                UserId          = userId,
                UserDisplayName = userDisplayName,
                Status          = SyncConstants.STATUS_COMPLETED,
                StartedAtUtc    = startedAtUtc,
                TotalRows       = totalRows,
                RowCountsJson   = JsonSerializer.Serialize(rowCounts),
            }, CancellationToken.None); // non-cancellable — seed already committed
        }
        catch (Exception ex)
        {
            // The local seed is already committed and is the authoritative outcome.
            // A failed server acknowledge must not surface as a failed seed — log and continue.
            _logger.LogWarning(ex,
                "Seed committed locally but server acknowledge failed for device {DeviceId}", deviceId);
        }

        _logger.LogInformation(
            "Seed complete: tenantId={TenantId}, anchor={Anchor:O}, tables={Count}",
            tenantId, anchor, rowCounts.Count);

        return new SeedResult(anchor, rowCounts);
    }

    /// <summary>Maximum length of the failure reason persisted to <c>SyncSession.ErrorMessage</c>.</summary>
    private const int MaxReasonLength = 1000;

    /// <summary>
    /// Builds a non-success acknowledge and reports it best-effort (swallows network errors,
    /// always uses <see cref="CancellationToken.None"/> so a cancelled token can't re-cancel
    /// the report itself). Never throws — callers rethrow the original exception.
    /// </summary>
    private async Task ReportOutcomeBestEffortAsync(
        Guid tenantId, Guid deviceId, string? userId, string? userDisplayName,
        DateTime startedAtUtc, Dictionary<string, int> rowCounts, string status, string reason)
    {
        var request = new SeedAcknowledgeRequest
        {
            DeviceId        = deviceId,
            TenantId        = tenantId,
            UserId          = userId,
            UserDisplayName = userDisplayName,
            Status          = status,
            StartedAtUtc    = startedAtUtc,
            TotalRows       = rowCounts.Values.Sum(),
            RowCountsJson   = JsonSerializer.Serialize(rowCounts),
            ErrorDetail     = Truncate(reason, MaxReasonLength),
        };
        await ReportSeedOutcomeAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// Produces a concise failure reason, unwrapping <see cref="TargetInvocationException"/>
    /// (raised when the seed writer invokes the typed upsert via reflection) so the persisted
    /// reason reflects the real cause rather than the reflection wrapper.
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        return $"{cause.GetType().Name}: {cause.Message}";
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max);

    /// <summary>
    /// Reports a non-success seed outcome (Cancelled or Failed) to the server.
    /// Fire-and-forget safe — swallows network errors so callers can ignore the result.
    /// </summary>
    public async Task ReportSeedOutcomeAsync(
        SeedAcknowledgeRequest request,
        CancellationToken ct = default)
    {
        try
        {
            await _serverApi.AcknowledgeSeedAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report seed outcome to server (status={Status})", request.Status);
        }
    }
}
