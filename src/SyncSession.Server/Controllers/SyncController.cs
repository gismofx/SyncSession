using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Constants;
using SyncSession.Core.DTOs;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Server.Services;

namespace SyncSession.Server.Controllers;

/// <summary>
/// Unified sync controller for push (client → server) and pull (server → client) operations.
/// </summary>
[ApiController]
[Route("api/v1/sync")]
[Produces("application/json")]
[Authorize(Policy = "SyncAccess")]
public class SyncController : ControllerBase
{
    private readonly IServerDatabase _database;
    private readonly ISessionTracker _sessionTracker;
    private readonly ITempTableManager _tempTableManager;
    private readonly ISeedService _seedService;
    private readonly ISyncGate _syncGate;
    private readonly SyncSessionOptions _options;
    private readonly ILogger<SyncController> _logger;
    private readonly IWebHostEnvironment _environment;

    public SyncController(
        IServerDatabase database,
        ISessionTracker sessionTracker,
        ITempTableManager tempTableManager,
        ISeedService seedService,
        ISyncGate syncGate,
        SyncSessionOptions options,
        ILogger<SyncController> logger,
        IWebHostEnvironment environment)
    {
        _database = database;
        _sessionTracker = sessionTracker;
        _tempTableManager = tempTableManager;
        _seedService = seedService;
        _syncGate = syncGate;
        _options = options;
        _logger = logger;
        _environment = environment;
    }

    #region Push Endpoints (Client → Server)

    /// <summary>
    /// Begins a new push session and allocates temp table resources for each table.
    /// </summary>
    /// <param name="request">The push session begin request containing client, device, and table details.</param>
    /// <returns>A response containing the session ID and temp table metadata for each table.</returns>
    [HttpPost("push/begin")]
    [ProducesResponseType(typeof(PushSessionBeginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PushSessionBeginResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PushSessionBeginResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PushSessionBeginResponse>> BeginPushSession([FromBody] PushSessionBeginRequest request)
    {
        try
        {
            if (_syncGate.IsGated)
            {
                Response.Headers["Retry-After"] = "60";
                return StatusCode(503, new PushSessionBeginResponse { Success = false, ErrorMessage = "Server is in maintenance mode. Retry after 60 seconds." });
            }

            var versionCheck = ValidateProtocolVersion(request.DeviceId);
            if (versionCheck != null) return versionCheck;

            _logger.LogInformation("Begin push session for device {DeviceId}", request.DeviceId);
            var userId          = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var userDisplayName = request.UserDisplayName
                ?? (string.IsNullOrEmpty(_options.DisplayNameClaimType) ? null : User.FindFirst(_options.DisplayNameClaimType)?.Value);
            var response = await _sessionTracker.CreatePushSessionAsync(request, userId, userDisplayName);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid request for BeginPushSession");
            return BadRequest(new PushSessionBeginResponse { Success = false, ErrorMessage = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeginPushSession failed");

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PushSessionBeginResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Uploads a batch of records into the temp table for a staging push session.
    /// </summary>
    /// <param name="request">The batch request containing session ID, table name, and records.</param>
    /// <returns>A response indicating how many records were accepted.</returns>
    [HttpPost("push/batch")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(PushBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PushBatchResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PushBatchResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(PushBatchResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PushBatchResponse>> UploadBatch([FromBody] PushBatchRequest request)
    {
        try
        {
            var sessionExists = await _sessionTracker.SessionExistsAsync(request.SessionId, expectedStatus: SyncConstants.STATUS_STAGING);
            if (!sessionExists)
            {
                return NotFound(new PushBatchResponse { Success = false, ErrorMessage = "Session not found or not in Staging status" });
            }

            var recordsAccepted = await _tempTableManager.InsertBatchAsync(request.SessionId, request.TableName, request.Records);
            await _sessionTracker.UpdateSessionActivityAsync(request.SessionId);

            return Ok(new PushBatchResponse { Success = true, RecordsAccepted = recordsAccepted });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation in UploadBatch");
            return Conflict(new PushBatchResponse { Success = false, ErrorMessage = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UploadBatch failed for session {SessionId}", request.SessionId);

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PushBatchResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Marks a table as fully uploaded and verifies the record count against what was received.
    /// </summary>
    /// <param name="request">The table complete request containing session ID, table name, and expected record count.</param>
    /// <returns>A response indicating whether the record count matched what the server received.</returns>
    [HttpPost("push/table-complete")]
    [ProducesResponseType(typeof(PushTableCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PushTableCompleteResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PushTableCompleteResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PushTableCompleteResponse>> CompleteTable([FromBody] PushTableCompleteRequest request)
    {
        try
        {
            var response = await _sessionTracker.CompleteTableAsync(request.SessionId, request.TableName, request.TotalRecordsSent);
            return response.Success ? Ok(response) : BadRequest(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid request for CompleteTable");
            return BadRequest(new PushTableCompleteResponse { Success = false, ErrorMessage = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompleteTable failed for session {SessionId}", request.SessionId);

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PushTableCompleteResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Marks a push session as ready for background processing.
    /// </summary>
    /// <param name="request">The session complete request containing the session ID to finalize.</param>
    /// <returns>A response confirming the session has been queued for processing.</returns>
    [HttpPost("push/complete")]
    [ProducesResponseType(typeof(PushSessionCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PushSessionCompleteResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PushSessionCompleteResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PushSessionCompleteResponse>> CompleteSession([FromBody] PushSessionCompleteRequest request)
    {
        try
        {
            var success = await _sessionTracker.MarkSessionReadyAsync(request.SessionId);
            if (!success)
            {
                return NotFound(new PushSessionCompleteResponse { Success = false, ErrorMessage = "Session not found or invalid status" });
            }

            // Session marked as Ready - SyncQueueBackgroundService will process within ~5 seconds
            _logger.LogInformation("Session {SessionId} queued for background processing", request.SessionId);

            return Ok(new PushSessionCompleteResponse { Success = true, QueuedForProcessing = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompleteSession failed for session {SessionId}", request.SessionId);

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PushSessionCompleteResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Refreshes the activity timestamp for a push session to prevent timeout.
    /// </summary>
    /// <param name="sessionId">The push session ID to keep alive.</param>
    /// <returns>A success indicator.</returns>
    [HttpPost("push/keepalive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PushKeepAlive([FromQuery] Guid sessionId)
    {
        try
        {
            await _sessionTracker.UpdateSessionActivityAsync(sessionId);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PushKeepAlive failed for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Returns the current status of a push session including processing state and sync version.
    /// </summary>
    /// <param name="sessionId">The push session ID to query.</param>
    /// <returns>The session status, or 404 if the session does not exist.</returns>
    [HttpGet("push/status/{sessionId}")]
    [ProducesResponseType(typeof(PushSessionStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PushSessionStatusResponse>> GetSessionStatus(Guid sessionId)
    {
        try
        {
            var status = await _sessionTracker.GetSessionStatusAsync(sessionId);
            return status == null ? NotFound(new { error = "Session not found" }) : Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSessionStatus failed for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    #endregion

    #region Pull Endpoints (Server → Client)

    /// <summary>
    /// Begins a pull session and snapshots unprocessed server records into temp tables for the device.
    /// </summary>
    /// <param name="request">The pull session begin request containing client, device, and table names.</param>
    /// <returns>A response with the pull session ID and per-table record counts and temp table metadata.</returns>
    [HttpPost("pull/begin")]
    [ProducesResponseType(typeof(PullSessionBeginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PullSessionBeginResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(PullSessionBeginResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PullSessionBeginResponse>> BeginPullSession([FromBody] PullSessionBeginRequest request)
    {
        try
        {
            if (_syncGate.IsGated)
            {
                Response.Headers["Retry-After"] = "60";
                return StatusCode(503, new PullSessionBeginResponse { Success = false, ErrorMessage = "Server is in maintenance mode. Retry after 60 seconds." });
            }

            var versionCheck = ValidateProtocolVersion(request.DeviceId);
            if (versionCheck != null) return versionCheck;

            _logger.LogInformation("Begin pull session for device {DeviceId}", request.DeviceId);
            var userId          = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var userDisplayName = request.UserDisplayName
                ?? (string.IsNullOrEmpty(_options.DisplayNameClaimType) ? null : User.FindFirst(_options.DisplayNameClaimType)?.Value);
            var response = await _sessionTracker.CreatePullSessionAsync(request, userId, userDisplayName);
            return response.Success ? Ok(response) : BadRequest(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid request for BeginPullSession");
            return BadRequest(new PullSessionBeginResponse { Success = false, ErrorMessage = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BeginPullSession failed");

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PullSessionBeginResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Returns a paginated batch of records from the pull session temp table for a specific table.
    /// </summary>
    /// <param name="request">The batch request containing pull session ID, table name, offset, and limit.</param>
    /// <returns>A batch of records with a <c>HasMore</c> flag indicating whether additional batches remain.</returns>
    [HttpPost("pull/batch")]
    [ProducesResponseType(typeof(PullBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PullBatchResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(PullBatchResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PullBatchResponse>> GetBatch([FromBody] PullBatchRequest request)
    {
        try
        {
            var batch = await _tempTableManager.GetPullBatchAsync(request.PullSessionId, request.TableName, request.Offset, request.Limit);
            await _sessionTracker.UpdatePullSessionActivityAsync(request.PullSessionId);

            return Ok(new PullBatchResponse
            {
                Success = true,
                Records = batch.Records,
                HasMore = batch.HasMore,
                TotalRecords = batch.TotalRecords
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation in GetBatch");
            return Conflict(new PullBatchResponse { Success = false, ErrorMessage = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBatch failed for pull session {PullSessionId}", request.PullSessionId);

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PullBatchResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Completes a pull session by marking sessions as processed and cleaning up temp table data.
    /// </summary>
    /// <param name="request">The pull complete request containing processed session IDs and table metadata for cleanup.</param>
    /// <returns>A response confirming the pull session was completed successfully.</returns>
    [HttpPost("pull/complete")]
    [ProducesResponseType(typeof(PullSessionCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PullSessionCompleteResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PullSessionCompleteResponse>> CompletePullSession([FromBody] PullSessionCompleteRequest request)
    {
        try
        {
            await _database.MarkSessionsProcessedAsync(request.DeviceId, request.ProcessedSessionIds);
            await _tempTableManager.CleanupPullSessionAsync(request.PullSessionId, request.Tables.Values);

            // Mark pull session as completed with row counts (38l: replaces activity logger)
            var totalRows = request.Tables.Values.Sum(t => t.TotalRecords ?? 0);
            await _database.UpdateSessionStatusAsync(
                request.PullSessionId, SyncConstants.STATUS_COMPLETED,
                totalRows: totalRows);
            await _database.DeleteSessionTablesAsync(request.PullSessionId);

            return Ok(new PullSessionCompleteResponse { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompletePullSession failed for pull session {PullSessionId}", request.PullSessionId);

            var errorMessage = _environment.IsDevelopment()
                ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
                : "Internal server error";

            return StatusCode(500, new PullSessionCompleteResponse { Success = false, ErrorMessage = errorMessage });
        }
    }

    /// <summary>
    /// Refreshes the activity timestamp for a pull session to prevent timeout.
    /// </summary>
    /// <param name="pullSessionId">The pull session ID to keep alive.</param>
    /// <returns>A success indicator.</returns>
    [HttpPost("pull/keepalive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PullKeepAlive([FromQuery] Guid pullSessionId)
    {
        try
        {
            await _sessionTracker.UpdatePullSessionActivityAsync(pullSessionId);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PullKeepAlive failed for pull session {PullSessionId}", pullSessionId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    #endregion

    #region Seed Endpoint (Server → Client, full-tenant streaming)

    /// <summary>
    /// Streams all records for the specified tenant as NDJSON for initial client seeding.
    /// Each line is a JSON-serialized <see cref="SeedLine"/> discriminated by <c>type</c>.
    /// </summary>
    /// <param name="tenantId">Tenant whose records to stream.</param>
    /// <param name="ct">Cancellation token (injected by ASP.NET Core).</param>
    /// <remarks>
    /// The <c>end</c> line carries an <c>anchor</c> timestamp captured before the first query.
    /// Clients must pass this anchor as the <c>since</c> parameter to their first incremental
    /// pull to guarantee no records written during streaming are missed.
    /// Stream interruption (network drop, cancellation) requires a full restart — no resume.
    /// </remarks>
    [HttpGet("seed/{tenantId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task StreamSeed(Guid tenantId, [FromQuery] Guid deviceId, CancellationToken ct)
    {
        if (deviceId == Guid.Empty)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("deviceId query parameter is required.", ct);
            return;
        }

        if (_syncGate.IsGated)
        {
            Response.StatusCode = 503;
            Response.Headers["Retry-After"] = "60";
            await Response.WriteAsync("Server is in maintenance mode. Retry after 60 seconds.", ct);
            return;
        }

        Response.ContentType = "application/x-ndjson";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "no-store";

        try
        {
            var writer = new System.IO.StreamWriter(Response.Body, leaveOpen: true);
            await using (writer.ConfigureAwait(false))
            {
                int lineCount = 0;
                await foreach (var line in _seedService.StreamSeedAsync(tenantId, deviceId, ct))
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(line);
                    await writer.WriteLineAsync(json.AsMemory(), CancellationToken.None);
                    lineCount++;

                    // Flush frequently during row streaming — keeps client buffer full,
                    // reducing ReadLineAsync wait time between client write batches.
                    if (line.Type != "row" || lineCount % 100 == 0)
                        await writer.FlushAsync(CancellationToken.None);
                }
                await writer.FlushAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Seed stream cancelled for tenant {TenantId}", tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seed stream failed for tenant {TenantId}", tenantId);
            // Cannot change status code after headers sent — stream is already open
        }
    }

    /// <summary>
    /// Acknowledges that a seed operation is complete for a device.
    /// Marks all currently-committed sessions as processed so the first incremental
    /// pull returns only post-seed delta records.
    /// </summary>
    /// <param name="request">Contains the device ID that completed the seed.</param>
    /// <returns>204 No Content on success.</returns>
    [HttpPost("seed/acknowledge")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> AcknowledgeSeed([FromBody] SeedAcknowledgeRequest request)
    {
        try
        {
            await _database.AcknowledgeSeedAsync(request.DeviceId, request.TenantId);
            _logger.LogInformation("Seed acknowledged for device {DeviceId} status={Status} rows={Rows}",
                request.DeviceId, request.Status, request.TotalRows);

            // Create a SessionRecord record for the seed (38l: replaces activity logger)
            await _database.CreateSessionAsync(new SessionRecord
            {
                SessionId = Guid.NewGuid(),
                TenantId = request.TenantId,
                DeviceId = request.DeviceId,
                UserId = request.UserId,
                UserDisplayName = request.UserDisplayName,
                SessionType = SyncConstants.SESSION_TYPE_SEED,
                Status = string.IsNullOrEmpty(request.Status)
                    ? SyncConstants.STATUS_COMPLETED   // back-compat: older clients omit Status
                    : request.Status,
                CreatedAtUtc = request.StartedAtUtc,
                LastActivityUtc = DateTime.UtcNow,
                CommittedAtUtc = DateTime.UtcNow,
                TotalRows = request.TotalRows,
                RowCountsJson = request.RowCountsJson,
                ErrorMessage = request.ErrorDetail,
            });

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AcknowledgeSeed failed for device {DeviceId}", request.DeviceId);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    #endregion

    #region Monitoring

    /// <summary>
    /// Returns recent sync session records for monitoring and audit purposes.
    /// Covers Push, Pull, and Seed operations. Ordered by most recent first.
    /// </summary>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<SyncSessionSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<SyncSessionSummary>>> GetRecentSessions(
        [FromQuery] Guid? tenantId = null,
        [FromQuery] int pageSize = 50)
    {
        try
        {
            pageSize = Math.Clamp(pageSize, 1, 200);
            var result = await _database.GetRecentSessionsAsync(tenantId, pageSize);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRecentSessions failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    #endregion

    #region Protocol Negotiation

    /// <summary>
    /// Validates the <c>X-SyncSystem-Protocol</c> header sent by the client.
    /// Returns <c>null</c> when the version is acceptable; otherwise returns
    /// a <c>426 Upgrade Required</c> result with a descriptive error body.
    /// </summary>
    private ActionResult? ValidateProtocolVersion(Guid deviceId)
    {
        if (!Request.Headers.TryGetValue(SyncProtocolVersion.ProtocolHeader, out var raw)
            || !int.TryParse(raw.FirstOrDefault(), out var clientVersion))
        {
            clientVersion = 0;
        }

        // Always capture the informational package version for logging.
        var pkgVersion = Request.Headers.TryGetValue(SyncProtocolVersion.PackageVersionHeader, out var pkgRaw)
            ? pkgRaw.FirstOrDefault() ?? "unknown"
            : "not provided";

        if (clientVersion >= SyncProtocolVersion.MinSupported
            && clientVersion <= SyncProtocolVersion.Current)
        {
            _logger.LogDebug(
                "Protocol version accepted: device={DeviceId}, protocol={ProtocolVersion}, package={PackageVersion}",
                deviceId, clientVersion, pkgVersion);
            return null;
        }

        _logger.LogWarning(
            "Protocol version rejected: device={DeviceId}, clientProtocol={ClientVersion}, " +
            "serverRange={MinSupported}-{Current}, clientPackage={PackageVersion}",
            deviceId, clientVersion,
            SyncProtocolVersion.MinSupported, SyncProtocolVersion.Current,
            pkgVersion);

        return StatusCode(426, new
        {
            error = "Upgrade Required",
            clientVersion,
            serverMinVersion = SyncProtocolVersion.MinSupported,
            serverCurrentVersion = SyncProtocolVersion.Current,
            message = $"Client protocol version {clientVersion} is not supported. " +
                      $"Server requires {SyncProtocolVersion.MinSupported}–{SyncProtocolVersion.Current}. " +
                      "Update the SyncSystem.Client NuGet package."
        });
    }

    #endregion

    #region Test/Debug Endpoints

#if DEBUG
    /// <summary>
    /// Removes all test data from the server database. Available in DEBUG builds only.
    /// </summary>
    [HttpPost("test/cleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CleanupTestData()
    {
        try
        {
            _logger.LogInformation("Cleaning up test data");
            
            // Clean up main tables
            await _database.ExecuteRawSqlAsync("DELETE FROM Customers");
            
            // Clean up sync tracking tables
            await _database.ExecuteRawSqlAsync("DELETE FROM ClientProcessedSessions");
            await _database.ExecuteRawSqlAsync("DELETE FROM SyncSessionTables");
            await _database.ExecuteRawSqlAsync("DELETE FROM SessionRecords");
            
            // Clean up temp tables (shared tables for all entities)
            try
            {
                await _database.ExecuteRawSqlAsync("TRUNCATE TABLE TempPushCustomers");
                await _database.ExecuteRawSqlAsync("TRUNCATE TABLE TempPullCustomers");
            }
            catch
            {
                // Fallback to DELETE if TRUNCATE fails
                await _database.ExecuteRawSqlAsync("DELETE FROM TempPushCustomers");
                await _database.ExecuteRawSqlAsync("DELETE FROM TempPullCustomers");
            }
            
            _logger.LogInformation("Test data cleanup completed");
            
            return Ok(new { success = true, message = "Test data cleaned up successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CleanupTestData failed");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
#endif

    #endregion
}
