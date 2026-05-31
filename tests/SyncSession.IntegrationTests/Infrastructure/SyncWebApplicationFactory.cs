using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SyncSession.Server;

namespace SyncSession.IntegrationTests.Infrastructure;

/// <summary>
/// Custom WebApplicationFactory that overrides the connection string so the server
/// uses an isolated test database from the shared MariaDB container.
/// </summary>
public class SyncWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _requireAuthorization;

    /// <param name="connectionString">Isolated test database connection string.</param>
    /// <param name="requireAuthorization">
    /// When true, registers <see cref="TestAuthHandler"/> as the auth scheme and enables
    /// the SyncAccess policy so that unauthenticated requests receive 401.
    /// When false (default), the allow-all SyncAccess policy from AddSyncSession() is used.
    /// </param>
    public SyncWebApplicationFactory(string connectionString, bool requireAuthorization = false)
    {
        _connectionString = connectionString;
        _requireAuthorization = requireAuthorization;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:SyncDatabase", _connectionString);
        builder.UseSetting("DatabaseProvider", "MySQL");
        builder.UseSetting("Serilog:WriteTo:0:Name", "Console");
        builder.UseEnvironment("Test");

        if (_requireAuthorization)
        {
            builder.UseSetting("SyncSystem:RequireAuthorization", "true");

            builder.ConfigureServices(services =>
            {
                services
                    .AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });

                services.AddAuthorization(o =>
                    o.AddPolicy("SyncAccess", p => p.RequireAuthenticatedUser()));
            });
        }
    }
}
