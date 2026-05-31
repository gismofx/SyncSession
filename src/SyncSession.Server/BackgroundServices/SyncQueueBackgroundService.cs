using SyncSession.Core.Interfaces;
using SyncSession.Server.Models;

namespace SyncSession.Server.BackgroundServices;

/// <summary>
/// Background service that processes sync sessions from the queue.
/// Runs continuously, polling for <c>Ready</c> sessions at the interval configured
/// by <see cref="ServerSyncConfiguration.QueuePollIntervalSeconds"/>.
/// </summary>
public class SyncQueueBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<SyncQueueBackgroundService> _logger;
    private readonly TimeSpan _pollInterval;

    public SyncQueueBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<SyncQueueBackgroundService> logger,
        ServerSyncConfiguration config)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(config.QueuePollIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Sync Queue Background Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing sync queue");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("Sync Queue Background Service stopped");
    }

    /// <summary>
    /// Processes all ready sessions by delegating to the registered <see cref="ISyncQueueProcessor"/>.
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<ISyncQueueProcessor>();

        var processedCount = await processor.ProcessReadySessionsAsync(cancellationToken);

        if (processedCount > 0)
        {
            _logger.LogInformation("Processed {SessionCount} sessions from queue", processedCount);
        }
    }
}
