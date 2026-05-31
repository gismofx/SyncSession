# SyncSystem.Server

Production ASP.NET Core server for offline-first synchronization.

## Architecture

- **Unified Sync Controller** - Single controller for push and pull operations
- **MySQL Implementation** - Production-ready server database
- **Session Management** - Track and coordinate sync sessions
- **Hybrid Temp Tables** - Shared tables for small syncs, dedicated for large
- **Error Handling** - Global exception middleware
- **Health Checks** - `/health` endpoint for monitoring

## Endpoints

### Push Operations
```
POST /api/sync/push/begin           - Start push session
POST /api/sync/push/batch           - Upload batch of records
POST /api/sync/push/table-complete  - Mark table complete
POST /api/sync/push/complete        - Complete session (queue for processing)
POST /api/sync/push/keepalive       - Keep session alive
GET  /api/sync/push/status/{id}     - Get session status
```

### Pull Operations
```
POST /api/sync/pull/begin     - Start pull session
GET  /api/sync/pull/batch     - Fetch batch of records
POST /api/sync/pull/complete  - Complete pull session
POST /api/sync/pull/keepalive - Keep session alive
```

### Other
```
GET /health  - Health check
GET /swagger - API documentation (development only)
```

## Configuration

**appsettings.json:**
```json
{
  "DatabaseProvider": "MySQL",
  "ConnectionStrings": {
    "SyncDatabase": "Server=localhost;Database=SyncDb;User=sync_user;..."
  },
  "SyncConfiguration": {
    "PushSharedTableThreshold": 10000,
    "PullSharedTableThreshold": 10000,
    "PushBatchSize": 1000,
    "PullBatchSize": 1000,
    "SessionActivityTimeoutMinutes": 30
  }
}
```

## Running the Server

**Development:**
```bash
cd src/SyncSystem.Server
dotnet run
# Server runs at https://localhost:5001
# Swagger UI: https://localhost:5001/swagger
```

**Production:**
```bash
dotnet publish -c Release
dotnet SyncSystem.Server.dll
```

## Database Setup

**Required MySQL tables:**
- SyncSessions
- ClientProcessedSessions
- SyncMetadata
- ClientSyncState
- Shared temp tables (TempPushCustomers, TempPullOrders, etc.)

See `/scripts/MySQL/` for schema.

## Current Status (Session 10)

✅ **Complete:**
- Unified SyncController with 9 endpoints
- Push/Pull DTOs
- SessionTracker service
- TempTableManager service
- MySqlServerDatabase implementation
- Error handling middleware
- Health checks
- Swagger documentation

⏸️ **TODO (Future Sessions):**
- Background queue processing
- Actual business logic in controller actions
- Keep-alive implementation
- Session cleanup jobs
- SQL Server / PostgreSQL implementations
- Authentication/authorization

## Multi-Tenancy

Set `CurrentTenantId` on `IServerDatabase` before processing requests:

```csharp
// In middleware or controller
var database = serviceProvider.GetRequiredService<IServerDatabase>();
database.CurrentTenantId = GetTenantIdFromAuth(context);
```

All sync operations automatically filter by tenant.
