using System;
using System.Threading.Tasks;
using SyncSession.Core.Models;
using SyncSession.Samples.Console.Infrastructure;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Console.Scenarios;

/// <summary>
/// Simple scenario: Single client push and pull with progress reporting
/// </summary>
public static class SimpleScenario
{
    public static async Task RunAsync(ProgramOptions options)
    {
        OutputHelper.WriteInfo("Single client demonstrating basic push/pull sync");
        OutputHelper.WriteBlankLine();

        var clientId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var dbPath = $"client-simple-{clientId:N}.db";

        // Create sync configuration
        var config = new ClientSyncConfiguration
        {
            PushBatchSize = 1000,
            PullBatchSize = 1000
        };
        config.RegisterTable<Customer>("Customers", priority: 1);
        config.TenantId = tenantId; // Customer is multi-tenant — scope the engine to this tenant

        using var client = new ClientSimulator(clientId, tenantId, dbPath, options.ServerUrl, "SimpleUser", config,true);

        try
        {
            // Initialize
            OutputHelper.WriteInfo("Initializing client database...");
            await client.InitializeAsync();
            OutputHelper.WriteSuccess($"Client initialized (ID: {clientId})");
            OutputHelper.WriteBlankLine();

            // Seed data
            OutputHelper.WriteInfo($"Generating {options.RecordCount} sample customers...");
            await client.SeedDataAsync(options.RecordCount);
            var dirtyCount = await client.GetDirtyCountAsync();
            OutputHelper.WriteSuccess($"Created {dirtyCount} customers (need sync)");
            OutputHelper.WriteBlankLine();

            // Sync with progress
            OutputHelper.WriteInfo("Starting synchronization...");
            OutputHelper.WriteBlankLine();

            var progress = new Progress<SyncProgress>(p =>
            {
                var message = p.CurrentTable != null
                    ? $"{p.CurrentTable}: {p.RecordsProcessed}/{p.TotalRecords}"
                    : p.StatusMessage;

                OutputHelper.WriteProgress(p.OverallPercent, message);
            });

            var result = await client.SyncAsync(progress);

            System.Console.WriteLine(); // New line after progress
            OutputHelper.WriteBlankLine();

            // Display results
            if (result.Success)
            {
                OutputHelper.WriteSuccess($"Sync completed in {(result.CompletedAtUtc - result.StartedAtUtc)?.TotalSeconds:F2}s");
                OutputHelper.WriteSuccess($"Pushed: {result.RecordsPushed} records");
                OutputHelper.WriteSuccess($"Pulled: {result.RecordsPulled} records");
                
                var totalRecords = await client.GetRecordCountAsync();
                var stillDirty = await client.GetDirtyCountAsync();
                
                OutputHelper.WriteBlankLine();
                OutputHelper.WriteInfo($"Total records in database: {totalRecords}");
                OutputHelper.WriteInfo($"Records needing sync: {stillDirty}");
            }
            else
            {
                OutputHelper.WriteError($"Sync failed: {result.ErrorMessage}");
            }
        }
        finally
        {
            // Cleanup
            //if (!options.PersistDatabases)
            //{
            //    client.DeleteDatabase();
            //}
        }
    }
}
