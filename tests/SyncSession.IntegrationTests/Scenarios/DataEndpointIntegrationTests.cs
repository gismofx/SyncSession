using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.IntegrationTests.Infrastructure;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Server;
using SyncSession.Server.Database;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Scenarios;

/// <summary>
/// Integration tests for DataController endpoints (Session 28b).
/// Tests the MVC controller-based data API gated by DataEndpointsEnabledFilter.
/// </summary>
[Collection("MariaDB Collection")]
public class DataEndpointIntegrationTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;

    public DataEndpointIntegrationTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(DataEndpointIntegrationTests));
    }

    public async Task DisposeAsync() => await Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a factory + client with data endpoints enabled.
    /// Caller must dispose the returned factory.
    /// </summary>
    private (SyncWebApplicationFactory factory, HttpClient client) CreateDataClient()
    {
        var factory = new SyncWebApplicationFactory(_connectionString);
        var client = factory.CreateClient();

        // Flip the data endpoints flag — DataEndpointsEnabledFilter reads this at request time
        var options = factory.Services.GetRequiredService<SyncSessionOptions>();
        options.EnableDataEndpoints = true;

        return (factory, client);
    }

    /// <summary>
    /// Seeds a Customer directly into the database via DirectWriteService (from DI).
    /// Returns the Customer entity with Id populated.
    /// </summary>
    private async Task<Customer> SeedCustomerAsync(SyncWebApplicationFactory factory, string name, string email)
    {
        using var scope = factory.Services.CreateScope();
        var writeService = scope.ServiceProvider.GetRequiredService<IDirectWriteService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = name,
            Email = email,
            Phone = "555-0000",
            Address = "Test Address",
            ModifiedByUserId = "seed-user",
            IsDeleted = false
        };

        await writeService.WriteAsync(customer, "seed-user", customer.TenantId.ToString());
        return customer;
    }

    // ── Data Endpoints Disabled (Filter Gate) ─────────────────────────────────

    [Fact]
    public async Task GetById_WhenDataEndpointsDisabled_Returns404()
    {
        using var factory = new SyncWebApplicationFactory(_connectionString);
        using var client = factory.CreateClient();
        // Do NOT flip EnableDataEndpoints — filter should block

        var response = await client.GetAsync($"/api/v1/data/Customers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /api/v1/data/{table}/{id} ─────────────────────────────────────────

    [Fact]
    public async Task GetById_ExistingRecord_ReturnsRecord()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customer = await SeedCustomerAsync(factory, "Acme Corp", "acme@test.com");

        var response = await client.GetAsync($"/api/v1/data/Customers/{customer.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("Name").GetString().Should().Be("Acme Corp");
        body.GetProperty("Email").GetString().Should().Be("acme@test.com");
    }

    [Fact]
    public async Task GetById_NonExistentRecord_Returns404()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.GetAsync($"/api/v1/data/Customers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_UnknownTable_Returns404()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.GetAsync($"/api/v1/data/FakeTable/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("Unknown table");
    }

    // ── POST /api/v1/data/{table}/query ──────────────────────────────────────

    [Fact]
    public async Task Query_WithFilters_ReturnsFilteredResults()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        await SeedCustomerAsync(factory, "Acme Corp", "acme@test.com");
        await SeedCustomerAsync(factory, "Beta Corp", "beta@test.com");
        await SeedCustomerAsync(factory, "Gamma Inc", "gamma@test.com");

        var query = new DataQuery
        {
            Filters = new List<DataFilter>
            {
                new() { Column = "Name", Operator = FilterOperator.Contains, Value = "Corp" }
            },
            Limit = 50
        };

        var response = await client.PostAsJsonAsync("/api/v1/data/Customers/query", query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("Total").GetInt32().Should().Be(2);
        result.GetProperty("Records").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Query_WithPagination_RespectsOffsetAndLimit()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        // Seed 5 customers
        for (int i = 1; i <= 5; i++)
            await SeedCustomerAsync(factory, $"Customer {i:D2}", $"c{i}@test.com");

        var query = new DataQuery { Offset = 1, Limit = 2 };

        var response = await client.PostAsJsonAsync("/api/v1/data/Customers/query", query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("Records").GetArrayLength().Should().Be(2);
        result.GetProperty("Total").GetInt32().Should().Be(5);
        result.GetProperty("Offset").GetInt32().Should().Be(1);
        result.GetProperty("Limit").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Query_IncludeDeleted_ReturnsSoftDeletedRecords()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customer = await SeedCustomerAsync(factory, "ToDelete Corp", "del@test.com");

        // Soft delete via HTTP
        await client.DeleteAsync($"/api/v1/data/Customers/{customer.Id}");

        // Query without IncludeDeleted — should not find it
        var queryExclude = new DataQuery
        {
            Filters = new List<DataFilter>
            {
                new() { Column = "Name", Operator = FilterOperator.Equals, Value = "ToDelete Corp" }
            }
        };
        var responseExclude = await client.PostAsJsonAsync("/api/v1/data/Customers/query", queryExclude);
        var resultExclude = await responseExclude.Content.ReadFromJsonAsync<JsonElement>();
        resultExclude.GetProperty("Total").GetInt32().Should().Be(0);

        // Query with IncludeDeleted — should find it
        var queryInclude = new DataQuery
        {
            Filters = new List<DataFilter>
            {
                new() { Column = "Name", Operator = FilterOperator.Equals, Value = "ToDelete Corp" }
            },
            IncludeDeleted = true
        };
        var responseInclude = await client.PostAsJsonAsync("/api/v1/data/Customers/query", queryInclude);
        var resultInclude = await responseInclude.Content.ReadFromJsonAsync<JsonElement>();
        resultInclude.GetProperty("Total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Query_LimitExceedingCap_ClampsTo500()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var query = new DataQuery { Limit = 1000 };

        var response = await client.PostAsJsonAsync("/api/v1/data/Customers/query", query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("Limit").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task Query_InvalidColumn_Returns400()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var query = new DataQuery
        {
            Filters = new List<DataFilter>
            {
                new() { Column = "NonExistentColumn", Operator = FilterOperator.Equals, Value = "test" }
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/data/Customers/query", query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("NonExistentColumn");
    }

    [Fact]
    public async Task Query_UnknownTable_Returns404()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.PostAsJsonAsync("/api/v1/data/FakeTable/query", new DataQuery());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/v1/data/{table} — Single Upsert ───────────────────────────

    [Fact]
    public async Task WriteSingle_NewCustomer_CreatesAndReturnsViaGet()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customerId = Guid.NewGuid();
        var customerJson = new
        {
            Id = customerId,
            TenantId = Guid.NewGuid(),
            Name = "New Customer",
            Email = "new@test.com",
            Phone = "555-1111",
            Address = "New Address",
            ModifiedByUserId = "api-user",
            IsDeleted = false
        };

        var writeResponse = await client.PostAsJsonAsync("/api/v1/data/Customers", customerJson);
        writeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Round-trip GET
        var getResponse = await client.GetAsync($"/api/v1/data/Customers/{customerId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("Name").GetString().Should().Be("New Customer");
        body.GetProperty("Email").GetString().Should().Be("new@test.com");
    }

    [Fact]
    public async Task WriteSingle_ExistingCustomer_UpdatesRecord()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customer = await SeedCustomerAsync(factory, "Original Name", "orig@test.com");

        // Update via HTTP
        var updateJson = new
        {
            Id = customer.Id,
            TenantId = customer.TenantId,
            Name = "Updated Name",
            Email = "updated@test.com",
            Phone = "555-2222",
            Address = "Updated Address",
            ModifiedByUserId = "api-user",
            IsDeleted = false
        };

        var writeResponse = await client.PostAsJsonAsync("/api/v1/data/Customers", updateJson);
        writeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Round-trip GET
        var getResponse = await client.GetAsync($"/api/v1/data/Customers/{customer.Id}");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("Name").GetString().Should().Be("Updated Name");
        body.GetProperty("Email").GetString().Should().Be("updated@test.com");
    }

    [Fact]
    public async Task WriteSingle_UnknownTable_Returns404()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.PostAsJsonAsync("/api/v1/data/FakeTable", new { Id = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /api/v1/data/{table}/{id} — Soft Delete ───────────────────────

    [Fact]
    public async Task Delete_ExistingRecord_SetIsDeletedTrue()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customer = await SeedCustomerAsync(factory, "Delete Me", "delete@test.com");

        var deleteResponse = await client.DeleteAsync($"/api/v1/data/Customers/{customer.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify via direct DB — record exists with IsDeleted = true
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var row = await connection.QuerySingleAsync(
            "SELECT IsDeleted FROM Customers WHERE Id = @Id",
            new { Id = customer.Id.ToString() });
        ((bool)row.IsDeleted).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_NonExistentRecord_Returns404()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.DeleteAsync($"/api/v1/data/Customers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_UnknownTable_Returns404()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var response = await client.DeleteAsync($"/api/v1/data/FakeTable/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/v1/data — Batch Write ──────────────────────────────────────

    [Fact]
    public async Task WriteBatch_MultipleCustomers_AllCreatedWithSharedSession()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var batchBody = new
        {
            records = new
            {
                Customers = new[]
                {
                    new { Id = id1, TenantId = tenantId, Name = "Batch A", Email = "a@test.com", Phone = "555-0001", Address = "Addr A", ModifiedByUserId = "batch-user", IsDeleted = false },
                    new { Id = id2, TenantId = tenantId, Name = "Batch B", Email = "b@test.com", Phone = "555-0002", Address = "Addr B", ModifiedByUserId = "batch-user", IsDeleted = false }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/data", batchBody);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("SessionId").GetString().Should().NotBeNullOrEmpty();

        // Verify both records exist
        var get1 = await client.GetAsync($"/api/v1/data/Customers/{id1}");
        get1.StatusCode.Should().Be(HttpStatusCode.OK);
        var get2 = await client.GetAsync($"/api/v1/data/Customers/{id2}");
        get2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify both share the same SyncSessionId
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var sessions = await connection.QueryAsync<Guid>(
            "SELECT DISTINCT SyncSessionId FROM Customers WHERE Id IN (@Id1, @Id2)",
            new { Id1 = id1.ToString(), Id2 = id2.ToString() });
        sessions.Should().HaveCount(1, "both records should share the same SyncSessionId");
    }

    [Fact]
    public async Task WriteBatch_UnknownTableInBatch_Returns400()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var batchBody = new
        {
            records = new Dictionary<string, object>
            {
                ["FakeTable"] = new[] { new { Id = Guid.NewGuid() } }
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/data", batchBody);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WriteBatch_EmptyRecords_Returns400()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var batchBody = new { records = new { } };

        var response = await client.PostAsJsonAsync("/api/v1/data", batchBody);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Sync Session Visibility ──────────────────────────────────────────────

    [Fact]
    public async Task WriteSingle_CreatesCommittedSyncSession()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customerId = Guid.NewGuid();
        var customerJson = new
        {
            Id = customerId,
            TenantId = Guid.NewGuid(),
            Name = "Session Test",
            Email = "session@test.com",
            Phone = "555-3333",
            Address = "Session Addr",
            ModifiedByUserId = "session-user",
            IsDeleted = false
        };

        var writeResponse = await client.PostAsJsonAsync("/api/v1/data/Customers", customerJson);
        writeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify sync session was created and committed
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var syncSessionId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT SyncSessionId FROM Customers WHERE Id = @Id",
            new { Id = customerId.ToString() });
        syncSessionId.Should().NotBeEmpty();

        var sessionStatus = await connection.ExecuteScalarAsync<string>(
            "SELECT Status FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = syncSessionId.ToString() });
        sessionStatus.Should().Be("Committed",
            because: "direct writes auto-commit sessions immediately");

        var sessionType = await connection.ExecuteScalarAsync<string>(
            "SELECT SessionType FROM SessionRecords WHERE SessionId = @SessionId",
            new { SessionId = syncSessionId.ToString() });
        sessionType.Should().Be("DirectWrite",
            because: "direct write sessions are typed as 'DirectWrite'");
    }

    [Fact]
    public async Task Delete_CreatesNewSyncSession_DifferentFromOriginal()
    {
        var (factory, client) = CreateDataClient();
        using var _ = factory;
        using var __ = client;

        var customer = await SeedCustomerAsync(factory, "Delete Session", "delsess@test.com");

        // Get original SyncSessionId
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        var originalSessionId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT SyncSessionId FROM Customers WHERE Id = @Id",
            new { Id = customer.Id.ToString() });

        // Delete via HTTP
        await client.DeleteAsync($"/api/v1/data/Customers/{customer.Id}");

        // Verify new SyncSessionId (different from original)
        var newSessionId = await connection.ExecuteScalarAsync<Guid>(
            "SELECT SyncSessionId FROM Customers WHERE Id = @Id",
            new { Id = customer.Id.ToString() });
        newSessionId.Should().NotBe(originalSessionId,
            because: "soft delete creates a new sync session");
    }

    // ── Multi-Tenant HTTP Tests (28d) ─────────────────────────────────────────

    /// <summary>
    /// Creates a factory + client with data endpoints enabled and authentication enforced.
    /// The client is pre-configured with X-Test-TenantId so every request carries a TenantId claim.
    /// Caller must dispose the returned factory.
    /// </summary>
    private (SyncWebApplicationFactory factory, HttpClient client) CreateAuthenticatedClient(Guid tenantId)
    {
        var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<SyncSessionOptions>();
        options.EnableDataEndpoints = true;

        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantIdHeader, tenantId.ToString());

        return (factory, client);
    }

    /// <summary>
    /// Seeds a Customer with an explicit TenantId via DirectWriteService.
    /// </summary>
    private async Task<Customer> SeedCustomerAsync(
        SyncWebApplicationFactory factory, string name, string email, Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var writeService = scope.ServiceProvider.GetRequiredService<IDirectWriteService>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Email = email,
            Phone = "555-0000",
            Address = "Test Address",
            ModifiedByUserId = "seed-user",
            IsDeleted = false
        };

        await writeService.WriteAsync(customer, "seed-user", tenantId.ToString());
        return customer;
    }

    [Fact]
    public async Task Query_TenantIsolation_TenantBCannotSeeTenantARecords()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seed Tenant A's record using Tenant A's authenticated factory
        var (factoryA, clientA) = CreateAuthenticatedClient(tenantA);
        using var _fa = factoryA;
        using var _ca = clientA;

        var customer = await SeedCustomerAsync(factoryA, "Tenant A Customer", "a@test.com", tenantA);

        // Query as Tenant B — should get zero records
        var (factoryB, clientB) = CreateAuthenticatedClient(tenantB);
        using var _fb = factoryB;
        using var _cb = clientB;

        var optionsB = factoryB.Services.GetRequiredService<SyncSessionOptions>();
        optionsB.EnableDataEndpoints = true;

        var query = new DataQuery
        {
            Filters = new List<DataFilter>
            {
                new() { Column = "Id", Operator = FilterOperator.Equals, Value = customer.Id.ToString() }
            }
        };

        var response = await clientB.PostAsJsonAsync("/api/v1/data/Customers/query", query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("Total").GetInt32().Should().Be(0,
            because: "Tenant B cannot see Tenant A's records");
    }

    [Fact]
    public async Task DataEndpoint_NoAuth_Returns401()
    {
        // Factory with RequireAuthorization=true but no X-Test-TenantId header on the client
        var factory = new SyncWebApplicationFactory(_connectionString, requireAuthorization: true);
        using var _ = factory;
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<SyncSessionOptions>();
        options.EnableDataEndpoints = true;

        var query = new DataQuery();
        var response = await client.PostAsJsonAsync("/api/v1/data/Customers/query", query);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
