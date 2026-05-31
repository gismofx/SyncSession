# SyncSystem Benchmark Baseline Summary
**Date:** 2026-01-25 13:28:12 UTC
**Total Benchmarks:** 158

---

## DatabaseInsertBenchmarks

### Database Operations
- **InsertBatch_IndividualInserts** BatchSize=100: 1.9 ms
- **InsertBatch_SingleTransaction** BatchSize=100: 1.0 ms
- **UpsertBatch_OnConflict** BatchSize=100: 1.0 ms
- **InsertBatch_IndividualInserts** BatchSize=1000: 21.6 ms
- **InsertBatch_SingleTransaction** BatchSize=1000: 8.5 ms
- **UpsertBatch_OnConflict** BatchSize=1000: 8.3 ms
- **InsertBatch_IndividualInserts** BatchSize=10000: 132.6 ms
- **InsertBatch_SingleTransaction** BatchSize=10000: 84.0 ms
- **UpsertBatch_OnConflict** BatchSize=10000: 79.3 ms

## DatabaseQueryBenchmarks

### Database Operations
- **CountBySession** RecordCount=100: 51.2 μs
- **CountDirtyRecords** RecordCount=100: 36.2 μs
- **SelectAll** RecordCount=100: 330.0 μs
- **SelectBySession** RecordCount=100: 129.2 μs
- **SelectDeleted** RecordCount=100: 88.9 μs
- **SelectDirtyRecords** RecordCount=100: 250.9 μs
- **CountBySession** RecordCount=1000: 90.0 μs
- **CountDirtyRecords** RecordCount=1000: 92.6 μs
- **SelectAll** RecordCount=1000: 3.0 ms
- **SelectBySession** RecordCount=1000: 744.8 μs
- **SelectDeleted** RecordCount=1000: 485.7 μs
- **SelectDirtyRecords** RecordCount=1000: 1.6 ms
- **CountBySession** RecordCount=10000: 217.6 μs
- **CountDirtyRecords** RecordCount=10000: 239.7 μs
- **SelectAll** RecordCount=10000: 20.9 ms
- **SelectBySession** RecordCount=10000: 7.6 ms
- **SelectDeleted** RecordCount=10000: 4.3 ms
- **SelectDirtyRecords** RecordCount=10000: 16.2 ms

## DatabaseUpdateBenchmarks

### Database Operations
- **HardDelete** RecordCount=100: 65.6 μs
- **SoftDelete** RecordCount=100: 62.9 μs
- **UpdateMarkClean** RecordCount=100: 84.5 μs
- **UpdateMultipleFields** RecordCount=100: 120.1 μs
- **UpdateSingleField** RecordCount=100: 67.5 μs
- **UpdateWithTransaction** RecordCount=100: 91.0 μs
- **HardDelete** RecordCount=1000: 116.0 μs
- **SoftDelete** RecordCount=1000: 100.5 μs
- **UpdateMarkClean** RecordCount=1000: 374.7 μs
- **UpdateMultipleFields** RecordCount=1000: 530.6 μs
- **UpdateSingleField** RecordCount=1000: 348.1 μs
- **UpdateWithTransaction** RecordCount=1000: 381.9 μs
- **HardDelete** RecordCount=10000: 196.5 μs
- **SoftDelete** RecordCount=10000: 192.1 μs
- **UpdateMarkClean** RecordCount=10000: 4.2 ms
- **UpdateMultipleFields** RecordCount=10000: 5.1 ms
- **UpdateSingleField** RecordCount=10000: 3.4 ms
- **UpdateWithTransaction** RecordCount=10000: 4.3 ms

## EntityReflectionBenchmarks

### Key Metrics
- **Column Extraction (Cached):** 89.2 ns avg, 0 B allocated
- **Cold Cache Penalty:** 2.3 μs (vs 89.2 ns cached)
- **CreateDynamicParameters:**
  - BatchSize=1: 1.9 μs, 0 B
  - BatchSize=1: 1.9 μs, 0 B
  - BatchSize=100: 2.0 μs, 0 B
  - BatchSize=100: 205.1 μs, 0 B
  - BatchSize=1000: 2.0 μs, 0 B
  - BatchSize=1000: 2.1 ms, 0 B
- **Dictionary Conversions:**
  - To Dictionary BatchSize=1: 1.5 μs, 0 B
  - To Dictionary BatchSize=1: 1.4 μs, 0 B
  - To Dictionary BatchSize=100: 1.5 μs, 0 B
  - To Dictionary BatchSize=100: 144.0 μs, 0 B
  - To Dictionary BatchSize=1000: 1.5 μs, 0 B
  - To Dictionary BatchSize=1000: 1.4 ms, 0 B
  - From Dictionary BatchSize=1: 929.2 ns, 0 B
  - From Dictionary BatchSize=1: 939.5 ns, 0 B
  - From Dictionary BatchSize=100: 952.3 ns, 0 B
  - From Dictionary BatchSize=100: 93.6 μs, 0 B
  - From Dictionary BatchSize=1000: 898.6 ns, 0 B
  - From Dictionary BatchSize=1000: 912.6 μs, 0 B

## JsonSerializationBenchmarks

### Serialization Performance
**Serialize:**
- Serialize_SingleCustomer BatchSize=1: 434.1 ns
- Serialize_CustomerBatch BatchSize=1: 491.7 ns
- SerializeDictionary_SingleRecord BatchSize=1: 2.4 μs
**Deserialize:**
- Deserialize_SingleCustomer BatchSize=1: 670.4 ns
- Deserialize_CustomerBatch BatchSize=1: 830.9 ns
- DeserializeDictionary_SingleRecord BatchSize=1: 3.5 μs

## SyncBenchmarks

- **CreateRecords** RecordCount=10000
  - Mean: 4.6 ms, Allocated: 0 B
- **CreateRecords** RecordCount=1000
  - Mean: 154.6 μs, Allocated: 0 B
- **FilterDirtyRecords** RecordCount=10000
  - Mean: 37.0 μs, Allocated: 0 B
- **CreateRecords** RecordCount=100
  - Mean: 15.6 μs, Allocated: 0 B
- **CountDirtyRecords** RecordCount=10000
  - Mean: 7.9 μs, Allocated: 0 B

---

## 🎯 Optimization Targets for Session 18b

### Slowest Operations
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=10000 - 132.6 ms
1. **JsonSerializationBenchmarks.DeserializeDictionary_Batch** BatchSize=10000 - 101.4 ms
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=10000 - 84.0 ms
1. **DatabaseInsertBenchmarks.UpsertBatch_OnConflict** BatchSize=10000 - 79.3 ms
1. **JsonSerializationBenchmarks.SerializeDictionary_Batch** BatchSize=10000 - 28.2 ms

### Highest Memory Allocations
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.UpsertBatch_OnConflict** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=1000 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=1000 - 0 B

