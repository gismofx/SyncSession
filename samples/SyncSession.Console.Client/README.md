# SyncSystem Console Client Demo

**Version:** 1.0.0  
**Purpose:** Interactive demonstration of SyncSystem's offline-first synchronization capabilities

---

## Overview

The SyncSystem Console Client demonstrates real-world synchronization scenarios with colored output, progress reporting, and failure recovery. Perfect for understanding how session-based tracking prevents lost records and handles concurrent operations.

**Key Features:**
- âœ… Interactive menu-driven interface
- âœ… Command-line scenario execution
- âœ… Real-time progress reporting with visual feedback
- âœ… Colored console output for clarity
- âœ… Automatic server health checks
- âœ… Ephemeral databases (clean slate for each run)

---

## Prerequisites

### Required

1. **.NET 8 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
2. **SyncSystem Server Running** - Default: `https://localhost:5001`
   - Build and run: `dotnet run --project src/SyncSystem.Server`
   - Or: Run from Visual Studio/Rider

### Database Requirements

- Server: MySQL 8.0+ or MariaDB 10.5+
- Client: SQLite 3.30+ (included with .NET)

---

## Quick Start

### 1. Start the Server

```bash
# From repository root
cd src/SyncSystem.Server
dotnet run
```

### 2. Run Interactive Menu (Default)

```bash
# From repository root
cd samples/SyncSystem.Console.Client
dotnet run
```

You'll see:
```
============================================================
               SYNCSYSTEM CONSOLE DEMO
============================================================

✓ Server is running!

============================================================
                  Select a Scenario
============================================================

  1. Simple Scenario       - Basic push/pull sync
  2. Multi-Client          - Concurrent sync (proves session-based tracking)
  3. Failure Recovery      - Network failure resilience
  4. Run All Scenarios     - Execute all 3 in sequence

  0. Exit

------------------------------------------------------------
Enter your choice (0-4):
```

### 3. Or Run Command-Line Mode

```bash
# Run specific scenario
dotnet run -- --scenario simple

# Run all scenarios
dotnet run -- --all
```

---

## Command-Line Usage

### Syntax

```bash
dotnet run -- [options]
```

### Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--scenario <name>` | `-s` | Scenario to run: `simple`, `multi`, `failure` | `simple` |
| `--all` | `-a` | Run all scenarios in sequence | - |
| `--persist` | `-p` | Keep database files after exit | false |
| `--verbose` | `-v` | Show detailed error traces | false (auto-enabled in debugger) |
| `--records <count>` | `-r` | Number of sample records to generate | 10 |
| `--batch-size <size>` | `-b` | Sync batch size | 1000 |
| `--server <url>` | - | Server URL | `https://localhost:5001` |
| `--help` | `-h` | Show help message | - |

### Examples

```bash
# Run simple scenario with 50 records
dotnet run -- --scenario simple --records 50

# Run multi-client scenario and keep databases for inspection
dotnet run -- --scenario multi --persist

# Run all scenarios with verbose output
dotnet run -- --all --verbose

# Use different server
dotnet run -- --scenario simple --server https://staging.example.com

# Generate 1000 records with custom batch size
dotnet run -- --records 1000 --batch-size 500
```

---

## Scenarios

### 1. Simple Scenario

**Purpose:** Demonstrate basic push/pull synchronization

**What it does:**
1. Creates a single client
2. Generates sample customer records locally
3. Pushes records to server
4. Pulls any server changes
5. Displays sync statistics and progress

**Best for:**
- Understanding the sync workflow
- Testing basic connectivity
- Verifying server configuration

**Example output:**
```
Single client demonstrating basic push/pull sync

✓ Client initialized (ID: abc123...)
✓ Created 10 customers (need sync)

Starting synchronization...

[Progress bar] Customers: 10/10

✓ Sync completed in 1.23s
✓ Pushed: 10 records
✓ Pulled: 0 records

Total records in database: 10
Records needing sync: 0
```

---

### 2. Multi-Client Scenario ⭐ **THE STAR DEMO**

**Purpose:** Prove session-based tracking prevents lost records during concurrent operations

**What it does:**
1. Creates two clients (A and B)
2. Client A creates 5 customers and pushes to server
3. Client B syncs and receives the 5 customers
4. Client A creates 5 MORE customers (NOT pushed yet)
5. Client B syncs again (gets nothing - A hasn't pushed)
6. Client A NOW pushes the 5 new customers
7. **THE CRITICAL TEST:** Client B syncs and MUST receive all 5 records

**Why this matters:**
Traditional version-based sync would **LOSE** those 5 records because:
- Client B synced to version V (step 5)
- Client A pushed, incrementing to V+1 (step 6)
- Client B thinks it's at V+1, pulls nothing → **LOST RECORDS**

Session-based tracking **PREVENTS** this by:
- Client B tracks WHICH SESSIONS it has processed
- Client A's push creates a NEW session
- Client B detects unseen session and pulls it

**Example output:**
```
⭐ PROVING SESSION-BASED TRACKING PREVENTS LOST RECORDS ⭐

This scenario demonstrates the core innovation:
  Even when Client B syncs while Client A pushes new data,
  Client B will receive those records on the next sync.

[... steps 1-6 ...]

============================================================
STEP 7: ⭐ THE CRITICAL TEST ⭐
Client B syncs and MUST receive Client A's 5 new customers
============================================================

✓✓✓ SUCCESS! Session-based tracking works! ✓✓✓
✓ Client B pulled 5 records
✓ Client B now has 10 total customers (expected: 10)

🎉 NO RECORDS LOST - Session tracking prevented the 'lost records' problem!
```

---

### 3. Failure Recovery Scenario

**Purpose:** Demonstrate resilience to network failures

**What it does:**
- **Test 1:** Failed Push → Recovery
  1. Creates 20 customers locally
  2. Simulates network failure mid-push (cancellation token)
  3. Verifies records remain dirty
  4. Retries sync successfully

- **Test 2:** Failed Pull → Recovery
  1. Seeds server with 30 customers from temp client
  2. Simulates network failure mid-pull
  3. Retries sync successfully
  4. Verifies UPSERT handles any duplicates

**Example output:**
```
TEST 1: Failed Push → Recovery

✓ Push interrupted (simulated network failure)
Records still needing sync: 20

Retrying sync after failure...
[Progress bar]

✓ Recovery successful! Pushed 20 records
✓ Records still dirty: 0 (expected: 0)
🎉 All records synced successfully after recovery!
```

---

## Interactive Mode

Running `dotnet run` without arguments enters interactive menu mode.

**Features:**
- Menu-driven scenario selection
- Press any key between scenarios
- Automatic cleanup on exit
- Can run all scenarios in sequence

**Navigation:**
- `1` = Simple Scenario
- `2` = Multi-Client Scenario
- `3` = Failure Recovery Scenario
- `4` = Run All Scenarios
- `0` = Exit and cleanup

---

## Understanding the Output

### Color Coding

- **Green (✓)** = Success, completed action
- **Red (✗)** = Error, failure
- **Yellow (⚠)** = Warning, caution
- **Cyan (ℹ)** = Information, status update

### Progress Bars

```
[████████████████████░░] 80% - Customers: 800/1000
```

- Shows current table being processed
- Records processed / total records
- Updates in real-time during sync

### Sync Statistics

```
✓ Sync completed in 1.23s
✓ Pushed: 50 records
✓ Pulled: 25 records
```

- Duration of sync operation
- Records sent to server (push)
- Records received from server (pull)

---

## Database Files

### Default Behavior (Ephemeral)

Database files are automatically deleted on exit:
- `client-*.db` (SQLite database)
- `client-*.db-wal` (Write-ahead log)
- `client-*.db-shm` (Shared memory)

### Persistent Mode (`--persist`)

Keep databases for inspection:
```bash
dotnet run -- --scenario multi --persist
```

**Inspect with:**
```bash
# DB Browser for SQLite
# https://sqlitebrowser.org/

# Or command-line
sqlite3 client-simple-abc123.db
```

**Useful queries:**
```sql
-- See all customers
SELECT * FROM Customers;

-- Check dirty records
SELECT * FROM Customers WHERE IsDirty = 1;

-- See sync metadata
SELECT Id, Name, IsDirty, ModifiedAtUtc, ModifiedByUserId FROM Customers;
```

---

## Configuration

### appsettings.json

```json
{
  "SyncServer": {
    "BaseUrl": "https://localhost:5001"
  }
}
```

**Override at runtime:**
```bash
dotnet run -- --server https://other-server.com
```

---

## Troubleshooting

### Server Not Running

```
✗ Cannot connect to server at https://localhost:5001
✗ Server may not be running or may be unreachable
```

**Solution:**
1. Start SyncSystem.Server: `dotnet run --project src/SyncSystem.Server`
2. Verify server is running: Open browser to `https://localhost:5001/swagger`
3. Check server URL in command or appsettings.json

### Certificate Errors (Development)

```
✗ The SSL connection could not be established
```

**Solution (Windows):**
```bash
dotnet dev-certs https --trust
```

**Solution (macOS/Linux):**
```bash
dotnet dev-certs https --clean
dotnet dev-certs https --trust
```

### Database Locked Errors

```
✗ SQLite database is locked
```

**Solution:**
- Close any open database browsers (DB Browser for SQLite)
- Ensure previous runs completed cleanup
- Use `--persist` flag to inspect files after exit

### Scenarios Timeout

**Multi-client scenario:**
If Client B doesn't receive records after 10 attempts:
- Server may be slow processing background queue
- Check server logs for errors
- Increase polling attempts in MultiClientScenario.cs

---

## Advanced Usage

### Custom Batch Sizes

Test performance with different batch sizes:
```bash
# Small batches (more API calls)
dotnet run -- --records 5000 --batch-size 100

# Large batches (fewer API calls)
dotnet run -- --records 5000 --batch-size 5000
```

### Load Testing

Generate large datasets:
```bash
# 10,000 records
dotnet run -- --scenario simple --records 10000

# 100,000 records (may take several minutes)
dotnet run -- --scenario simple --records 100000
```

### Debugging

Auto-enables verbose mode when running under debugger:
```bash
# Visual Studio / Rider: Just F5 (verbose auto-enabled)

# Or manually:
dotnet run -- --verbose
```

---

## What's Next?

After running the console demos:

1. **Review the Code**
   - See how ClientSyncEngine is used
   - Study progress reporting implementation
   - Examine conflict resolution patterns

2. **Try the API**
   - Swagger UI: `https://localhost:5001/swagger`
   - Explore push/pull endpoints
   - Test with Postman/curl

3. **Build Your Own Client**
   - Reference `ClientSimulator.cs` as template
   - Implement UI with progress reporting
   - Add your business entities

---

## Architecture Notes

### Session-Based Tracking

The core innovation demonstrated in the multi-client scenario:

**Traditional (Version-Based):**
```
Client tracks: "I'm at version 100"
Server: Records at version 101 exist
Client: "I'm already at 101" → MISSES RECORDS
```

**SyncSystem (Session-Based):**
```
Client tracks: "I've processed sessions [A, B, C]"
Server: Session D was committed
Client: "Session D is unseen" → PULLS SESSION D
```

**Benefits:**
- Zero lost records during concurrent operations
- Multiple clients can push simultaneously
- Order-independent synchronization
- Background processing friendly

### Offline-First Design

Clients work without connectivity:
1. Local changes marked as `IsDirty = true`
2. Sync when connection available
3. Push then pull workflow
4. Last-in-wins conflict resolution

### Multi-User Audit

Every record tracks:
- **WHO:** `ModifiedByUserId` 
- **WHEN:** `ModifiedAtUtc`
- **WHAT:** Complete record history via sessions

---

## Contributing

Found an issue or want to improve the demos?

1. Check existing issues
2. Open a discussion
3. Submit a pull request

---

## License

See repository LICENSE file

---

**Version:** 1.0.0  
**Last Updated:** January 18, 2026
