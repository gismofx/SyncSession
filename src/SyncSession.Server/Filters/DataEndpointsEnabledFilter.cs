using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SyncSession.Server.Filters;

/// <summary>
/// Action filter that gates DataController endpoints behind <see cref="SyncSessionOptions.EnableDataEndpoints"/>.
/// Returns 404 when data endpoints haven't been activated via <c>MapSyncDataEndpoints()</c>.
/// </summary>
public class DataEndpointsEnabledFilter : IActionFilter
{
    private readonly SyncSessionOptions _options;

    public DataEndpointsEnabledFilter(SyncSessionOptions options)
    {
        _options = options;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_options.EnableDataEndpoints)
        {
            context.Result = new NotFoundResult();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
