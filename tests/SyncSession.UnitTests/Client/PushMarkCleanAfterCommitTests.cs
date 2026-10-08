using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using SyncSession.Client.Database;
using SyncSession.Client.Engine;
using SyncSession.Core.Attributes;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// A push may only mark local rows clean once the server has committed them, and only the rows it
/// actually sent that have not been saved again since. Anything else loses data silently: a row
/// marked clean is never selected for a push again, so if the server never committed it, the
/// server never gets it.
/// </summary>
/// <remarks>
/// These tests assert the local database itself (a real <see cref="SqliteClientDatabase"/> over
/// in-memory SQLite), not which methods were called — the property that matters is the
/// <c>IsDirty</c> flag on the rows, so that is what is read back.
/// </remarks>
public class PushMarkCleanAfterCommitTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteClientDatabase _clientDb;
    private readonly Mock<ISyncServerApi> _server = new();
    private readonly Guid _sessionId = Guid.NewGuid();

    // Ids the server was sent, in order, across every push in a test.
    private readonly List<List<Guid>> _batchesSent = new();

    public PushMarkCleanAfterCommitTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _clientDb = new SqliteClientDatabase(_connection);

        _connection.Execute(@"
            CREATE TABLE PushMarkCleanItems (
                Id TEXT PRIMARY KEY,
                Name TEXT NULL,
                IsDirty INTEGER NOT NULL DEFAULT 0,
                ModifiedAtUtc TEXT NULL,
                SyncSessionId TEXT NULL,
                ModifiedByUserId TEXT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0)");

        _server.Setup(s => s.BeginPushAsync(It.IsAny<List<TableSyncInfo>>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
               .ReturnsAsync(_sessionId);
        _server.Setup(s => s.PushBatchAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<PushMarkCleanItem>>()))
               .Callback<Guid, IEnumerable<PushMarkCleanItem>>((_, batch) => _batchesSent.Add(batch.Select(r => r.Id).ToList()))
               .Returns(Task.CompletedTask);
        _server.Setup(s => s.CompleteTableAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>()))
               .Returns(Task.CompletedTask);
        _server.Setup(s => s.CompletePushAsync(It.IsAny<Guid>()))
               .Returns(Task.CompletedTask);
    }

    public void Dispose() => _clientDb.Dispose();

    // -------------------------------------------------------------------------
    // The server did not commit: nothing may be marked clean
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Push_ServerReportsFailed_RowsStayDirty()
    {
        var ids = await InsertDirtyRowsAsync(3);
        ServerStatusIs(Failed("Deadlock found when trying to get lock; try restarting transaction"));

        var act = () => BuildEngine().PushAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Push session failed on server*");
        (await DirtyIdsAsync()).Should().BeEquivalentTo(ids,
            "the server rolled the session back, so every row in it is still unsent");
    }

    [Fact]
    public async Task Push_CommitNeverConfirmed_RowsStayDirty()
    {
        var ids = await InsertDirtyRowsAsync(3);
        ServerStatusIs(Processing());

        var act = () => BuildEngine(timeoutSeconds: 1).PushAsync();

        await act.Should().ThrowAsync<TimeoutException>();
        (await DirtyIdsAsync()).Should().BeEquivalentTo(ids,
            "without a commit confirmation the rows must be offered again on the next sync");
    }

    [Fact]
    public async Task Push_ServerReportsFailed_NextPushSendsTheSameRowsAgain()
    {
        var ids = await InsertDirtyRowsAsync(3);
        var engine = BuildEngine();

        ServerStatusIs(Failed("Deadlock found when trying to get lock; try restarting transaction"));
        await engine.Invoking(e => e.PushAsync()).Should().ThrowAsync<InvalidOperationException>();

        ServerStatusIs(Committed());
        var pushed = await engine.PushAsync();

        pushed.Should().Be(3, "the failed push's rows are still unsent");
        _batchesSent.Last().Should().BeEquivalentTo(ids);
        (await DirtyIdsAsync()).Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // The server committed: clean exactly what was sent and not changed since
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Push_Committed_SentRowsAreMarkedClean()
    {
        await InsertDirtyRowsAsync(3);
        ServerStatusIs(Committed());

        var pushed = await BuildEngine().PushAsync();

        pushed.Should().Be(3);
        (await DirtyIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Push_RowSavedLocallyWhileUploading_StaysDirtyAfterCommit()
    {
        var ids = await InsertDirtyRowsAsync(3);
        var editedDuringPush = ids[0];
        var addedDuringPush = Guid.NewGuid();

        // The user saves while the batch is on the wire: one sent row is edited again and a brand
        // new row appears. Neither change is in what the server received.
        _server.Setup(s => s.PushBatchAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<PushMarkCleanItem>>()))
               .Callback<Guid, IEnumerable<PushMarkCleanItem>>((_, batch) =>
               {
                   _batchesSent.Add(batch.Select(r => r.Id).ToList());
                   _connection.Execute(
                       "UPDATE PushMarkCleanItems SET Name = 'edited', IsDirty = 1, ModifiedAtUtc = @At WHERE Id = @Id",
                       new { Id = editedDuringPush.ToString(), At = DateTime.UtcNow.AddSeconds(5) });
                   InsertRow(addedDuringPush, DateTime.UtcNow.AddSeconds(5));
               })
               .Returns(Task.CompletedTask);
        ServerStatusIs(Committed());

        await BuildEngine().PushAsync();

        (await DirtyIdsAsync()).Should().BeEquivalentTo(new[] { editedDuringPush, addedDuringPush },
            "a change saved after the batch was read has not been sent, so it must stay dirty");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private ISyncEngine BuildEngine(int timeoutSeconds = 5)
    {
        var config = new ClientSyncConfiguration
        {
            PushStatusPollIntervalMs = 100,
            PushStatusTimeoutSeconds = timeoutSeconds
        };
        config.RegisterTable<PushMarkCleanItem>();
        return ClientSyncEngineBuilder.Build(_clientDb, _server.Object, Guid.NewGuid(), config);
    }

    private async Task<List<Guid>> InsertDirtyRowsAsync(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        var at = DateTime.UtcNow;
        foreach (var id in ids)
            InsertRow(id, at);
        return await Task.FromResult(ids);
    }

    private void InsertRow(Guid id, DateTime modifiedAtUtc) =>
        _connection.Execute(
            "INSERT INTO PushMarkCleanItems (Id, Name, IsDirty, ModifiedAtUtc, ModifiedByUserId, IsDeleted) " +
            "VALUES (@Id, 'row', 1, @At, 'user', 0)",
            new { Id = id.ToString(), At = modifiedAtUtc });

    private async Task<List<Guid>> DirtyIdsAsync() =>
        (await _connection.QueryAsync<string>("SELECT Id FROM PushMarkCleanItems WHERE IsDirty = 1"))
            .Select(Guid.Parse).ToList();

    private void ServerStatusIs(PushSessionStatusResponse status) =>
        _server.Setup(s => s.GetPushStatusAsync(It.IsAny<Guid>())).ReturnsAsync(status);

    private PushSessionStatusResponse Committed() => new()
    {
        SessionId = _sessionId, Status = "Committed", SyncVersion = 1,
        CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow, CommittedAtUtc = DateTime.UtcNow
    };

    private PushSessionStatusResponse Processing() => new()
    {
        SessionId = _sessionId, Status = "Processing",
        CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow
    };

    private PushSessionStatusResponse Failed(string error) => new()
    {
        SessionId = _sessionId, Status = "Failed", ErrorMessage = error,
        CreatedAtUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow
    };
}

[SyncTable("PushMarkCleanItems", Priority = 98)]
public class PushMarkCleanItem : ISyncEntity
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public bool IsDirty { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "user";
    public bool IsDeleted { get; set; }
}
