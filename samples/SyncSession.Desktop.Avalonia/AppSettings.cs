using System;

namespace SyncSession.Samples.Desktop;

/// <summary>
/// Immutable application configuration values resolved at startup.
/// </summary>
public sealed record AppSettings(string ServerUrl, Guid? TenantId, string UserId);
