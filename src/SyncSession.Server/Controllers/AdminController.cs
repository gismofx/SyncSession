using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SyncSession.Core.DTOs;
using SyncSession.Core.Interfaces;
using SyncSession.Server.Services;

namespace SyncSession.Server.Controllers;

/// <summary>
/// Admin endpoints for server maintenance operations.
/// Requires the <c>SyncAdminAccess</c> policy — consumers define what that means
/// (e.g. <c>RequireClaim("SyncAdmin", "true")</c> for a dedicated admin claim).
/// When <c>RequireAuthorization = false</c> the policy is allow-all.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
[Authorize(Policy = "SyncAdminAccess")]
public class AdminController : ControllerBase
{
    private readonly ISyncGate _syncGate;
    private readonly IServerDatabase _database;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        ISyncGate syncGate,
        IServerDatabase database,
        ILogger<AdminController> logger)
    {
        _syncGate = syncGate;
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Returns current maintenance mode status including active session and queue depth.
    /// Always available — not blocked by the gate itself.
    /// </summary>
    [HttpGet("maintenance")]
    [ProducesResponseType(typeof(MaintenanceStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetMaintenanceStatus()
    {
        try
        {
            return Ok(await BuildStatusAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMaintenanceStatus failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Enables maintenance mode. New sessions are blocked at all entry points;
    /// in-flight sessions complete normally.
    /// </summary>
    [HttpPost("maintenance/enable")]
    [ProducesResponseType(typeof(MaintenanceStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> EnableMaintenance()
    {
        try
        {
            _syncGate.Enable();
            _logger.LogWarning("Maintenance mode ENABLED");
            return Ok(await BuildStatusAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnableMaintenance failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Disables maintenance mode, restoring normal sync operation.
    /// </summary>
    [HttpPost("maintenance/disable")]
    [ProducesResponseType(typeof(MaintenanceStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DisableMaintenance()
    {
        try
        {
            _syncGate.Disable();
            _logger.LogInformation("Maintenance mode DISABLED");
            return Ok(await BuildStatusAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisableMaintenance failed");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<MaintenanceStatusDto> BuildStatusAsync()
    {
        using var connection = await _database.GetConnectionAsync();

        var activeSessionCount = await connection.QuerySingleAsync<int>(@"
            SELECT COUNT(*)
            FROM SessionRecords
            WHERE Status NOT IN ('Committed', 'Failed', 'Completed', 'Cancelled')");

        var queueDepth = await connection.QuerySingleAsync<int>(@"
            SELECT COUNT(*)
            FROM SessionRecords
            WHERE Status = 'Ready'");

        return new MaintenanceStatusDto
        {
            MaintenanceEnabled  = _syncGate.IsGated,
            ActiveSessionCount  = activeSessionCount,
            QueueDepth          = queueDepth,
            ReadyForMaintenance = _syncGate.IsGated && activeSessionCount == 0 && queueDepth == 0
        };
    }
}
