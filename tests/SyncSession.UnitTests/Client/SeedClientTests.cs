using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SyncSession.Client.Seeding;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Unit tests for <see cref="SeedClient"/>. All tests use mocked dependencies —
/// no real database or HTTP connections are involved.
/// </summary>
public class SeedClientTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<SeedLine> ToAsyncEnumerable(
        IEnumerable<SeedLine> lines,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            yield return line;
            await Task.Yield();
        }
    }

    private static List<SeedLine> BuildStream(
        string tableName,
        int rowCount,
        DateTime? anchor = null,
        Guid? tenantId = null)
    {
        var tid = tenantId ?? Guid.NewGuid();
        var ts = anchor ?? DateTime.UtcNow;
        var lines = new List<SeedLine>
        {
            SeedLine.Begin(tid, ts, new List<string> { tableName }),
            SeedLine.TableStart(tableName, rowCount),
        };
        for (var i = 0; i < rowCount; i++)
            lines.Add(SeedLine.Row(tableName, new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }));
        lines.Add(SeedLine.TableEnd(tableName));
        lines.Add(SeedLine.End(ts));
        return lines;
    }

    private static SeedClient CreateClient(Mock<ISeedServerApi> mockApi)
        => new(mockApi.Object, NullLogger<SeedClient>.Instance);

    // ── Tests 1–3 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_SingleTable_CallsWriterInCorrectOrder()
    {
        var callOrder = new List<string>();
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Callback<string, int, CancellationToken>((_, _, _) => callOrder.Add("Begin"))
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()))
                  .Callback<string, IReadOnlyList<Dictionary<string, object?>>, CancellationToken>((_, _, _) => callOrder.Add("Write"))
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, CancellationToken>((_, _) => callOrder.Add("End"))
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>()))
                  .Callback<CancellationToken>(_ => callOrder.Add("Commit"))
                  .Returns(Task.CompletedTask);

        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        callOrder.Should().Equal("Begin", "Write", "End", "Commit");
    }

    [Fact]
    public async Task SeedAsync_ExactBatchSize_OneWriteRowsCall()
    {
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 500);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object, batchSize: 500);

        mockWriter.Verify(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SeedAsync_BatchSizePlusOne_TwoWriteRowsCalls()
    {
        var writtenCounts = new List<int>();
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 501);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()))
                  .Callback<string, IReadOnlyList<Dictionary<string, object?>>, CancellationToken>((_, rows, _) => writtenCounts.Add(rows.Count))
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object, batchSize: 500);

        writtenCounts.Should().Equal(500, 1);
    }

    // ── Tests 4–6 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_BelowBatchSize_OneWriteRowsCall()
    {
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 499);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object, batchSize: 500);

        mockWriter.Verify(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SeedAsync_ReturnsAnchorFromEndLine()
    {
        var expectedAnchor = new DateTime(2026, 3, 13, 10, 0, 0, DateTimeKind.Utc);
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1, anchor: expectedAnchor);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        result.Anchor.Should().Be(expectedAnchor);
    }

    [Fact]
    public async Task SeedAsync_MultipleTableRows_CorrectRowCountsByTable()
    {
        var tid = Guid.NewGuid();
        var ts = DateTime.UtcNow;
        var lines = new List<SeedLine>
        {
            SeedLine.Begin(tid, ts, new List<string> { "Customers", "Orders" }),
            SeedLine.TableStart("Customers", 3),
        };
        for (var i = 0; i < 3; i++)
            lines.Add(SeedLine.Row("Customers", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }));
        lines.Add(SeedLine.TableEnd("Customers"));
        lines.Add(SeedLine.TableStart("Orders", 7));
        for (var i = 0; i < 7; i++)
            lines.Add(SeedLine.Row("Orders", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }));
        lines.Add(SeedLine.TableEnd("Orders"));
        lines.Add(SeedLine.End(ts));

        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(lines, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        result.RowCountsByTable["Customers"].Should().Be(3);
        result.RowCountsByTable["Orders"].Should().Be(7);
    }

    // ── Tests 7–9 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_StreamEndsWithoutEndLine_ThrowsSeedInterruptedException()
    {
        var tid = Guid.NewGuid();
        var ts = DateTime.UtcNow;
        // Stream intentionally omits the 'end' line
        var lines = new List<SeedLine>
        {
            SeedLine.Begin(tid, ts, new List<string> { "Customers" }),
            SeedLine.TableStart("Customers", 1),
            SeedLine.Row("Customers", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }),
            SeedLine.TableEnd("Customers"),
            // no SeedLine.End(...)
        };

        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(lines, ct));
        SeedAcknowledgeRequest? captured = null;
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        await act.Should().ThrowAsync<SeedInterruptedException>();
        mockWriter.Verify(w => w.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);

        // No 'end' line is a failure — must self-report Failed.
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(SyncConstants.STATUS_FAILED);
    }

    [Fact]
    public async Task SeedAsync_ExceptionMidStream_ThrowsSeedInterruptedException()
    {
        var inner = new IOException("Connection reset by peer");
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns(ThrowingStream(inner));
        SeedAcknowledgeRequest? captured = null;
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        var ex = await act.Should().ThrowAsync<SeedInterruptedException>();
        ex.WithInnerException<IOException>().WithMessage(inner.Message);

        // Mid-stream failure must self-report Failed with the real cause in ErrorDetail.
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(SyncConstants.STATUS_FAILED);
        captured.ErrorDetail.Should().Contain(inner.Message);
    }

    private static async IAsyncEnumerable<SeedLine> ThrowingStream(Exception toThrow,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tid = Guid.NewGuid();
        var ts = DateTime.UtcNow;
        yield return SeedLine.Begin(tid, ts, new List<string> { "Customers" });
        yield return SeedLine.TableStart("Customers", 1);
        await Task.Yield();
        throw toThrow;
    }

    [Fact]
    public async Task SeedAsync_LocalWriteFails_ReportsFailedWithUnwrappedReason()
    {
        // ClientDatabaseSeedWriter invokes the typed upsert via reflection, so a DB error
        // surfaces wrapped as TargetInvocationException. The reported reason must unwrap it.
        var dbError = new InvalidOperationException("UNIQUE constraint failed: Customers.Id");
        var wrapped = new TargetInvocationException("Exception has been thrown by the target of an invocation.", dbError);

        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        SeedAcknowledgeRequest? captured = null;
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(wrapped);

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        await act.Should().ThrowAsync<SeedInterruptedException>();
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(SyncConstants.STATUS_FAILED);
        captured.ErrorDetail.Should().Contain(dbError.Message);
        captured.ErrorDetail.Should().NotContain("target of an invocation",
            "the reflection wrapper message must be unwrapped, not reported");
    }

    [Fact]
    public async Task SeedAsync_LongFailureReason_TruncatedTo1000Chars()
    {
        var longMessage = new string('x', 5000);
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        SeedAcknowledgeRequest? captured = null;
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException(longMessage));

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        await act.Should().ThrowAsync<SeedInterruptedException>();
        captured.Should().NotBeNull();
        captured!.ErrorDetail!.Length.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public async Task SeedAsync_SuccessAcknowledgeThrows_DoesNotThrow()
    {
        // The local seed is committed before acknowledge — a failed acknowledge must not
        // surface as a failed seed. SeedAsync should swallow it and return normally.
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("network down"));
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        result.Should().NotBeNull();
        result.RowCountsByTable["Customers"].Should().Be(1);
    }

    [Fact]
    public async Task SeedAsync_AcknowledgeSeedCalledAfterCommit()
    {
        var callOrder = new List<string>();
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((_, _) => callOrder.Add("Acknowledge"))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>()))
                  .Callback<CancellationToken>(_ => callOrder.Add("Commit"))
                  .Returns(Task.CompletedTask);

        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        callOrder.Should().Equal("Commit", "Acknowledge");
    }

    // ── Tests 10–12 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_ProgressReportedCorrectly_BatchFlushAndTableEnd()
    {
        var reports = new List<SeedProgress>();
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        var stream = BuildStream("Customers", 1000);

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(stream, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var progress = new Progress<SeedProgress>(p => reports.Add(p));
        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object, progress, batchSize: 500);

        // 3 reports: flush@500, flush@1000, table_end
        reports.Should().HaveCount(3);
        reports[0].RowsWritten.Should().Be(500);
        reports[0].TablesComplete.Should().Be(0);
        reports[1].RowsWritten.Should().Be(1000);
        reports[1].TablesComplete.Should().Be(0);
        reports[2].RowsWritten.Should().Be(1000);
        reports[2].TablesComplete.Should().Be(1);
    }

    [Fact]
    public async Task SeedAsync_Cancellation_CommitNotCalled_ButReportsCancelled()
    {
        var cts = new CancellationTokenSource();
        SeedAcknowledgeRequest? captured = null;
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(BuildStream("Customers", 1000), ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);

        // Cancel when WriteRowsAsync is first called (mid-stream)
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()))
                  .Callback<string, IReadOnlyList<Dictionary<string, object?>>, CancellationToken>((_, _, _) => cts.Cancel())
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        mockWriter.Verify(w => w.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Cancellation now self-reports a Cancelled session (was previously not logged).
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(SyncConstants.STATUS_CANCELLED);
    }

    [Fact]
    public async Task SeedAsync_CancellationReport_UsesCancellationNone()
    {
        // The failure/cancel report must not be passed the cancelled token, or the report
        // itself would throw OperationCanceledException and never reach the server.
        var cts = new CancellationTokenSource();
        CancellationToken ackToken = new(canceled: true); // sentinel; overwritten on call
        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();

        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(BuildStream("Customers", 1000), ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((_, t) => ackToken = t)
               .Returns(Task.CompletedTask);

        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>()))
                  .Callback<string, IReadOnlyList<Dictionary<string, object?>>, CancellationToken>((_, _, _) => cts.Cancel())
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        ackToken.CanBeCanceled.Should().BeFalse("failure/cancel report must use CancellationToken.None");
    }

    [Fact]
    public async Task SeedAsync_UnknownLineType_IsIgnored()
    {
        var tid = Guid.NewGuid();
        var ts = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);
        var lines = new List<SeedLine>
        {
            SeedLine.Begin(tid, ts, new List<string> { "Customers" }),
            new SeedLine { Type = "future_extension" },   // unknown — must be ignored
            SeedLine.TableStart("Customers", 2),
            SeedLine.Row("Customers", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }),
            SeedLine.Row("Customers", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }),
            SeedLine.TableEnd("Customers"),
            SeedLine.End(ts),
        };

        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(lines, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        result.Anchor.Should().Be(ts);
        result.RowCountsByTable["Customers"].Should().Be(2);
    }

    // ── Tests 13–14 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_TwoTables_SecondBeginCalledAfterFirstEnd()
    {
        var callOrder = new List<string>();
        var tid = Guid.NewGuid();
        var ts = DateTime.UtcNow;
        var lines = new List<SeedLine>
        {
            SeedLine.Begin(tid, ts, new List<string> { "Customers", "Orders" }),
            SeedLine.TableStart("Customers", 1),
            SeedLine.Row("Customers", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }),
            SeedLine.TableEnd("Customers"),
            SeedLine.TableStart("Orders", 1),
            SeedLine.Row("Orders", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }),
            SeedLine.TableEnd("Orders"),
            SeedLine.End(ts),
        };

        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(lines, ct));
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        mockWriter.Setup(w => w.BeginTableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Callback<string, int, CancellationToken>((table, _, _) => callOrder.Add($"Begin:{table}"))
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.WriteRowsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<Dictionary<string, object?>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.EndTableAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, CancellationToken>((table, _) => callOrder.Add($"End:{table}"))
                  .Returns(Task.CompletedTask);
        mockWriter.Setup(w => w.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        var beginCustomers = callOrder.IndexOf("Begin:Customers");
        var endCustomers   = callOrder.IndexOf("End:Customers");
        var beginOrders    = callOrder.IndexOf("Begin:Orders");

        beginCustomers.Should().BeLessThan(endCustomers, "Begin:Customers must precede End:Customers");
        endCustomers.Should().BeLessThan(beginOrders, "End:Customers must precede Begin:Orders");
    }

    [Fact]
    public async Task SeedAsync_MissingBeginLine_ThrowsSeedInterruptedException()
    {
        var lines = new List<SeedLine>
        {
            // 'begin' intentionally omitted
            SeedLine.TableStart("Customers", 1),
            SeedLine.Row("Customers", new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString() }),
            SeedLine.TableEnd("Customers"),
            SeedLine.End(DateTime.UtcNow),
        };

        var mockApi = new Mock<ISeedServerApi>();
        var mockWriter = new Mock<ISeedDatabaseWriter>();
        mockApi.Setup(a => a.StreamSeedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns((Guid _, Guid _, CancellationToken ct) => ToAsyncEnumerable(lines, ct));
        SeedAcknowledgeRequest? captured = null;
        mockApi.Setup(a => a.AcknowledgeSeedAsync(It.IsAny<SeedAcknowledgeRequest>(), It.IsAny<CancellationToken>()))
               .Callback<SeedAcknowledgeRequest, CancellationToken>((r, _) => captured = r)
               .Returns(Task.CompletedTask);

        var act = async () => await CreateClient(mockApi).SeedAsync(Guid.NewGuid(), Guid.NewGuid(), mockWriter.Object);

        // Malformed stream now surfaces as a SeedInterruptedException (consistent with all
        // other seed failures), wrapping the original InvalidOperationException guard.
        var ex = await act.Should().ThrowAsync<SeedInterruptedException>();
        ex.WithInnerException<InvalidOperationException>().WithMessage("*'table'*'begin'*");

        // ...and self-reports a Failed seed session.
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(SyncConstants.STATUS_FAILED);
    }
}
