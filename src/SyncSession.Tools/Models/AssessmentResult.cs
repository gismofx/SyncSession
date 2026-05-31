namespace SyncSession.Tools.Models;

public class AssessmentResult
{
    public string ConnectionString { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public string Mode { get; set; } = "clone";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public List<TableAssessment> Tables { get; set; } = new();
    public List<string> UnmatchedDbTables { get; set; } = new();
    public List<string> UnmatchedEntityClasses { get; set; } = new();

    public CheckStatus OverallStatus
    {
        get
        {
            var tableStatus = Tables.Count == 0 ? CheckStatus.Pass : Tables.Max(t => t.OverallStatus);
            var entityStatus = UnmatchedEntityClasses.Count > 0 ? CheckStatus.Fail : CheckStatus.Pass;
            return (CheckStatus)Math.Max((int)tableStatus, (int)entityStatus);
        }
    }

    public int PassCount => Tables.Count(t => t.OverallStatus == CheckStatus.Pass);
    public int WarnCount => Tables.Count(t => t.OverallStatus == CheckStatus.Warn);
    public int FailCount => Tables.Count(t => t.OverallStatus == CheckStatus.Fail);
}
