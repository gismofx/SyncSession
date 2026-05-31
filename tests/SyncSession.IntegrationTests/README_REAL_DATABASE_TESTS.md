# Real Database Integration Tests

## Overview

These tests use **Testcontainers** to spin up real MariaDB/MySQL instances in Docker containers. This validates the sync system against actual database behavior, not just in-memory SQLite.

## Prerequisites

1. **Docker Desktop** must be installed and running
2. Docker daemon must be accessible

## Running the Tests

### From Visual Studio
1. Open Test Explorer (Test → Test Explorer)
2. Build solution (Ctrl+Shift+B)
3. Run tests in `RealDatabaseTests` class
4. Docker will automatically download MariaDB image (first time only)
5. Container spins up, runs tests, then cleans up

### From Command Line
```bash
cd tests/SyncSystem.IntegrationTests
dotnet test --filter "FullyQualifiedName~RealDatabaseTests"
```

### Run Specific Test
```bash
dotnet test --filter "FullyQualifiedName~RealDatabaseTests.PushSession_ShouldStoreDataInTempTable_ThenCommitToMainTable"
```

## What's Being Tested

### Test 1: Database Setup
✅ Verifies MariaDB container starts  
✅ Confirms all tables created correctly  
✅ Validates schema matches design

### Test 2: Push Workflow
✅ Session creation  
✅ Batch insertion to temp table  
✅ Background processor commit  
✅ Data in main table with correct SyncVersion

### Test 3: Pull Workflow
✅ Unseen session detection  
✅ Temp table population  
✅ Client retrieval  
✅ Session tracking (processed state)

### Test 4: Concurrent Operations
✅ Multiple clients pushing simultaneously  
✅ Unique version assignment  
✅ **No lost records** (session-based tracking)  
✅ Client C sees all sessions from A and B

### Test 5: Cleanup
✅ CleanDatabaseAsync removes all data  
✅ Ready for next test

## Performance

- Container startup: ~2-5 seconds (cached)
- Schema creation: ~500ms
- Each test: ~1-3 seconds
- Container cleanup: ~1 second

**Total suite runtime: ~15-20 seconds**

## Troubleshooting

### "Docker daemon not running"
**Solution:** Start Docker Desktop

### "Unable to find image 'mariadb:11.2'"
**Solution:** First run downloads image (~300MB). Subsequent runs use cache.

### Tests timing out
**Solution:** Increase timeout in test runner settings

### Permission errors on Windows
**Solution:** Run Docker Desktop as Administrator

## Differences from SQLite Tests

| Aspect | SQLite Tests | MariaDB Tests |
|--------|-------------|---------------|
| Speed | Instant | 2-5 sec startup |
| Isolation | Per-test | Shared container |
| Cleanup | Automatic | Manual (`CleanDatabaseAsync`) |
| Realism | Low | **High** ✅ |
| FK behavior | Configurable | True MySQL |
| DateTime precision | TEXT | DATETIME(6) |
| Auto-increment | AUTOINCREMENT | AUTO_INCREMENT |
| Connection pooling | N/A | Real |

## When to Use Each

**Use SQLite tests for:**
- Fast unit tests
- Local development
- CI/CD pipelines (no Docker)

**Use MariaDB tests for:**
- Integration testing
- Pre-production validation
- Performance benchmarks
- MySQL-specific features

## Next Steps

After validating these tests pass:
1. ✅ Background queue processor works
2. ✅ Session-based tracking prevents lost records
3. ✅ Concurrent operations handled correctly

**Ready to implement server controllers!**
