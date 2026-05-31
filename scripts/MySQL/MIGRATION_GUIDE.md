# Database Migration Guide

## Migration: ClientId → DeviceId (Session 16g)

### Problem
Server returns error: `Unknown column 'cps.DeviceId' in 'where clause'`

### Cause
Code was updated to use `DeviceId` for multi-device support, but the MySQL database schema wasn't updated.

### Solution
Run the migration script to update the `ClientProcessedSessions` table.

---

## Quick Start

### Option 1: Using MySQL Command Line

```bash
# Connect to MySQL
mysql -u your_username -p

# Run the migration
source scripts/MySQL/002_Migration_ClientId_To_DeviceId.sql
```

### Option 2: Using MySQL Workbench

1. Open MySQL Workbench
2. Connect to your SyncDb database
3. Open `scripts/MySQL/002_Migration_ClientId_To_DeviceId.sql`
4. Click Execute (⚡ icon)

### Option 3: Using Docker/Testcontainers

If your server uses Testcontainers (integration tests):
- No migration needed - fixtures already updated

If your server uses Docker:
```bash
docker exec -i mysql_container mysql -u root -p SyncDb < scripts/MySQL/002_Migration_ClientId_To_DeviceId.sql
```

---

## What the Migration Does

1. **Drops** `ClientProcessedSessions` table (safe - tracking data only)
2. **Recreates** with `DeviceId` instead of `ClientId`
3. **Adds indexes** for performance

### Impact

- ✅ **Safe**: No business data lost
- ✅ **Self-healing**: Clients automatically rebuild tracking data as they sync
- ⚠️ **Temporary**: Clients may re-pull some already-processed sessions (redundant but harmless)

### Schema Change

**Before:**
```sql
PRIMARY KEY (ClientId, SessionId)
```

**After:**
```sql
PRIMARY KEY (DeviceId, SessionId)
```

---

## Verification

After running the migration, verify the schema:

```sql
DESCRIBE ClientProcessedSessions;
```

Expected output:
```
+----------------+-------------+------+-----+
| Field          | Type        | Null | Key |
+----------------+-------------+------+-----+
| DeviceId       | char(36)    | NO   | PRI |
| SessionId      | char(36)    | NO   | PRI |
| ProcessedAtUtc | datetime(6) | NO   |     |
+----------------+-------------+------+-----+
```

---

## Troubleshooting

### Error: "Table doesn't exist"
- Make sure you're connected to the correct database
- Check database name in connection string (appsettings.json)

### Error: "Access denied"
- User needs DROP and CREATE privileges
- Run as admin: `mysql -u root -p`

### Want to preserve existing data?
Use the alternative ALTER command in the migration script (commented out).

---

## Next Steps

1. Run the migration
2. Restart the SyncSystem.Server
3. Run the console sample again
4. Verify sync works

---

**Created:** January 17, 2026  
**Session:** 16h - DeviceId Migration (Phase 6-7)
