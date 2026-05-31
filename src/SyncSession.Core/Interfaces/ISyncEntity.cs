using System;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// public interface for sync infrastructure properties that are auto-managed by the sync system.
/// These properties are excluded from business operations and dynamic parameter generation.
/// </summary>
public interface ISyncInfrastructure
{
    /// <summary>
    /// Client-side: Whether this record has local changes not yet synced.
    /// Server implementations should ignore this property.
    /// </summary>
    bool IsDirty { get; set; }

    /// <summary>
    /// Server-side: When this record was last modified (UTC).
    /// Server generates this automatically. Client uses for conflict detection.
    /// </summary>
    DateTime? ModifiedAtUtc { get; set; }
    
    /// <summary>
    /// Server-side: The session ID that last modified this record.
    /// Server assigns during commit. Used for session-based change tracking.
    /// </summary>
    Guid? SyncSessionId { get; set; }
}

/// <summary>
/// Base interface for all syncable entities.
/// Entities implementing this interface can be synchronized using the generic sync engine.
/// Composed of infrastructure properties (auto-managed) and business properties (preserved during sync).
/// </summary>
public interface ISyncEntity : ISyncInfrastructure
{
    /// <summary>
    /// Unique identifier for the record (GUID stored as string for database compatibility).
    /// </summary>
    Guid Id { get; set; }

    /// <summary>
    /// Indicates whether this entity has been soft-deleted. Deleted entities are retained 
    /// in the database for sync propagation but excluded from normal business queries.
    /// The sync system uses this flag to propagate deletions across clients without 
    /// losing the entity record needed for conflict resolution and audit trails.
    /// Business property: preserved during sync operations.
    /// </summary>
    bool IsDeleted { get; set; }
    
    /// <summary>
    /// The user ID who last modified this record.
    /// Used for multi-user audit tracking and conflict resolution.
    /// Use "System" or "Server" for system-generated changes.
    /// Business property: preserved during sync operations.
    /// </summary>
    string ModifiedByUserId { get; set; }
}
