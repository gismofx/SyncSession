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
/// Benchmarks for database INSERT operations (requires empty table).
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class DatabaseInsertBenchmarks
{
    private SqliteConnection _connection = null!;
    private List<Customer> _customerBatch = null!;
    private string _insertSql = null!;
    private string _upsertSql = null!;

    [Params(100, 1000, 10000)]
    public int BatchSize;

    [GlobalSetup]
    public void Setup()
    {
        // Register Dapper handlers for SQLite Guid↔TEXT (must be in benchmark process)
        SqlMapper.AddTypeHandler(new SqliteGuidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteNullableGuidTypeHandler());
        
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.OpenAsync().GetAwaiter().GetResult();

        var schemaSql = SqliteSchemaHelper.GetCreateTableSql<Customer>();
        _connection.ExecuteAsync(schemaSql).GetAwaiter().GetResult();

        // Pre-build SQL from reflection
        var allColumns = EntityReflectionHelper.GetAllPropertyNames<Customer>();
        var columnsStr = string.Join(", ", allColumns);
        var paramsStr = string.Join(", ", allColumns.Select(c => $"@{c}"));
        var updateStr = string.Join(",\n                ",
            allColumns.Where(c => c != "Id").Select(c => $"{c} = excluded.{c}"));

        _insertSql = $@"
            INSERT INTO Customers ({columnsStr})
            VALUES ({paramsStr})";

        _upsertSql = $@"
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
    public void Cleanup()
    {
        _connection.DisposeAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<int> InsertBatch_SingleTransaction()
    {
        using var transaction = _connection.BeginTransaction();
        var count = await _connection.ExecuteAsync(_insertSql, _customerBatch, transaction);
        transaction.Commit();
        return count;
    }

    [Benchmark]
    public async Task<int> InsertBatch_IndividualInserts()
    {
        var count = 0;
        foreach (var customer in _customerBatch)
        {
            count += await _connection.ExecuteAsync(_insertSql, customer);
        }
        return count;
    }

    [Benchmark]
    public async Task<int> UpsertBatch_OnConflict()
    {
        using var transaction = _connection.BeginTransaction();
        var count = await _connection.ExecuteAsync(_upsertSql, _customerBatch, transaction);
        transaction.Commit();
        return count;
    }
}
