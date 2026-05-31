using System.Collections.Generic;

namespace SyncSession.Core.DTOs.Pull;

/// <summary>
/// Response containing a typed batch of records during a pull sync.
/// </summary>
/// <typeparam name="T">Entity type being synchronized.</typeparam>
public class PullBatchResponse<T>
{
    /// <summary>Gets or sets a value indicating whether the batch was retrieved successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the typed entity records returned in this batch.</summary>
    public List<T> Records { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether additional batches remain for this table.</summary>
    public bool HasMore { get; set; }

    /// <summary>Gets or sets the total number of records available for this table in the pull session.</summary>
    public int TotalRecords { get; set; }

    /// <summary>Gets or sets the error message when the request fails.</summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Non-generic pull batch response used by the server controller for serialization.
/// </summary>
/// <remarks>
/// Records are returned as key-value dictionaries. The client deserializes them into
/// typed entities via <c>EntityReflectionHelper.DictionaryToEntity&lt;T&gt;</c>.
/// </remarks>
public class PullBatchResponse
{
    /// <summary>Gets or sets a value indicating whether the batch was retrieved successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the records returned in this batch as key-value dictionaries.</summary>
    public List<Dictionary<string, object?>> Records { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether additional batches remain for this table.</summary>
    public bool HasMore { get; set; }

    /// <summary>Gets or sets the total number of records available for this table in the pull session.</summary>
    public int TotalRecords { get; set; }

    /// <summary>Gets or sets the error message when the request fails.</summary>
    /// <value>The error description, or <c>null</c> if the request succeeded.</value>
    public string? ErrorMessage { get; set; }
}
