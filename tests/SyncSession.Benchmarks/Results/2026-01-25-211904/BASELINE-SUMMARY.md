# SyncSystem Benchmark Baseline Summary
**Date:** 2026-01-25 21:45:28 UTC
**Total Benchmarks:** 176

---

## DatabaseInsertBenchmarks

### Database Operations
- **InsertBatch_IndividualInserts** BatchSize=100: 2.0 ms
- **InsertBatch_SingleTransaction** BatchSize=100: 1.1 ms
- **UpsertBatch_OnConflict** BatchSize=100: 1.1 ms
- **InsertBatch_IndividualInserts** BatchSize=1000: 22.1 ms
- **InsertBatch_SingleTransaction** BatchSize=1000: 8.4 ms
- **UpsertBatch_OnConflict** BatchSize=1000: 8.5 ms
- **InsertBatch_IndividualInserts** BatchSize=10000: 133.2 ms
- **InsertBatch_SingleTransaction** BatchSize=10000: 79.4 ms
- **UpsertBatch_OnConflict** BatchSize=10000: 87.8 ms

## DatabaseQueryBenchmarks

### Database Operations
- **CountBySession** RecordCount=100: 48.0 μs
- **CountDirtyRecords** RecordCount=100: 39.5 μs
- **SelectAll** RecordCount=100: 356.6 μs
- **SelectBySession** RecordCount=100: 151.4 μs
- **SelectDeleted** RecordCount=100: 97.4 μs
- **SelectDirtyRecords** RecordCount=100: 238.9 μs
- **CountBySession** RecordCount=1000: 82.0 μs
- **CountDirtyRecords** RecordCount=1000: 68.0 μs
- **SelectAll** RecordCount=1000: 3.2 ms
- **SelectBySession** RecordCount=1000: 774.9 μs
- **SelectDeleted** RecordCount=1000: 471.0 μs
- **SelectDirtyRecords** RecordCount=1000: 1.7 ms
- **CountBySession** RecordCount=10000: 201.3 μs
- **CountDirtyRecords** RecordCount=10000: 223.3 μs
- **SelectAll** RecordCount=10000: 21.9 ms
- **SelectBySession** RecordCount=10000: 8.6 ms
- **SelectDeleted** RecordCount=10000: 4.4 ms
- **SelectDirtyRecords** RecordCount=10000: 12.9 ms

## DatabaseUpdateBenchmarks

### Database Operations
- **HardDelete** RecordCount=100: 65.5 μs
- **SoftDelete** RecordCount=100: 62.0 μs
- **UpdateMarkClean** RecordCount=100: 88.3 μs
- **UpdateMultipleFields** RecordCount=100: 111.7 μs
- **UpdateSingleField** RecordCount=100: 75.2 μs
- **UpdateWithTransaction** RecordCount=100: 86.3 μs
- **HardDelete** RecordCount=1000: 96.1 μs
- **SoftDelete** RecordCount=1000: 101.6 μs
- **UpdateMarkClean** RecordCount=1000: 362.3 μs
- **UpdateMultipleFields** RecordCount=1000: 507.8 μs
- **UpdateSingleField** RecordCount=1000: 322.8 μs
- **UpdateWithTransaction** RecordCount=1000: 369.0 μs
- **HardDelete** RecordCount=10000: 183.8 μs
- **SoftDelete** RecordCount=10000: 145.0 μs
- **UpdateMarkClean** RecordCount=10000: 4.3 ms
- **UpdateMultipleFields** RecordCount=10000: 5.3 ms
- **UpdateSingleField** RecordCount=10000: 3.4 ms
- **UpdateWithTransaction** RecordCount=10000: 4.3 ms

## EntityReflectionBenchmarks

### Key Metrics
- **Column Extraction (Cached):** 83.0 ns avg, 0 B allocated
- **Cold Cache Penalty:** 2.3 μs (vs 83.0 ns cached)
- **CreateDynamicParameters:**
  - BatchSize=1: 2.1 μs, 0 B
  - BatchSize=1: 2.0 μs, 0 B
  - BatchSize=100: 2.0 μs, 0 B
  - BatchSize=100: 194.9 μs, 0 B
  - BatchSize=1000: 1.9 μs, 0 B
  - BatchSize=1000: 2.1 ms, 0 B
- **Dictionary Conversions:**
  - To Dictionary BatchSize=1: 1.5 μs, 0 B
  - To Dictionary BatchSize=1: 1.4 μs, 0 B
  - To Dictionary BatchSize=100: 1.5 μs, 0 B
  - To Dictionary BatchSize=100: 137.8 μs, 0 B
  - To Dictionary BatchSize=1000: 1.5 μs, 0 B
  - To Dictionary BatchSize=1000: 1.4 ms, 0 B
  - From Dictionary BatchSize=1: 1.1 μs, 0 B
  - From Dictionary BatchSize=1: 1.1 μs, 0 B
  - From Dictionary BatchSize=100: 1.1 μs, 0 B
  - From Dictionary BatchSize=100: 103.5 μs, 0 B
  - From Dictionary BatchSize=1000: 1.1 μs, 0 B
  - From Dictionary BatchSize=1000: 1.1 ms, 0 B

## JsonSerializationBenchmarks

### Serialization Performance
**Serialize:**
- Serialize_SingleCustomer BatchSize=1: 438.1 ns
- Serialize_CustomerBatch BatchSize=1: 494.2 ns
- SerializeDictionary_SingleRecord BatchSize=1: 2.5 μs
**Deserialize:**
- Deserialize_SingleCustomer BatchSize=1: 760.7 ns
- Deserialize_CustomerBatch BatchSize=1: 913.7 ns
- DeserializeDictionary_SingleRecord BatchSize=1: 3.7 μs

## ProductionDatabaseBenchmarks

### Database Operations
- **Baseline_RawSQL_MarkClean** BatchSize=100: 37.2 μs
- **Baseline_RawSQL_SelectDirty** BatchSize=100: 73.3 μs
- **Baseline_RawSQL_Upsert** BatchSize=100: 1.3 ms
- **Production_GetDirtyRecords** BatchSize=100: 89.1 μs
- **Production_MarkRecordsClean** BatchSize=100: 54.3 μs
- **Production_UpsertBatch** BatchSize=100: 1.3 ms
- **Baseline_RawSQL_MarkClean** BatchSize=1000: 44.2 μs
- **Baseline_RawSQL_SelectDirty** BatchSize=1000: 65.6 μs
- **Baseline_RawSQL_Upsert** BatchSize=1000: 11.8 ms
- **Production_GetDirtyRecords** BatchSize=1000: 88.4 μs
- **Production_MarkRecordsClean** BatchSize=1000: 52.5 μs
- **Production_UpsertBatch** BatchSize=1000: 12.2 ms
- **Baseline_RawSQL_MarkClean** BatchSize=10000: 82.4 μs
- **Baseline_RawSQL_SelectDirty** BatchSize=10000: 149.8 μs
- **Baseline_RawSQL_Upsert** BatchSize=10000: 96.9 ms
- **Production_GetDirtyRecords** BatchSize=10000: 167.2 μs
- **Production_MarkRecordsClean** BatchSize=10000: 105.9 μs
- **Production_UpsertBatch** BatchSize=10000: 92.3 ms

## SyncBenchmarks

- **CreateRecords** RecordCount=10000
  - Mean: 4.0 ms, Allocated: 0 B
- **CreateRecords** RecordCount=1000
  - Mean: 157.6 μs, Allocated: 0 B
- **FilterDirtyRecords** RecordCount=10000
  - Mean: 35.2 μs, Allocated: 0 B
- **CreateRecords** RecordCount=100
  - Mean: 14.6 μs, Allocated: 0 B
- **CountDirtyRecords** RecordCount=10000
  - Mean: 7.7 μs, Allocated: 0 B

---

## 🎯 Optimization Targets for Session 18b

### Slowest Operations
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=10000 - 133.2 ms
1. **ProductionDatabaseBenchmarks.Baseline_RawSQL_Upsert** BatchSize=10000 - 96.9 ms
1. **ProductionDatabaseBenchmarks.Production_UpsertBatch** BatchSize=10000 - 92.3 ms
1. **DatabaseInsertBenchmarks.UpsertBatch_OnConflict** BatchSize=10000 - 87.8 ms
1. **JsonSerializationBenchmarks.DeserializeDictionary_Batch** BatchSize=10000 - 86.6 ms

### Highest Memory Allocations
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.UpsertBatch_OnConflict** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=1000 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=1000 - 0 B

