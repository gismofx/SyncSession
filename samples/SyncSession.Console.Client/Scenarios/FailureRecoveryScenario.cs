using System;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.Models;
using SyncSession.Samples.Console.Infrastructure;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Console.Scenarios;

/// <summary>
/// Failure recovery scenario: Demonstrates resilience to network failures
/// </summary>
public static class FailureRecoveryScenario
{
    public static async Task RunAsync(ProgramOptions options)
    {
        OutputHelper.WriteInfo("Demonstrating network failure resilience and recovery");
        OutputHelper.WriteBlankLine();

        var clientId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var dbPath = $"client-failure-{clientId:N}.db";

        // Create sync configuration
        var config = new ClientSyncConfiguration
        {
            PushBatchSize = 1000,
            PullBatchSize = 1000,
        };
        config.RegisterTable<Customer>("Customers", priority: 1);
        config.TenantId = tenantId; // Customer is multi-tenant — scope the engine to this tenant

        using var client = new ClientSimulator(clientId, tenantId, dbPath, options.ServerUrl, "FailureUser", config, true);

        try
        {
            // Initialize
            OutputHelper.WriteInfo("Initializing client database...");
            await client.InitializeAsync();
            OutputHelper.WriteSuccess($"Client initialized (ID: {clientId})");
            OutputHelper.WriteBlankLine();

            // Test 1: Failed Push + Recovery
            await TestFailedPushRecovery(client, options);
            
            OutputHelper.WriteBlankLine();
            OutputHelper.WriteDivider('=');
            OutputHelper.WriteBlankLine();

            // Test 2: Failed Pull + Recovery
            await TestFailedPullRecovery(client, options);
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

    private static async Task TestFailedPushRecovery(ClientSimulator client, ProgramOptions options)
    {
        OutputHelper.WriteDivider();
        OutputHelper.WriteInfo("TEST 1: Failed Push → Recovery");
        OutputHelper.WriteDivider();
        OutputHelper.WriteBlankLine();

        // Seed data
        OutputHelper.WriteInfo("Creating 20 customers locally...");
        await client.SeedDataAsync(20);
        var dirtyCount = await client.GetDirtyCountAsync();
        OutputHelper.WriteSuccess($"Created {dirtyCount} customers (need sync)");
        OutputHelper.WriteBlankLine();

        // Simulate failure mid-push
        OutputHelper.WriteWarning("Simulating network failure during push...");
        
        using var cts = new CancellationTokenSource();
        
        // Cancel after a short delay to simulate mid-push failure
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // Let push start
            cts.Cancel();
        });

        try
        {
            await client.SyncAsync(cancellationToken: cts.Token);
            OutputHelper.WriteError("Expected cancellation but sync completed!");
        }
        catch (OperationCanceledException)
        {
            OutputHelper.WriteSuccess("✓ Push interrupted (simulated network failure)");
        }
        catch (Exception ex)
        {
            OutputHelper.WriteWarning($"Push failed with: {ex.GetType().Name}");
        }

        // Check state after failure
        var stillDirty = await client.GetDirtyCountAsync();
        OutputHelper.WriteInfo($"Records still needing sync: {stillDirty}");
        OutputHelper.WriteBlankLine();

        // Retry - should succeed
        OutputHelper.WriteInfo("Retrying sync after failure...");
        
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

        if (result.Success)
        {
            OutputHelper.WriteSuccess($"✓ Recovery successful! Pushed {result.RecordsPushed} records");
            
            var finalDirty = await client.GetDirtyCountAsync();
            OutputHelper.WriteSuccess($"Records still dirty: {finalDirty} (expected: 0)");
            
            if (finalDirty == 0)
            {
                OutputHelper.WriteSuccess("🎉 All records synced successfully after recovery!");
            }
        }
        else
        {
            OutputHelper.WriteError($"Recovery failed: {result.ErrorMessage}");
        }
    }

    private static async Task TestFailedPullRecovery(ClientSimulator client, ProgramOptions options)
    {
        OutputHelper.WriteDivider();
        OutputHelper.WriteInfo("TEST 2: Failed Pull → Recovery");
        OutputHelper.WriteDivider();
        OutputHelper.WriteBlankLine();

        OutputHelper.WriteInfo("For this test, we need server-side data...");
        OutputHelper.WriteInfo("Creating temporary client to seed server data...");
        
        // Create another client to seed server data
        var tempClientId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var tempDbPath = $"client-temp-{tempClientId:N}.db";

        var config = new ClientSyncConfiguration
        {
            PushBatchSize = 1000,
            PullBatchSize = 1000
        };
        config.RegisterTable<Customer>("Customers", priority: 1);
        config.TenantId = tenantId; // Customer is multi-tenant — scope the engine to this tenant

        //using var tempClient = new ClientSimulator(tempClientId, tempDbPath, options.ServerUrl, "TempUser");//did not have a config
        using var tempClient = new ClientSimulator(tempClientId, tenantId, tempDbPath, options.ServerUrl, "TempUser", config);

        await tempClient.InitializeAsync();
        await tempClient.SeedDataAsync(30);
        
        OutputHelper.WriteInfo("Pushing 30 records from temporary client to server...");
        var tempResult = await tempClient.SyncAsync();
        OutputHelper.WriteSuccess($"Server now has {tempResult.RecordsPushed} new records");

        //tempClient.DeleteDatabase(); //Todo: Handle this better, implement better solution
        OutputHelper.WriteBlankLine();

        // Now simulate failed pull
        OutputHelper.WriteWarning("Simulating network failure during pull...");
        
        var recordsBefore = await client.GetRecordCountAsync();
        
        using var cts = new CancellationTokenSource();
        
        // Cancel after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            cts.Cancel();
        });

        try
        {
            await client.SyncAsync(cancellationToken: cts.Token);
            OutputHelper.WriteError("Expected cancellation but sync completed!");
        }
        catch (OperationCanceledException)
        {
            OutputHelper.WriteSuccess("✓ Pull interrupted (simulated network failure)");
        }
        catch (Exception ex)
        {
            OutputHelper.WriteWarning($"Pull failed with: {ex.GetType().Name}");
        }

        var recordsAfterFail = await client.GetRecordCountAsync();
        OutputHelper.WriteInfo($"Records before: {recordsBefore}, after failed pull: {recordsAfterFail}");
        OutputHelper.WriteBlankLine();

        // Retry - should get all records via UPSERT deduplication
        OutputHelper.WriteInfo("Retrying sync after failure...");
        
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

        if (result.Success)
        {
            var finalRecords = await client.GetRecordCountAsync();
            OutputHelper.WriteSuccess($"✓ Recovery successful! Pulled {result.RecordsPulled} records");
            OutputHelper.WriteSuccess($"Total records: {finalRecords}");
            
            if (finalRecords >= 30)
            {
                OutputHelper.WriteSuccess("🎉 All server records received successfully!");
                OutputHelper.WriteInfo("Note: UPSERT handled any duplicate records from partial pull");
            }
        }
        else
        {
            OutputHelper.WriteError($"Recovery failed: {result.ErrorMessage}");
        }
    }
}
