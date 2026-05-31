# MySQL Database Setup for SyncSystem

## Quick Start

### Step 1: Create Your Database

**Choose your preferred database name and collation:**

```sql
-- Option A: General purpose (recommended for most cases)
CREATE DATABASE YourDbName 
  CHARACTER SET utf8mb4 
  COLLATE utf8mb4_general_ci;

-- Option B: Accurate sorting (slower, better for international text)
CREATE DATABASE YourDbName 
  CHARACTER SET utf8mb4 
  COLLATE utf8mb4_unicode_ci;

-- Option C: Case-sensitive (if you need it)
CREATE DATABASE YourDbName 
  CHARACTER SET utf8mb4 
  COLLATE utf8mb4_bin;
```

**Common database names:**
- `SyncDb` (default in examples)
- `ProductionSync`
- `YourAppSync`
- `YourCompany_Sync`

### Step 2: Create Database User (Optional but Recommended)

```sql
-- Create dedicated sync user
CREATE USER 'sync_user'@'%' IDENTIFIED BY 'your_secure_password';

-- Grant permissions (minimal required)
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP 
ON YourDbName.* 
TO 'sync_user'@'%';

FLUSH PRIVILEGES;
```

### Step 3: Configure Connection String

**In User Secrets (Development):**

```bash
cd src/SyncSystem.Server
dotnet user-secrets set "ConnectionStrings:SyncDatabase" "Server=localhost;Database=YourDbName;User=sync_user;Password=your_password;AllowPublicKeyRetrieval=true;"
```

**In appsettings.Production.json (Production):**

```json
{
  "ConnectionStrings": {
    "SyncDatabase": "Server=prod-server;Database=YourDbName;User=sync_user;Password=ENV_VAR_PASSWORD;"
  }
}
```

### Step 4: Run the Server

```bash
dotnet run --project src/SyncSystem.Server
```

**First run output:**
```
Testing database connection...
✓ Database connection successful
Checking database schema...
Beginning database upgrade
Executing Database Server script '001_Infrastructure.sql'
Executing Database Server script '002_ExampleBusinessTables.sql'
✅ Database migration successful! Executed 2 script(s)
```

**Subsequent runs:**
```
ℹ️  Database is already up to date (no new scripts to run)
```

---

## Migration Scripts

### 001_Infrastructure.sql (REQUIRED)

Creates core sync infrastructure:
- SyncSessions
- SyncSessionTables
- ClientProcessedSessions
- SyncMetadata
- ClientSyncState
- SyncLog
- Monitoring views

**This script is required for all deployments.**

### 002_ExampleBusinessTables.sql (OPTIONAL)

Creates example business tables and temp tables:
- Customers, Orders, OrderItems (with sync columns)
- TempPush* tables (staging areas)
- TempPull* tables (pull snapshots)

**Skip this if you have your own schema.**

---

## Configuration Options

### Database Provider

In `appsettings.json`:

```json
{
  "DatabaseProvider": "MySQL"  // or "MariaDB" (both work the same)
}
```

### Collation Choice Guide

| Collation | Performance | Sorting Accuracy | Use Case |
|-----------|-------------|------------------|----------|
| `utf8mb4_general_ci` | ⚡ Fast | Good | Most applications |
| `utf8mb4_unicode_ci` | Slower | ✅ Best | International text |
| `utf8mb4_bin` | ⚡ Fastest | Case-sensitive | Technical IDs |

**Recommendation:** Use `utf8mb4_general_ci` unless you have specific internationalization requirements.

---

## Using Your Own Schema

### Don't run 002_ExampleBusinessTables.sql

Either:
- Delete it before first run
- Comment out in DbUp if already deployed

### Create your own business tables

Follow the pattern in `002_ExampleBusinessTables.sql`:

```sql
CREATE TABLE YourTable (
    Id CHAR(36) PRIMARY KEY,
    
    -- Your business columns --
    YourColumn VARCHAR(255) NOT NULL,
    
    -- REQUIRED sync columns --
    SyncVersion BIGINT NOT NULL DEFAULT 0,
    ModifiedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    SyncSessionId CHAR(36) NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    
    -- REQUIRED indexes --
    INDEX IX_YourTable_SyncVersion (SyncVersion),
    INDEX IX_YourTable_Session (SyncSessionId)
) ENGINE=InnoDB;
```

### Create matching temp tables

See detailed patterns in `002_ExampleBusinessTables.sql` comments.

### Register in SyncMetadata

```sql
INSERT INTO SyncMetadata (TableName, CurrentVersion)
VALUES ('YourTable', 0);
```

### Configure in appsettings.json

```json
{
  "SyncConfiguration": {
    "Tables": {
      "YourTable": {
        "Priority": 1,
        "Enabled": true
      }
    }
  }
}
```

---

## Production Deployment

### Option 1: Manual Database Creation (Recommended)

1. DBA creates database with org standards:
   ```sql
   CREATE DATABASE ProdSync 
     CHARACTER SET utf8mb4 
     COLLATE utf8mb4_general_ci;
   ```

2. Configure connection string in Azure App Settings / AWS Secrets / etc.

3. Deploy application - DbUp runs migrations automatically

### Option 2: Automated with DbUp

DbUp will execute all `.sql` files in `scripts/MySQL/` folder in alphabetical order.

Add new migrations as:
- `003_YourMigration.sql`
- `004_AnotherChange.sql`

**DbUp tracks executed scripts** in `SchemaVersions` table - safe to run multiple times.

---

## Troubleshooting

### "Database does not exist" error

Create the database manually first:
```sql
CREATE DATABASE YourDbName CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
```

### "Access denied" error

Grant proper permissions:
```sql
GRANT ALL PRIVILEGES ON YourDbName.* TO 'sync_user'@'%';
FLUSH PRIVILEGES;
```

### "Table already exists" errors

The new scripts use `CREATE TABLE IF NOT EXISTS` so this shouldn't happen. If it does:
- Check which script version you're running
- Verify DbUp's `SchemaVersions` table

### Change database name after deployment

1. Create new database with new name
2. Export data from old database
3. Import into new database
4. Update connection string
5. Restart server (DbUp will see existing schema)

---

## Migration Best Practices

### Naming Convention

```
001_Infrastructure.sql           # Core sync tables
002_ExampleBusinessTables.sql    # Optional examples
003_AddIndexToCustomers.sql      # Your changes
004_AddProductsTable.sql         # New table
005_AlterOrdersAddStatus.sql     # Schema change
```

### Always Use IF NOT EXISTS

```sql
CREATE TABLE IF NOT EXISTS YourTable (...);
ALTER TABLE YourTable ADD COLUMN IF NOT EXISTS NewColumn VARCHAR(50);
```

### Test Migrations

1. Test on dev database
2. Backup production
3. Run on staging
4. Monitor for errors
5. Deploy to production

---

## Advanced: Database Creation Automation

If you want the server to auto-create the database, add this **before** DbUp runs in `Program.cs`:

```csharp
// Extract database name from connection string
var builder = new MySqlConnectionStringBuilder(connectionString);
var dbName = builder.Database;
builder.Database = null; // Connect without database

// Create database if not exists
using (var conn = new MySqlConnection(builder.ConnectionString))
{
    await conn.OpenAsync();
    await conn.ExecuteAsync($@"
        CREATE DATABASE IF NOT EXISTS `{dbName}` 
        CHARACTER SET utf8mb4 
        COLLATE utf8mb4_general_ci
    ");
}
```

**Note:** This requires elevated permissions (CREATE DATABASE). Not recommended for production.

---

## References

- [MySQL Character Sets](https://dev.mysql.com/doc/refman/8.0/en/charset-mysql.html)
- [Collation Comparison](https://stackoverflow.com/questions/766809/whats-the-difference-between-utf8-general-ci-and-utf8-unicode-ci)
- [DbUp Documentation](https://dbup.readthedocs.io/)

---

**Questions?** Check the main [technical-reference.md](../../technical-reference.md) for SQL patterns and setup guides.
