# SyncSession

[![Tests](https://github.com/gismofx/SyncSession/actions/workflows/test.yml/badge.svg)](https://github.com/gismofx/SyncSession/actions/workflows/test.yml)
[![NuGet: Core](https://img.shields.io/nuget/v/SyncSession.Core?label=Core)](https://www.nuget.org/packages/SyncSession.Core)
[![NuGet: Client](https://img.shields.io/nuget/v/SyncSession.Client?label=Client)](https://www.nuget.org/packages/SyncSession.Client)
[![NuGet: Server](https://img.shields.io/nuget/v/SyncSession.Server?label=Server)](https://www.nuget.org/packages/SyncSession.Server)
[![Downloads](https://img.shields.io/nuget/dt/SyncSession.Client?label=downloads)](https://www.nuget.org/packages/SyncSession.Client)
[![GitHub Stars](https://img.shields.io/github/stars/gismofx/SyncSession?style=social)](https://github.com/gismofx/SyncSession)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512bd4?logo=dotnet)](https://dotnet.microsoft.com)

**Production-ready, offline-first synchronization library for .NET applications.**

SyncSession enables multiple clients to sync data with a central server while handling network failures, concurrent operations, and large datasets through session-based change tracking.

---

## The Problem SyncSession Solves

Traditional version-based sync misses records during concurrent operations:

1. Client A pushes 1,000 records → version increments to 101
2. Client B pulls during processing → sees version 100
3. Client A commits → version is now 101
4. Client B pulls again (already at 101) → **misses 1,000 records**

**SyncSession's solution:** clients track *which sessions they've processed* rather than *what version they're at*. No lost records, even under concurrent load.

---

## Features

- ✅ **Session-based tracking** — no lost records during concurrent operations
- ✅ **Offline-first** — clients can be offline for extended periods
- ✅ **Hybrid temp table strategy** — optimized for both small and large syncs
- ✅ **Multi-table atomic sessions** — referential integrity maintained
- ✅ **Last-in-wins conflict resolution** — server version wins on conflict
- ✅ **Multi-tenant support** — tenant-scoped push/pull/seed operations
- ✅ **Full audit trail** — WHO + WHEN + WHAT on every synced record
- ✅ **Zero-configuration client setup** — assembly scanning, no manual registration

---

## Installation

```bash
# Server (ASP.NET Core)
dotnet add package SyncSession.Server

# Client (.NET 8.0+ / .NET 10.0+)
dotnet add package SyncSession.Client
```

**Server requirements:** MySQL 8.0+ or MariaDB 10.5+
**Client requirements:** SQLite 3.30+

---

## Quick Start

### 1. Define your entity

```csharp
[SyncTable("Customers", Priority = 1)]
public class Customer : ISyncEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    // ISyncEntity
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? SyncSessionId { get; set; }
    public bool IsDeleted { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
}
```

### 2. Configure the server (`Program.cs`)

```csharp
builder.Services.AddSyncSession(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("SyncDatabase");
    options.EntityAssembly = typeof(Customer).Assembly;
});

var app = builder.Build();
await app.UseSyncSession();
app.MapSyncEndpoints();
```

### 3. Configure the client

```csharp
var syncEngine = ClientSyncEngineBuilder.Build(
    clientDatabase: clientDb,
    serverClient: httpSyncApi,
    deviceId: deviceId,
    configuration: new ClientSyncConfiguration
    {
        PushBatchSize = 1000,
        PullBatchSize = 1000
    },
    entitiesAssembly: typeof(Customer).Assembly
);

await syncEngine.SynchronizeAsync();
```

> 📖 **New to SyncSession?** See the [Getting Started guide](docs/getting-started.md) for a full walkthrough and common gotchas.

### Two ways to sync

`SynchronizeAsync` on the **engine** gives you full control (you handle retry and connectivity). `SyncCoordinator` wraps the engine to add skip-if-offline and automatic retry — handy for a simple "Sync now" button:

```csharp
// Full control — you own retry + connectivity
await engine.SynchronizeAsync(progress, cancellationToken: ct);

// Batteries-included — skips when offline, retries transient failures
var coordinator = new SyncCoordinator(engine);
await coordinator.SyncAsync(progress, requireNetwork: true, cancellationToken: ct);
```

See [Getting Started](docs/getting-started.md#two-ways-to-sync-engine-vs-coordinator) for DI registration and when to use each.

---

## Architecture

```
┌─────────────────────────────────────────────┐
│        Client Application (Desktop/Mobile)  │
└──────────────────┬──────────────────────────┘
                   │ HTTP/HTTPS
┌──────────────────▼──────────────────────────┐
│           SyncSession.Client                │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐   │
│  │SyncEngine│  │PushEngine│  │PullEngine│   │
│  └──────────┘  └──────────┘  └──────────┘   │
│  ┌───────────────────────────────────────┐  │
│  │   SqliteClientDatabase (Local SQLite) │  │
│  └───────────────────────────────────────┘  │
└──────────────────┬──────────────────────────┘
                   │ REST API
┌──────────────────▼──────────────────────────┐
│           SyncSession.Server                │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐   │
│  │ Push API │  │ Pull API │  │Health API│   │
│  └──────────┘  └──────────┘  └──────────┘   │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐   │
│  │TempTable │  │ Session  │  │  Queue   │   │
│  │ Manager  │  │ Tracker  │  │Processor │   │
│  └──────────┘  └──────────┘  └──────────┘   │
│  ┌───────────────────────────────────────┐  │
│  │         MySQL / MariaDB               │  │
│  └───────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

---

## Configuration

### Server (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "SyncDatabase": "Server=localhost;Database=SyncDb;..."
  }
}
```

Key server defaults (all overridable via `ServerSyncConfiguration`):

| Setting | Default | Description |
|---|---|---|
| `PushSharedTableThreshold` | 10,000 | Records above this use dedicated temp tables |
| `PullSharedTableThreshold` | 10,000 | Records above this use dedicated temp tables |
| `SessionActivityTimeoutMinutes` | 30 | Minutes before stale sessions are failed |
| `SessionRetentionDays` | 0 | Days before purging old sessions (0 = never) |
| `QueuePollIntervalSeconds` | 5 | Background queue poll interval |

### Client

| Setting | Default | Description |
|---|---|---|
| `PushBatchSize` | 1,000 | Records per push batch (max 10,000) |
| `PullBatchSize` | 1,000 | Records per pull batch (max 10,000) |
| `PushStatusTimeoutSeconds` | 300 | Max wait for server commit |

---

## Performance

Benchmarks run against a local MySQL instance:

| Operation | Records | Throughput |
|---|---|---|
| Push | 1,000 | ~833/sec |
| Push | 10,000 | ~1,176/sec |
| Push | 100,000 | ~1,124/sec |
| Pull | 1,000 | ~1,250/sec |
| Pull | 10,000 | ~1,613/sec |
| Pull | 100,000 | ~1,408/sec |

> Re-run benchmarks with `dotnet run --project tests/SyncSession.Benchmarks -c Release` for current figures.

---

## Roadmap

**Post-1.0:**
- PostgreSQL server support
- Custom conflict resolution strategies
- Real-time sync via WebSocket/SignalR
- Differential sync (delta updates only)
- Compression for network payloads

---

## Contributing

Contributions are welcome. Please open an issue before submitting a pull request for significant changes.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes
4. Open a Pull Request

---

## License

MIT License — see [LICENSE](LICENSE) for details.
