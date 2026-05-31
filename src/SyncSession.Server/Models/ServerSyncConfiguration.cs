using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using SyncSession.Core.Models;

namespace SyncSession.Server.Models;

/// <summary>
/// Configuration for the server-side sync infrastructure.
/// Controls temp table strategy, session lifecycle, background service intervals,
/// and transaction behavior.
/// </summary>
public class ServerSyncConfiguration : SyncConfiguration
{
    /// <summary>
    /// Record count threshold above which a dedicated temp table is used for push operations
    /// instead of the shared temp table (default: 10000).
    /// </summary>
    public int PushSharedTableThreshold { get; set; } = 10000;

    /// <summary>
    /// Record count threshold above which a dedicated temp table is used for pull operations
    /// instead of the shared temp table (default: 10000).
    /// </summary>
    public int PullSharedTableThreshold { get; set; } = 10000;

    /// <summary>
    /// Minutes of inactivity before a push or pull session is considered stale and failed
    /// by the cleanup service (default: 30).
    /// </summary>
    public int SessionActivityTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Interval in minutes between cleanup service cycles (default: 60).
    /// Controls how frequently stale sessions, orphaned temp tables, and shared table rows
    /// are purged.
    /// </summary>
    public int SharedTableCleanupIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Age in days before orphaned dedicated temp tables are dropped (default: 1).
    /// </summary>
    public int OrphanedTableCleanupDays { get; set; } = 1;

    /// <summary>
    /// Age in days before completed or failed sessions are permanently purged.
    /// <para>
    /// Default <c>0</c> = never purge. Retaining all sessions prevents the offline-client
    /// data gap: a client offline longer than the retention window would otherwise have its
    /// unprocessed sessions purged before it returns, silently missing those records. With
    /// purge disabled, <c>SyncSessions</c> and <c>ClientProcessedSessions</c> grow over time
    /// but the pull anti-join (indexed on the composite PK) stays correct and fast.
    /// </para>
    /// <para>
    /// Set &gt; 0 to opt into time-based purge. The operator then accepts gap risk for clients
    /// that stay offline longer than the configured window. Range when enabled: 1–3650.
    /// </para>
    /// </summary>
    public int SessionRetentionDays { get; set; } = 0;

    /// <summary>
    /// Interval in seconds between queue processor polls for ready sessions (default: 5).
    /// </summary>
    public int QueuePollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Hours of inactivity before an <c>Active</c> seed snapshot row (and its snapshot tables)
    /// is treated as orphaned and dropped by <c>TempTableCleanupService</c> (default: 4).
    /// Increase this value for very large datasets that take longer than 4 hours to stream.
    /// </summary>
    public int SeedSnapshotOrphanHours { get; set; } = 4;

    /// <summary>
    /// Transaction isolation level for session processing (default: Serializable).
    /// Serializable prevents phantom reads during concurrent push operations, guaranteeing
    /// that pull operations see either the old or the new state — never a partial write.
    /// </summary>
    public IsolationLevel TransactionIsolationLevel { get; set; } = IsolationLevel.Serializable;

    /// <inheritdoc/>
    public override void Validate()
    {
        var errors = new List<string>();

        if (PushSharedTableThreshold < 1)
            errors.Add($"PushSharedTableThreshold must be >= 1 (was {PushSharedTableThreshold}).");

        if (PullSharedTableThreshold < 1)
            errors.Add($"PullSharedTableThreshold must be >= 1 (was {PullSharedTableThreshold}).");

        if (SessionActivityTimeoutMinutes < 1 || SessionActivityTimeoutMinutes > 1440)
            errors.Add($"SessionActivityTimeoutMinutes must be between 1 and 1440 (was {SessionActivityTimeoutMinutes}).");

        if (SharedTableCleanupIntervalMinutes < 1 || SharedTableCleanupIntervalMinutes > 1440)
            errors.Add($"SharedTableCleanupIntervalMinutes must be between 1 and 1440 (was {SharedTableCleanupIntervalMinutes}).");

        if (OrphanedTableCleanupDays < 1 || OrphanedTableCleanupDays > 365)
            errors.Add($"OrphanedTableCleanupDays must be between 1 and 365 (was {OrphanedTableCleanupDays}).");

        if (SessionRetentionDays < 0 || SessionRetentionDays > 3650)
            errors.Add($"SessionRetentionDays must be 0 (never purge) or between 1 and 3650 (was {SessionRetentionDays}).");

        if (QueuePollIntervalSeconds < 1 || QueuePollIntervalSeconds > 300)
            errors.Add($"QueuePollIntervalSeconds must be between 1 and 300 (was {QueuePollIntervalSeconds}).");

        if (SeedSnapshotOrphanHours < 1 || SeedSnapshotOrphanHours > 168)
            errors.Add($"SeedSnapshotOrphanHours must be between 1 and 168 (was {SeedSnapshotOrphanHours}).");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"ServerSyncConfiguration is invalid:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Select(e => $"  - {e}")));
    }
}
