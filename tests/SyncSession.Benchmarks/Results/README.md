# SyncSystem Benchmark Results

This directory contains performance benchmark results for SyncSystem. Results are automatically exported after each benchmark run with timestamped folders.

---

## Folder Structure

```
Results/
├── 2026-01-25-140530/          # Timestamp folder for each run
│   ├── results.json            # Machine-readable (tracking over time)
│   ├── results.md              # Human-readable (GitHub markdown)
│   └── results.html            # HTML report (detailed charts)
└── README.md                   # This file
```

---

## How to Interpret Results

### Key Metrics

**Mean**: Average execution time across all iterations (primary metric)

**Median**: Middle value (50th percentile) - less affected by outliers

**StdDev**: Standard deviation - consistency of results (lower = more consistent)

**Min/Max**: Fastest and slowest execution times

**P95**: 95th percentile - 95% of operations complete within this time

**Allocated**: Total memory allocated per operation

**Gen0/Gen1/Gen2**: Garbage collection counts (lower = better)

### Benchmark Categories

**EntityReflectionBenchmarks**
- Column name extraction (cached vs cold cache)
- Property classification checks
- Dynamic parameter creation
- Entity ↔ Dictionary conversions
- Property value access

**JsonSerializationBenchmarks**
- Direct serialization/deserialization
- Dictionary-based patterns (HTTP API)
- JsonElement unwrapping overhead
- Push/Pull DTO patterns
- Memory allocation profiles

**DatabaseSqliteBenchmarks**
- Insert operations (batch vs individual)
- Upsert operations (ON CONFLICT)
- Query operations (SELECT, COUNT)
- Update operations (mark clean, single field)
- Delete operations (soft vs hard)

---

## Baseline Targets (Session 18a Goals)

### EntityReflectionHelper

**Column Extraction (Cached):**
- Mean: < 100 ns (cache hit)
- Allocated: 0 B (cache hit should allocate nothing)
- Cold cache: < 50 μs (one-time reflection cost acceptable)

**Property Classification:**
- Mean: < 50 ns (HashSet lookup)
- Allocated: 0 B

**CreateDynamicParameters:**
- Single record: < 1 μs
- Batch (1000): < 1 ms
- Allocated: Proportional to record count

**Dictionary Conversions:**
- Single record: < 5 μs
- Batch (1000): < 5 ms
- Allocated: Proportional to record count

### JSON Serialization

**Direct Serialization:**
- Single customer: < 1 μs
- Batch (1000): < 1 ms
- Batch (10000): < 10 ms

**Dictionary-Based (HTTP):**
- Single record: < 10 μs (includes EntityToDictionary)
- Batch (1000): < 10 ms

### Database Operations (SQLite)

**Insert Batch:**
- 100 records: < 5 ms
- 1000 records: < 50 ms
- 10000 records: < 500 ms

**Upsert Batch:**
- 100 records: < 10 ms
- 1000 records: < 100 ms
- 10000 records: < 1000 ms

**Query Operations:**
- SELECT dirty: < 1 ms per 1000 records
- COUNT: < 0.5 ms

---

## Tracking Improvements

### Comparing Runs

Use JSON files for programmatic comparison:

```bash
# Compare two runs
diff Results/2026-01-25-140530/results.json \
     Results/2026-01-25-150000/results.json
```

### Key Questions

**Performance regression:**
- Did Mean time increase significantly (>10%)?
- Did memory allocations increase?
- Did GC collections increase?

**Performance improvement:**
- Did optimization reduce Mean time?
- Did caching reduce cold-cache overhead?
- Did allocations decrease?

### Session 18b Goals (Expression Trees)

After implementing expression tree optimization:
- **GetPropertyValue**: 10-100x faster (compiled delegate vs reflection)
- **CreateDynamicParameters**: 5-10x faster (no PropertyInfo.GetValue calls)
- **Allocations**: Should remain constant or decrease

---

## Running Benchmarks

```bash
cd tests/SyncSystem.Benchmarks
dotnet run -c Release

# Run specific benchmark class
dotnet run -c Release --filter "*EntityReflection*"

# Run with custom args
dotnet run -c Release -- --filter "*Json*" --job short
```

**Important:** Always run in **Release** mode for accurate results.

---

## Benchmark Categories by Phase

**Phase 1: EntityReflectionHelper** (In-memory, fast)
- Baseline for Session 18b optimization
- Critical hot path validation

**Phase 2: JSON Serialization** (In-memory, fast)
- Push/Pull operation overhead
- JsonElement unwrapping cost

**Phase 3: Database Operations** (I/O bound, slower)
- SQLite: Generic operations
- MySQL (Testcontainers): Specific features

**Phase 4: End-to-End** (Full stack, slowest)
- Service layer throughput
- Queue processing
- Multi-table operations

---

## Notes

- Benchmark results are machine-specific (CPU, memory, disk)
- Use same machine for before/after comparisons
- Run multiple times and look for consistency (low StdDev)
- Outliers happen - focus on Median and Mean
- Memory diagnostics include allocation overhead, not total working set

---

**Last Updated:** January 25, 2026  
**Session:** 18a - Baseline Metrics & Bottleneck Identification
