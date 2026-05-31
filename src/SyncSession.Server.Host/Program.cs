using Serilog;
using SyncSession.Server;
using SyncSession.Server.Extensions;
using SyncSession.Server.Middleware;
using SyncSession.Samples.Shared.Entities;

// ── Bootstrap logger (before DI is built) ────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .Build())
    .CreateBootstrapLogger();

Log.Information("Starting SyncSystem Server...");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog (host concern — sinks are deployment-specific) ────────────────
    builder.Host.UseSerilog((ctx, svc, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(svc));

    // ── SyncSystem library wiring ─────────────────────────────────────────────
    var connectionString = builder.Configuration.GetConnectionString("SyncDatabase")
        ?? throw new InvalidOperationException("SyncDatabase connection string is required.");

    builder.Services.AddSyncSystem(opts =>
    {
        opts.ConnectionString     = connectionString;
        opts.DatabaseProvider     = builder.Configuration.GetValue<string>("DatabaseProvider") ?? "MySQL";
        opts.EntityAssembly       = typeof(Customer).Assembly;
        opts.EnableSwagger        = true;
        opts.AutoMigrate          = true;
        opts.RequireAuthorization = builder.Configuration.GetValue<bool?>("SyncSystem:RequireAuthorization") ?? false;
        opts.ValidateSchema       = builder.Configuration.GetValue<bool?>("SyncSystem:ValidateSchema") ?? true;

        // Bind server sync settings from appsettings.json ["SyncConfiguration"] section
        builder.Configuration.GetSection("SyncConfiguration").Bind(opts.Sync);
    });

    // ── Authentication (production) ───────────────────────────────────────────
    // When SyncSystem:RequireAuthorization = true, configure your auth scheme here
    // and define the "SyncAccess" policy. Example — JWT Bearer:
    //
    //   builder.Services
    //       .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    //       .AddJwtBearer(o =>
    //       {
    //           o.Authority = builder.Configuration["Auth:Authority"];
    //           o.Audience  = builder.Configuration["Auth:Audience"];
    //       });
    //   builder.Services.AddAuthorization(o =>
    //       o.AddPolicy("SyncAccess", p => p.RequireAuthenticatedUser()));
    //
    // Add the Microsoft.AspNetCore.Authentication.JwtBearer package and uncomment.
    // Any ASP.NET Core-compatible scheme works (API key, certificate, custom handler).

    // ── CORS (host concern — origins are deployment-specific) ─────────────────
    builder.Services.AddCors(options =>
    {
        if (builder.Environment.IsDevelopment())
        {
            options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        }
        else
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                          ?? Array.Empty<string>();
            options.AddPolicy("Production", p =>
                p.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader().AllowCredentials());
        }
    });

    // ── Build ─────────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ── Middleware pipeline ───────────────────────────────────────────────────
    app.UseMiddleware<RequestLoggingMiddleware>();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseHttpsRedirection();
    app.UseCors(app.Environment.IsDevelopment() ? "AllowAll" : "Production");
    app.UseAuthentication(); // no-op when RequireAuthorization = false (no scheme registered)
    app.UseAuthorization();

    // ── Startup tasks: AutoMigrate + schema validation ────────────────────────
    await app.UseSyncSystem();

    // ── Endpoints ─────────────────────────────────────────────────────────────
    app.MapSyncEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.Information("Shutting down SyncSystem Server");
    Log.CloseAndFlush();
}

// Required for WebApplicationFactory in integration tests
public partial class Program { }
