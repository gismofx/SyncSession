using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using Testcontainers.MariaDb;
using Xunit;

namespace SyncSession.IntegrationTests.Fixtures;

/// <summary>
/// Shared MariaDB container fixture for all integration tests.
/// Creates a single container that is reused across all test classes.
/// Each test gets its own isolated database within this container.
/// </summary>
public class MariaDbFixture : IAsyncLifetime
{
    private MariaDbContainer? _container;
    private readonly ConcurrentDictionary<string, bool> _createdDatabases = new();

    /// <summary>
    /// Base connection string (without database specified, using root user)
    /// </summary>
    public string BaseConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Initialize the shared MariaDB container
    /// </summary>
    public async Task InitializeAsync()
    {
        Console.WriteLine("🚀 Starting shared MariaDB container...");

        _container = new MariaDbBuilder()
            .WithImage("mariadb:11.2")
            .WithDatabase("mysql")  // Connect to default mysql database
            .WithUsername("root")   // Use root for full privileges
            .WithPassword("root")   // Root password
            .WithCommand("--max_connections=500")  // Support many parallel tests
            .WithCommand("--max_allowed_packet=64M")  // Support large batches
            .WithCleanUp(true)
            .Build();

        await _container.StartAsync();

        // Wait for MariaDB to be fully ready
        await Task.Delay(3000);

        // Get base connection string (no database, root user)
        var builder = new MySqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "",  // Clear database name
            Pooling = false  // Each test gets its own connection
        };
        BaseConnectionString = builder.ConnectionString;

        Console.WriteLine("✅ MariaDB container started and ready");
        Console.WriteLine($"   Connection: {BaseConnectionString.Replace("root", "***")}");
    }

    /// <summary>
    /// Create a new isolated database for a test.
    /// Database name is based on test name + GUID for uniqueness.
    /// </summary>
    /// <param name="testName">Name of the test (e.g., test class or method name)</param>
    /// <returns>Connection string to the new database</returns>
    public async Task<string> CreateTestDatabaseAsync(string testName)
    {
        // Create unique database name
        var sanitizedName = testName
            .Replace(".", "_")
            .Replace(" ", "_")
            .Replace("-", "_");
        var dbName = $"SyncDb_{sanitizedName}_{Guid.NewGuid():N}";
        if (dbName.Length > 64) // MySQL limit
        {
            dbName = dbName.Substring(0, 64);
        }
        Console.WriteLine($"   📊 Creating test database: {dbName}");

        // Create database (root has CREATE DATABASE privilege)
        using var connection = new MySqlConnection(BaseConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE `{dbName}`");

        // Build connection string for this database
        var builder = new MySqlConnectionStringBuilder(BaseConnectionString)
        {
            Database = dbName
        };
        var testConnectionString = builder.ConnectionString;

        // Initialize schema
        await InitializeDatabaseSchemaAsync(testConnectionString);

        _createdDatabases.TryAdd(dbName, true);
        Console.WriteLine($"   ✅ Database ready: {dbName}");

        return testConnectionString;
    }

    /// <summary>
    /// Create the complete server database schema in a test database
    /// by executing the canonical SQL migration scripts from the output directory.
    /// </summary>
    private async Task InitializeDatabaseSchemaAsync(string connectionString)
    {
        var scriptsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "MySQL");

        using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var scriptName in new[] { "001_Infrastructure.sql", "002_ExampleBusinessTables.sql" })
        {
            var scriptPath = Path.Combine(scriptsDir, scriptName);
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Migration script not found: {scriptPath}");

            var sql = await File.ReadAllTextAsync(scriptPath);
            await connection.ExecuteAsync(sql);
        }
    }

    /// <summary>
    /// Get a list of all databases created during testing (for debugging)
    /// </summary>
    public string[] GetCreatedDatabases()
    {
        return _createdDatabases.Keys.ToArray();
    }

    /// <summary>
    /// Dispose of the shared container
    /// </summary>
    public async Task DisposeAsync()
    {
        Console.WriteLine($"🧹 Cleaning up MariaDB container ({_createdDatabases.Count} databases created)...");
        
        if (_container != null)
        {
            await _container.DisposeAsync();
        }

        Console.WriteLine("✅ MariaDB container stopped");
    }
}
