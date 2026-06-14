# Getting Started

This guide walks through setting up SyncSession in a **new (greenfield)** project —
defining entities, wiring the server, configuring a client, and running your first
sync — followed by a list of common gotchas worth reading before you ship.

> Adding SyncSession to an **existing** app that already has tables and data? That
> path (additive schema migration, dual-field periods, per-tenant rollout) is
> covered separately in the Migration Guide.

---

## Prerequisites

- **.NET SDK 8.0 or 10.0** — packages target `net8.0` and `net10.0`.
- **Server database:** MySQL 8.0+ or MariaDB 10.5+.
- **Client database:** SQLite 3.30+ (any provider — desktop, mobile, or WASM).

## Install

| Package | Install where | Purpose |
|---------|---------------|---------|
| `SyncSession.Server` | Your ASP.NET Core host | Push/pull/seed REST API + queue processing |
| `SyncSession.Client` | Your client app | Local SQLite store + sync engine |
| `SyncSession.Core` | Pulled in transitively | Shared entities, interfaces, DTOs |

```bash
dotnet add package SyncSession.Server   # server host
dotnet add package SyncSession.Client   # client app
```

Both server and client reference the **same entity types**, so put your entity
classes in a shared project both can reference.

---

## Step 1 — Model your entities

Every synced type implements `ISyncEntity` and carries a `[SyncTable]` attribute.
The interface splits properties into two groups:

- **Business properties** you own and set: `Id`, `IsDeleted`, `ModifiedByUserId`,
  plus your own fields (`Name`, `Email`, …).
- **Infrastructure properties** the library manages: `ModifiedAtUtc`,
  `SyncSessionId`, and `IsDirty` (client-side change flag). Don't set these by hand
  — with one exception noted in the gotchas (`IsDirty`).

```csharp
[SyncTable("Customers", Priority = 1)]
public class Customer : ISyncEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    // Infrastructure (library-managed)
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? SyncSessionId { get; set; }

    // Business (you set these)
    public bool IsDeleted { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
}
```

**`Priority`** controls table sync order. Parents must sync before children, so a
table referenced by a foreign key gets a lower number than the table referencing it.

**Multi-tenant?** Implement `IMultiTenantSyncEntity` instead — it adds a `TenantId`
string, and the server scopes every push/pull/seed to the caller's tenant.

---

## Step 2 — Configure the server

Register SyncSession in DI, run its startup hook, and map the endpoints:

```csharp
builder.Services.AddSyncSession(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("SyncDatabase");
    options.EntityAssembly   = typeof(Customer).Assembly;
});

var app = builder.Build();

app.UseCors();              // see gotcha #2 — CORS must come first
app.UseHttpsRedirection();

await app.UseSyncSession(); // provisions session-tracking infrastructure
app.MapSyncEndpoints();     // push / pull / seed / health routes

app.Run();
```

### Database schema

SyncSession owns its session-tracking tables (`SyncSessions`,
`ClientProcessedSessions`, and temp tables) — these are provisioned for you. Your
job is to ensure each **business** table carries the sync columns:

```sql
CREATE TABLE Customers (
    Id               VARCHAR(36) PRIMARY KEY,   -- see gotcha #1 (not CHAR(36))
    Name             VARCHAR(255) NOT NULL,
    Email            VARCHAR(255) NOT NULL,
    ModifiedByUserId VARCHAR(100) NOT NULL DEFAULT 'System',
    IsDeleted        TINYINT(1)   NOT NULL DEFAULT 0,
    ModifiedAtUtc    DATETIME(6)  NOT NULL,
    SyncSessionId    VARCHAR(36)  NULL,
    INDEX IX_Customers_Session (SyncSessionId)
);
```

> Note: records do **not** store a version number. `SyncVersion` lives only on
> `SyncSessions`; records link to it via `SyncSessionId`. This is what makes
> session-based tracking immune to the lost-records problem.

---

## Step 3 — Configure the client

Point the client at its local SQLite database and the server's HTTP API, then build
the engine. Assembly scanning discovers every `[SyncTable]` type — no manual
registration:

```csharp
// Provision the library's local bookkeeping tables (LocalSyncState + LocalSyncMetadata).
// Idempotent (CREATE TABLE IF NOT EXISTS), so it is safe on every startup. Do this before
// the first seed or sync, or those operations hit a missing table.
await clientDb.InitializeAsync();

var syncEngine = ClientSyncEngineBuilder.Build(
    clientDatabase:   clientDb,        // your SQLite-backed IClientDatabase
    serverClient:     httpSyncApi,     // HTTP client pointed at the server
    deviceId:         deviceId,        // stable per-device identifier
    configuration:    new ClientSyncConfiguration
    {
        PushBatchSize = 1000,
        PullBatchSize = 1000
    },
    entitiesAssembly: typeof(Customer).Assembly);
```

Each client SQLite table mirrors the server columns, with booleans stored as
`INTEGER` and `IsDirty` added for local change tracking:

```sql
CREATE TABLE Customers (
    Id               TEXT PRIMARY KEY,
    Name             TEXT NOT NULL,
    Email            TEXT NOT NULL,
    IsDirty          INTEGER NOT NULL DEFAULT 0,
    ModifiedAtUtc    TEXT NOT NULL,
    ModifiedByUserId TEXT NOT NULL DEFAULT 'Local',
    IsDeleted        INTEGER NOT NULL DEFAULT 0
);
```

> **Provision the local tables first.** With the built-in `SqliteClientDatabase`, the
> `await clientDb.InitializeAsync()` call above creates the library's `LocalSyncState` and
> `LocalSyncMetadata` bookkeeping tables (it does **not** create your business tables, which
> you own). Every `IClientDatabase` exposes `InitializeAsync()`, so a custom store (e.g. a
> WASM/IndexedDB one) implements it too and creates both bookkeeping tables in its own startup
> path — run the shared `SqliteClientSchema.AllStatements` DDL so the schema can't drift. See Gotcha #14.

## Step 4 — Run a sync

```csharp
SyncResult result = await syncEngine.SynchronizeAsync();
// result.RecordsPushed, result.RecordsPulled, result.Success
```

One call performs the full **push-then-pull** cycle: local dirty records upload
first, the server processes them in the background and commits with a version, then
the client pulls every session it hasn't seen. After a successful push, uploaded
records are marked clean automatically.

To write data that will sync, persist it through **your app's own local data layer**
(Dapper, raw SQL, whatever you already use) and set `IsDirty = true` plus the user
stamp. SyncSession does not impose a local write API for your data — on the next
cycle the engine collects flagged rows via `GetDirtyRecordsAsync<T>()` and clears
them after a successful push.

```csharp
var c = new Customer { Name = "Ada", Email = "ada@example.com",
                       ModifiedByUserId = currentUser.Id, IsDirty = true };
await myCustomerRepository.UpsertAsync(c);   // your code (e.g. Dapper)

await syncEngine.SynchronizeAsync();
```

---

## Sync progress & cancellation

`SynchronizeAsync` (and `PushAsync` / `PullAsync`) accept an
`IProgress<SyncProgress>` and a `CancellationToken`. Pass a progress reporter to
drive a UI — updates fire at **per-batch** granularity, so a progress bar moves
smoothly through large tables rather than jumping table-to-table.

```csharp
var progress = new Progress<SyncProgress>(p =>
{
    // p.Phase            — Connecting, PushTable, PullTable, Complete, …
    // p.CurrentTable     — table currently syncing
    // p.TablesCompleted / p.TotalTables    — overall step count
    // p.RecordsProcessed / p.TotalRecords  — progress within the current table
    // p.StatusMessage    — ready-to-display text
    UpdateProgressBar(p.TablesCompleted, p.TotalTables);
    statusLabel.Text = p.StatusMessage;
});

using var cts = new CancellationTokenSource();

SyncResult result = await syncEngine.SynchronizeAsync(progress, cancellationToken: cts.Token);
```

`SyncProgress.Phase` is a `SyncPhase` enum that walks
`Connecting → PushBegin → PushTable → PushWaiting → PushComplete → PullBegin →
PullTable → PullComplete → Complete` (plus `Cancelled`), so you can show
phase-specific messaging.

Cancellation is cooperative and stops the push/pull loop cleanly. One caveat: once
the client has handed a push session to the server, it sees the commit through
regardless — cancelling the wait doesn't cancel server-side processing, so any
not-yet-confirmed records simply re-push on the next sync cycle.

---

## Two ways to sync: engine vs. coordinator

SyncSession gives you two entry points. The **engine** is the core — it does the
actual work and hands you full control. The **coordinator** is a thin convenience
wrapper that adds connectivity-gating and automatic retry on top of an engine you
already built.

**Engine — full control (you own retry, connectivity, and error UX):**

```csharp
try
{
    SyncResult result = await engine.SynchronizeAsync(progress, cancellationToken: ct);
    // result.Success, result.RecordsPushed, result.RecordsPulled
}
catch (Exception ex)
{
    // your own connectivity check, backoff, and messaging
}
```

**Coordinator — batteries-included (skip-if-offline + auto-retry):**

```csharp
var coordinator = new SyncCoordinator(engine);

// Returns a failed SyncResult (no throw) if offline; retries transient failures.
SyncResult result = await coordinator.SyncAsync(progress, requireNetwork: true, cancellationToken: ct);

if (!result.Success)
    ShowMessage(result.ErrorMessage);   // e.g. "Network unavailable"
```

Both take the same `IProgress<SyncProgress>` and `CancellationToken` and return a
`SyncResult`. The coordinator simply wraps an engine instance.

> **Offline behavior differs by return type.** `SyncAsync` returns a failed
> `SyncResult` when offline (it has a result object to carry the failure). The
> targeted `PushAsync`/`PullAsync` return a record *count*, so they instead **throw
> `NetworkUnavailableException`** when offline. All three honor `requireNetwork` —
> pass `requireNetwork: false` to skip the pre-check and let the engine's own call
> surface any connectivity failure.

| Use the **engine** when… | Use the **coordinator** when… |
|---|---|
| You want your own retry/backoff policy | You want built-in retry on transient failures |
| You manage connectivity yourself | You want sync to no-op cleanly when offline |
| You need custom error handling or UX | A simple "Sync now" button is enough |
| You drive push and pull separately | You just want one full sync call |

**Rule of thumb:** reach for the engine when you want control, the coordinator when
you want convenience. If in doubt, start with the engine — the coordinator adds
nothing you can't do yourself, just saves you writing it.

## Dependency injection

There's no client-side `AddSyncSession` helper — register the engine yourself via a
factory delegate. Register the **concrete `ClientSyncEngine`** so the coordinator can
depend on it, then optionally expose `ISyncEngine` and/or the coordinator:

```csharp
// Engine (concrete type — the coordinator depends on it)
services.AddSingleton<ClientSyncEngine>(sp =>
{
    var db        = sp.GetRequiredService<IClientDatabase>();
    var serverApi = sp.GetRequiredService<ISyncServerApi>();
    return ClientSyncEngineBuilder.Build(db, serverApi, deviceId, config, typeof(Customer).Assembly);
});

// Optional: expose the same instance as ISyncEngine (the control path)
services.AddSingleton<ISyncEngine>(sp => sp.GetRequiredService<ClientSyncEngine>());

// Optional: the convenience coordinator (the batteries-included path)
services.AddSingleton<SyncCoordinator>(sp =>
    new SyncCoordinator(sp.GetRequiredService<ClientSyncEngine>()));
```

Then inject whichever you prefer — `ISyncEngine` for control, `SyncCoordinator` for
convenience.

> ⚠️ **Warning — rebuild the engine when the logged-in user changes.** The engine
> snapshots `TenantId` and `UserDisplayName` from the configuration at `Build()` time
> and never refreshes them. On any client where one user can log out and another can
> log in — **especially shared computers** — a long-lived singleton keeps the
> *previous* user's identity:
>
> - **Same tenant, different user:** sync sessions are recorded under the previous
>   user's name in the server audit trail (`SessionRecords.UserDisplayName`) — the
>   "who synced" history is wrong.
> - **Different tenant:** worse — sync scopes to the *previous* tenant's data.
>
> If logins can change at runtime, **do not register the engine or coordinator as a
> long-lived singleton.** Rebuild the engine on login — or per sync, as DVMApp does —
> so the current user's tenant and display name are used. `Build()` is cheap
> (assembly scan + handler creation, no I/O), so rebuilding per sync is fine.
>
> **Or pass identity per call.** If you'd rather hold one engine, hand each sync a
> `SyncContext` to override the user and assert the tenant without rebuilding:
>
> ```csharp
> await engine.SynchronizeAsync(progress, context: new SyncContext
> {
>     ExpectedTenantId = currentUser.TenantId,      // fail-closed guard
>     UserDisplayName  = currentUser.DisplayName     // audit override for this sync
> });
> ```
>
> `UserDisplayName` overrides the audit name for that sync. `ExpectedTenantId` is a
> **fail-closed guard**: if it doesn't match the tenant the engine was built with, the
> sync throws `TenantMismatchException` and does no I/O — it never silently syncs the
> wrong tenant. Note it *verifies* the tenant rather than re-scoping it, so a genuine
> tenant switch still needs a rebuild.
>
> Per-record audit via `ModifiedByUserId` (which your app stamps at write time) is
> independent of this and stays correct.

---

## Tenant binding (multi-tenant)

For multi-tenant apps, SyncSession binds each local database to exactly **one
tenant** and refuses to sync it under any other. This closes a gap the per-call
`ExpectedTenantId` guard cannot cover on its own: that guard compares the caller's
expected tenant to the tenant the engine was *built* with, so if both come from the
same (wrong) logged-in user they agree, and a pull could still write a second
tenant's rows into a database that already holds the first tenant's data.

The binding is an independent, persisted source of truth. It is written **at seed**
(`ClientDatabaseSeedWriter` stamps the seeded tenant when the seed commits), and the
engine asserts it on every multi-tenant push/pull/sync **before any I/O**:

- **Bound tenant matches** the engine's tenant: sync proceeds.
- **Bound tenant differs:** throws `TenantMismatchException`, no I/O. A different
  user logging in on the same device cannot sync their tenant into this database.
- **No binding yet:** governed by `ClientSyncConfiguration.TenantBindingPolicy`.

```csharp
var config = new ClientSyncConfiguration
{
    TenantId = currentUser.TenantId,
    TenantBindingPolicy = TenantBindingPolicy.Reject   // default
};
```

**`TenantBindingPolicy`:**

- **`Reject`** (default, fail-closed): an unbound database throws
  `TenantBindingMissingException`. A database should arrive already bound (seeded), so
  a missing binding is treated as suspect.
- **`Adopt`**: bind the configured tenant to the database on first sync. Use this
  when *migrating* databases that were populated before tenant binding existed: they
  have no marker yet, and Adopt fills it in on the next sync. Once every live database
  has been bound, switch back to `Reject`.

`Adopt` only ever fills a **missing** binding. A database already bound to a
*different* tenant is still rejected with `TenantMismatchException`, regardless of
policy. Single-tenant configurations (no `IMultiTenantSyncEntity` tables) skip binding
entirely.

> The binding lives in a small `LocalSyncMetadata` key/value table that
> `IClientDatabase.InitializeAsync()` provisions alongside `LocalSyncState`. A custom
> `IClientDatabase` must implement `GetClientMetadataAsync`/`SetClientMetadataAsync`
> and create that table (see Gotchas).

---

## Gotchas

Things that bite people, roughly in order of how often:

1. **Use `VARCHAR(36)` for GUID string keys, never `CHAR(36)`.** The MySql.Data
   connector auto-converts `CHAR(36)` to `System.Guid`, which breaks Dapper's
   string mapping for `Id` and `SyncSessionId`. Always declare these as
   `VARCHAR(36)`.

2. **`UseCors()` must come before `UseHttpsRedirection()`.** ASP.NET Core middleware
   is ordered, and getting this backwards produces cross-origin failures that look
   like auth or networking bugs. CORS goes first.

3. **Set `IsDirty = true` on every local insert/update.** This is the one
   infrastructure flag you do touch. A record that isn't flagged dirty is invisible
   to the push — it simply never syncs, with no error.

4. **Don't set `ModifiedAtUtc` or `SyncSessionId` yourself.** These are
   server-managed. Writing them locally is at best ignored and at worst confuses
   audit/version lookups.

5. **Always push before pull.** `SynchronizeAsync()` already does push-then-pull. If
   you drive the lower-level `PushAsync`/`PullAsync` directly, preserve that order —
   pulling first can strand un-pushed local edits.

6. **Set `[SyncTable(Priority = n)]` so parents sync before children.** Foreign-key
   parents need a lower priority number than the tables that reference them, or
   referential integrity fails mid-session.

7. **Conflict resolution is last-in-wins, push-first.** Because the client pushes
   before it pulls, your local edit reaches the server first and wins. A record
   still marked dirty when a pull arrives is **kept** (not overwritten) and pushed
   on the next cycle, so unsynced local work is never silently clobbered.

8. **`SessionRetentionDays` defaults to `0` (never purge).** Session rows accumulate
   indefinitely by default — fine for correctness, but on a high-churn server set a
   retention window or plan for the growth.

9. **Multi-tenant: configure a `TenantId`, and stamp a valid one on every record.**
   If you register any `IMultiTenantSyncEntity` table, the client engine now requires
   a non-null `ClientSyncConfiguration.TenantId` — building without one throws
   `InvalidOperationException` (a null tenant would otherwise select *all* tenants'
   dirty rows for push). A *wrong-but-present* tenant still won't error: the server
   simply scopes push/pull/seed to that tenant's claim, so you'd sync the wrong (or
   empty) data set. Set the current user's tenant, and stamp it on every record.

10. **`ModifiedByUserId` is yours to populate.** It defaults to `System`/`Local`, but
    the audit trail is only as useful as the values you stamp — set the real user on
    every write.

11. **Integration tests need Docker.** The integration suite spins up a real MariaDB
    via Testcontainers, so the Docker daemon must be running to build from source.

12. **Rebuild the engine when the logged-in user changes — shared computers
    especially.** The engine snapshots the user's `TenantId` and `UserDisplayName` at
    `Build()`. If one user logs out and another logs in on the same client, a cached
    singleton attributes sync sessions to the *previous* user (and scopes to their
    tenant). Rebuild on login, or pass a `SyncContext` per sync to override the
    display name and fail-closed-guard the tenant — see the warning under
    *Dependency injection* above.

13. **Multi-tenant databases are bound to one tenant.** A seeded client is stamped
    with its tenant; the engine then rejects any sync whose tenant differs
    (`TenantMismatchException`) and, by default, rejects an *unbound* database
    (`TenantBindingMissingException`). Migrating databases that were seeded before
    this existed? Set `TenantBindingPolicy.Adopt` to bind them on first sync, then
    switch back to `Reject`. See *Tenant binding* above.

14. **A custom `IClientDatabase` must implement the metadata store.** Tenant binding
    persists via `GetClientMetadataAsync`/`SetClientMetadataAsync`, backed by a
    `LocalSyncMetadata` table. The built-in `SqliteClientDatabase` provides both and
    creates the table in `InitializeAsync()`. Since `InitializeAsync()` is part of the
    `IClientDatabase` interface, a hand-rolled store (e.g. a WASM/IndexedDB one) must
    provide it too — create the `LocalSyncState`/`LocalSyncMetadata` tables there using
    `SqliteClientSchema.AllStatements` for the canonical DDL — or multi-tenant binding cannot persist.

---

## Seeding large datasets

Populating a brand-new client by pulling record-by-record over HTTP is fine for
modest data but impractical for very large tables (hundreds of thousands to
millions of rows) — the batched round trips dominate. For that initial-population
case, use the **seeding** path (`SyncSession.Client.Seeding`), which bulk-loads a
client far faster than an incremental pull, after which normal `SynchronizeAsync()`
keeps it current. For multi-tenant apps the seed also **binds the database to its tenant** on commit (see *Tenant binding* above). Reach for seeding on first launch against a large existing dataset;
stick with incremental sync for everything after.

---

## Next steps

- **README** — feature overview, configuration tables, and performance numbers.
- **Migration Guide** — adding SyncSession to an existing app with data already in
  place *(additive schema migration, dual-field periods, per-tenant rollout)*.
- **CONTRIBUTING.md** — building and testing from source.
