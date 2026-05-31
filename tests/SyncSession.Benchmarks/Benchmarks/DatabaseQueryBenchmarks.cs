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
/// Benchmarks for database SELECT/COUNT operations (requires populated table).
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class DatabaseQueryBenchmarks
{
    private SqliteConnection _connection = null!;
    private Guid _sessionId;
    private string _insertSql = null!;

    [Params(100, 1000, 10000)]
    public int RecordCount;

    [GlobalSetup]
    public void Setup()
    {
        // Register Dapper handlers for SQLite Guid↔TEXT (must be in benchmark process)
        SqlMapper.AddTypeHandler(new SqliteGuidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteNullableGuidTypeHandler());
        
        _sessionId = Guid.NewGuid();

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.OpenAsync().GetAwaiter().GetResult();

        var schemaSql = SqliteSchemaHelper.GetCreateTableSql<Customer>();
        _connection.ExecuteAsync(schemaSql).GetAwaiter().GetResult();

        // Pre-build insert SQL from reflection
        var allColumns = EntityReflectionHelper.GetAllPropertyNames<Customer>();
        var columnsStr = string.Join(", ", allColumns);
        var paramsStr = string.Join(", ", allColumns.Select(c => $"@{c}"));
        _insertSql = $"INSERT INTO Customers ({columnsStr}) VALUES ({paramsStr})";
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _connection.ExecuteAsync("DELETE FROM Customers").GetAwaiter().GetResult();

        var customers = Enumerable.Range(0, RecordCount)
            .Select(i =>
            {
                var c = TestDataGenerator.CreateCustomer(
                    modifiedByUserId: $"user-{i % 10}",
                    isDirty: i % 2 == 0);
                c.Phone = i % 3 == 0 ? $"555-{i:0000}" : null;
                c.SyncSessionId = i % 5 == 0 ? _sessionId : null;
                c.IsDeleted = i % 10 == 0;
                return c;
            }).ToList();

        using var transaction = _connection.BeginTransaction();
        _connection.ExecuteAsync(_insertSql, customers, transaction).GetAwaiter().GetResult();
        transaction.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.DisposeAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<List<Customer>> SelectDirtyRecords()
    {
        var results = await _connection.QueryAsync<Customer>(
            "SELECT * FROM Customers WHERE IsDirty = 1");
        return results.ToList();
    }

    [Benchmark]
    public async Task<List<Customer>> SelectBySession()
    {
        var results = await _connection.QueryAsync<Customer>(
            "SELECT * FROM Customers WHERE SyncSessionId = @SessionId",
            new { SessionId = _sessionId });
        return results.ToList();
    }

    [Benchmark]
    public async Task<List<Customer>> SelectAll()
    {
        var results = await _connection.QueryAsync<Customer>(
            "SELECT * FROM Customers");
        return results.ToList();
    }

    [Benchmark]
    public async Task<int> CountDirtyRecords()
    {
        return await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Customers WHERE IsDirty = 1");
    }

    [Benchmark]
    public async Task<int> CountBySession()
    {
        return await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Customers WHERE SyncSessionId = @SessionId",
            new { SessionId = _sessionId });
    }

    [Benchmark]
    public async Task<List<Customer>> SelectDeleted()
    {
        var results = await _connection.QueryAsync<Customer>(
            "SELECT * FROM Customers WHERE IsDeleted = 1");
        return results.ToList();
    }
}
