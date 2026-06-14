using System;
using System.Threading.Tasks;
using SyncSession.Core.Models;
using SyncSession.Samples.Console.Infrastructure;
using SyncSession.Samples.Shared.Entities;

namespace SyncSession.Samples.Console.Scenarios;

/// <summary>
/// Multi-client scenario: Proves session-based tracking prevents lost records
/// </summary>
public static class MultiClientScenario
{
    public static async Task RunAsync(ProgramOptions options)
    {
        OutputHelper.WriteInfo("⭐ PROVING SESSION-BASED TRACKING PREVENTS LOST RECORDS ⭐");
        OutputHelper.WriteBlankLine();
        OutputHelper.WriteWarning("This scenario demonstrates the core innovation:");
        System.Console.WriteLine("  Even when Client B syncs while Client A pushes new data,");
        System.Console.WriteLine("  Client B will receive those records on the next sync.");
        OutputHelper.WriteBlankLine();

        // STEP 0: Clean up server before test
        await CleanupServerAsync(options.ServerUrl);
        OutputHelper.WriteBlankLine();

        var clientAId = Guid.NewGuid();
        var clientBId = Guid.NewGuid();
        var dbPathA = $"client-multi-A-{clientAId:N}.db";
        var dbPathB = $"client-multi-B-{clientBId:N}.db";

        // Create sync configuration
        var configA = new ClientSyncConfiguration
        {
            PushBatchSize = 1000,
            PullBatchSize = 1000
        };

        var configB = new ClientSyncConfiguration
        {
            PushBatchSize = 1000,
            PullBatchSize = 1000
        };

        //config.RegisterTable<Customer>("Customers", priority: 1);
        //AutoConfig will find it


        //using var clientA = new ClientSimulator(clientAId, dbPathA, options.ServerUrl, "UserA", configA);
        //using var clientB = new ClientSimulator(clientBId, dbPathB, options.ServerUrl, "UserB", configB);
        var tenantId = Guid.NewGuid();
        configA.TenantId = tenantId; // Customer is multi-tenant — scope both engines to this tenant
        configB.TenantId = tenantId;
        using var clientA = new ClientSimulator(
            clientAId,tenantId, dbPathA, options.ServerUrl, "UserA", configA,
            deleteOnDispose: !options.PersistDatabases);

        using var clientB = new ClientSimulator(
            clientBId, tenantId, dbPathB, options.ServerUrl, "UserB", configB,
            deleteOnDispose: !options.PersistDatabases);



        try
        {
            // Step 1: Initialize both clients
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 1: Initialize both clients");
            OutputHelper.WriteDivider();
            
            await clientA.InitializeAsync();
            OutputHelper.WriteSuccess($"Client A initialized (ID: {clientAId:N})");

            await clientA.ExecuteSql("SELECT Id, ModifiedAtUtc FROM Customers LIMIT 1");


            await clientB.InitializeAsync();
            OutputHelper.WriteSuccess($"Client B initialized (ID: {clientBId:N})");
            OutputHelper.WriteBlankLine();

            // Step 2: Client A seeds and pushes 5 customers
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 2: Client A creates 5 customers and pushes to server");
            OutputHelper.WriteDivider();
            
            await clientA.SeedDataAsync(5);
            OutputHelper.WriteSuccess("Client A: Created 5 customers locally");
            await clientA.ExecuteSql("SELECT Id, ModifiedAtUtc FROM Customers LIMIT 1");


            var resultA1 = await clientA.SyncAsync();
            OutputHelper.WriteSuccess($"Client A: Pushed {resultA1.RecordsPushed} customers to server");
            OutputHelper.WriteBlankLine();

            // Step 3: Client B syncs and gets the 5 records
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 3: Client B syncs and pulls Client A's 5 customers");
            OutputHelper.WriteDivider();
            
            var resultB1 = await clientB.SyncAsync();
            OutputHelper.WriteSuccess($"Client B: Pulled {resultB1.RecordsPulled} customers from server");
            
            var countB1 = await clientB.GetRecordCountAsync();
            OutputHelper.WriteSuccess($"Client B: Now has {countB1} total customers");
            OutputHelper.WriteBlankLine();

            // Step 4: Client A creates 5 MORE (but doesn't push yet)
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 4: Client A creates 5 MORE customers (NOT pushed yet)");
            OutputHelper.WriteDivider();
            
            await clientA.SeedDataAsync(5);
            var dirtyA = await clientA.GetDirtyCountAsync();
            OutputHelper.WriteSuccess($"Client A: Has {dirtyA} dirty records waiting to sync");
            OutputHelper.WriteBlankLine();

            // Step 5: Client B syncs again (should get nothing - A hasn't pushed)
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 5: Client B syncs again (Client A hasn't pushed new records yet)");
            OutputHelper.WriteDivider();
            
            var resultB2 = await clientB.SyncAsync();
            OutputHelper.WriteInfo($"Client B: Pulled {resultB2.RecordsPulled} new customers (expected: 0)");
            
            var countB2 = await clientB.GetRecordCountAsync();
            OutputHelper.WriteInfo($"Client B: Still has {countB2} total customers");
            OutputHelper.WriteBlankLine();

            // Step 6: NOW Client A pushes the new 5 records
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 6: NOW Client A pushes the 5 new customers");
            OutputHelper.WriteDivider();
            
            var resultA2 = await clientA.SyncAsync();
            OutputHelper.WriteSuccess($"Client A: Pushed {resultA2.RecordsPushed} new customers to server");
            
            var countA = await clientA.GetRecordCountAsync();
            OutputHelper.WriteSuccess($"Client A: Has {countA} total customers");
            OutputHelper.WriteBlankLine();

            // Step 7: THE CRITICAL TEST - Client B syncs and MUST get the 5 records
            OutputHelper.WriteDivider();
            OutputHelper.WriteInfo("STEP 7: ⭐ THE CRITICAL TEST ⭐");
            OutputHelper.WriteInfo("Client B syncs and MUST receive Client A's 5 new customers");
            OutputHelper.WriteDivider();


            // Step 7: Poll-sync until Client B gets the records
            var expectedCount = 10;
            var attempts = 1;
            var maxAttempts = 10; // 10 attempts = max 5 seconds
            int currentCount = 0;
            SyncResult resultB3 = new();
            do
            {
                OutputHelper.WriteInfo($"Attempt {attempts}/{maxAttempts}");
                resultB3 = await clientB.SyncAsync();
                currentCount = await clientB.GetRecordCountAsync();
                 
                if (currentCount == expectedCount)
                    break; // Got them!

                if (++attempts >= maxAttempts)
                    throw new TimeoutException($"Client B didn't receive records after {maxAttempts} sync attempts");

                await Task.Delay(1000); // Wait between sync attempts

            } while (true);


            //var beforeCount = await clientB.GetRecordCountAsync();
            //var attempts = 0;
            //SyncResult resultB3 = new();
            //while (await clientB.GetRecordCountAsync() == beforeCount && attempts++ < 20)
            //{
            //    //await Task.Delay(500);
            //    resultB3 = await clientB.SyncAsync();
            //}

            var countB3 = currentCount;
            
            OutputHelper.WriteBlankLine();
            
            if (resultB3.RecordsPulled == 5 && countB3 == 10)
            {
                OutputHelper.WriteSuccess("✓✓✓ SUCCESS! Session-based tracking works! ✓✓✓");
                OutputHelper.WriteSuccess($"Client B pulled {resultB3.RecordsPulled} records");
                OutputHelper.WriteSuccess($"Client B now has {countB3} total customers (expected: 10)");
                OutputHelper.WriteBlankLine();
                OutputHelper.WriteSuccess("🎉 NO RECORDS LOST - Session tracking prevented the 'lost records' problem!");
            }
            else
            {
                OutputHelper.WriteError("✗✗✗ FAILED! Records were lost! ✗✗✗");
                OutputHelper.WriteError($"Client B pulled {resultB3.RecordsPulled} records (expected: 5)");
                OutputHelper.WriteError($"Client B has {countB3} total customers (expected: 10)");
            }
            
            OutputHelper.WriteBlankLine();

            // Summary
            OutputHelper.WriteDivider('=');
            OutputHelper.WriteHeader("SUMMARY");
            OutputHelper.WriteDivider('=');
            
            System.Console.WriteLine("Traditional version-based sync would have LOST the 5 records because:");
            System.Console.WriteLine("  1. Client B synced to version V (step 5)");
            System.Console.WriteLine("  2. Client A pushed, incrementing version to V+1 (step 6)");
            System.Console.WriteLine("  3. Client B thinks it's already at V+1, so pulls nothing (LOST!)");
            OutputHelper.WriteBlankLine();
            System.Console.WriteLine("Session-based tracking PREVENTS this by:");
            System.Console.WriteLine("  1. Client B tracks WHICH SESSIONS it has processed");
            System.Console.WriteLine("  2. Client A's push creates a NEW session (step 6)");
            System.Console.WriteLine("  3. Client B sees the unseen session and pulls it (step 7)");
            OutputHelper.WriteBlankLine();
            OutputHelper.WriteSuccess("This is the CORE INNOVATION of SyncSession! 🎉");
        }
        finally
        {
            // Cleanup
            //if (!options.PersistDatabases)
            //{
            //    clientA.Dispose();
            //    clientB.Dispose();


            //    clientA.DeleteDatabase();
            //    clientB.DeleteDatabase();
            //}
        }
    }

    private static async Task CleanupServerAsync(string serverUrl)
    {
        using var httpClient = new System.Net.Http.HttpClient();
        try
        {
            OutputHelper.WriteInfo("STEP 0: Cleaning up server test data...");
            var response = await httpClient.PostAsync($"{serverUrl}/api/v1/sync/test/cleanup", null);
            
            if (response.IsSuccessStatusCode)
            {
                OutputHelper.WriteSuccess("✓ Server test data cleaned up successfully");
            }
            else
            {
                OutputHelper.WriteWarning($"Server cleanup returned status: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            OutputHelper.WriteWarning($"Server cleanup failed (non-critical): {ex.Message}");
            OutputHelper.WriteWarning("Continuing with test - server may have stale data");
        }
    }
}
