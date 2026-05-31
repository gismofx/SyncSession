using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using SyncSession.Core.Validation;

namespace SyncSession.Core.DTOs.Push;

/// <summary>
/// Request to push a batch of typed records during a sync session.
/// </summary>
/// <remarks>
/// Used by the client to serialize entities directly — no dictionary conversion required.
/// The server deserializes this as <see cref="PushBatchRequest"/> (non-generic),
/// where JSON properties become <c>Dictionary</c> keys. Server-side column filtering
/// via <c>ITableMetadataCache.GetValidPushColumns</c> strips client-only properties
/// (e.g., <c>IsDirty</c>, <c>SyncSessionId</c>, <c>ModifiedAtUtc</c>) before temp table insertion.
/// </remarks>
/// <typeparam name="T">Entity type implementing <see cref="SyncSystem.Core.Interfaces.ISyncEntity"/>.</typeparam>
public class PushBatchRequest<T>
{
    /// <summary>Gets or sets the session ID this batch belongs to.</summary>
    [RequiredGuid]
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the business table name records are destined for (e.g., <c>Customers</c>).</summary>
    [Required]
    public string TableName { get; set; } = string.Empty;

    /// <summary>Gets or sets the typed entity records to push.</summary>
    [Required]
    public IEnumerable<T> Records { get; set; } = null!;
}

/// <summary>
/// Non-generic push batch request used by the server controller for deserialization.
/// </summary>
/// <remarks>
/// JSON properties from <see cref="PushBatchRequest{T}"/> are deserialized as <c>JsonElement</c>
/// values in dictionaries. Server-side column filtering in <c>InsertBatchIntoTempTableAsync</c>
/// intersects dictionary keys with <c>ITableMetadataCache.GetValidPushColumns</c> to strip
/// client-only properties before temp table insertion.
/// </remarks>
public class PushBatchRequest
{
    /// <summary>Gets or sets the session ID this batch belongs to.</summary>
    [RequiredGuid]
    public Guid SessionId { get; set; }

    /// <summary>Gets or sets the business table name records are destined for (e.g., <c>Customers</c>).</summary>
    [Required]
    public string TableName { get; set; } = string.Empty;

    /// <summary>Gets or sets the deserialized records as key-value dictionaries.</summary>
    [Required]
    public IEnumerable<Dictionary<string, object?>> Records { get; set; } = null!;
}
