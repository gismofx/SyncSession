using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SyncSession.Client.Utilities;

/// <summary>
/// Retry policy with exponential backoff for transient failures.
/// </summary>
public class RetryPolicy
{
    private readonly int _maxRetries;
    private readonly int _baseDelayMs;

    /// <summary>
    /// Initializes a new instance of <see cref="RetryPolicy"/>.
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3).</param>
    /// <param name="baseDelayMs">Base delay in milliseconds for exponential backoff (default: 1000).</param>
    public RetryPolicy(int maxRetries = 3, int baseDelayMs = 1000)
    {
        _maxRetries = maxRetries;
        _baseDelayMs = baseDelayMs;
    }

    /// <summary>
    /// Executes an action with retry logic, using exponential backoff on transient failures.
    /// </summary>
    /// <typeparam name="T">Return type of the action.</typeparam>
    /// <param name="action">The action to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the successful action invocation.</returns>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < _maxRetries)
            {
                var delay = _baseDelayMs * Math.Pow(2, attempt - 1);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
        }
        
        // Last attempt without retry
        return await action();
    }

    /// <summary>
    /// Returns <c>true</c> if the exception represents a transient failure that warrants a retry.
    /// </summary>
    /// <summary>
    /// Determines whether the exception represents a transient failure that warrants a retry.
    /// </summary>
    private bool IsTransientError(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is TimeoutException
            || (ex.InnerException is HttpRequestException);
    }
}
