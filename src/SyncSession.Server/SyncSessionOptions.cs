using System.Reflection;
using SyncSession.Server.Models;

namespace SyncSession.Server;

/// <summary>
/// Configuration options for the SyncSystem server library.
/// Passed to <see cref="Extensions.SyncSessionExtensions.AddSyncSystem"/> to configure
/// all sync infrastructure including database, services, and background workers.
/// </summary>
public class SyncSessionOptions
{
    /// <summary>
    /// Database connection string. Required.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Database provider. Supported values: <c>MySQL</c>, <c>MariaDB</c>, <c>SQLite</c>.
    /// Default: <c>MySQL</c>.
    /// </summary>
    public string DatabaseProvider { get; set; } = "MySQL";

    /// <summary>
    /// Assembly containing entity types decorated with <c>[SyncTable]</c>.
    /// Used for automatic table discovery and registration. Required.
    /// </summary>
    public Assembly EntityAssembly { get; set; } = null!;

    /// <summary>
    /// Controls whether the <c>SyncAccess</c> authorization policy is enforced on all sync endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>false</c> (local dev / trusted networks): <see cref="Extensions.SyncSessionExtensions.AddSyncSystem"/>
    /// registers an allow-all <c>SyncAccess</c> policy automatically — no authentication middleware required.
    /// Set <c>"RequireAuthorization": false</c> in <c>appsettings.Development.json</c>.
    /// </para>
    /// <para>
    /// When <c>true</c> (production): the library does <b>not</b> register the policy.
    /// The consumer must configure authentication and define the <c>SyncAccess</c> policy:
    /// <code>
    /// // Example — JWT Bearer
    /// builder.Services
    ///     .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    ///     .AddJwtBearer(o => { o.Authority = "https://your-idp/"; o.Audience = "syncsystem-api"; });
    ///
    /// builder.Services.AddAuthorization(o =>
    ///     o.AddPolicy("SyncAccess", p => p.RequireAuthenticatedUser()));
    ///
    /// // In the middleware pipeline (before MapSyncEndpoints):
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// </code>
    /// Any ASP.NET Core-compatible auth scheme works: API key, certificate, custom handler, etc.
    /// </para>
    /// </remarks>
    /// <value>Default: <c>true</c>.</value>
    public bool RequireAuthorization { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, automatically runs pending SyncSystem infrastructure migrations
    /// on startup via DbUp before schema validation occurs.
    /// Safe to leave enabled permanently — DbUp tracks applied scripts and is idempotent.
    /// Default: <c>false</c>. Set <c>true</c> to replace manual <c>DatabaseMigrator</c> calls.
    /// </summary>
    public bool AutoMigrate { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, validates that all registered <c>[SyncTable]</c> types have the
    /// required <c>ISyncEntity</c> columns present in the actual database tables on startup.
    /// Default: <c>true</c>. Implemented in Session 27d.
    /// </summary>
    public bool ValidateSchema { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, registers Swagger/OpenAPI documentation services.
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableSwagger { get; set; } = true;

    /// <summary>
    /// Server-specific sync configuration: thresholds, timeouts, cleanup intervals,
    /// queue poll interval, and session retention. Defaults are production-ready.
    /// </summary>
    public ServerSyncConfiguration Sync { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, data query and direct write endpoints (<c>/api/v1/data/...</c>) are active.
    /// Set automatically by <see cref="Extensions.SyncSessionExtensions.MapSyncDataEndpoints"/>.
    /// Default: <c>false</c>.
    /// </summary>
    public bool EnableDataEndpoints { get; set; } = false;

    /// <summary>
    /// JWT claim type used to extract a human-readable display name for SyncSessions audit columns.
    /// Defaults to <c>ClaimTypes.Name</c> ("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name").
    /// Common alternatives: <c>"name"</c>, <c>"preferred_username"</c>, <c>"email"</c>.
    /// Set to <c>null</c> or empty to disable display name extraction.
    /// </summary>
    public string DisplayNameClaimType { get; set; } =
        System.Security.Claims.ClaimTypes.Name;
}
