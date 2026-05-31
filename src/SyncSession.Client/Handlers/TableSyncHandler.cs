using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;

namespace SyncSession.Client.Handlers;

/// <summary>
/// Strongly-typed handler for table synchronization operations.
/// Eliminates reflection by providing compile-time type safety for push/pull operations.
/// </summary>
/// <typeparam name="T">Entity type that implements ISyncEntity</typeparam>
public class TableSyncHandler<T> : ITableSyncHandler where T : class, ISyncEntity
{
    private readonly IClientDatabase _clientDb;
    private readonly ISyncServerApi _serverClient;
    private readonly string _tableName;
    private readonly int _pushBatchSize;
    private readonly int _pullBatchSize;
    private readonly Guid? _tenantId;

    /// <summary>
    /// Initializes a new instance of <see cref="TableSyncHandler{T}"/>.
    /// </summary>
    /// <param name="clientDatabase">Client database for local record operations.</param>
    /// <param name="serverClient">Server API client for push/pull communication.</param>
    /// <param name="tableConfig">Table metadata including name and entity type.</param>
    /// <param name="syncConfiguration">Client sync configuration providing batch sizes.</param>
    public TableSyncHandler(
        IClientDatabase clientDatabase,
        ISyncServerApi serverClient,
        TableConfig tableConfig,
        ClientSyncConfiguration syncConfiguration)
    {
        _clientDb = clientDatabase ?? throw new ArgumentNullException(nameof(clientDatabase));
        _serverClient = serverClient ?? throw new ArgumentNullException(nameof(serverClient));
        _tableName = tableConfig?.TableName ?? throw new ArgumentNullException(nameof(tableConfig));
        _pushBatchSize = syncConfiguration?.PushBatchSize ?? throw new ArgumentNullException(nameof(syncConfiguration));
        _pullBatchSize = syncConfiguration.PullBatchSize;
        _tenantId = syncConfiguration.TenantId;
    }

    /// <inheritdoc />
    public async Task<int> GetDirtyCountAsync()
    {
        var records = await _clientDb.GetDirtyRecordsAsync<T>(_tenantId);
        return records.Count();
    }

    /// <inheritdoc />
    public async Task<int> PushAsync(
        Guid sessionId,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dirtyRecords = await _clientDb.GetDirtyRecordsAsync<T>(_tenantId);
        if (!dirtyRecords.Any())
            return 0;

        var recordsList = dirtyRecords.ToList();
        var total = recordsList.Count;
        var recordsProcessed = 0;

        foreach (var batch in recordsList.Chunk(_pushBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _serverClient.PushBatchAsync<T>(sessionId, batch);

            recordsProcessed += batch.Length;
            progress?.Report((recordsProcessed, total));
        }

        await _clientDb.MarkRecordsCleanAsync<T>(_tenantId);

        return total;
    }

    /// <inheritdoc />
    public async Task<(int RecordsPulled, List<Guid> SessionIds)> PullAsync(
        Guid pullSessionId,
        IDbTransaction? transaction = null,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int totalPulled = 0;
        int totalRecords = 0; // Updated from first batch or accumulated
        var sessionIds = new HashSet<Guid>();
        int offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (records, hasMore, batchTotal) = await _serverClient.PullBatchAsync<T>(
                pullSessionId,
                offset,
                _pullBatchSize);

            var recordList = records.ToList();
            if (!recordList.Any())
                break;

            // Use server-reported total when available
            if (batchTotal > 0)
                totalRecords = batchTotal;

            foreach (var record in recordList.Where(r => r.SyncSessionId != null))
                sessionIds.Add(record.SyncSessionId!.Value);

            await _clientDb.UpsertBatchAsync(recordList, _tenantId, transaction);

            totalPulled += recordList.Count;
            offset += recordList.Count;
            progress?.Report((totalPulled, totalRecords > 0 ? totalRecords : totalPulled));

            if (!hasMore)
                break;
        }

        return (totalPulled, sessionIds.ToList());
    }
}
