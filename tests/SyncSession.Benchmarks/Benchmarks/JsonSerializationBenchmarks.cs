using System;
using System.Collections.Generic;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Utilities;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for JSON serialization/deserialization - used in every HTTP push/pull operation.
/// Tests System.Text.Json performance with Dictionary ↔ Entity conversions.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class JsonSerializationBenchmarks
{
    private Customer _sampleCustomer = null!;
    private List<Customer> _customerBatch = null!;
    private string _serializedCustomer = null!;
    private string _serializedBatch = null!;
    private Dictionary<string, object?> _customerDict = null!;
    private JsonElement _jsonElement;

    [Params(1, 100, 1000, 10000)]
    public int BatchSize;

    [GlobalSetup]
    public void Setup()
    {
        _sampleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "John Doe",
            Email = "john@example.com",
            Phone = "555-1234",
            IsDirty = true,
            ModifiedAtUtc = DateTime.UtcNow,
            ModifiedByUserId = "user-123",
            IsDeleted = false
        };

        // Create batch
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

        // Pre-serialize for deserialization benchmarks
        _serializedCustomer = JsonSerializer.Serialize(_sampleCustomer);
        _serializedBatch = JsonSerializer.Serialize(_customerBatch);

        // Create dictionary with JsonElement (simulates HTTP response)
        _customerDict = new Dictionary<string, object?>
        {
            ["Id"] = Guid.NewGuid(),
            ["Name"] = "Jane Smith",
            ["Email"] = "jane@example.com",
            ["ModifiedByUserId"] = "user-456",
            ["IsDeleted"] = false,
            ["ModifiedAtUtc"] = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(_customerDict);
        var doc = JsonDocument.Parse(json);
        _jsonElement = doc.RootElement;
    }

    #region Direct Serialization/Deserialization

    [Benchmark]
    public string Serialize_SingleCustomer()
    {
        return JsonSerializer.Serialize(_sampleCustomer);
    }

    [Benchmark]
    public string Serialize_CustomerBatch()
    {
        return JsonSerializer.Serialize(_customerBatch);
    }

    [Benchmark]
    public Customer? Deserialize_SingleCustomer()
    {
        return JsonSerializer.Deserialize<Customer>(_serializedCustomer);
    }

    [Benchmark]
    public List<Customer>? Deserialize_CustomerBatch()
    {
        return JsonSerializer.Deserialize<List<Customer>>(_serializedBatch);
    }

    #endregion

    #region Dictionary-Based Serialization (HTTP API Pattern)

    [Benchmark]
    public string SerializeDictionary_SingleRecord()
    {
        var dict = EntityReflectionHelper.EntityToDictionary(_sampleCustomer);
        return JsonSerializer.Serialize(dict);
    }

    [Benchmark]
    public string SerializeDictionary_Batch()
    {
        var dicts = new List<Dictionary<string, object?>>();
        foreach (var customer in _customerBatch)
        {
            dicts.Add(EntityReflectionHelper.EntityToDictionary(customer));
        }
        return JsonSerializer.Serialize(dicts);
    }

    [Benchmark]
    public Customer DeserializeDictionary_SingleRecord()
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(_serializedCustomer);
        return EntityReflectionHelper.DictionaryToEntity<Customer>(dict!);
    }

    [Benchmark]
    public List<Customer> DeserializeDictionary_Batch()
    {
        var dicts = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(_serializedBatch);
        var results = new List<Customer>();
        foreach (var dict in dicts!)
        {
            results.Add(EntityReflectionHelper.DictionaryToEntity<Customer>(dict));
        }
        return results;
    }

    #endregion

    #region JsonElement Unwrapping (Real-World HTTP Pattern)

    [Benchmark]
    public Customer DeserializeWithJsonElement()
    {
        // Simulates HttpServerClient.PullRecordsAsync pattern
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(_jsonElement.GetRawText());
        return EntityReflectionHelper.DictionaryToEntity<Customer>(dict!);
    }

    [Benchmark]
    public object? UnwrapJsonElementValue()
    {
        // Simulates EntityReflectionHelper.DictionaryToEntity JsonElement handling
        if (_jsonElement.TryGetProperty("Name", out var nameElement))
        {
            return nameElement.GetString();
        }
        return null;
    }

    #endregion

    #region Push/Pull DTO Patterns

    [Benchmark]
    public string SerializePushBatch()
    {
        // Simulates PushBatchRequest<T> serialization
        var records = new List<Dictionary<string, object?>>();
        foreach (var customer in _customerBatch)
        {
            records.Add(EntityReflectionHelper.EntityToDictionary(customer));
        }

        var request = new
        {
            SessionId = Guid.NewGuid(),
            TableName = "Customers",
            BatchNumber = 1,
            Records = records
        };

        return JsonSerializer.Serialize(request);
    }

    [Benchmark]
    public string SerializePullBatchResponse()
    {
        // Simulates PullBatchResponse serialization
        var records = new List<Dictionary<string, object?>>();
        foreach (var customer in _customerBatch)
        {
            records.Add(EntityReflectionHelper.EntityToDictionary(customer));
        }

        var response = new
        {
            Success = true,
            Records = records,
            HasMore = false,
            TotalRecords = BatchSize
        };

        return JsonSerializer.Serialize(response);
    }

    #endregion

    #region UnwrapJsonElement DateTime Strategy Comparison

    // Compares three strategies for handling datetime strings that arrive over HTTP as
    // ISO 8601 strings with timezone offsets (e.g. "2026-02-22T16:39:52-05:00").
    //
    // Strategy A (current): TryGetDateTimeOffset on every string — always parses.
    // Strategy B: digit-prefix guard — skips parse for names/emails/etc.
    // Strategy C: type-aware unwrap — only parses when target property is DateTime/DateTimeOffset.

    private List<Dictionary<string, object?>> _jsonElementBatch = null!;

    [GlobalSetup(Targets = new[]
    {
        nameof(UnwrapJsonElements_CurrentStrategy_TryParseEveryString),
        nameof(UnwrapJsonElements_DigitPrefixGuard),
        nameof(UnwrapJsonElements_TypeAware)
    })]
    public void SetupUnwrapBenchmarks()
    {
        // Simulate what arrives from PostAsJsonAsync: camelCase keys, datetime as ISO 8601 offset string
        var records = new List<Dictionary<string, object?>>();
        for (int i = 0; i < BatchSize; i++)
        {
            var dict = new Dictionary<string, object?>
            {
                ["id"]               = Guid.NewGuid().ToString(),
                ["tenantId"]         = Guid.NewGuid().ToString(),
                ["name"]             = $"Customer {i}",
                ["email"]            = $"customer{i}@example.com",
                ["phone"]            = "555-1234",
                ["address"]          = "123 Main St",
                ["modifiedByUserId"] = $"user-{i}",
                ["isDeleted"]        = false,
                ["modifiedAtUtc"]    = DateTimeOffset.Now.ToString("O")   // e.g. 2026-02-22T16:39:52-05:00
            };
            records.Add(dict);
        }

        // Serialize and re-deserialize so values are JsonElement (real HTTP path)
        var json = JsonSerializer.Serialize(records);
        _jsonElementBatch = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json)!;
    }

    [Benchmark]
    public List<Dictionary<string, object?>> UnwrapJsonElements_CurrentStrategy_TryParseEveryString()
    {
        return _jsonElementBatch
            .Select(r => r.ToDictionary(
                kvp => kvp.Key,
                kvp => UnwrapString_TryParseEvery(kvp.Value)))
            .ToList();
    }

    [Benchmark]
    public List<Dictionary<string, object?>> UnwrapJsonElements_DigitPrefixGuard()
    {
        return _jsonElementBatch
            .Select(r => r.ToDictionary(
                kvp => kvp.Key,
                kvp => UnwrapString_DigitGuard(kvp.Value)))
            .ToList();
    }

    [Benchmark]
    public List<Dictionary<string, object?>> UnwrapJsonElements_TypeAware()
    {
        // Uses EntityReflectionHelper property type map to only parse known DateTime columns
        var dateTimeColumns = typeof(Customer)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?))
            .Select(p => p.Name.ToLowerInvariant())
            .ToHashSet();

        return _jsonElementBatch
            .Select(r => r.ToDictionary(
                kvp => kvp.Key,
                kvp => UnwrapString_TypeAware(kvp.Value, dateTimeColumns.Contains(kvp.Key.ToLowerInvariant()))))
            .ToList();
    }

    // Strategy A: current implementation — TryGetDateTimeOffset on every string
    private static object? UnwrapString_TryParseEvery(object? value)
    {
        if (value is not JsonElement element) return value;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (element.TryGetDateTimeOffset(out var dto)) return dto.UtcDateTime;
            return element.GetString();
        }
        return UnwrapNonString(element);
    }

    // Strategy B: digit-prefix guard before TryGetDateTimeOffset
    private static object? UnwrapString_DigitGuard(object? value)
    {
        if (value is not JsonElement element) return value;
        if (element.ValueKind == JsonValueKind.String)
        {
            var s = element.GetString();
            if (s != null && s.Length >= 10 && char.IsDigit(s[0]) && element.TryGetDateTimeOffset(out var dto))
                return dto.UtcDateTime;
            return s;
        }
        return UnwrapNonString(element);
    }

    // Strategy C: type-aware — only parse when caller says it's a datetime column
    private static object? UnwrapString_TypeAware(object? value, bool isDateTimeColumn)
    {
        if (value is not JsonElement element) return value;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (isDateTimeColumn && element.TryGetDateTimeOffset(out var dto)) return dto.UtcDateTime;
            return element.GetString();
        }
        return UnwrapNonString(element);
    }

    private static object? UnwrapNonString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => element.ToString()
    };

    #endregion

    #region Memory Allocation Patterns

    [Benchmark]
    public List<Dictionary<string, object?>> EntityToDictionary_MemoryProfile()
    {
        // Measures allocation overhead of conversion
        var results = new List<Dictionary<string, object?>>();
        foreach (var customer in _customerBatch)
        {
            results.Add(EntityReflectionHelper.EntityToDictionary(customer));
        }
        return results;
    }

    [Benchmark]
    public List<Customer> DictionaryToEntity_MemoryProfile()
    {
        // Measures allocation overhead of conversion
        var results = new List<Customer>();
        for (int i = 0; i < BatchSize; i++)
        {
            results.Add(EntityReflectionHelper.DictionaryToEntity<Customer>(_customerDict));
        }
        return results;
    }

    #endregion
}
