using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using MySqlX.XDevAPI.Relational;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Services;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server.Models;

namespace SyncSession.IntegrationTests.Fixtures;

/// <summary>
/// Factory for creating isolated test databases within the shared MariaDB container.
/// Provides helper methods for database operations during tests.
/// </summary>
public class TestDatabaseFactory
{
    private readonly MariaDbFixture _fixture;
    private string? _currentConnectionString;

    public TestDatabaseFactory(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Create a new isolated database for a test
    /// </summary>
    /// <param name="testName">Name of the test (will be sanitized)</param>
    /// <returns>Connection string to the new database</returns>
    public async Task<string> CreateDatabaseAsync(string testName)
    {
        _currentConnectionString = await _fixture.CreateTestDatabaseAsync(testName);
        return _currentConnectionString;
    }

    /// <summary>
    /// Get a new connection to the current test database
    /// </summary>
    public async Task<MySqlConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_currentConnectionString))
        {
            throw new InvalidOperationException(
                "No database created. Call CreateDatabaseAsync first.");
        }

        var connection = new MySqlConnection(_currentConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Get the current database connection string
    /// </summary>
    public string GetConnectionString()
    {
        if (string.IsNullOrEmpty(_currentConnectionString))
        {
            throw new InvalidOperationException(
                "No database created. Call CreateDatabaseAsync first.");
        }

        return _currentConnectionString;
    }

    /// <summary>
    /// Create a production-like SyncConfiguration with tables configured.
    /// This is the canonical configuration for all integration tests.
    /// </summary>
    public static ServerSyncConfiguration CreateDefaultSyncConfiguration()
    {
        var config = new ServerSyncConfiguration
        {
            PushSharedTableThreshold = 10000,
            PullSharedTableThreshold = 10000,
            SessionActivityTimeoutMinutes = 30,
        };

        config.RegisterTable<Customer>(priority: 1, enabled: true);
        config.RegisterTable<Order>(priority: 2, enabled: true);
        config.RegisterTable<OrderItem>(priority: 3, enabled: true);

        return config;
    }

    public static ITableMetadataCache CreateDefaultTableMetaDataCache(SyncConfiguration config)
    {
        return new TableMetadataCache(config);
    }

    /// <summary>
    /// Convert SyncConfiguration.Tables into TableSyncInfo[] for PushSessionBeginRequest.
    /// This mirrors how the client would derive the tables list from configuration.
    /// </summary>
    /// <param name="config">The sync configuration</param>
    /// <param name="estimatedCounts">Optional estimated record counts per table</param>
    /// <returns>Array of TableSyncInfo for the request</returns>
    public static TableSyncInfo[] GetTableSyncInfos(
        SyncConfiguration config,
        Dictionary<string, int>? estimatedCounts = null)
    {
        if (config.Tables == null || config.Tables.Count == 0)
        {
            throw new InvalidOperationException(
                "SyncConfiguration.Tables must be configured before creating TableSyncInfos");
        }

        return config.Tables
            .Where(kvp => kvp.Value.Enabled)
            .OrderBy(kvp => kvp.Value.Priority)
            .Select(kvp => new TableSyncInfo
            {
                TableName = kvp.Key,
                EstimatedRecordCount = estimatedCounts?.ContainsKey(kvp.Key) == true
                    ? estimatedCounts[kvp.Key]
                    : 0
            })
            .ToArray();
    }
}
