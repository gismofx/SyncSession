using System.Diagnostics;

namespace SyncSession.Server.Middleware;

/// <summary>
/// Middleware that logs all incoming HTTP requests with method, path, status code, and elapsed time.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestPath = context.Request.Path;
        var requestMethod = context.Request.Method;

        _logger.LogInformation(
            "Request started: {Method} {Path} from {IP}",
            requestMethod,
            requestPath,
            context.Connection.RemoteIpAddress);

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            
            _logger.LogInformation(
                "Request completed: {Method} {Path} - Status {StatusCode} in {ElapsedMs}ms",
                requestMethod,
                requestPath,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
