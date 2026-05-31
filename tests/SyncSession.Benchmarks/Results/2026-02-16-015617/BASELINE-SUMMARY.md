# SyncSystem Benchmark Baseline Summary
**Date:** 2026-02-16 07:32:35 UTC
**Total Benchmarks:** 191

---

## DatabaseInsertBenchmarks

### Database Operations
- **InsertBatch_IndividualInserts** BatchSize=100: 2.6 ms
- **InsertBatch_SingleTransaction** BatchSize=100: 1.6 ms
- **UpsertBatch_OnConflict** BatchSize=100: 1.5 ms
- **InsertBatch_IndividualInserts** BatchSize=1000: 27.5 ms
- **InsertBatch_SingleTransaction** BatchSize=1000: 13.7 ms
- **UpsertBatch_OnConflict** BatchSize=1000: 12.7 ms
- **InsertBatch_IndividualInserts** BatchSize=10000: 176.6 ms
- **InsertBatch_SingleTransaction** BatchSize=10000: 90.5 ms
- **UpsertBatch_OnConflict** BatchSize=10000: 87.3 ms

## DatabaseQueryBenchmarks

### Database Operations
- **CountBySession** RecordCount=100: 55.2 μs
- **CountDirtyRecords** RecordCount=100: 44.4 μs
- **SelectAll** RecordCount=100: 453.8 μs
- **SelectBySession** RecordCount=100: 185.6 μs
- **SelectDeleted** RecordCount=100: 114.1 μs
- **SelectDirtyRecords** RecordCount=100: 311.9 μs
- **CountBySession** RecordCount=1000: 71.3 μs
- **CountDirtyRecords** RecordCount=1000: 79.5 μs
- **SelectAll** RecordCount=1000: 4.0 ms
- **SelectBySession** RecordCount=1000: 1.1 ms
- **SelectDeleted** RecordCount=1000: 618.6 μs
- **SelectDirtyRecords** RecordCount=1000: 2.4 ms
- **CountBySession** RecordCount=10000: 206.5 μs
- **CountDirtyRecords** RecordCount=10000: 249.9 μs
- **SelectAll** RecordCount=10000: 27.4 ms
- **SelectBySession** RecordCount=10000: 9.6 ms
- **SelectDeleted** RecordCount=10000: 5.6 ms
- **SelectDirtyRecords** RecordCount=10000: 17.1 ms

## DatabaseUpdateBenchmarks

### Database Operations
- **HardDelete** RecordCount=100: 74.2 μs
- **SoftDelete** RecordCount=100: 63.6 μs
- **UpdateMarkClean** RecordCount=100: 125.4 μs
- **UpdateMultipleFields** RecordCount=100: 100.3 μs
- **UpdateSingleField** RecordCount=100: 81.0 μs
- **UpdateWithTransaction** RecordCount=100: 86.8 μs
- **HardDelete** RecordCount=1000: 122.0 μs
- **SoftDelete** RecordCount=1000: 115.9 μs
- **UpdateMarkClean** RecordCount=1000: 431.9 μs
- **UpdateMultipleFields** RecordCount=1000: 520.2 μs
- **UpdateSingleField** RecordCount=1000: 380.8 μs
- **UpdateWithTransaction** RecordCount=1000: 484.5 μs
- **HardDelete** RecordCount=10000: 212.5 μs
- **SoftDelete** RecordCount=10000: 192.0 μs
- **UpdateMarkClean** RecordCount=10000: 5.4 ms
- **UpdateMultipleFields** RecordCount=10000: 6.2 ms
- **UpdateSingleField** RecordCount=10000: 4.1 ms
- **UpdateWithTransaction** RecordCount=10000: 5.4 ms

## EntityReflectionBenchmarks

### Key Metrics
- **Column Extraction (Cached):** 92.6 ns avg, 0 B allocated
- **Cold Cache Penalty:** 2.6 μs (vs 92.6 ns cached)
- **CreateDynamicParameters:**
  - BatchSize=1: 2.4 μs, 0 B
  - BatchSize=1: 2.5 μs, 0 B
  - BatchSize=100: 2.4 μs, 0 B
  - BatchSize=100: 225.1 μs, 0 B
  - BatchSize=1000: 2.4 μs, 0 B
  - BatchSize=1000: 2.4 ms, 0 B
- **Dictionary Conversions:**
  - To Dictionary BatchSize=1: 1.8 μs, 0 B
  - To Dictionary BatchSize=1: 1.7 μs, 0 B
  - To Dictionary BatchSize=100: 1.8 μs, 0 B
  - To Dictionary BatchSize=100: 176.2 μs, 0 B
  - To Dictionary BatchSize=1000: 1.8 μs, 0 B
  - To Dictionary BatchSize=1000: 1.7 ms, 0 B
  - From Dictionary BatchSize=1: 1.2 μs, 0 B
  - From Dictionary BatchSize=1: 1.2 μs, 0 B
  - From Dictionary BatchSize=100: 1.2 μs, 0 B
  - From Dictionary BatchSize=100: 117.9 μs, 0 B
  - From Dictionary BatchSize=1000: 1.2 μs, 0 B
  - From Dictionary BatchSize=1000: 1.2 ms, 0 B

## JsonSerializationBenchmarks

### Serialization Performance
**Serialize:**
- Serialize_SingleCustomer BatchSize=1: 523.1 ns
- Serialize_CustomerBatch BatchSize=1: 575.9 ns
- SerializeDictionary_SingleRecord BatchSize=1: 3.0 μs
**Deserialize:**
- Deserialize_SingleCustomer BatchSize=1: 881.6 ns
- Deserialize_CustomerBatch BatchSize=1: 1.0 μs
- DeserializeDictionary_SingleRecord BatchSize=1: 4.5 μs

## ProductionDatabaseBenchmarks

### Database Operations
- **Baseline_RawSQL_MarkClean** BatchSize=100: 59.0 μs
- **Baseline_RawSQL_SelectDirty** BatchSize=100: 72.0 μs
- **Baseline_RawSQL_Upsert** BatchSize=100: 1.6 ms
- **Production_GetDirtyRecords** BatchSize=100: 85.0 μs
- **Production_MarkRecordsClean** BatchSize=100: 56.4 μs
- **Production_UpsertBatch** BatchSize=100: 1.9 ms
- **Baseline_RawSQL_MarkClean** BatchSize=1000: 51.2 μs
- **Baseline_RawSQL_SelectDirty** BatchSize=1000: 73.1 μs
- **Baseline_RawSQL_Upsert** BatchSize=1000: 14.9 ms
- **Production_GetDirtyRecords** BatchSize=1000: 82.0 μs
- **Production_MarkRecordsClean** BatchSize=1000: 58.5 μs
- **Production_UpsertBatch** BatchSize=1000: 14.2 ms
- **Baseline_RawSQL_MarkClean** BatchSize=10000: 109.0 μs
- **Baseline_RawSQL_SelectDirty** BatchSize=10000: 175.1 μs
- **Baseline_RawSQL_Upsert** BatchSize=10000: 111.5 ms
- **Production_GetDirtyRecords** BatchSize=10000: 192.5 μs
- **Production_MarkRecordsClean** BatchSize=10000: 110.2 μs
- **Production_UpsertBatch** BatchSize=10000: 102.4 ms

## PushSerializationBenchmarks

- **Current_Customer_DictConvert_Filter_Serialize** BatchSize=1000
  - Mean: 2.7 ms, Allocated: 0 B
- **Current_Product_DictConvert_Filter_Serialize** BatchSize=1000
  - Mean: 2.2 ms, Allocated: 0 B
- **Overhead_DictConvert_Filter_Only** BatchSize=1000
  - Mean: 1.7 ms, Allocated: 0 B
- **Proposed_Customer_DirectSerialize** BatchSize=1000
  - Mean: 625.9 μs, Allocated: 0 B
- **Proposed_Product_DirectSerialize** BatchSize=1000
  - Mean: 540.3 μs, Allocated: 0 B

## SyncBenchmarks

- **CreateRecords** RecordCount=10000
  - Mean: 3.0 ms, Allocated: 0 B
- **CreateRecords** RecordCount=1000
  - Mean: 138.5 μs, Allocated: 0 B
- **FilterDirtyRecords** RecordCount=10000
  - Mean: 32.1 μs, Allocated: 0 B
- **CreateRecords** RecordCount=100
  - Mean: 13.9 μs, Allocated: 0 B
- **CountDirtyRecords** RecordCount=10000
  - Mean: 7.7 μs, Allocated: 0 B

---

## 🎯 Optimization Targets for Session 18b

### Slowest Operations
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=10000 - 176.6 ms
1. **ProductionDatabaseBenchmarks.Baseline_RawSQL_Upsert** BatchSize=10000 - 111.5 ms
1. **JsonSerializationBenchmarks.DeserializeDictionary_Batch** BatchSize=10000 - 103.1 ms
1. **ProductionDatabaseBenchmarks.Production_UpsertBatch** BatchSize=10000 - 102.4 ms
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=10000 - 90.5 ms

### Highest Memory Allocations
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.UpsertBatch_OnConflict** BatchSize=100 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_SingleTransaction** BatchSize=1000 - 0 B
1. **DatabaseInsertBenchmarks.InsertBatch_IndividualInserts** BatchSize=1000 - 0 B

