# Integration Test Fixtures

## Overview

This directory contains the **optimized test fixture infrastructure** for SyncSystem integration tests. The key innovation is using a **single shared MariaDB container** across all tests, with each test getting its own **isolated database**.

## Performance Benefits

**Old Approach (DatabaseTestFixture.cs):**
- ❌ Each test class creates a new MariaDB container
- ❌ ~15-20 seconds startup time per container
- ❌ For 10 test classes = ~3+ minutes just for containers
- ❌ High memory and Docker overhead

**New Approach (MariaDbFixture.cs):**
- ✅ One shared container for ALL tests
- ✅ ~15 seconds startup ONE TIME
- ✅ Each test gets isolated database (~500ms creation)
- ✅ For 10 test classes = ~15 seconds container + ~5 seconds databases = **~20 seconds total**
- ✅ **90% faster** than old approach

## Architecture

### MariaDbFixture
- **Lifetime:** Shared across all test classes in the collection
- **Responsibility:** Create and manage the MariaDB container
- **Methods:**
  - `CreateTestDatabaseAsync(testName)` - Create isolated database
  - `GetCreatedDatabases()` - List all databases created

### MariaDbCollection
- **Purpose:** xUnit collection definition
- **Usage:** All test classes marked with `[Collection("MariaDB Collection")]` share the fixture

### TestDatabaseFactory
- **Purpose:** Helper for creating and accessing test databases
- **Methods:**
  - `CreateDatabaseAsync(testName)` - Create database and return connection string
  - `GetConnectionAsync()` - Get connection to current test database
  - `GetConnectionString()` - Get connection string for current database

### TestDataGenerator
- **Purpose:** Generate realistic test data
- **Methods:**
  - `CreateCustomer()` - Generate single customer
  - `CreateCustomers(count)` - Generate multiple customers
  - `CreateOrder()` - Generate order for customer
  - `CreateOrders()` - Generate multiple orders
  - `CreateOrderItem()` - Generate order item
  - `CreateOrderItems()` - Generate multiple items
  - `CreateCustomerWithOrders()` - Generate complete hierarchy
  - `CustomerToDictionary()` - Convert to API format
  - `OrderToDictionary()` - Convert to API format
  - `OrderItemToDictionary()` - Convert to API format

## Usage Pattern

### 1. Basic Test Structure

```csharp
[Collection("MariaDB Collection")]  // ← Share the container
public class MyIntegrationTests
{
    private readonly MariaDbFixture _fixture;
    private readonly TestDatabaseFactory _dbFactory;

    public MyIntegrationTests(MariaDbFixture fixture)
    {
        _fixture = fixture;
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task MyTest()
    {
        // Create isolated database for this test
        var connectionString = await _dbFactory.CreateDatabaseAsync(
            nameof(MyTest));  // Database name based on test name

        // Use connection string with your services
        var serverDb = new MySqlServerDatabase(connectionString, logger);
        
        // ... rest of test
    }
}
```

### 2. Using TestDataGenerator

```csharp
[Fact]
public async Task TestWithCustomers()
{
    var connectionString = await _dbFactory.CreateDatabaseAsync(nameof(TestWithCustomers));
    
    // Generate test data
    var customers = TestDataGenerator.CreateCustomers(10, "user-123", isDirty: true);
    
    // Convert to API format
    var batch = customers.ConvertAll(TestDataGenerator.CustomerToDictionary);
    
    // Use in test...
}
```

### 3. Complete Example

```csharp
[Fact]
public async Task PushSession_WithUserTracking_PreservesModifiedByUserId()
{
    // Arrange
    var connectionString = await _dbFactory.CreateDatabaseAsync(
        nameof(PushSession_WithUserTracking_PreservesModifiedByUserId));

    var serverDb = new MySqlServerDatabase(connectionString, logger);
    var config = new SyncConfiguration { PushSharedTableThreshold = 10000 };
    var tempTableManager = new TempTableManager(serverDb, config, logger);
    var sessionTracker = new SessionTracker(serverDb, tempTableManager, logger);

    // Generate test data
    var customer = TestDataGenerator.CreateCustomer(modifiedByUserId: "user-456");
    var batch = new List<Dictionary<string, object?>> 
    { 
        TestDataGenerator.CustomerToDictionary(customer) 
    };

    // Act
    var beginResponse = await sessionTracker.CreatePushSessionAsync(new PushSessionBeginRequest
    {
        ClientId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        Tables = new[] { new TableSyncInfo { TableName = "Customers", EstimatedRecordCount = 1 } }
    });

    await tempTableManager.InsertBatchAsync(beginResponse.SessionId, "Customers", batch);

    // Assert - Verify ModifiedByUserId preserved
    using var connection = await _dbFactory.GetConnectionAsync();
    var record = await connection.QuerySingleAsync<dynamic>(
        "SELECT ModifiedByUserId FROM TempPushCustomers WHERE SessionId = @SessionId",
        new { SessionId = beginResponse.SessionId.ToString() });

    ((string)record.ModifiedByUserId).Should().Be("user-456");
}
```

## Key Benefits

1. **Speed:** 90% faster than creating containers per test class
2. **Isolation:** Each test gets its own database - no interference
3. **Convenience:** TestDataGenerator provides realistic test data
4. **Flexibility:** Full control over test data and assertions
5. **Reliability:** Shared container reduces Docker flakiness

## Migration Guide

### Old Pattern (DatabaseTestFixture)
```csharp
public class MyTests : IAsyncLifetime
{
    private readonly DatabaseTestFixture _fixture;

    public MyTests()
    {
        _fixture = new DatabaseTestFixture();  // ❌ New container per class
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }
}
```

### New Pattern (MariaDbFixture)
```csharp
[Collection("MariaDB Collection")]  // ✅ Share container
public class MyTests
{
    private readonly TestDatabaseFactory _dbFactory;

    public MyTests(MariaDbFixture fixture)
    {
        _dbFactory = new TestDatabaseFactory(fixture);
    }

    [Fact]
    public async Task MyTest()
    {
        // ✅ Create isolated DB in ~500ms
        var connectionString = await _dbFactory.CreateDatabaseAsync(nameof(MyTest));
        // ... test code
    }
}
```

## Best Practices

1. **Always use `[Collection("MariaDB Collection")]`** - Ensures fixture sharing
2. **Name databases after test method** - Use `nameof(TestMethod)` for clarity
3. **Don't cleanup databases** - Container disposal handles it automatically
4. **Use TestDataGenerator** - Consistent, realistic test data
5. **Parallel-safe** - Each test has isolated database

## Troubleshooting

### "Docker not found"
- Install Docker Desktop
- Ensure Docker daemon is running

### "Container startup timeout"
- Check Docker has sufficient resources (CPU/RAM)
- Default timeout is adequate for most systems

### "Database creation slow"
- First database takes ~1-2 seconds (schema creation)
- Subsequent databases are ~500ms (schema already optimized)

## Performance Metrics

- **Container startup:** ~15 seconds (one-time)
- **Database creation:** ~500ms per test
- **Schema initialization:** Included in database creation
- **Total for 50 tests:** ~15s + (50 × 0.5s) = **~40 seconds**
- **Old approach (50 tests):** ~15s × 10 classes = **~150+ seconds**

**Result: 73% faster overall test suite execution**
