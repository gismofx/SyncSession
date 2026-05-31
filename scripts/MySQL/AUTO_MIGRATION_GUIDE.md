# Automatic Database Migration - Session 16h

## ✅ Solution: Automatic Migration on Server Startup

The server uses **DbUp** for automatic database migrations. When you restart the server, the migration will run automatically!

---

## What Happens on Next Server Start

1. **Server starts** and reads `appsettings.json`
2. **DatabaseMigrator** checks for new scripts in `Scripts/MySQL/`
3. **Finds**: `003_Migration_ClientId_To_DeviceId.sql` (not run yet)
4. **Executes** the migration script
5. **Updates** `ClientProcessedSessions` table: `ClientId` → `DeviceId`
6. **Tracks** execution in `SchemaVersions` table (won't run again)
7. **✅ Done** - server starts normally

---

## Files Created/Updated

### 1. **`003_Migration_ClientId_To_DeviceId.sql`** (NEW - AUTO-RUNS)
   - **Idempotent**: Safe to run multiple times
   - **Smart**: Checks if migration needed before running
   - **Safe**: Uses ALTER instead of DROP
   - **Automatic**: DbUp runs it on startup

### 2. **`MIGRATION_GUIDE.md`** (Reference)
   - Manual migration instructions (if needed)
   - Troubleshooting guide
   - Not needed for automatic migration

### 3. **`002_Migration_ClientId_To_DeviceId.sql`** (❌ DELETE THIS)
   - Old file with wrong numbering
   - Conflicts with `002_ExampleBusinessTables.sql`
   - Should be deleted manually

---

## What You Need to Do

### Step 1: Delete Old Migration File
```bash
# Delete this file:
scripts/MySQL/002_Migration_ClientId_To_DeviceId.sql
```

### Step 2: Rebuild and Restart Server
```bash
# Build (copies scripts to output directory)
dotnet build src/SyncSystem.Server

# Run server
dotnet run --project src/SyncSystem.Server
```

### Step 3: Watch Server Logs

You should see:
```
[INFO] Checking database schema...
✅ Database migration successful! Executed 1 script(s):
   - 003_Migration_ClientId_To_DeviceId.sql
```

Or if already migrated:
```
ℹ️  Database is already up to date (no new scripts to run)
```

---

## How DbUp Works

```
scripts/MySQL/
├── 001_Infrastructure.sql          ← Already run (creates tables)
├── 002_ExampleBusinessTables.sql   ← Already run (example data)
└── 003_Migration_ClientId_To_DeviceId.sql  ← NEW (will run next startup)
```

**DbUp tracks executed scripts in the `SchemaVersions` table:**

| ScriptName                              | Applied          |
|-----------------------------------------|------------------|
| 001_Infrastructure.sql                  | 2026-01-15 10:00 |
| 002_ExampleBusinessTables.sql           | 2026-01-15 10:00 |
| 003_Migration_ClientId_To_DeviceId.sql  | *(not yet)*      |

**On next startup:**
- DbUp sees `003_Migration...` hasn't run
- Executes it automatically
- Adds entry to `SchemaVersions`
- Won't run again (even if file changes)

---

## Verification

After server restarts, verify the migration worked:

```sql
-- Check the schema
DESCRIBE ClientProcessedSessions;

-- Should show:
-- DeviceId      | char(36)    | NO   | PRI |
-- SessionId     | char(36)    | NO   | PRI |
-- ProcessedAtUtc| datetime(6) | NO   |     |

-- Check DbUp tracking
SELECT * FROM SchemaVersions ORDER BY Applied DESC LIMIT 5;

-- Should include: 003_Migration_ClientId_To_DeviceId.sql
```

---

## Troubleshooting

### Migration Doesn't Run
- **Check**: Is the script in `bin/Debug/net8.0/Scripts/MySQL/`?
- **Fix**: Rebuild the project (`dotnet build`)
- **Verify**: Script has `.sql` extension

### "Table already exists" Error
- **Cause**: Migration tried to CREATE instead of ALTER
- **Fix**: Already handled - migration uses ALTER
- **Safe**: Migration is idempotent

### "Already migrated" Message
- **Meaning**: Table already uses DeviceId
- **Action**: No migration needed, you're good!

---

## Next Steps

1. ❌ **Delete** `scripts/MySQL/002_Migration_ClientId_To_DeviceId.sql`
2. 🔨 **Build** the server project
3. ▶️ **Run** the server
4. 👀 **Watch** the startup logs
5. ✅ **Test** the console sample

**After restart, the console sample should work!**

---

**Created:** January 17, 2026  
**Session:** 16h - DeviceId Migration (Automatic with DbUp)
