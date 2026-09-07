using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Server.Models;

namespace SyncSession.Server.BackgroundServices;

/// <summary>
/// Ensures the indexes the pull path needs, shortly after the application has started.
/// </summary>
/// <remarks>
/// Runs once per process, not on a schedule: the required set changes only when the registered entity
/// tables change, which needs a redeploy anyway.
/// <para>
/// Deliberately off the startup path. Creating an index on a multi-million-row table takes minutes,
/// and doing that inside <c>UseSyncSession()</c> would leave the application not yet listening —
/// indistinguishable from an outage.
/// </para>
/// <para>
/// The check itself always runs, even when <see cref="ServerSyncConfiguration.ManageIndexes"/> is
/// <c>false</c>: reading <c>INFORMATION_SCHEMA</c> needs no privileges, and an installation that has
/// opted out of the DDL still wants to be told which index to apply by hand.
/// </para>
/// </remarks>
public class SyncIndexBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<SyncIndexBackgroundService> _logger;
    private readonly bool _manageIndexes;
    private readonly TimeSpan _startDelay;

    public SyncIndexBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<SyncIndexBackgroundService> logger,
        ServerSyncConfiguration config)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _manageIndexes = config.ManageIndexes;
        _startDelay = TimeSpan.FromSeconds(
            config.IndexCheckStartDelaySeconds > 0 ? config.IndexCheckStartDelaySeconds : 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IServerDatabase>();

            // ManageIndexes maps straight onto createMissing: the check and its per-table logging are
            // identical either way, and the flag decides only whether the DDL is issued.
            var actions = await db.EnsureSyncIndexesAsync(_manageIndexes, stoppingToken);

            var pending = actions.Count(a => a.Outcome == SyncIndexOutcome.WouldCreate);

            _logger.LogInformation(
                "Index check finished (ManageIndexes={ManageIndexes}): {Created} created, " +
                "{Present} already present, {Pending} missing, {Skipped} skipped, {Failed} failed",
                _manageIndexes,
                actions.Count(a => a.Outcome == SyncIndexOutcome.Created),
                actions.Count(a => a.Outcome == SyncIndexOutcome.AlreadyPresent),
                pending,
                actions.Count(a => a.Outcome == SyncIndexOutcome.Skipped),
                actions.Count(a => a.Outcome == SyncIndexOutcome.Failed));

            if (!_manageIndexes && pending > 0)
            {
                // Say it once, loudly: an opted-out installation is choosing the slow pull, and this is
                // the line that tells whoever reads the log which index to apply by hand.
                _logger.LogWarning(
                    "Index management is disabled and {Pending} index(es) are missing; " +
                    "pull performance degrades as the unseen-session backlog grows", pending);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Index check cancelled during shutdown");
        }
        catch (Exception ex)
        {
            // A server that cannot index is slow, not broken. Never take the process down for this.
            _logger.LogError(ex, "Index check failed");
        }
    }
}
