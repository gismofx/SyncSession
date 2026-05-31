using SyncSession.Tools.Models;

namespace SyncSession.Tools.Assessment;

/// <summary>
/// Formats an AssessmentResult as a human-readable report file.
/// </summary>
public static class AssessmentReporter
{
    public static void WriteReport(AssessmentResult result, string outputPath)
    {
        using var w = new StreamWriter(outputPath);

        w.WriteLine("================================================================================");
        w.WriteLine("SYNCSYSTEM MIGRATION ASSESSMENT REPORT");
        w.WriteLine("================================================================================");
        w.WriteLine($"Generated : {result.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        w.WriteLine($"Mode      : {result.Mode}");
        w.WriteLine($"Assembly  : {result.AssemblyPath}");
        w.WriteLine();
        w.WriteLine($"SUMMARY: {result.PassCount} PASS  {result.WarnCount} WARN  {result.FailCount} FAIL");
        w.WriteLine("================================================================================");
        w.WriteLine();
        // ── Section 1: Assessed Tables ────────────────────────────────────
        if (result.Tables.Count > 0)
        {
            w.WriteLine("ASSESSED TABLES");
            w.WriteLine("--------------------------------------------------------------------------------");
            foreach (var table in result.Tables)
            {
                var icon = table.OverallStatus switch
                {
                    CheckStatus.Pass => "[ PASS ]",
                    CheckStatus.Warn => "[ WARN ]",
                    CheckStatus.Fail => "[ FAIL ]",
                    _ => "[  ?   ]"
                };

                var entityLabel = table.EntityClassName != null
                    ? $"{table.EntityClassName}{(table.IsMultiTenant ? " (IMultiTenantSyncEntity)" : "")}"
                    : "(no entity)";

                w.WriteLine($"{icon}  {table.TableName}  →  {entityLabel}");

                foreach (var check in table.Checks)
                {
                    var prefix = check.Status switch
                    {
                        CheckStatus.Pass => "         ✓",
                        CheckStatus.Warn => "         ⚠",
                        CheckStatus.Fail => "         ✗",
                        _ => "          "
                    };
                    w.WriteLine($"{prefix} {check.Message}");
                }
            }
            w.WriteLine();
        }

        // ── Section 2: Unmatched Entity Classes (real problem) ────────────
        if (result.UnmatchedEntityClasses.Count > 0)
        {
            w.WriteLine("ENTITY CLASSES WITH NO MATCHING DATABASE TABLE  ← action required");
            w.WriteLine("--------------------------------------------------------------------------------");
            foreach (var e in result.UnmatchedEntityClasses)
                w.WriteLine($"  [ FAIL ]  {e}  →  no table found in database");
            w.WriteLine();
        }

        // ── Section 3: Unmatched DB Tables (informational) ────────────────
        if (result.UnmatchedDbTables.Count > 0)
        {
            w.WriteLine("DATABASE TABLES WITH NO ENTITY CLASS  (informational — not assessed)");
            w.WriteLine("--------------------------------------------------------------------------------");
            foreach (var t in result.UnmatchedDbTables)
                w.WriteLine($"  {t}");
            w.WriteLine();
        }

        w.WriteLine("================================================================================");
        w.WriteLine($"Overall: {result.OverallStatus.ToString().ToUpperInvariant()}");
        if (result.OverallStatus == CheckStatus.Pass)
            w.WriteLine("All checks passed. Ready to proceed with migration scripts.");
        else if (result.OverallStatus == CheckStatus.Warn)
            w.WriteLine("Warnings found. Review before proceeding — no blockers detected.");
        else
            w.WriteLine("Failures found. Resolve FAIL items before running migration scripts.");
        w.WriteLine("================================================================================");
    }
}
