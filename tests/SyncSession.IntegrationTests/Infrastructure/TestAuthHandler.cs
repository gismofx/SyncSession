using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SyncSession.IntegrationTests.Infrastructure;

/// <summary>
/// Test authentication handler that reads tenant and user identity from request headers.
/// Inject X-Test-TenantId and X-Test-UserId headers to simulate authenticated requests.
/// Returns NoResult (unauthenticated) when X-Test-TenantId header is absent.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestAuth";
    public const string TenantIdHeader = "X-Test-TenantId";
    public const string UserIdHeader = "X-Test-UserId";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TenantIdHeader, out var tenantId))
            return Task.FromResult(AuthenticateResult.NoResult());

        var userId = Request.Headers.TryGetValue(UserIdHeader, out var uid)
            ? uid.ToString()
            : "test-user";

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, userId),
            new Claim("TenantId", tenantId.ToString())
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
