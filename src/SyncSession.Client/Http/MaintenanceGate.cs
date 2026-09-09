using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SyncSession.Core.Exceptions;

namespace SyncSession.Client.Http;

/// <summary>
/// Turns a gated server response (<c>503</c>) into <see cref="SyncMaintenanceException"/>.
/// </summary>
/// <remarks>
/// Called before <c>EnsureSuccessStatusCode()</c>, whose message is the resource string
/// <c>net_http_message_not_success_statuscode_reason</c> — it discards both facts the server
/// sent: why it refused, and how long to wait.
/// </remarks>
internal static class MaintenanceGate
{
    /// <summary>Throws if the server refused this call because it is gated; otherwise returns.</summary>
    internal static async Task ThrowIfGatedAsync(
        HttpResponseMessage response,
        CancellationToken ct = default)
    {
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return;

        throw new SyncMaintenanceException(
            await ReadReasonAsync(response, ct).ConfigureAwait(false),
            ReadRetryAfterSeconds(response));
    }

    /// <summary>
    /// Reads the reason the server gave, in the two shapes it actually sends: the begin endpoints
    /// return the DTO as JSON with <c>errorMessage</c>, and the seed endpoint writes plain text
    /// (SyncController.cs:454-460). Anything else — an HTML error page, an empty body — is a 503
    /// from something in front of the app, and reports no reason rather than a misleading one.
    /// </summary>
    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return null;

            body = body.Trim();

            if (body.StartsWith("{", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                // Both casings, and PascalCase first because that is what this server actually
                // sends: SyncSessionExtensions.cs:110 sets PropertyNamingPolicy = null. A host that
                // leaves the web defaults in place sends camelCase, so accept either.
                foreach (var name in new[] { "ErrorMessage", "errorMessage" })
                {
                    if (document.RootElement.TryGetProperty(name, out var message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString();
                    }
                }

                return null;
            }

            // Plain text only, and only if it reads like a sentence rather than a page.
            return body.Length <= 300 && !body.Contains("<", StringComparison.Ordinal) ? body : null;
        }
        catch (Exception)
        {
            // An unreadable body is not a different failure — the 503 is still the fact.
            return null;
        }
    }

    /// <summary>
    /// Reads <c>Retry-After</c>. The server sends the delta form only ("60"), so that is the one
    /// shape handled; anything else falls back to the default rather than inventing a number.
    /// </summary>
    private static int ReadRetryAfterSeconds(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;

        return delta.HasValue && delta.Value.TotalSeconds >= 1
            ? (int)delta.Value.TotalSeconds
            : SyncMaintenanceException.DefaultRetryAfterSeconds;
    }
}
