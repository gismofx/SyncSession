using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SyncSession.Core.Models;

/// <summary>
/// Represents a single line in a SyncSystem NDJSON seed stream.
/// Each line is a JSON object discriminated by <see cref="Type"/>.
/// </summary>
/// <remarks>
/// Stream line sequence:
/// <list type="number">
///   <item><c>begin</c> — stream header with tenant, anchor timestamp, and table list</item>
///   <item><c>table</c> — table header with advisory row count</item>
///   <item><c>row</c> — one data record (repeated per row)</item>
///   <item><c>table_end</c> — end of table marker</item>
///   <item>Repeat <c>table</c>/<c>row</c>/<c>table_end</c> for each registered table</item>
///   <item><c>end</c> — stream footer with anchor for first incremental pull</item>
/// </list>
/// Null properties are omitted from JSON serialization.
/// </remarks>
public sealed class SeedLine
{
    /// <summary>Discriminator: begin | table | row | table_end | end.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    // ── begin ────────────────────────────────────────────────────────────

    /// <summary>Tenant ID being seeded. Present on <c>begin</c> lines.</summary>
    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }

    /// <summary>
    /// UTC timestamp captured before the first query.
    /// Present on <c>begin</c> lines. Same value as <see cref="Anchor"/> on the <c>end</c> line.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? GeneratedAt { get; init; }

    /// <summary>Ordered list of table names that will follow. Present on <c>begin</c> lines.</summary>
    [JsonPropertyName("tables")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Tables { get; init; }

    // ── table / row / table_end ───────────────────────────────────────────

    /// <summary>Table name. Present on <c>table</c>, <c>row</c>, and <c>table_end</c> lines.</summary>
    [JsonPropertyName("table")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Table { get; init; }

    /// <summary>
    /// Advisory total row count for the table. Present on <c>table</c> lines.
    /// <c>-1</c> means count was unavailable (skipped for performance on large tables).
    /// </summary>
    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }

    /// <summary>Record data as column → value pairs. Present on <c>row</c> lines.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Data { get; init; }

    /// <summary>
    /// Array of records. Present on <c>rows</c> lines (multi-row bundle).
    /// Reduces ReadLineAsync call count by bundling N rows per line.
    /// </summary>
    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Dictionary<string, object?>>? Rows { get; init; }

    /// <summary>
    /// Raw NDJSON line string as received from the stream.
    /// Set by <see cref="SyncSystem.Client.Http.HttpSeedServerApi"/> after deserialization.
    /// Used by <see cref="SyncSystem.Client.Seeding.IRawSeedDatabaseWriter"/> to skip
    /// Dictionary re-serialization in the hot path.
    /// Not serialized — populated at runtime only.
    /// </summary>
    [JsonIgnore]
    public string? RawLine { get; set; }

    // ── end ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UTC timestamp to use as the <c>since</c> parameter for the first incremental pull.
    /// Captured before the first query so no records written during streaming are missed.
    /// Present on <c>end</c> lines.
    /// </summary>
    [JsonPropertyName("anchor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Anchor { get; init; }

    // ── Factory methods ───────────────────────────────────────────────────

    /// <summary>Creates a <c>begin</c> line.</summary>
    public static SeedLine Begin(Guid tenantId, DateTime generatedAt, List<string> tables) => new()
    {
        Type = "begin",
        TenantId = tenantId.ToString(),
        GeneratedAt = generatedAt,
        Tables = tables
    };

    /// <summary>Creates a <c>table</c> header line. Pass <c>-1</c> for <paramref name="total"/> if unavailable.</summary>
    public static SeedLine TableStart(string table, int total) => new()
    {
        Type = "table",
        Table = table,
        Total = total
    };

    /// <summary>Creates a <c>row</c> line for a single data record.</summary>
    public static SeedLine Row(string table, Dictionary<string, object?> data) => new()
    {
        Type = "row",
        Table = table,
        Data = data
    };

    /// <summary>Creates a <c>rows</c> line bundling multiple records into one NDJSON line.
    /// Reduces client ReadLineAsync call count by N×.</summary>
    public static SeedLine Bundle(string table, List<Dictionary<string, object?>> rows) => new()
    {
        Type = "rows",
        Table = table,
        Rows = rows
    };

    /// <summary>Creates a <c>table_end</c> line.</summary>
    public static SeedLine TableEnd(string table) => new() { Type = "table_end", Table = table };

    /// <summary>Creates an <c>end</c> line with the anchor timestamp for incremental pull.</summary>
    public static SeedLine End(DateTime anchor) => new() { Type = "end", Anchor = anchor };

    /// <summary>
    /// Creates a <c>preparing</c> heartbeat line emitted during snapshot table creation.
    /// Clients should display this as progress feedback. Unknown line types are ignored by <c>SeedClient</c>.
    /// </summary>
    public static SeedLine Preparing(string tableName) => new()
    {
        Type = "preparing",
        Table = tableName
    };
}
