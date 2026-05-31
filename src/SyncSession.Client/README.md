# SyncSystem.Client

Production-ready client library for offline-first synchronization.

## Components

### Engine
- **ClientSyncEngine** - Core sync orchestrator with type-safe entities

### Database
- **SqliteClientDatabase** - Production SQLite implementation with automatic tenant filtering

### Services
- **SyncCoordinator** - High-level API for common sync scenarios

### Utilities
- **NetworkHelper** - Network connectivity checking
- **RetryPolicy** - Exponential backoff retry logic

## Current Status (Session 9)

✅ **Complete:**
- Type-safe sync engine
- SQLite client database with tenant filtering
- High-level coordinator API
- Network helpers and retry logic

⏸️ **Pending (Session 10):**
- HTTP communication layer (currently uses IServerDatabase directly for testing)
- Server API integration
- DTOs for wire protocol

## Usage Example

```csharp
// Initialize database
var connection = new SqliteConnection("Data Source=app.db");
var clientDb = new SqliteClientDatabase(connection, tenantId: "tenant-123");
await clientDb.InitializeAsync();

// Configure sync
var config = new SyncConfiguration { BatchSize = 1000 };
config.DiscoverAndRegisterTables(Assembly.GetExecutingAssembly());

// Create sync engine (NOTE: serverDb will be HTTP-based in Session 10)
var syncEngine = new ClientSyncEngine(clientDb, serverDb, clientId, config);

// Create coordinator with automatic retry
var coordinator = new SyncCoordinator(syncEngine);
coordinator.ProgressChanged += (s, e) => Console.WriteLine(e.Message);

// Sync!
var result = await coordinator.SyncAsync();
Console.WriteLine($"Pushed: {result.RecordsPushed}, Pulled: {result.RecordsPulled}");
```

## Multi-Tenancy

Automatically filters all operations by `CurrentTenantId`:

```csharp
// Set tenant context (usually from logged-in user)
clientDb.CurrentTenantId = "tenant-abc-123";

// All sync operations automatically filter by this tenant
await coordinator.SyncAsync();
```

## Network Handling

Built-in network checking and retry:

```csharp
// Require network for sync
var result = await coordinator.SyncAsync(requireNetwork: true);

// Check network manually
if (networkHelper.IsNetworkAvailable())
{
    await coordinator.SyncAsync();
}
```
