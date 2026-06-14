using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using SyncSession.Client.Database;
using SyncSession.Client.Engine;
using SyncSession.Client.Http;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Console.Infrastructure;

/// <summary>
/// Simulates a client application with local database and HTTP-based sync.
/// Wraps ClientSyncEngine for clean separation of concerns.
/// </summary>
public class ClientSimulator : IDisposable
{
    private readonly Guid _clientId;
    private readonly Guid _deviceId;
    private readonly Guid _tenantId;
    private readonly string _dbPath;
    private readonly string _userId;
    private readonly System.Net.Http.HttpClient _httpClient;
    private readonly HttpSyncServerApi _serverClient;
    private readonly ClientSyncEngine _syncEngine;
    private readonly DataSeeder _dataSeeder;
    private readonly SqliteClientDatabase _clientDb;
    private bool _disposed;

    public Guid ClientId => _clientId;
    public Guid DeviceId => _deviceId;
    public string DatabasePath => _dbPath;
    public string UserId => _userId;


    public ClientSimulator(
        Guid clientId,
        Guid tenantId,
        string dbPath,
        string serverUrl,
        string userId,
        ClientSyncConfiguration config,
        bool deleteOnDispose = true)
    {
        _clientId = clientId;
        _tenantId = tenantId; 
        _deviceId = clientId; // For single-device demo
        _dbPath = dbPath;
        _userId = userId;
        _deleteOnDispose = deleteOnDispose;

        // Initialize components
        _httpClient = new System.Net.Http.HttpClient();
        _serverClient = new HttpSyncServerApi(_httpClient, serverUrl, _deviceId);
        
        var sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _clientDb = new SqliteClientDatabase(sqliteConnection);
        
        // Use builder to auto-discover tables and create handlers
        _syncEngine = ClientSyncEngineBuilder.Build(
            _clientDb,
            _serverClient,
            _deviceId,
            config,
            typeof(Customer).Assembly);
        
        _dataSeeder = new DataSeeder();
    }

    /// <summary>
    /// Initialize the client database schema
    /// </summary>
    public async Task InitializeAsync()
    {
        //using var connection = new SqliteConnection($"Data Source={_dbPath}");
        var connection = await _clientDb.GetConnectionAsync();

        // Create the library's state + metadata tables (LocalSyncState, LocalSyncMetadata), then
        // bind this freshly-created database to its tenant. Real apps get the binding automatically
        // at seed (ClientDatabaseSeedWriter.CommitAsync); this generate-and-push sample, which has
        // no server-seed step, establishes it explicitly so the engine's tenant-binding guard
        // (TenantBindingPolicy.Reject by default) is satisfied.
        await _clientDb.InitializeAsync();
        await _clientDb.SetBoundTenantAsync(_tenantId);

        // Create Customers table
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Customers (
                Id TEXT PRIMARY KEY,
                TenantId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Email TEXT NOT NULL,
                Phone TEXT,
                Address TEXT,
                IsDirty INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                ModifiedAtUtc TEXT NOT NULL,
                SyncSessionId TEXT,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'DemoUser'
            )");

        // Create Products table (shared reference data, no TenantId)
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Products (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                SKU TEXT NOT NULL,
                Price REAL NOT NULL DEFAULT 0,
                IsDirty INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                ModifiedAtUtc TEXT NOT NULL,
                SyncSessionId TEXT,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'System'
            )");

        // Create Orders table
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Orders (
                Id TEXT PRIMARY KEY,
                TenantId TEXT NOT NULL,
                CustomerId TEXT NOT NULL,
                OrderNumber TEXT NOT NULL,
                TotalAmount REAL NOT NULL DEFAULT 0,
                OrderDate TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Pending',
                IsDirty INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                ModifiedAtUtc TEXT NOT NULL,
                SyncSessionId TEXT,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'System'
            )");

        // Create OrderItems table
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS OrderItems (
                Id TEXT PRIMARY KEY,
                OrderId TEXT NOT NULL,
                ProductId TEXT NOT NULL,
                ProductName TEXT NOT NULL,
                Quantity INTEGER NOT NULL DEFAULT 0,
                UnitPrice REAL NOT NULL DEFAULT 0,
                IsDirty INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                ModifiedAtUtc TEXT NOT NULL,
                SyncSessionId TEXT,
                ModifiedByUserId TEXT NOT NULL DEFAULT 'System'
            )");

        // Create index for dirty records
        await connection.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS IX_Customers_Dirty 
            ON Customers(IsDirty) WHERE IsDirty = 1");

        // Create index for non-deleted records (performance for business queries)
        await connection.ExecuteAsync(@"
        CREATE INDEX IF NOT EXISTS IX_Customers_NotDeleted 
        ON Customers(IsDeleted) WHERE IsDeleted = 0");

        // Enable WAL mode
        await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// Seed sample customer data
    /// </summary>
    public async Task SeedDataAsync(int count)
    {
        var customers = _dataSeeder.GenerateCustomers(count, _tenantId);
        var connection = await _clientDb.GetConnectionAsync();

        foreach (var customer in customers)
        {
            await connection.ExecuteAsync(@"
            INSERT INTO Customers (Id, TenantId, Name, Email, IsDirty, ModifiedAtUtc, ModifiedByUserId)
            VALUES (@Id, @TenantId, @Name, @Email, 1, @ModifiedAtUtc, @ModifiedByUserId)",
                new
                {
                    customer.Id,
                    customer.TenantId,
                    customer.Name,
                    customer.Email,
                    ModifiedAtUtc = DateTime.UtcNow.ToString("O"),
                    customer.ModifiedByUserId,
                });
        }
    }

    /// <summary>
    /// Perform synchronization (delegates to ClientSyncEngine)
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await _syncEngine.SynchronizeAsync(progress, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Get current record count
    /// </summary>
    public async Task<int> GetRecordCountAsync()
    {
        var connection = await _clientDb.GetConnectionAsync();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM Customers");
    }

    /// <summary>
    /// Get dirty record count
    /// </summary>
    public async Task<int> GetDirtyCountAsync()
    {
        var connection = await _clientDb.GetConnectionAsync();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM Customers WHERE IsDirty = 1");
    }

    private readonly bool _deleteOnDispose;

    public void Dispose()
    {
        if (_disposed) return;

        _syncEngine?.Dispose();
        _httpClient?.Dispose();

        // Dispose database connection
        if (_clientDb is IDisposable disposableDb)
        {
            disposableDb.Dispose();
        }

        SqliteConnection.ClearAllPools();

        // Delete database files if configured
        if (_deleteOnDispose)
        {
            Thread.Sleep(100); // Let OS release handles

            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
            if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
        }

        _disposed = true;
    }

    public async Task ExecuteSql(string sql)
    {
        var conn = await _clientDb.GetConnectionAsync();
        var raw = await conn.QueryAsync(sql);
    }


    /// <summary>
    /// Delete the client database file
    /// </summary>
    [Obsolete("use using pattern",true)]
    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();
        Thread.Sleep(100);

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        var walPath = _dbPath + "-wal";
        var shmPath = _dbPath + "-shm";

        if (File.Exists(walPath)) File.Delete(walPath);
        if (File.Exists(shmPath)) File.Delete(shmPath);
    }
}
