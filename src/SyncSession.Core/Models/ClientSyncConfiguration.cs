using System.Collections.Generic;
using System;
using System.Linq;

namespace SyncSession.Core.Models;

/// <summary>
/// Configuration for the client-side sync engine.
/// Controls push/pull batch sizes and polling behavior when waiting for server commit.
/// </summary>
public class ClientSyncConfiguration : SyncConfiguration
{
    private int _batchSize = 1000;

    /// <summary>
    /// Optional tenant ID for multi-tenant deployments.
    /// When set, all push/pull operations are scoped to this tenant.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Optional human-readable display name for the current user (38l).
    /// Sent to the server on push/pull/seed requests for audit logging on SyncSessions.
    /// Set from application-level user profile data (e.g., ApplicationUser.DisplayName).
    /// </summary>
    public string? UserDisplayName { get; set; }

    /// <summary>
    /// What to do when a multi-tenant sync runs against a local database that has no persisted
    /// tenant binding (default: <see cref="TenantBindingPolicy.Reject"/>). Set to
    /// <see cref="TenantBindingPolicy.Adopt"/> to bind the configured tenant on first sync when
    /// migrating a database that was populated before tenant binding existed. A binding that is
    /// present but for a <em>different</em> tenant is always rejected, regardless of this setting.
    /// </summary>
    public TenantBindingPolicy TenantBindingPolicy { get; set; } = TenantBindingPolicy.Reject;

    /// <summary>
    /// Number of records sent to the server per push batch request (default: 1000).
    /// </summary>
    public int PushBatchSize { get; set; } = 1000;

    /// <summary>
    /// Number of records requested from the server per pull batch GET (default: 1000).
    /// Passed as the <c>?limit=</c> query parameter on each pull batch request.
    /// </summary>
    public int PullBatchSize { get; set; } = 1000;

    /// <summary>
    /// Convenience setter — assigns the same value to both <see cref="PushBatchSize"/>
    /// and <see cref="PullBatchSize"/>.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            _batchSize = value;
            PushBatchSize = value;
            PullBatchSize = value;
        }
    }

    /// <summary>
    /// Interval between push-status polls in milliseconds (default: 1000).
    /// Controls how frequently the client checks whether the server has committed its push session.
    /// </summary>
    public int PushStatusPollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum time in seconds to wait for a push session to reach <c>Committed</c> status
    /// before timing out (default: 300).
    /// Should account for queue backlog — a loaded server with many sessions ahead may take
    /// several minutes before processing begins.
    /// </summary>
    public int PushStatusTimeoutSeconds { get; set; } = 300;

    /// <inheritdoc/>
    public override void Validate()
    {
        var errors = new List<string>();

        if (PushBatchSize < 1 || PushBatchSize > 10_000)
            errors.Add($"PushBatchSize must be between 1 and 10,000 (was {PushBatchSize}).");

        if (PullBatchSize < 1 || PullBatchSize > 10_000)
            errors.Add($"PullBatchSize must be between 1 and 10,000 (was {PullBatchSize}).");

        if (PushStatusPollIntervalMs < 100)
            errors.Add($"PushStatusPollIntervalMs must be >= 100 (was {PushStatusPollIntervalMs}).");

        if (PushStatusTimeoutSeconds < 1)
            errors.Add($"PushStatusTimeoutSeconds must be >= 1 (was {PushStatusTimeoutSeconds}).");

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"ClientSyncConfiguration is invalid:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors.Select(e => $"  - {e}")));
    }
}
