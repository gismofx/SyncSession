using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks comparing current push serialization (Entity → Dictionary → filter → JSON)
/// vs proposed approach (Entity → JSON directly via PushBatchRequest&lt;T&gt;).
/// 
/// Context: HttpSyncServerApi.PushBatchAsync&lt;T&gt; currently converts every entity
/// to a Dictionary, filters out infrastructure columns, then serializes the dictionary.
/// Option B sends typed entities directly and lets the server filter on insert.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class PushSerializationBenchmarks
{
    private List<Customer> _customerBatch = null!;
    private List<Product> _productBatch = null!;
    private IReadOnlyList<string> _pushColumns = null!;
    private IReadOnlyList<string> _productPushColumns = null!;
    private Guid _sessionId;

    [Params(10, 100, 1000)]
    public int BatchSize;

    [GlobalSetup]
    public void Setup()
    {
        _sessionId = Guid.NewGuid();

        _pushColumns = EntityReflectionHelper.GetColumnsForPushSelect<Customer>();
        _productPushColumns = EntityReflectionHelper.GetColumnsForPushSelect<Product>();

        _customerBatch = new List<Customer>();
        for (int i = 0; i < BatchSize; i++)
        {
            _customerBatch.Add(new Customer
            {
                Id = Guid.NewGuid(),
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                Phone = $"555-{i:D4}",
                TenantId = Guid.NewGuid(),
                IsDirty = true,
                ModifiedAtUtc = DateTime.UtcNow,
                ModifiedByUserId = $"user-{i}",
                IsDeleted = false
            });
        }

        _productBatch = new List<Product>();
        for (int i = 0; i < BatchSize; i++)
        {
            _productBatch.Add(new Product
            {
                Id = Guid.NewGuid(),
                Name = $"Product {i}",
                SKU = $"SKU-{i:D6}",
                Price = 9.99m + i,
                IsDirty = true,
                ModifiedAtUtc = DateTime.UtcNow,
                ModifiedByUserId = $"user-{i}",
                IsDeleted = false
            });
        }
    }

    #region Current Approach: Entity → Dictionary → Filter → Serialize

    /// <summary>
    /// Current HttpSyncServerApi.PushBatchAsync flow for Customer:
    /// Entity → EntityToDictionary (reflection) → FilterDictionary (LINQ) → serialize non-generic PushBatchRequest
    /// </summary>
    [Benchmark(Baseline = true)]
    public string Current_Customer_DictConvert_Filter_Serialize()
    {
        var recordDicts = _customerBatch
            .Select(r => EntityToDictionary(r))
            .Select(dict => FilterDictionary(dict, _pushColumns))
            .ToList();

        var request = new PushBatchRequest
        {
            SessionId = _sessionId,
            TableName = "Customers",
            Records = recordDicts
        };

        return JsonSerializer.Serialize(request);
    }

    /// <summary>
    /// Same flow for Product (different property count)
    /// </summary>
    [Benchmark]
    public string Current_Product_DictConvert_Filter_Serialize()
    {
        var recordDicts = _productBatch
            .Select(r => EntityToDictionary(r))
            .Select(dict => FilterDictionary(dict, _productPushColumns))
            .ToList();

        var request = new PushBatchRequest
        {
            SessionId = _sessionId,
            TableName = "Products",
            Records = recordDicts
        };

        return JsonSerializer.Serialize(request);
    }

    #endregion

    #region Proposed Approach: Entity → Serialize directly (typed)

    /// <summary>
    /// Proposed: serialize PushBatchRequest&lt;T&gt; directly. No dictionary conversion.
    /// Server-side filtering handles column exclusion.
    /// </summary>
    [Benchmark]
    public string Proposed_Customer_DirectSerialize()
    {
        var request = new PushBatchRequest<Customer>
        {
            SessionId = _sessionId,
            TableName = "Customers",
            Records = _customerBatch
        };

        return JsonSerializer.Serialize(request);
    }

    [Benchmark]
    public string Proposed_Product_DirectSerialize()
    {
        var request = new PushBatchRequest<Product>
        {
            SessionId = _sessionId,
            TableName = "Products",
            Records = _productBatch
        };

        return JsonSerializer.Serialize(request);
    }

    #endregion

    #region Isolated: Dict conversion + filter cost (no serialization)

    /// <summary>
    /// Isolate the dictionary conversion + filtering cost
    /// </summary>
    [Benchmark]
    public List<Dictionary<string, object?>> Overhead_DictConvert_Filter_Only()
    {
        return _customerBatch
            .Select(r => EntityToDictionary(r))
            .Select(dict => FilterDictionary(dict, _pushColumns))
            .ToList();
    }

    #endregion

    #region Helper Methods (copied from HttpSyncServerApi to benchmark exact production code)

    private static Dictionary<string, object?> EntityToDictionary<T>(T entity) where T : ISyncEntity
    {
        var dict = new Dictionary<string, object?>();
        var properties = typeof(T).GetProperties();

        foreach (var prop in properties)
        {
            dict[prop.Name] = prop.GetValue(entity);
        }

        return dict;
    }

    private static Dictionary<string, object?> FilterDictionary(
        Dictionary<string, object?> dict,
        IReadOnlyList<string> allowedColumns)
    {
        return dict
            .Where(kvp => allowedColumns.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    #endregion
}
