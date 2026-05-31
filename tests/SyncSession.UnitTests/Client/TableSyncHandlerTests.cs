using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SyncSession.Client.Handlers;
using SyncSession.Core.Attributes;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Tests for TableSyncHandler - verifies type-safe synchronization operations
/// </summary>
public class TableSyncHandlerTests
{
    [Fact]
    public async Task GetDirtyCountAsync_ReturnsDirtyRecordCount()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        var dirtyRecords = new List<TestHandlerEntity>
        {
            new() { Id = Guid.NewGuid(), IsDirty = true },
            new() { Id = Guid.NewGuid(), IsDirty = true }
        };
        
        mockDb.Setup(x => x.GetDirtyRecordsAsync<TestHandlerEntity>(It.IsAny<Guid?>()))
            .ReturnsAsync(dirtyRecords);

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration());

        // Act
        var count = await handler.GetDirtyCountAsync();

        // Assert
        count.Should().Be(2);
    }

    [Fact]
    public async Task PushAsync_NoDirtyRecords_ReturnsZero()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        mockDb.Setup(x => x.GetDirtyRecordsAsync<TestHandlerEntity>(It.IsAny<Guid?>()))
            .ReturnsAsync(new List<TestHandlerEntity>());

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration());

        // Act
        var count = await handler.PushAsync(Guid.NewGuid());

        // Assert
        count.Should().Be(0);
        mockClient.Verify(x => x.PushBatchAsync<TestHandlerEntity>(
            It.IsAny<Guid>(),
            It.IsAny<TestHandlerEntity[]>()), Times.Never);
    }

    [Fact]
    public async Task PushAsync_WithDirtyRecords_PushesBatchAndMarksClean()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        var dirtyRecords = new List<TestHandlerEntity>
        {
            new() { Id = Guid.NewGuid(), IsDirty = true },
            new() { Id = Guid.NewGuid(), IsDirty = true }
        };
        
        mockDb.Setup(x => x.GetDirtyRecordsAsync<TestHandlerEntity>(It.IsAny<Guid?>()))
            .ReturnsAsync(dirtyRecords);

        var config = CreateTestConfig();
        var sessionId = Guid.NewGuid();
        
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration { PushBatchSize = 1000 });

        // Act
        var count = await handler.PushAsync(sessionId);

        // Assert
        count.Should().Be(2);
        mockClient.Verify(x => x.PushBatchAsync<TestHandlerEntity>(
            sessionId,
            It.Is<TestHandlerEntity[]>(b => b.Length == 2)), Times.Once);
        mockDb.Verify(x => x.MarkRecordsCleanAsync<TestHandlerEntity>(It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task PushAsync_LargeDataset_BatchesCorrectly()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        var dirtyRecords = Enumerable.Range(1, 250)
            .Select(i => new TestHandlerEntity { Id = Guid.NewGuid(), IsDirty = true })
            .ToList();
        
        mockDb.Setup(x => x.GetDirtyRecordsAsync<TestHandlerEntity>(It.IsAny<Guid?>()))
            .ReturnsAsync(dirtyRecords);

        var config = CreateTestConfig();
        var sessionId = Guid.NewGuid();
        
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration { PushBatchSize = 100 });

        // Act
        var count = await handler.PushAsync(sessionId);

        // Assert
        count.Should().Be(250);
        mockClient.Verify(x => x.PushBatchAsync<TestHandlerEntity>(
            sessionId,
            It.Is<TestHandlerEntity[]>(b => b.Length == 100)), Times.Exactly(2));
        mockClient.Verify(x => x.PushBatchAsync<TestHandlerEntity>(
            sessionId,
            It.Is<TestHandlerEntity[]>(b => b.Length == 50)), Times.Once);
    }

    [Fact]
    public async Task PullAsync_NoRecords_ReturnsZeroAndEmptySessionIds()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        mockClient.Setup(x => x.PullBatchAsync<TestHandlerEntity>(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync((new List<TestHandlerEntity>(), false, 0));

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration());

        // Act
        var (count, sessionIds) = await handler.PullAsync(Guid.NewGuid());

        // Assert
        count.Should().Be(0);
        sessionIds.Should().BeEmpty();
        mockDb.Verify(x => x.UpsertBatchAsync<TestHandlerEntity>(
            It.IsAny<TestHandlerEntity[]>(), It.IsAny<Guid?>(), It.IsAny<System.Data.IDbTransaction>()), Times.Never);
    }

    [Fact]
    public async Task PullAsync_WithRecords_UpsertsAndReturnsSessionIds()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        var sessionId1 = Guid.NewGuid();
        var sessionId2 = Guid.NewGuid();
        
        var serverRecords = new List<TestHandlerEntity>
        {
            new() { Id = Guid.NewGuid(), SyncSessionId = sessionId1 },
            new() { Id = Guid.NewGuid(), SyncSessionId = sessionId2 },
            new() { Id = Guid.NewGuid(), SyncSessionId = sessionId1 }
        };
        
        mockClient.Setup(x => x.PullBatchAsync<TestHandlerEntity>(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync((serverRecords, false, 3));

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration { PullBatchSize = 1000 });

        // Act
        var (count, sessionIds) = await handler.PullAsync(Guid.NewGuid());

        // Assert
        count.Should().Be(3);
        sessionIds.Should().HaveCount(2);
        sessionIds.Should().Contain(sessionId1);
        sessionIds.Should().Contain(sessionId2);
        mockDb.Verify(x => x.UpsertBatchAsync<TestHandlerEntity>(
            It.Is<IEnumerable<TestHandlerEntity>>(b => b.Count() == 3),
            It.IsAny<Guid?>(), It.IsAny<System.Data.IDbTransaction>()), Times.Once);
    }

    [Fact]
    public async Task PushAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();
        
        var dirtyRecords = Enumerable.Range(1, 10)
            .Select(i => new TestHandlerEntity { Id = Guid.NewGuid(), IsDirty = true })
            .ToList();
        
        mockDb.Setup(x => x.GetDirtyRecordsAsync<TestHandlerEntity>(It.IsAny<Guid?>()))
            .ReturnsAsync(dirtyRecords);

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration { PushBatchSize = 1 });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await handler.PushAsync(Guid.NewGuid(), cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PushAsync_WithProgress_ReportsTupleWithCorrectTotal()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();

        var dirtyRecords = Enumerable.Range(1, 5)
            .Select(i => new TestHandlerEntity { Id = Guid.NewGuid(), IsDirty = true })
            .ToList();

        mockDb.Setup(x => x.GetDirtyRecordsAsync<TestHandlerEntity>(It.IsAny<Guid?>()))
            .ReturnsAsync(dirtyRecords);

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration { PushBatchSize = 2 });

        var reports = new List<(int Current, int Total)>();
        var progress = new SynchronousProgress<(int Current, int Total)>(p => reports.Add(p));

        // Act
        await handler.PushAsync(Guid.NewGuid(), progress);

        // Assert
        reports.Should().NotBeEmpty();
        reports.Should().AllSatisfy(r => r.Total.Should().Be(5),
            "total should always equal dirty count (5)");
        reports.Last().Current.Should().Be(5,
            "final report should show all records processed");
        // 3 batches: 2, 2, 1
        reports.Should().HaveCount(3);
        reports[0].Current.Should().Be(2);
        reports[1].Current.Should().Be(4);
        reports[2].Current.Should().Be(5);
    }

    [Fact]
    public async Task PullAsync_WithProgress_ReportsTupleWithServerTotal()
    {
        // Arrange
        var mockDb = new Mock<IClientDatabase>();
        var mockClient = new Mock<ISyncServerApi>();

        var sessionId = Guid.NewGuid();
        var batch1 = Enumerable.Range(1, 3)
            .Select(i => new TestHandlerEntity { Id = Guid.NewGuid(), SyncSessionId = sessionId })
            .ToList();
        var batch2 = Enumerable.Range(1, 2)
            .Select(i => new TestHandlerEntity { Id = Guid.NewGuid(), SyncSessionId = sessionId })
            .ToList();

        // First call returns 3 records with total=5, second call returns 2 records (no more)
        mockClient.SetupSequence(x => x.PullBatchAsync<TestHandlerEntity>(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((batch1, true, 5))
            .ReturnsAsync((batch2, false, 5));

        var config = CreateTestConfig();
        var handler = new TableSyncHandler<TestHandlerEntity>(
            mockDb.Object,
            mockClient.Object,
            config,
            new ClientSyncConfiguration { PullBatchSize = 3 });

        var reports = new List<(int Current, int Total)>();
        var progress = new SynchronousProgress<(int Current, int Total)>(p => reports.Add(p));

        // Act
        await handler.PullAsync(Guid.NewGuid(), progress: progress);

        // Assert
        reports.Should().HaveCount(2);
        reports.Should().AllSatisfy(r => r.Total.Should().Be(5),
            "total should come from server-reported batchTotal");
        reports[0].Current.Should().Be(3);
        reports[1].Current.Should().Be(5);
    }

    private static TableConfig CreateTestConfig()
    {
        return new TableConfig
        {
            EntityType = typeof(TestHandlerEntity),
            TableName = "TestEntities",
            Priority = 1
        };
    }
}

[SyncTable("TestHandlerEntity")]
public class TestHandlerEntity : ISyncEntity
{
    public Guid Id { get; set; } = Guid.Empty;
    public bool IsDirty { get;set; } = false;
    public DateTime? ModifiedAtUtc { get; set; }
    public Guid? SyncSessionId { get; set; }
    public string ModifiedByUserId { get; set; } = "System";
    public bool IsDeleted { get; set; } = false;
}

/// <summary>
/// Synchronous IProgress implementation for unit testing.
/// Invokes the callback inline instead of posting to the sync context,
/// making progress assertions reliable without Task.Delay hacks.
/// </summary>
public class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;
    public SynchronousProgress(Action<T> callback) => _callback = callback;
    public void Report(T value) => _callback(value);
}
