using Microsoft.AspNetCore.Mvc;
using SyncSession.Core.Interfaces;
using Dapper;

namespace SyncSession.Server.Controllers;

/// <summary>
/// Health check and system status endpoints.
/// </summary>
[ApiController]
[Route("api/v1/health")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    private readonly IServerDatabase _database;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IServerDatabase database,
        ILogger<HealthController> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Basic health check - returns OK if server is running
    /// </summary>
    /// <returns>Health status</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "v1"
        });
    }

    /// <summary>
    /// Detailed health check - includes database connectivity and queue metrics
    /// </summary>
    /// <returns>Detailed health information</returns>
    [HttpGet("detailed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetDetailedHealth()
    {
        var health = new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "v1",
            components = new Dictionary<string, object>()
        };

        try
        {
            // Test database connectivity
            using var connection = await _database.GetConnectionAsync();
            
            // Get queue depth
            var queueDepth = await connection.QuerySingleAsync<int>(@"
                SELECT COUNT(*) 
                FROM SessionRecords 
                WHERE Status = 'Ready'");

            health.components["database"] = new
            {
                status = "healthy",
                provider = connection.GetType().Name,
                queueDepth
            };

            // Get active sessions
            var activeSessions = await connection.QuerySingleAsync<int>(@"
                SELECT COUNT(*) 
                FROM SessionRecords 
                WHERE Status IN ('Staging', 'Processing')");

            health.components["activeSessions"] = new
            {
                status = "healthy",
                count = activeSessions
            };

            // Get completed sessions in last hour
            var recentCompletedSessions = await connection.QuerySingleAsync<int>(@"
                SELECT COUNT(*) 
                FROM SessionRecords 
                WHERE Status = 'Committed' 
                  AND CommittedAtUtc >= DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 HOUR)");

            health.components["throughput"] = new
            {
                status = "healthy",
                sessionsPerHour = recentCompletedSessions
            };

            return Ok(health);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");

            health.components["database"] = new
            {
                status = "unhealthy",
                error = ex.Message
            };

            return StatusCode(StatusCodes.Status503ServiceUnavailable, health);
        }
    }

    /// <summary>
    /// Get system metrics
    /// </summary>
    /// <returns>System performance metrics</returns>
    [HttpGet("metrics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMetrics()
    {
        try
        {
            using var connection = await _database.GetConnectionAsync();

            // Session statistics
            var sessionStats = await connection.QuerySingleAsync<dynamic>(@"
                SELECT 
                    COUNT(*) AS TotalSessions,
                    COALESCE(SUM(CASE WHEN Status = 'Staging' THEN 1 ELSE 0 END), 0) AS StagingSessions,
                    COALESCE(SUM(CASE WHEN Status = 'Ready' THEN 1 ELSE 0 END), 0) AS ReadySessions,
                    COALESCE(SUM(CASE WHEN Status = 'Processing' THEN 1 ELSE 0 END), 0) AS ProcessingSessions,
                    COALESCE(SUM(CASE WHEN Status = 'Committed' THEN 1 ELSE 0 END), 0) AS CommittedSessions,
                    COALESCE(SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END), 0) AS FailedSessions
                FROM SessionRecords");

            // Recent activity (last 24 hours)
            var recentActivity = await connection.QuerySingleAsync<dynamic>(@"
                SELECT 
                    COUNT(*) AS SessionsLast24Hours,
                    COALESCE(SUM(CASE WHEN Status = 'Committed' THEN 1 ELSE 0 END), 0) AS CompletedLast24Hours,
                    COALESCE(SUM(CASE WHEN Status = 'Failed' THEN 1 ELSE 0 END), 0) AS FailedLast24Hours
                FROM SessionRecords
                WHERE CreatedAtUtc >= DATE_SUB(UTC_TIMESTAMP(), INTERVAL 24 HOUR)");

            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                sessions = new
                {
                    total = (int)sessionStats.TotalSessions,
                    staging = (int)sessionStats.StagingSessions,
                    ready = (int)sessionStats.ReadySessions,
                    processing = (int)sessionStats.ProcessingSessions,
                    committed = (int)sessionStats.CommittedSessions,
                    failed = (int)sessionStats.FailedSessions
                },
                last24Hours = new
                {
                    total = (int)recentActivity.SessionsLast24Hours,
                    completed = (int)recentActivity.CompletedLast24Hours,
                    failed = (int)recentActivity.FailedLast24Hours,
                    successRate = recentActivity.SessionsLast24Hours > 0
                        ? Math.Round((double)recentActivity.CompletedLast24Hours / recentActivity.SessionsLast24Hours * 100, 2)
                        : 100.0
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve metrics");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "Failed to retrieve metrics",
                message = ex.Message
            });
        }
    }
}
