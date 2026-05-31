using System.Net;
using System.Security;
using System.Text.Json;

namespace SyncSession.Server.Middleware;

/// <summary>
/// Global error handling middleware that maps unhandled exceptions to HTTP status codes.
/// </summary>
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (SecurityException ex)
        {
            _logger.LogWarning(ex, "Security violation from {IP}: {Message}", 
                context.Connection.RemoteIpAddress, ex.Message);
            await HandleExceptionAsync(context, ex, HttpStatusCode.Forbidden);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid request from {IP}: {Message}", 
                context.Connection.RemoteIpAddress, ex.Message);
            await HandleExceptionAsync(context, ex, HttpStatusCode.BadRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception from {IP}: {Message}", 
                context.Connection.RemoteIpAddress, ex.Message);
            await HandleExceptionAsync(context, ex, HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// Writes a JSON error response with the specified status code.
    /// </summary>
    private static async Task HandleExceptionAsync(HttpContext context, Exception exception, HttpStatusCode statusCode)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var response = new
        {
            success = false,
            error = exception.Message,
            statusCode = (int)statusCode
        };

        var json = JsonSerializer.Serialize(response);
        await context.Response.WriteAsync(json);
    }
}
