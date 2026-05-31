using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.Data.Sqlite;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.Schema;
using SyncSession.Samples.Shared.TestData;

namespace SyncSession.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for database UPDATE/DELETE operations (requires populated table).
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class DatabaseUpdateBenchmarks
{
    private SqliteConnection _connection = null!;
    private string _insertSql = null!;

    [Params(100, 1000, 10000)]
    public int RecordCount;

    [GlobalSetup]
    public void Setup()
    {
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
    public async Task<int> UpdateMarkClean()
    {
        return await _connection.ExecuteAsync(
            "UPDATE Customers SET IsDirty = 0 WHERE IsDirty = 1");
    }

    [Benchmark]
    public async Task<int> UpdateSingleField()
    {
        return await _connection.ExecuteAsync(
            "UPDATE Customers SET ModifiedAtUtc = @Now WHERE IsDirty = 1",
            new { Now = DateTime.UtcNow.ToString("O") });
    }

    [Benchmark]
    public async Task<int> UpdateMultipleFields()
    {
        return await _connection.ExecuteAsync(@"
            UPDATE Customers 
            SET Name = 'Updated ' || Name,
                ModifiedAtUtc = @Now,
                IsDirty = 0
            WHERE IsDirty = 1",
            new { Now = DateTime.UtcNow.ToString("O") });
    }

    [Benchmark]
    public async Task<int> SoftDelete()
    {
        return await _connection.ExecuteAsync(
            "UPDATE Customers SET IsDeleted = 1 WHERE Id IN (SELECT Id FROM Customers WHERE IsDeleted = 0 LIMIT 10)");
    }

    [Benchmark]
    public async Task<int> HardDelete()
    {
        return await _connection.ExecuteAsync(
            "DELETE FROM Customers WHERE Id IN (SELECT Id FROM Customers LIMIT 10)");
    }

    [Benchmark]
    public async Task<int> UpdateWithTransaction()
    {
        using var transaction = _connection.BeginTransaction();
        var count = await _connection.ExecuteAsync(
            "UPDATE Customers SET IsDirty = 0 WHERE IsDirty = 1",
            transaction: transaction);
        transaction.Commit();
        return count;
    }
}
