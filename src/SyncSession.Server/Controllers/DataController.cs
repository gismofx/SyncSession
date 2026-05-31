using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Utilities;
using SyncSession.Server.Filters;

namespace SyncSession.Server.Controllers;

/// <summary>
/// REST endpoints for direct data access (reads and writes) outside the sync protocol.
/// Opt-in via <c>MapSyncDataEndpoints()</c>.
/// </summary>
[ApiController]
[Route("api/v1/data")]
[Produces("application/json")]
[Authorize(Policy = "SyncAccess")]
[ServiceFilter(typeof(DataEndpointsEnabledFilter))]
public class DataController : ControllerBase
{
    private readonly IServerDatabase _database;
    private readonly IDirectWriteService _writeService;
    private readonly ITableMetadataCache _metadataCache;
    private readonly ILogger<DataController> _logger;
    private readonly IWebHostEnvironment _environment;

    public DataController(
        IServerDatabase database,
        IDirectWriteService writeService,
        ITableMetadataCache metadataCache,
        ILogger<DataController> logger,
        IWebHostEnvironment environment)
    {
        _database = database;
        _writeService = writeService;
        _metadataCache = metadataCache;
        _logger = logger;
        _environment = environment;
    }

    // ── Read Endpoints ──────────────────────────────────────────────────

    /// <summary>
    /// Retrieves a single record by ID.
    /// </summary>
    [HttpGet("{table}/{id:guid}")]
    [ProducesResponseType(typeof(Dictionary<string, object?>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string table, Guid id)
    {
        try
        {
            if (!ValidateTable(table, out var errorResult))
                return errorResult!;

            var tenantId = GetTenantId(table);
            var record = await _database.GetByIdAsync(table, id, tenantId);

            if (record == null)
                return NotFound(new { error = $"Record '{id}' not found in table '{table}'." });

            return Ok(record);
        }
        catch (Exception ex)
        {
            return HandleError(ex, "GetById");
        }
    }

    /// <summary>
    /// Executes a filtered, paginated query against a table.
    /// </summary>
    [HttpPost("{table}/query")]
    [ProducesResponseType(typeof(DataQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Query(string table, [FromBody] DataQuery query)
    {
        try
        {
            if (!ValidateTable(table, out var errorResult))
                return errorResult!;

            // Unwrap JsonElement values in filters — System.Text.Json deserializes
            // object? properties as JsonElement, which Dapper can't bind as parameters.
            if (query.Filters != null)
            {
                foreach (var filter in query.Filters)
                {
                    if (filter.Value is JsonElement je)
                        filter.Value = EntityReflectionHelper.UnwrapJsonElement(je);
                }
            }

            var tenantId = GetTenantId(table);
            var result = await _database.QueryAsync(table, query, tenantId);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return HandleError(ex, "Query");
        }
    }

    // ── Write Endpoints ─────────────────────────────────────────────────

    /// <summary>
    /// Batch write records across multiple tables in a single transaction.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(DirectWriteBatchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> WriteBatch([FromBody] JsonElement body)
    {
        try
        {
            var userId = GetUserId();

            if (!body.TryGetProperty("records", out var recordsElement) || recordsElement.ValueKind != JsonValueKind.Object)
                return BadRequest(new { error = "Request body must contain a 'records' object keyed by table name." });

            var tableRecords = DeserializeBatchRecords(recordsElement);
            if (tableRecords.Count == 0)
                return BadRequest(new { error = "No records provided." });

            var tenantId = GetTenantIdString();
            var result = await _writeService.WriteBatchAsync(tableRecords, userId, tenantId);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return HandleError(ex, "WriteBatch");
        }
    }

    /// <summary>
    /// Write or update a single record.
    /// </summary>
    [HttpPost("{table}")]
    [ProducesResponseType(typeof(DirectWriteBatchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> WriteSingle(string table, [FromBody] JsonElement body)
    {
        try
        {
            if (!ValidateTable(table, out var errorResult))
                return errorResult!;

            var userId = GetUserId();
            var entityType = _metadataCache.GetEntityType(table);
            var entity = JsonSerializer.Deserialize(body.GetRawText(), entityType, JsonOptions);

            if (entity == null)
                return BadRequest(new { error = "Failed to deserialize request body to entity." });

            var tableRecords = new Dictionary<string, List<object>>
            {
                [table] = new() { entity }
            };

            var tenantId = GetTenantIdString();
            var result = await _writeService.WriteBatchAsync(tableRecords, userId, tenantId);
            return Ok(result);
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return HandleError(ex, "WriteSingle");
        }
    }

    /// <summary>
    /// Soft delete a record by ID.
    /// </summary>
    [HttpDelete("{table}/{id:guid}")]
    [ProducesResponseType(typeof(DirectWriteResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string table, Guid id)
    {
        try
        {
            if (!ValidateTable(table, out var errorResult))
                return errorResult!;

            var userId = GetUserId();
            var tenantId = GetTenantIdString();
            var result = await _writeService.DeleteAsync(table, id, userId, tenantId);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return HandleError(ex, "Delete");
        }
    }

    // ── Private Helpers ─────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Validates that the table name maps to a registered sync table.
    /// </summary>
    private bool ValidateTable(string table, out IActionResult? errorResult)
    {
        errorResult = null;
        try
        {
            _ = _metadataCache.GetEntityType(table);
            return true;
        }
        catch (InvalidOperationException)
        {
            errorResult = NotFound(new { error = $"Unknown table: '{table}'." });
            return false;
        }
    }

    /// <summary>
    /// Extracts the user ID from the authenticated principal.
    /// Falls back to "anonymous" for unauthenticated requests (shouldn't happen with [Authorize]).
    /// </summary>
    private string GetUserId()
        => User.Identity?.Name ?? "anonymous";

    /// <summary>
    /// Extracts the tenant ID as a string from JWT claims (null if not multi-tenant).
    /// </summary>
    private string? GetTenantIdString()
        => User.FindFirst("TenantId")?.Value;

    /// <summary>
    /// Returns parsed tenant GUID if the table is multi-tenant, otherwise null.
    /// </summary>
    private Guid? GetTenantId(string table)
    {
        if (!_metadataCache.IsMultiTenant(table))
            return null;

        var tenantClaim = User.FindFirst("TenantId")?.Value;
        if (string.IsNullOrWhiteSpace(tenantClaim))
            return null;

        return Guid.TryParse(tenantClaim, out var tenantId) ? tenantId : null;
    }

    /// <summary>
    /// Deserializes the batch records JSON element into the expected dictionary format.
    /// Keys are table names, values are lists of deserialized entity objects.
    /// </summary>
    private Dictionary<string, List<object>> DeserializeBatchRecords(JsonElement recordsElement)
    {
        var result = new Dictionary<string, List<object>>();

        foreach (var tableProp in recordsElement.EnumerateObject())
        {
            var tableName = tableProp.Name;
            Type entityType;
            try
            {
                entityType = _metadataCache.GetEntityType(tableName);
            }
            catch (InvalidOperationException)
            {
                throw new ArgumentException($"Unknown table name: '{tableName}'.");
            }

            // Resolve canonical table name (JSON keys may be camelCased by client serializer)
            var canonicalName = TableNameResolver.GetTableName(entityType);

            if (tableProp.Value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"Table '{canonicalName}' records must be a JSON array.");

            var records = new List<object>();
            foreach (var element in tableProp.Value.EnumerateArray())
            {
                var entity = JsonSerializer.Deserialize(element.GetRawText(), entityType, JsonOptions);
                if (entity != null)
                    records.Add(entity);
            }

            if (records.Count > 0)
                result[canonicalName] = records;
        }

        return result;
    }

    /// <summary>
    /// Standardized error response with dev-mode stack trace.
    /// </summary>
    private IActionResult HandleError(Exception ex, string operation)
    {
        _logger.LogError(ex, "DataController.{Operation} failed", operation);

        var message = _environment.IsDevelopment()
            ? $"Internal server error: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}"
            : "Internal server error";

        return StatusCode(500, new { error = message });
    }
}
