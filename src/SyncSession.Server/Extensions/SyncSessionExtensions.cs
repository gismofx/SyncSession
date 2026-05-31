using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using SyncSession.Core.Interfaces;
using SyncSession.Core.Models;
using SyncSession.Core.Services;
using SyncSession.Core.Utilities;
using SyncSession.Server.BackgroundServices;
using SyncSession.Server.Database;
using SyncSession.Server.Filters;
using SyncSession.Server.Models;
using SyncSession.Server.Services;

namespace SyncSession.Server.Extensions;

/// <summary>
/// Extension methods for registering and mapping SyncSystem server infrastructure.
/// </summary>
public static class SyncSessionExtensions
{
    /// <summary>
    /// Registers all SyncSystem services: database, sync services, background workers,
    /// health checks, and optional Swagger. Call from <c>Program.cs</c> or a host startup class.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure <see cref="SyncSessionOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddSyncSystem(
        this IServiceCollection services,
        Action<SyncSessionOptions> configure)
    {
        var options = new SyncSessionOptions();
        configure(options);
        services.AddSingleton(options); // available for MapSyncEndpoints swagger check
        return services.AddSyncSystem(options);
    }

    /// <summary>
    /// Registers all SyncSystem services using a pre-built <see cref="SyncSessionOptions"/> instance.
    /// </summary>
    public static IServiceCollection AddSyncSystem(
        this IServiceCollection services,
        SyncSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("SyncSessionOptions.ConnectionString is required.");
        if (options.EntityAssembly is null)
            throw new InvalidOperationException("SyncSessionOptions.EntityAssembly is required.");

        // ── Configuration ────────────────────────────────────────────────────
        var syncConfig = options.Sync;
        syncConfig.DiscoverAndRegisterTables(options.EntityAssembly);
        syncConfig.Validate();

        // Guard against re-initialization when WebApplicationFactory creates multiple
        // host instances in the same process during integration testing.
        if (!EntityReflectionHelper.IsInitialized)
            EntityReflectionHelper.Initialize(syncConfig);

        services.AddSingleton(syncConfig);
        services.AddSingleton<SyncConfiguration>(syncConfig); // base type binding

        // ── Metadata cache ───────────────────────────────────────────────────
        services.AddSingleton<ITableMetadataCache>(_ => new TableMetadataCache(syncConfig));

        // ── Database ─────────────────────────────────────────────────────────
        RegisterDatabase(services, options);

        // ── Sync services ─────────────────────────────────────────────────────
        services.AddScoped<ISessionTracker, SessionTracker>();
        services.AddScoped<ITempTableManager, TempTableManager>();
        services.AddScoped<ISyncQueueProcessor, SyncQueueProcessor>();

        // Direct write services (Session 28a)
        services.AddScoped<IDirectWriteService, DirectWriteService>();
        services.AddSingleton<IDirectWriteTenantValidator, DefaultDirectWriteTenantValidator>();

        // Seed service (Session 31a)
        services.AddScoped<ISeedService, SeedService>();

        // Activity logging removed in 38l — audit data now on SyncSessions directly

        // Data endpoint gating filter (Session 28b)
        services.AddScoped<DataEndpointsEnabledFilter>();

        // Cleanup services — IEnumerable<ICleanupService> consumed by background worker
        services.AddScoped<ICleanupService, SessionCleanupService>();
        services.AddScoped<ICleanupService, SharedTableCleanupService>();
        services.AddScoped<ICleanupService, TempTableCleanupService>();

        // ── Background workers ────────────────────────────────────────────────
        services.AddHostedService<SyncQueueBackgroundService>();
        services.AddHostedService<SyncCleanupBackgroundService>();

        // ── Controllers ───────────────────────────────────────────────────────
        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);

        services.AddEndpointsApiExplorer();

        // ── Swagger ───────────────────────────────────────────────────────────
        if (options.EnableSwagger)
            RegisterSwagger(services);

        // ── Health checks ─────────────────────────────────────────────────────
        services.AddHealthChecks()
            .AddCheck("database", () =>
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Database configured"))
            .AddCheck("background-queue", () =>
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Queue running"));

        // ── Authorization ─────────────────────────────────────────────────────
        // When RequireAuthorization = false (e.g. local dev), register an allow-all
        // "SyncAccess" policy so [Authorize(Policy = "SyncAccess")] on SyncController
        // passes without any auth middleware. When true, the policy is NOT registered
        // here — the consumer must configure their auth scheme and define "SyncAccess"
        // (e.g. AddAuthentication().AddJwtBearer() + AddAuthorization(o => o.AddPolicy(...))).
        if (!options.RequireAuthorization)
        {
            services.AddAuthorization(o =>
                o.AddPolicy("SyncAccess", p => p.RequireAssertion(_ => true)));
        }
        else
        {
            // Ensure the authorization middleware is registered even when the consumer
            // owns the policy — avoids "No authN scheme" exceptions at startup.
            services.AddAuthorization();
        }

        return services;
    }

    /// <summary>
    /// Runs SyncSystem startup tasks: schema validation (when <see cref="SyncSessionOptions.ValidateSchema"/> is <c>true</c>).
    /// Call after <c>builder.Build()</c> and before <c>MapSyncEndpoints()</c>.
    /// </summary>
    /// <param name="app">The built <see cref="WebApplication"/>.</param>
    /// <returns>The same <see cref="WebApplication"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown on startup if required infrastructure tables or entity columns are missing.
    /// </exception>
    /// <remarks>
    /// Mirrors the standard ASP.NET Core <c>AddX()</c> / <c>UseX()</c> / <c>MapX()</c> pattern.
    /// <c>UseSyncSystem()</c> will also house <c>AutoMigrate</c> when that feature arrives (Session 29a).
    /// </remarks>
    public static async Task<WebApplication> UseSyncSystem(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<SyncSessionOptions>();

        // ── AutoMigrate ───────────────────────────────────────────────────────
        if (options.AutoMigrate)
        {
            var provider = options.DatabaseProvider.ToLowerInvariant();
            if (provider is "mysql" or "mariadb" or "postgres" or "sqlite")
            {
                var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("SyncSystem.AutoMigrate");
                if (!DatabaseMigrator.EnsureDatabase(options.ConnectionString, provider, null, logger))
                    throw new InvalidOperationException(
                        "SyncSystem AutoMigrate failed. Check logs for details.");
            }
        }

        // ── Shared temp tables ─────────────────────────────────────────────
        // Ensure TempPush{Table} / TempPull{Table} exist for every registered entity.
        // Runs after AutoMigrate (entity tables must exist) and before schema validation.
        // Also patches schema drift: adds columns that are in the entity table but
        // missing from an existing temp table (e.g. after a column was added to the entity).
        {
            using var tempScope = app.Services.CreateScope();
            var tempDb = tempScope.ServiceProvider.GetRequiredService<IServerDatabase>();
            await tempDb.EnsureSharedTempTablesAsync();
        }

        if (!options.ValidateSchema)
            return app;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IServerDatabase>();
        var syncConfig = scope.ServiceProvider.GetRequiredService<ServerSyncConfiguration>();

        var errors = new List<string>();

        // ── Infrastructure table validation ───────────────────────────────────
        // Only the critical columns that SyncSystem absolutely depends on are checked.
        // Additional columns (e.g. ErrorMessage, TenantId) are optional / additive.
        var infraTables = new Dictionary<string, string[]>
        {
            ["SessionRecords"]           = ["SessionId", "SessionType", "Status", "SyncVersion", "CreatedAtUtc", "LastActivityUtc"],
            ["ClientProcessedSessions"]  = ["DeviceId", "SessionId", "ProcessedAtUtc"],
            ["SyncSessionTables"]        = ["SessionId", "TableName", "TempTableName", "UsesSharedTable", "EstimatedRecordCount", "Status"],
        };

        foreach (var (tableName, requiredColumns) in infraTables)
        {
            var columns = await db.GetTableColumnsAsync(tableName);
            if (columns.Count == 0)
            {
                errors.Add($"Infrastructure table '{tableName}' is missing. Run SyncSystem migrations.");
                continue;
            }

            var missing = requiredColumns
                .Except(columns, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var col in missing)
                errors.Add($"Infrastructure table '{tableName}' is missing column '{col}'.");
        }

        // ── Entity table validation ───────────────────────────────────────────
        // Every registered [SyncTable] type must have the core ISyncEntity server-side columns.
        string[] requiredEntityColumns = ["Id", "IsDeleted", "ModifiedByUserId", "ModifiedAtUtc", "SyncSessionId"];

        foreach (var tableConfig in syncConfig.GetTables())
        {
            var columns = await db.GetTableColumnsAsync(tableConfig.TableName);
            if (columns.Count == 0)
            {
                errors.Add($"Table '{tableConfig.TableName}' is missing. Run migrations or create the table manually.");
                continue;
            }

            var missing = requiredEntityColumns
                .Except(columns, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var col in missing)
                errors.Add($"Table '{tableConfig.TableName}' is missing required column '{col}'. " +
                           "Run SyncSystem migrations or add the column manually.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"SyncSystem schema validation failed with {errors.Count} error(s):\n" +
                string.Join("\n", errors.Select(e => $"  - {e}")));

        app.Logger.LogInformation(
            "SyncSystem schema validation passed ({TableCount} tables OK)",
            infraTables.Count + syncConfig.GetTables().Count());

        return app;
    }

    /// <summary>
    /// Maps all SyncSystem endpoints: sync API controllers, health checks, and API info root.
    /// Call on <see cref="WebApplication"/> after <c>Build()</c>.
    /// </summary>
    public static WebApplication MapSyncEndpoints(this WebApplication app)
    {
        app.MapControllers();

        app.MapHealthChecks("/health");
        app.MapHealthChecks("/api/v1/health");

        app.MapGet("/", () => Results.Ok(new
        {
            name = "SyncSystem API",
            version = "v1",
            status = "running",
            documentation = "/swagger",
            health = "/health",
            timestamp = DateTime.UtcNow
        }))
        .WithName("Root")
        .WithTags("Info");

        var options = app.Services.GetService<SyncSessionOptions>()
                     ?? new SyncSessionOptions { EnableSwagger = true };

        if (options.EnableSwagger && app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "SyncSystem API v1");
                c.RoutePrefix = "swagger";
                c.DocumentTitle = "SyncSystem API Documentation";
                c.DisplayRequestDuration();
            });
        }

        return app;
    }

    /// <summary>
    /// Activates the optional data query and direct write REST endpoints (<c>/api/v1/data/...</c>).
    /// Call after <c>MapSyncEndpoints()</c>. Without this call, DataController returns 404 for all routes.
    /// </summary>
    /// <remarks>
    /// Endpoints provided:
    /// <list type="bullet">
    ///   <item><c>GET  /api/v1/data/{table}/{id}</c> — single record by ID</item>
    ///   <item><c>POST /api/v1/data/{table}/query</c> — filtered, paginated query</item>
    ///   <item><c>POST /api/v1/data</c> — batch write (multiple tables, transactional)</item>
    ///   <item><c>POST /api/v1/data/{table}</c> — single-record upsert</item>
    ///   <item><c>DELETE /api/v1/data/{table}/{id}</c> — soft delete</item>
    /// </list>
    /// All endpoints require the <c>SyncAccess</c> authorization policy.
    /// </remarks>
    public static WebApplication MapSyncDataEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<SyncSessionOptions>();
        options.EnableDataEndpoints = true;
        return app;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void RegisterDatabase(IServiceCollection services, SyncSessionOptions options)
    {
        switch (options.DatabaseProvider.ToLowerInvariant())
        {
            case "mysql":
            case "mariadb":
                // [TODO POST-MIGRATION] Remove the keyed "syncsystem" registration once all tenants
                // are Migrated and SyncModeAwareServerDatabase is deleted from the consumer app.
                // At that point this factory is the only IServerDatabase registration needed.
                services.AddScoped<IServerDatabase>(sp =>
                    CreateMySqlServerDatabase(sp, options.ConnectionString));

                // Keyed registration consumed by SyncModeAwareServerDatabase in apps that need
                // dual-DB routing during a migration period (e.g. Legacy → Migrated transition).
                // [TODO POST-MIGRATION] Remove this keyed registration once all tenants are Migrated.
                services.AddKeyedScoped<IServerDatabase>("syncsystem", (sp, _) =>
                    CreateMySqlServerDatabase(sp, options.ConnectionString));
                break;

            case "sqlite":
                services.AddScoped<IServerDatabase>(sp =>
                {
                    var config = sp.GetRequiredService<ServerSyncConfiguration>();
                    var cache  = sp.GetRequiredService<ITableMetadataCache>();
                    var conn   = new SqliteConnection(options.ConnectionString);
                    conn.Open();
                    return new SqliteServerDatabase(conn, cache, config);
                });
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported DatabaseProvider '{options.DatabaseProvider}'. Supported: MySQL, MariaDB, SQLite");
        }
    }

    private static MySqlServerDatabase CreateMySqlServerDatabase(IServiceProvider sp, string connectionString)
    {
        var config = sp.GetRequiredService<ServerSyncConfiguration>();
        var cache  = sp.GetRequiredService<ITableMetadataCache>();
        var logger = sp.GetRequiredService<ILogger<MySqlServerDatabase>>();
        return new MySqlServerDatabase(connectionString, cache, config, logger);
    }

    /// <summary>
    /// Registers a legacy (pre-migration) <see cref="IServerDatabase"/> keyed as <c>"legacy"</c>,
    /// backed by the supplied <paramref name="connectionString"/>. Consumed by
    /// <c>SyncModeAwareServerDatabase</c> to route seed and sync operations for non-Migrated tenants
    /// to the original data source while Migrated tenants use the SyncSystem DB.
    /// </summary>
    /// <remarks>
    /// Call this after <c>AddSyncSystem()</c>. The unkeyed <see cref="IServerDatabase"/> must then
    /// be replaced with a <c>SyncModeAwareServerDatabase</c> proxy in the consumer's
    /// <c>Program.cs</c> (see DVMApp integration for reference).
    /// <para>
    /// [TODO POST-MIGRATION] Delete this method and all call sites once all tenants are Migrated.
    /// At that point remove the proxy, remove this registration, and the unkeyed
    /// <see cref="IServerDatabase"/> from <c>AddSyncSystem()</c> is the only one needed.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSyncSystemLegacyDatabase(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddKeyedScoped<IServerDatabase>("legacy", (sp, _) =>
            CreateMySqlServerDatabase(sp, connectionString));
        return services;
    }

    private static void RegisterSwagger(IServiceCollection services)
    {
        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "SyncSystem API",
                Version = "v1",
                Description = "Offline-first data synchronization for distributed .NET applications.",
                Contact = new OpenApiContact { Name = "SyncSystem" },
                License = new OpenApiLicense
                {
                    Name = "MIT",
                    Url = new Uri("https://opensource.org/licenses/MIT")
                }
            });

            var xmlPath = Path.Combine(AppContext.BaseDirectory,
                $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xmlPath))
                o.IncludeXmlComments(xmlPath);
        });
    }
}
