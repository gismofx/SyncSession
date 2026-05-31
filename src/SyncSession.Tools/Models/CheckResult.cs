namespace SyncSession.Tools.Models;

public enum CheckStatus { Pass, Warn, Fail }

public record CheckResult(CheckStatus Status, string Message);
