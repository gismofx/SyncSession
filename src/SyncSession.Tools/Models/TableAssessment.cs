namespace SyncSession.Tools.Models;

public class TableAssessment
{
    public string TableName { get; set; } = string.Empty;
    public bool ExistsInDatabase { get; set; }
    public bool HasMatchingEntity { get; set; }
    public string? EntityClassName { get; set; }
    public bool IsMultiTenant { get; set; }
    public string PrimaryKeyType { get; set; } = string.Empty;
    public bool PrimaryKeyIsUuid { get; set; }
    public List<string> ExistingColumns { get; set; } = new();
    public List<string> MissingSyncColumns { get; set; } = new();
    public List<string> MultiTenantCandidateColumns { get; set; } = new();
    public List<CheckResult> Checks { get; set; } = new();

    public CheckStatus OverallStatus => Checks.Count == 0
        ? CheckStatus.Pass
        : Checks.Max(c => c.Status);
}
