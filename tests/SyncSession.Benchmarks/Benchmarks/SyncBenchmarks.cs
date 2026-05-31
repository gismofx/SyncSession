using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;

namespace SyncSession.Benchmarks.Benchmarks;

/// <summary>
/// Simple entity for benchmarking (Session 10: Updated to type-safe approach)
/// </summary>
[SyncTable("BenchmarkRecords", Priority = 1)]
public class BenchmarkRecord : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Value { get; set; }
    
    // ISyncEntity properties
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "Benchmark";
    public bool IsDeleted { get; set; } = false;
}

[MemoryDiagnoser]
public class SyncBenchmarks
{
    private List<BenchmarkRecord> _records = new();

    [Params(100, 1000, 10000)]
    public int RecordCount;

    [GlobalSetup]
    public void Setup()
    {
        _records = new List<BenchmarkRecord>();
        for (int i = 0; i < RecordCount; i++)
        {
            _records.Add(new BenchmarkRecord
            {
                Id = Guid.NewGuid(),
                Name = $"Record {i}",
                Email = $"user{i}@example.com",
                Value = i,
                IsDirty = i % 2 == 0 // Half are dirty
            });
        }
    }

    [Benchmark]
    public void CreateRecords()
    {
        var records = new List<BenchmarkRecord>();
        for (int i = 0; i < RecordCount; i++)
        {
            records.Add(new BenchmarkRecord
            {
                Id = Guid.NewGuid(),
                Name = $"Record {i}",
                Email = $"user{i}@example.com",
                Value = i
            });
        }
    }

    [Benchmark]
    public int CountDirtyRecords()
    {
        int count = 0;
        foreach (var record in _records)
        {
            if (record.IsDirty)
                count++;
        }
        return count;
    }

    [Benchmark]
    public List<BenchmarkRecord> FilterDirtyRecords()
    {
        var dirty = new List<BenchmarkRecord>();
        foreach (var record in _records)
        {
            if (record.IsDirty)
                dirty.Add(record);
        }
        return dirty;
    }
}
