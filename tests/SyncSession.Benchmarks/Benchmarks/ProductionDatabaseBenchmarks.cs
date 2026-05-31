using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.Data.Sqlite;
using SyncSession.Client.Database;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.Schema;
using SyncSession.Samples.Shared.TestData;

namespace SyncSession.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for production database operations using IClientDatabase abstraction.
/// Compare against DatabaseInsertBenchmarks to measure abstraction overhead.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class ProductionDatabaseBenchmarks
{
    private SqliteConnection _connection = null!;
    private SqliteClientDatabase _clientDb = null!;
    private List<Customer> _customerBatch = null!;
    private string _baselineUpsertSql = null!;

    [Params(100, 1000, 10000)]
    public int BatchSize;

    [GlobalSetup]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        // Schema from reflection — single source of truth
        var schemaSql = SqliteSchemaHelper.GetCreateTableSql<Customer>();
        schemaSql += @"
CREATE TABLE IF NOT EXISTS LocalSyncState (
    TableName TEXT PRIMARY KEY,
    LastSyncVersion INTEGER NOT NULL DEFAULT 0,
    LastSyncCompletedAtUtc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'
);";
        await _connection.ExecuteAsync(schemaSql);

        _clientDb = new SqliteClientDatabase(_connection);
        await _clientDb.InitializeAsync();

        // Pre-build baseline SQL from reflection
        var allColumns = EntityReflectionHelper.GetAllPropertyNames<Customer>();
        var columnsStr = string.Join(", ", allColumns);
        var paramsStr = string.Join(", ", allColumns.Select(c => $"@{c}"));
        var updateStr = string.Join(",\n                ",
            allColumns.Where(c => c != "Id").Select(c => $"{c} = excluded.{c}"));

        _baselineUpsertSql = $@"
            INSERT INTO Customers ({columnsStr})
            VALUES ({paramsStr})
            ON CONFLICT(Id) DO UPDATE SET
                {updateStr}";
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _connection.ExecuteAsync("DELETE FROM Customers").GetAwaiter().GetResult();

        _customerBatch = Enumerable.Range(0, BatchSize)
            .Select(i =>
            {
                var c = TestDataGenerator.CreateCustomer(
                    modifiedByUserId: $"user-{i % 10}",
                    isDirty: i % 2 == 0);
                c.Phone = i % 3 == 0 ? $"555-{i:0000}" : null;
                c.IsDeleted = i % 10 == 0;
                return c;
            }).ToList();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _clientDb.Dispose();
        await _connection.DisposeAsync();
    }

    // ==================== PRODUCTION PATH BENCHMARKS ====================

    [Benchmark]
    public async Task Production_UpsertBatch()
    {
        await _clientDb.UpsertBatchAsync(_customerBatch);
    }

    [Benchmark]
    public async Task<List<Customer>> Production_GetDirtyRecords()
    {
        await _connection.ExecuteAsync("UPDATE Customers SET IsDirty = 1");
        var records = await _clientDb.GetDirtyRecordsAsync<Customer>();
        return records.ToList();
    }

    [Benchmark]
    public async Task Production_MarkRecordsClean()
    {
        await _connection.ExecuteAsync("UPDATE Customers SET IsDirty = 1");
        await _clientDb.MarkRecordsCleanAsync<Customer>();
    }

    // ==================== BASELINE (Raw SQL) ====================

    [Benchmark(Baseline = true)]
    public async Task<int> Baseline_RawSQL_Upsert()
    {
        return await _connection.ExecuteAsync(_baselineUpsertSql, _customerBatch);
    }

    [Benchmark]
    public async Task<List<Customer>> Baseline_RawSQL_SelectDirty()
    {
        await _connection.ExecuteAsync("UPDATE Customers SET IsDirty = 1");
        var records = await _connection.QueryAsync<Customer>(
            "SELECT * FROM Customers WHERE IsDirty = 1");
        return records.ToList();
    }

    [Benchmark]
    public async Task<int> Baseline_RawSQL_MarkClean()
    {
        await _connection.ExecuteAsync("UPDATE Customers SET IsDirty = 1");
        return await _connection.ExecuteAsync("UPDATE Customers SET IsDirty = 0 WHERE IsDirty = 1");
    }
}
