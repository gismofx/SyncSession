namespace SyncSession.Core.Models;

/// <summary>
/// Status string constants for seed snapshot rows.
/// These describe the state of a seed operation, distinct from session status values in
/// <see cref="SyncSession.Core.Constants.SyncConstants"/>.
/// </summary>
public static class SeedSnapshotStatus
{
    /// <summary>Seed is in progress; snapshot tables are being streamed to the client.</summary>
    public const string Active = "Active";
    /// <summary>Seed completed successfully; snapshot tables have been dropped.</summary>
    public const string Complete = "Complete";
    /// <summary>Seed failed or was interrupted; snapshot tables may need cleanup.</summary>
    public const string Failed = "Failed";
}
