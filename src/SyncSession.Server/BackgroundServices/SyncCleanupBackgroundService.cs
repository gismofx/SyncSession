using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncSession.Core.Interfaces;
using SyncSession.Server.Models;

namespace SyncSession.Server.BackgroundServices;

/// <summary>
/// Background service that periodically runs all registered <see cref="ICleanupService"/>
/// implementations to maintain database health:
/// <list type="bullet">
///   <item><description>Detect and fail stale sessions (timed out)</description></item>
///   <item><description>Drop orphaned dedicated temp tables</description></item>
///   <item><description>Purge old completed sessions (retention policy)</description></item>
///   <item><description>Clean old rows from shared temp tables</description></item>
/// </list>
/// New cleanup strategies can be added by registering additional <see cref="ICleanupService"/>
/// implementations in DI — no changes required here.
/// </summary>
public class SyncCleanupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<SyncCleanupBackgroundService> _logger;
    private readonly TimeSpan _cleanupInterval;

    public SyncCleanupBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<SyncCleanupBackgroundService> logger,
        ServerSyncConfiguration config)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        _cleanupInterval = TimeSpan.FromMinutes(
            config.SharedTableCleanupIntervalMinutes > 0
                ? config.SharedTableCleanupIntervalMinutes
                : 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Cleanup service started (interval: {CleanupInterval} minutes)",
            _cleanupInterval.TotalMinutes);

        // Wait a bit before first cleanup (let server initialize)
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup cycle failed");
            }

            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Cleanup service stopped");
    }

    /// <summary>
    /// Executes one complete cleanup cycle by dispatching to all registered
    /// <see cref="ICleanupService"/> implementations in registration order.
    /// </summary>
    private async Task RunCleanupCycleAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        _logger.LogDebug("Starting cleanup cycle");

        using var scope = _serviceScopeFactory.CreateScope();
        var cleanupServices = scope.ServiceProvider.GetRequiredService<IEnumerable<ICleanupService>>();

        foreach (var service in cleanupServices)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var count = await service.ExecuteCleanupAsync();

                if (count > 0)
                    _logger.LogInformation("{Description}: {Count} item(s) cleaned up",
                        service.GetCleanupDescription(), count);
                else
                    _logger.LogDebug("{Description}: nothing to clean up",
                        service.GetCleanupDescription());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup failed for {Description}",
                    service.GetCleanupDescription());
            }
        }

        var duration = DateTime.UtcNow - startTime;
        _logger.LogDebug("Cleanup cycle completed in {Duration:F2}s", duration.TotalSeconds);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleanup service shutting down...");
        await base.StopAsync(cancellationToken);
    }
}
