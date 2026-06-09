using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SyncSession.Core.DTOs.Pull;
using SyncSession.Core.DTOs.Push;
using SyncSession.Core.Models;

namespace SyncSession.Core.Interfaces;

/// <summary>
/// Interface for client-side synchronization operations.
/// Represents a client that communicates with a SyncSystem server via HTTP API.
/// </summary>
/// <remarks>
/// This interface defines the client-side contract for synchronization operations.
/// Implementations communicate with the server using REST API endpoints.
/// The typical implementation is HttpServerClient which uses HTTP requests.
/// </remarks>
public interface ISyncServerApi
{
    #region Push Operations
    
    /// <summary>
    /// Begins a push session to upload local changes to the server.
    /// </summary>
    /// <param name="tables">List of tables to sync with estimated record counts.</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments.</param>
    /// <param name="userDisplayName">Optional display name of the initiating user for audit purposes.</param>
    /// <returns>Session ID for the push operation.</returns>
    /// <remarks>
    /// This initiates a push session on the server.
    /// The server allocates temp tables and returns a session ID.
    /// Call PushBatchAsync to upload data, then CompletePushAsync to finalize.
    /// </remarks>
    Task<Guid> BeginPushAsync(List<TableSyncInfo> tables, Guid? tenantId = null, string? userDisplayName = null);
    
    /// <summary>
    /// Uploads a batch of records to the server for a specific table.
    /// </summary>
    /// <typeparam name="T">Entity type implementing ISyncEntity.</typeparam>
    /// <param name="sessionId">Session ID from BeginPushAsync.</param>
    /// <param name="records">Records to upload.</param>
    /// <remarks>
    /// Can be called multiple times for the same table to upload in batches.
    /// Records are staged in temp tables on the server.
    /// Table name is extracted from the [SyncTable] attribute on type T.
    /// </remarks>
    Task PushBatchAsync<T>(Guid sessionId, IEnumerable<T> records) where T : ISyncEntity;
    
    /// <summary>
    /// Marks a table as fully uploaded and verifies the record count against what the server received.
    /// </summary>
    /// <param name="sessionId">Session ID this table belongs to.</param>
    /// <param name="tableName">Business table name (e.g., <c>Customers</c>).</param>
    /// <param name="totalRecordsSent">Total records sent across all batches for this table.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the server's actual record count does not match <paramref name="totalRecordsSent"/>.
    /// The session remains in Staging status and will be swept by the cleanup service.
    /// Do not call <see cref="CompletePushAsync"/> after this exception.
    /// </exception>
    /// <remarks>
    /// Must be called after all batches for a table have been uploaded via <see cref="PushBatchAsync{T}"/>,
    /// and before moving to the next table or calling <see cref="CompletePushAsync"/>.
    /// A count mismatch indicates data loss in transit and is treated as a hard failure.
    /// </remarks>
    Task CompleteTableAsync(Guid sessionId, string tableName, int totalRecordsSent);

    /// <summary>
    /// Completes a push session and queues it for server-side processing.
    /// </summary>
    /// <param name="sessionId">Session ID to complete.</param>
    /// <remarks>
    /// Marks the push session as ready for background processing.
    /// Server will asynchronously process staged records and assign a sync version.
    /// Client should poll for completion or wait before starting a pull.
    /// </remarks>
    Task CompletePushAsync(Guid sessionId);

    /// <summary>
    /// Gets the current status of a push session for commit polling.
    /// </summary>
    /// <param name="sessionId">Session ID to query.</param>
    /// <returns>Session status including Status, SyncVersion (when Committed), and ErrorMessage (when Failed).</returns>
    /// <remarks>
    /// Call after CompletePushAsync to wait for server-side background processing.
    /// Poll until Status is "Committed" or "Failed". Typical processing is 1–5 seconds.
    /// </remarks>
    Task<PushSessionStatusResponse> GetPushStatusAsync(Guid sessionId);
    
    #endregion
    
    #region Pull Operations
    
    /// <summary>
    /// Begins a pull session to download server changes.
    /// </summary>
    /// <param name="tableNames">List of table names to pull.</param>
    /// <param name="tenantId">Optional tenant ID for multi-tenant deployments.</param>
    /// <param name="userDisplayName">Optional display name of the initiating user for audit purposes.</param>
    /// <returns>Pull session begin response with table metadata and record counts.</returns>
    /// <remarks>
    /// Server identifies which sessions this client hasn't processed yet.
    /// Creates temp pull tables with records from those sessions.
    /// Call PullBatchAsync to download data, then CompletePullAsync to finalize.
    /// </remarks>
    Task<PullSessionBeginResponse> BeginPullAsync(List<string> tableNames, Guid? tenantId = null, string? userDisplayName = null);
    
    /// <summary>
    /// Downloads a single batch of records from the server for a specific table.
    /// </summary>
    /// <typeparam name="T">Entity type implementing ISyncEntity.</typeparam>
    /// <param name="pullSessionId">Pull session ID from BeginPullAsync.</param>
    /// <param name="offset">Starting position for pagination (0-based).</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <returns>Tuple containing records, whether more records exist, and total record count.</returns>
    /// <remarks>
    /// Use this for memory-efficient streaming - fetch and process batches incrementally.
    /// Table name is extracted from the [SyncTable] attribute on type T.
    /// </remarks>
    Task<(IEnumerable<T> Records, bool HasMore, int TotalRecords)> PullBatchAsync<T>(
        Guid pullSessionId,
        int offset,
        int limit) where T : ISyncEntity;
    
    
    /// <summary>
    /// Completes a pull session and marks sessions as processed.
    /// </summary>
    /// <param name="pullSessionId">Pull session ID to complete.</param>
    /// <param name="processedSessionIds">Session IDs that were successfully applied.</param>
    /// <remarks>
    /// Records which sessions this client has processed in ClientProcessedSessions.
    /// This prevents re-pulling the same data on the next sync.
    /// Server cleans up temp pull tables after completion.
    /// </remarks>
    Task CompletePullAsync(Guid pullSessionId, List<Guid> processedSessionIds);
    
    #endregion
}
