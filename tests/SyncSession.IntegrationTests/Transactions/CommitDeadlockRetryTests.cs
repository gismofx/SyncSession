using System;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.IntegrationTests.Fixtures;
using SyncSession.Samples.Shared.Entities;
using SyncSession.Samples.Shared.TestData;
using SyncSession.Server.Database;
using SyncSession.Server.Models;
using SyncSession.Server.Services;
using Xunit;

namespace SyncSession.IntegrationTests.Transactions;

/// <summary>
/// A push session whose commit transaction loses a deadlock must be retried, not failed. MySQL and
/// MariaDB roll the whole transaction back on a deadlock and say to restart it; failing the session
/// instead throws away a push that would have succeeded a moment later.
/// </summary>
/// <remarks>
/// The deadlock used here is a real one, provoked on the test MariaDB between two connections, so
/// the exception the processor sees has the server's own error code and SQLSTATE — not a hand-built
/// imitation. It is raised inside the processor's real commit transaction after the upserts have
/// run, so the retry also proves the rollback left nothing behind.
/// </remarks>
[Collection("MariaDB Collection")]
public class CommitDeadlockRetryTests : IAsyncLifetime
{
    private readonly MariaDbFixture _fixture;
    private string _connectionString = string.Empty;
    private MySqlServerDatabase? _serverDb;
    private TempTableManager? _tempTableManager;
    private SessionTracker? _sessionTracker;

    public CommitDeadlockRetryTests(MariaDbFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _connectionString = await _fixture.CreateTestDatabaseAsync(nameof(CommitDeadlockRetryTests));

        var config = new ServerSyncConfiguration
        {
            PushSharedTableThreshold = 10000,
            PullSharedTableThreshold = 10000,
            TransactionIsolationLevel = IsolationLevel.Serializable
        };
        config.DiscoverAndRegisterTables(typeof(Customer).Assembly);

        var cache = TestDatabaseFactory.CreateDefaultTableMetaDataCache(config);
        _serverDb = new MySqlServerDatabase(_connectionString, cache, config, NullLogger<MySqlServerDatabase>.Instance);
        _tempTableManager = new TempTableManager(_serverDb, config, NullLogger<TempTableManager>.Instance);
        _sessionTracker = new SessionTracker(_serverDb, _tempTableManager, NullLogger<SessionTracker>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProcessSession_CommitLosesADeadlockOnce_IsRetriedAndCommits()
    {
        var deadlock = await ProvokeRealDeadlockAsync();
        deadlock.ErrorCode.Should().Be(MySqlErrorCode.LockDeadlock);
        deadlock.SqlState.Should().Be("40001");

        var customers = TestDataGenerator.CreateCustomersDict(3);
        var sessionId = await StageReadyCustomerSessionAsync(customers);

        var database = DeadlockOnceDatabase.Wrap(_serverDb!, deadlock);
        var processor = new SyncQueueProcessor(database, _tempTableManager!, NullLogger<SyncQueueProcessor>.Instance);

        await processor.ProcessSessionAsync(sessionId);

        DeadlockOnceDatabase.AttemptsOf(database).Should().Be(2, "the first commit attempt deadlocked");
        (await _sessionTracker!.GetSessionAsync(sessionId))!.Status.Should().Be("Committed");
        // The deadlock setup seeded two customers of its own, so count the three this session staged.
        var stagedIds = customers.Select(c => c["Id"]!.ToString()!).ToArray();
        (await CountCustomersAsync(stagedIds)).Should().Be(3, "the retried commit must land every staged row");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Two transactions each lock one row, then each asks for the other's row. InnoDB detects the
    /// cycle and kills one of them with error 1213; whichever it picks, that exception is returned.
    /// </summary>
    private async Task<MySqlException> ProvokeRealDeadlockAsync()
    {
        var seeded = TestDataGenerator.CreateCustomersDict(2);
        var seedSession = await StageReadyCustomerSessionAsync(seeded);
        await new SyncQueueProcessor(_serverDb!, _tempTableManager!, NullLogger<SyncQueueProcessor>.Instance)
            .ProcessSessionAsync(seedSession);
        var a = seeded[0]["Id"]!.ToString();
        var b = seeded[1]["Id"]!.ToString();

        await using var c1 = new MySqlConnection(_connectionString);
        await using var c2 = new MySqlConnection(_connectionString);
        await c1.OpenAsync();
        await c2.OpenAsync();
        await using var t1 = await c1.BeginTransactionAsync();
        await using var t2 = await c2.BeginTransactionAsync();

        await c1.ExecuteAsync("UPDATE Customers SET Name = 'c1' WHERE Id = @Id", new { Id = a }, t1);
        await c2.ExecuteAsync("UPDATE Customers SET Name = 'c2' WHERE Id = @Id", new { Id = b }, t2);

        var first = c1.ExecuteAsync("UPDATE Customers SET Name = 'c1' WHERE Id = @Id", new { Id = b }, t1);
        await Task.Delay(500); // let c1 block on b before c2 closes the cycle
        var second = c2.ExecuteAsync("UPDATE Customers SET Name = 'c2' WHERE Id = @Id", new { Id = a }, t2);

        MySqlException? caught = null;
        foreach (var task in new[] { first, second })
        {
            try { await task; }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock) { caught = ex; }
        }

        caught.Should().NotBeNull("two transactions waiting on each other's rows must deadlock");
        return caught!;
    }

    private async Task<Guid> StageReadyCustomerSessionAsync(System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> customers)
    {
        var response = await _sessionTracker!.CreatePushSessionAsync(new PushSessionBeginRequest
        {
            DeviceId = Guid.NewGuid(),
            Tables = new System.Collections.Generic.List<TableSyncInfo>
            {
                new() { TableName = "Customers", EstimatedRecordCount = customers.Count }
            }
        });
        await _tempTableManager!.InsertBatchAsync(response.SessionId, "Customers", customers);
        await _sessionTracker.CompleteTableAsync(response.SessionId, "Customers", customers.Count);
        await _sessionTracker.MarkSessionReadyAsync(response.SessionId);
        return response.SessionId;
    }

    private async Task<int> CountCustomersAsync(string[] ids)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Customers WHERE Id IN @Ids", new { Ids = ids });
    }
}

/// <summary>
/// Passes every call through to the real database, except that the first
/// <see cref="IServerDatabase.ExecuteInTransactionAsync"/> runs the caller's real work inside the
/// real transaction and then throws the supplied deadlock — so the transaction genuinely rolls back
/// after doing work, as it does when InnoDB picks it as the deadlock victim.
/// </summary>
public class DeadlockOnceDatabase : DispatchProxy
{
    private IServerDatabase _inner = null!;
    private Exception _deadlock = null!;
    private int _attempts;

    public static IServerDatabase Wrap(IServerDatabase inner, Exception deadlock)
    {
        var proxy = Create<IServerDatabase, DeadlockOnceDatabase>();
        var self = (DeadlockOnceDatabase)(object)proxy;
        self._inner = inner;
        self._deadlock = deadlock;
        return proxy;
    }

    public static int AttemptsOf(IServerDatabase proxy) => ((DeadlockOnceDatabase)(object)proxy)._attempts;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IServerDatabase.ExecuteInTransactionAsync))
        {
            var operations = (Func<IDbTransaction, Task>)args![0]!;
            if (++_attempts == 1)
            {
                return _inner.ExecuteInTransactionAsync(async tx =>
                {
                    await operations(tx);
                    throw _deadlock;
                });
            }
            return _inner.ExecuteInTransactionAsync(operations);
        }

        try { return method.Invoke(_inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
