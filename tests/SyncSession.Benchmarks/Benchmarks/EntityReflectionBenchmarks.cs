using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Dapper;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for EntityReflectionHelper - critical hot path used in every sync operation.
/// Tests both performance and memory allocation characteristics.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class EntityReflectionBenchmarks
{
    private Customer _sampleCustomer = null!;
    private Dictionary<string, object?> _sampleDictionary = null!;
    private Guid _sessionId;
    private List<Customer> _customerBatch = null!;

    [Params(1, 100, 1000)]
    public int BatchSize;

    [GlobalSetup]
    public void Setup()
    {
        _sessionId = Guid.NewGuid();
        
        _sampleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "John Doe",
            Email = "john@example.com",
            Phone = "555-1234",
            IsDirty = true,
            ModifiedAtUtc = DateTime.UtcNow,
            ModifiedByUserId = "user-123",
            SyncSessionId = _sessionId
        };

        _sampleDictionary = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Name"] = "Jane Smith",
            ["Email"] = "jane@example.com",
            ["Phone"] = "555-5678",
            ["ModifiedByUserId"] = "user-456",
            ["IsDeleted"] = false,
            ["ModifiedAtUtc"] = DateTime.UtcNow
        };

        // Create batch for bulk operations
        _customerBatch = new List<Customer>();
        for (int i = 0; i < BatchSize; i++)
        {
            _customerBatch.Add(new Customer
            {
                Id = Guid.NewGuid(),
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                IsDirty = i % 2 == 0,
                ModifiedByUserId = $"user-{i}"
            });
        }

        // Warm up caches
        EntityReflectionHelper.GetColumnsForPullUpsert<Customer>();
        EntityReflectionHelper.GetColumnsForPushSelect<Customer>();
        EntityReflectionHelper.GetColumnsForServerUpsert<Customer>();
        EntityReflectionHelper.GetColumnsForServerSelect<Customer>();
    }

    #region Column Name Extraction

    [Benchmark]
    public IReadOnlyList<string> GetColumnsForPullUpsert_Cached()
    {
        return EntityReflectionHelper.GetColumnsForPullUpsert<Customer>();
    }

    [Benchmark]
    public IReadOnlyList<string> GetColumnsForPushSelect_Cached()
    {
        return EntityReflectionHelper.GetColumnsForPushSelect<Customer>();
    }

    [Benchmark]
    public IReadOnlyList<string> GetColumnsForServerUpsert_Cached()
    {
        return EntityReflectionHelper.GetColumnsForServerUpsert<Customer>();
    }

    [Benchmark]
    public IReadOnlyList<string> GetColumnsForServerSelect_Cached()
    {
        return EntityReflectionHelper.GetColumnsForServerSelect<Customer>();
    }

    [Benchmark]
    public IReadOnlyList<string> GetColumnsForPullUpsert_ColdCache()
    {
        EntityReflectionHelper.ClearCache();
        return EntityReflectionHelper.GetColumnsForPullUpsert<Customer>();
    }

    #endregion

    #region Property Classification

    [Benchmark]
    public bool IsSyncInfrastructureProperty_InfraProperty()
    {
        return EntityReflectionHelper.IsSyncInfrastructureProperty("IsDirty");
    }

    [Benchmark]
    public bool IsSyncInfrastructureProperty_BusinessProperty()
    {
        return EntityReflectionHelper.IsSyncInfrastructureProperty("Name");
    }

    [Benchmark]
    public bool IsClientOnlyProperty_Check()
    {
        return EntityReflectionHelper.IsClientOnlyProperty("IsDirty");
    }

    #endregion

    #region Dynamic Parameters

    [Benchmark]
    public DynamicParameters CreateDynamicParameters_SingleRecord()
    {
        return EntityReflectionHelper.CreateDynamicParameters(_sampleCustomer, _sessionId);
    }

    [Benchmark]
    public List<DynamicParameters> CreateDynamicParameters_Batch()
    {
        var results = new List<DynamicParameters>();
        foreach (var customer in _customerBatch)
        {
            results.Add(EntityReflectionHelper.CreateDynamicParameters(customer, _sessionId));
        }
        return results;
    }

    #endregion

    #region Entity ↔ Dictionary Conversions

    [Benchmark]
    public Dictionary<string, object?> EntityToDictionary_SingleRecord()
    {
        return EntityReflectionHelper.EntityToDictionary(_sampleCustomer);
    }

    [Benchmark]
    public List<Dictionary<string, object?>> EntityToDictionary_Batch()
    {
        var results = new List<Dictionary<string, object?>>();
        foreach (var customer in _customerBatch)
        {
            results.Add(EntityReflectionHelper.EntityToDictionary(customer));
        }
        return results;
    }

    [Benchmark]
    public Customer DictionaryToEntity_SingleRecord()
    {
        return EntityReflectionHelper.DictionaryToEntity<Customer>(_sampleDictionary);
    }

    [Benchmark]
    public List<Customer> DictionaryToEntity_Batch()
    {
        var results = new List<Customer>();
        for (int i = 0; i < BatchSize; i++)
        {
            results.Add(EntityReflectionHelper.DictionaryToEntity<Customer>(_sampleDictionary));
        }
        return results;
    }

    #endregion

    #region Property Access

    [Benchmark]
    public object? GetPropertyValue_SingleProperty()
    {
        return EntityReflectionHelper.GetPropertyValue(_sampleCustomer, "Name");
    }

    [Benchmark]
    public List<object?> GetPropertyValue_AllProperties()
    {
        var properties = EntityReflectionHelper.GetAllPropertyNames<Customer>();
        var results = new List<object?>();
        foreach (var prop in properties)
        {
            results.Add(EntityReflectionHelper.GetPropertyValue(_sampleCustomer, prop));
        }
        return results;
    }

    #endregion
}
