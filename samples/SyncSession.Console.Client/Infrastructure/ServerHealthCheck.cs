using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace SyncSession.Samples.Console.Infrastructure;

/// <summary>
/// Checks if the sync server is running and accessible
/// </summary>
public class ServerHealthCheck
{
    private readonly HttpClient _httpClient;
    private readonly string _serverUrl;

    public ServerHealthCheck(string serverUrl)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Check if server is accessible (fail-fast with retries)
    /// </summary>
    /// <param name="maxRetries">Maximum retry attempts (default: 5)</param>
    /// <param name="delayMs">Delay between retries in milliseconds (default: 1000)</param>
    /// <returns>True if server is running, false otherwise</returns>
    public async Task<bool> CheckAsync(int maxRetries = 5, int delayMs = 1000)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var healthUrl = $"{_serverUrl}/api/v1/health";
                
                if (attempt == 1)
                    System.Console.Write($"Checking server at {healthUrl}...");
                else
                    System.Console.Write($" (attempt {attempt}/{maxRetries})");
                
                var response = await _httpClient.GetAsync(healthUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    System.Console.WriteLine(" ✓");
                    return true;
                }
                
                System.Console.WriteLine($" Status: {response.StatusCode}");
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                // Server not ready yet, wait and retry
                await Task.Delay(delayMs);
                continue;
            }
            catch (HttpRequestException ex)
            {
                System.Console.WriteLine($" ✗");
                System.Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException) when (attempt < maxRetries)
            {
                // Timeout, retry
                await Task.Delay(delayMs);
                continue;
            }
            catch (TaskCanceledException)
            {
                System.Console.WriteLine($" ✗ (timeout)");
                return false;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($" ✗");
                System.Console.WriteLine($"Error: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }
        
        System.Console.WriteLine($" ✗ (max retries exceeded)");
        return false;
    }

    /// <summary>
    /// Display helpful message if server is not running
    /// </summary>
    public void DisplayServerNotRunningMessage()
    {
        OutputHelper.WriteBlankLine();
        OutputHelper.WriteError($"Server not running at {_serverUrl}");
        OutputHelper.WriteBlankLine();
        OutputHelper.WriteInfo("To start the server:");
        System.Console.WriteLine("  cd src/SyncSystem.Server");
        System.Console.WriteLine("  dotnet run");
        OutputHelper.WriteBlankLine();
        OutputHelper.WriteInfo("Or use a different server:");
        System.Console.WriteLine("  dotnet run --server https://other-server-url");
        OutputHelper.WriteBlankLine();
    }
}
