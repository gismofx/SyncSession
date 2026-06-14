using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SyncSession.Client.Database;
using SyncSession.Core.Constants;
using SyncSession.Core.Interfaces;
using Xunit;

namespace SyncSession.IntegrationTests.Transactions;

/// <summary>
/// SQLite-only (no MariaDB) integration tests for the client metadata key/value store and the
/// tenant-binding mapping over a REAL SqliteClientDatabase: round-trip, missing key, overwrite,
/// case-sensitive keys, Initialize idempotency, and the Guid binding round-trip.
/// </summary>
public class ClientMetadataBindingTests : IAsyncLifetime
{
    private SqliteClientDatabase? _clientDb;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source={Guid.NewGuid():N}.db";
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        _clientDb = new SqliteClientDatabase(connection);
        await _clientDb.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_clientDb != null)
        {
            _clientDb.Dispose();
            _clientDb = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100);
            var dbPath = _connectionString.Replace("Data Source=", "");
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch (IOException) { }
            }
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Metadata_SetThenGet_RoundTrips()
    {
        await _clientDb!.SetClientMetadataAsync("SomeKey", "SomeValue");
        (await _clientDb.GetClientMetadataAsync("SomeKey")).Should().Be("SomeValue");
    }

    [Fact]
    public async Task Metadata_MissingKey_ReturnsNull()
    {
        (await _clientDb!.GetClientMetadataAsync("DoesNotExist")).Should().BeNull();
    }

    [Fact]
    public async Task Metadata_Set_Overwrites()
    {
        await _clientDb!.SetClientMetadataAsync("K", "first");
        await _clientDb.SetClientMetadataAsync("K", "second");
        (await _clientDb.GetClientMetadataAsync("K")).Should().Be("second");
    }

    [Fact]
    public async Task Metadata_Key_IsCaseSensitive()
    {
        await _clientDb!.SetClientMetadataAsync("BoundTenantId", "A");

        // Different case is a DISTINCT key (default BINARY collation).
        (await _clientDb.GetClientMetadataAsync("boundtenantid")).Should().BeNull();

        await _clientDb.SetClientMetadataAsync("boundtenantid", "B");
        (await _clientDb.GetClientMetadataAsync("BoundTenantId")).Should().Be("A");
        (await _clientDb.GetClientMetadataAsync("boundtenantid")).Should().Be("B");
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndPreservesData()
    {
        await _clientDb!.SetClientMetadataAsync("K", "v");
        await _clientDb.InitializeAsync(); // CREATE TABLE IF NOT EXISTS — must not throw or wipe
        (await _clientDb.GetClientMetadataAsync("K")).Should().Be("v");
    }

    [Fact]
    public async Task BoundTenant_RoundTrips_ThroughRealSqlite()
    {
        var tenant = Guid.NewGuid();
        await _clientDb!.SetBoundTenantAsync(tenant);

        (await _clientDb.GetBoundTenantAsync()).Should().Be(tenant);
        // Stored under the well-known key as the GUID string.
        (await _clientDb.GetClientMetadataAsync(ClientMetadataKeys.BoundTenantId)).Should().Be(tenant.ToString());
    }

    [Fact]
    public async Task BoundTenant_Unbound_ReturnsNull()
    {
        (await _clientDb!.GetBoundTenantAsync()).Should().BeNull();
    }
}
