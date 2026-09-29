using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncSession.Client.Http;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// How big a piece of the seed response the client asks for on each read.
/// </summary>
/// <remarks>
/// In the browser on .NET 9+, each read of a response stream is one JS promise round trip whose
/// .NET continuation is scheduled through a ~4–5 ms browser timer. The seed is hundreds of MB, so
/// the read size decides the seed's speed: 1 KB reads measured 0.20 MB/s on .NET 10, 64 KB reads
/// 9.35 MB/s. Nothing fails when the buffer is small — it is only slow — so this test pins the size
/// itself. It cannot measure the browser's cost here; it guards the one line that decides how often
/// that cost is paid.
/// </remarks>
public class HttpSeedServerApiReadBufferTests
{
    private const int MinimumReadBytes = 64 * 1024;

    [Fact]
    public async Task StreamSeedAsync_AsksTheResponseStreamForAtLeast64KbPerRead()
    {
        var body = SeedBody(lines: 200); // ~300 KB: several reads at 64 KB, hundreds at 1 KB
        var recording = new ReadSizeRecordingStream(new MemoryStream(body));
        var api = Api(recording);

        var yielded = 0;
        await foreach (var _ in api.StreamSeedAsync(Guid.NewGuid(), Guid.NewGuid())) yielded++;

        yielded.Should().Be(200, "the buffer size must not change what is read, only how");
        recording.RequestedSizes.Should().NotBeEmpty();
        recording.RequestedSizes.Min().Should().BeGreaterThanOrEqualTo(MinimumReadBytes,
            "every read is a timer hop in the browser, so 1 KB reads make a large seed crawl");
    }

    [Fact]
    public async Task StreamSeedAsync_WithALargeBuffer_StillSplitsLinesThatStraddleReads()
    {
        // Lines far longer than one read, so a line is always assembled from several pieces.
        var body = SeedBody(lines: 3, rowsPerLine: 2_000);
        var api = Api(new ReadSizeRecordingStream(new MemoryStream(body)));

        var lines = new List<string>();
        await foreach (var line in api.StreamSeedAsync(Guid.NewGuid(), Guid.NewGuid())) lines.Add(line.RawLine!);

        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.StartsWith("{\"type\":\"rows\"") && l.EndsWith("]}"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpSeedServerApi Api(Stream responseBody)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(responseBody) };
        return new HttpSeedServerApi(new HttpClient(new StubHandler(response)), "https://sync.example.com/api",
            NullLogger<HttpSeedServerApi>.Instance);
    }

    /// <summary>NDJSON shaped like the server's: one "rows" bundle per line.</summary>
    private static byte[] SeedBody(int lines, int rowsPerLine = 100)
    {
        var sb = new StringBuilder();
        for (int l = 0; l < lines; l++)
        {
            sb.Append("{\"type\":\"rows\",\"table\":\"History\",\"rows\":[");
            for (int r = 0; r < rowsPerLine; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append("{\"Id\":\"").Append(Guid.NewGuid()).Append("\",\"Code\":\"C").Append(r).Append("\"}");
            }
            sb.Append("]}\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Records the buffer size each read asks for — what a browser read costs is per call.</summary>
    private sealed class ReadSizeRecordingStream(Stream inner) : Stream
    {
        public List<int> RequestedSizes { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            RequestedSizes.Add(count);
            return inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            RequestedSizes.Add(count);
            return inner.ReadAsync(buffer, offset, count, ct);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            RequestedSizes.Add(buffer.Length);
            return inner.ReadAsync(buffer, ct);
        }
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response);
    }
}
